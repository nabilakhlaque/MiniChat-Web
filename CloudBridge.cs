using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MiniChat
{
    /// <summary>
    /// Molnbrygga via en publik ntfy-server (standard https://ntfy.sh) – reservväg när ingen
    /// direkt förbindelse finns. Allt går över HTTPS på port 443 (via systemets proxy om det finns en).
    ///
    /// Familjekoden ger både ämnesnamnet (topic) och krypteringsnyckeln. Servern ser bara krypterade
    /// blobbar. Små ramar skickas som meddelandetext, större (långa texter, ljud, filer, profilbilder)
    /// som bilaga med en liten krypterad metadata-ram i Message-huvudet.
    ///
    /// Servern cachar meddelanden i ca 12 timmar, så den som varit offline får dem när den kommer in.
    /// </summary>
    public class CloudBridge
    {
        public const string DefaultServer = "https://ntfy.sh";
        const int MaxTextFrame = 2700;   // bytes klartext som ryms i ett ntfy-meddelande (4 kB) efter kryptering + base64
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        readonly string _server;
        readonly string _topic;
        readonly byte[] _enc, _mac;
        readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        readonly Queue<byte[]> _outbox = new Queue<byte[]>();
        readonly object _lock = new object();
        volatile bool _running;
        volatile bool _connected;
        Thread _subThread, _pubThread;
        HttpWebRequest _subReq;
        string _lastId;
        string _lastError = "";

        /// <summary>(ram, bilage-url eller null, bilagans storlek)</summary>
        public event Action<byte[], string, long> Received;
        /// <summary>(ansluten?, meddelande)</summary>
        public event Action<bool, string> StateChanged;

        public bool Connected { get { return _connected; } }
        public string Topic { get { return _topic; } }

        public CloudBridge(string server, string familyCode)
        {
            _server = (string.IsNullOrEmpty(server) ? DefaultServer : server.Trim()).TrimEnd('/');
            if (!_server.StartsWith("http://") && !_server.StartsWith("https://")) _server = "https://" + _server;
            string code = NormalizeCode(familyCode);
            using (SHA256 sha = SHA256.Create())
                _topic = "mc-" + BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes("MiniChat-topic-v1:" + code))).Replace("-", "").Substring(0, 24).ToLowerInvariant();
            byte[] k;
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(code, Utf8.GetBytes("MiniChat-cloud-salt-v1"), 60000))
                k = kdf.GetBytes(32);
            _enc = Crypto.Hmac(k, "MiniChat cloud enc");
            _mac = Crypto.Hmac(k, "MiniChat cloud mac");
        }

        public static string NormalizeCode(string code)
        {
            return (code ?? "").Trim().ToLowerInvariant();
        }

        // ================================================================ start/stopp

        public void Start()
        {
            if (_running) return;
            _running = true;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 8);
            }
            catch { }
            _subThread = new Thread(SubscribeLoop) { IsBackground = true, Name = "MiniChat cloud sub" };
            _pubThread = new Thread(PublishLoop) { IsBackground = true, Name = "MiniChat cloud pub" };
            _subThread.Start();
            _pubThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_subReq != null) _subReq.Abort(); } catch { }
            lock (_lock) Monitor.PulseAll(_lock);
            SetState(false, null);
        }

        // ================================================================ publicera

        /// <summary>Lägger en ram i kön. Små ramar blir meddelandetext, stora blir bilaga (CBLOB).</summary>
        public void Publish(byte[] frame)
        {
            lock (_lock)
            {
                _outbox.Enqueue(frame);
                while (_outbox.Count > 200) _outbox.Dequeue();
                Monitor.PulseAll(_lock);
            }
        }

        void PublishLoop()
        {
            while (_running)
            {
                byte[] frame = null;
                lock (_lock)
                {
                    if (_outbox.Count == 0) Monitor.Wait(_lock, 5000);
                    if (_outbox.Count > 0) frame = _outbox.Peek();
                }
                if (frame == null) continue;

                int wait = 0;
                bool ok = false;
                try
                {
                    if (frame.Length <= MaxTextFrame) ok = PostText(Convert.ToBase64String(Crypto.Seal(_enc, _mac, frame)));
                    else ok = PutAttachment(Network.Frame("CBLOB|" + Guid.NewGuid().ToString("N"), null), frame, null);
                }
                catch (WebException ex)
                {
                    HttpWebResponse r = ex.Response as HttpWebResponse;
                    int code = r != null ? (int)r.StatusCode : 0;
                    if (code == 429) { wait = 60000; SetState(_connected, "Molnbryggan: för många meddelanden – väntar en minut"); }
                    else if (code == 413) { ok = true; SetState(_connected, "Molnbryggan: meddelandet var för stort och hoppades över"); }
                    else wait = 15000;
                    _lastError = ex.Message;
                }
                catch (Exception ex) { wait = 15000; _lastError = ex.Message; }

                if (ok)
                {
                    lock (_lock) if (_outbox.Count > 0 && _outbox.Peek() == frame) _outbox.Dequeue();
                }
                else if (wait > 0)
                {
                    lock (_lock) Monitor.Wait(_lock, wait);
                }
            }
        }

        /// <summary>Skickar en liten ram direkt (t.ex. CBYE vid avslut) med kort timeout. Fel ignoreras.</summary>
        public void TrySendNow(byte[] frame)
        {
            try
            {
                HttpWebRequest req = NewRequest(_server + "/" + _topic, "POST");
                req.Timeout = 3000;
                req.ReadWriteTimeout = 3000;
                req.ContentType = "text/plain; charset=utf-8";
                byte[] data = Utf8.GetBytes(Convert.ToBase64String(Crypto.Seal(_enc, _mac, frame)));
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse()) { }
            }
            catch { }
        }

        bool PostText(string body)
        {
            HttpWebRequest req = NewRequest(_server + "/" + _topic, "POST");
            req.ContentType = "text/plain; charset=utf-8";
            byte[] data = Utf8.GetBytes(body);
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
        }

        /// <summary>Skickar en krypterad bilaga med en krypterad metadata-ram i Message-huvudet. Blockerar tills klart.</summary>
        public bool PutAttachment(byte[] headerFrame, byte[] attachment, Action<long, long> progress)
        {
            byte[] boxed = Crypto.Seal(_enc, _mac, attachment);
            HttpWebRequest req = NewRequest(_server + "/" + _topic, "PUT");
            req.ContentType = "application/octet-stream";
            req.Headers["Filename"] = "m.bin";
            req.Headers["Message"] = Convert.ToBase64String(Crypto.Seal(_enc, _mac, headerFrame));
            req.ContentLength = boxed.Length;
            req.AllowWriteStreamBuffering = false;
            req.Timeout = 600000;
            req.ReadWriteTimeout = 600000;
            using (Stream s = req.GetRequestStream())
            {
                int off = 0;
                while (off < boxed.Length)
                {
                    int n = Math.Min(64 * 1024, boxed.Length - off);
                    s.Write(boxed, off, n);
                    off += n;
                    if (progress != null) progress(off, boxed.Length);
                }
            }
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
        }

        /// <summary>Hämtar och dekrypterar en bilaga. Blockerar tills klart.</summary>
        public byte[] Download(string url, long expectedSize, Action<long, long> progress)
        {
            HttpWebRequest req = NewRequest(url, "GET");
            req.Timeout = 60000;
            req.ReadWriteTimeout = 600000;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                long total = resp.ContentLength > 0 ? resp.ContentLength : expectedSize;
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (progress != null) progress(ms.Length, total);
                }
                return Crypto.Unseal(_enc, _mac, ms.ToArray());
            }
        }

        HttpWebRequest NewRequest(string url, string method)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "MiniChat";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.KeepAlive = true;
            req.AllowAutoRedirect = true;
            req.Proxy = WebRequest.DefaultWebProxy; // skolans/jobbets proxy om det finns en
            if (req.Proxy != null) req.Proxy.Credentials = CredentialCache.DefaultCredentials;
            return req;
        }

        // ================================================================ prenumerera

        void SubscribeLoop()
        {
            int backoff = 2000;
            while (_running)
            {
                try
                {
                    string url = _server + "/" + _topic + "/json?since=" + (_lastId ?? "12h");
                    HttpWebRequest req = NewRequest(url, "GET");
                    req.ReadWriteTimeout = 120000; // servern skickar keepalive var 45:e sekund
                    _subReq = req;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader r = new StreamReader(resp.GetResponseStream(), Utf8))
                    {
                        SetState(true, null);
                        backoff = 2000;
                        string line;
                        while (_running && (line = r.ReadLine()) != null)
                        {
                            if (line.Length == 0) continue;
                            try { HandleLine(line); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    _lastError = ex.Message;
                    SetState(false, "Molnbryggan tappade kontakten – försöker igen");
                }
                if (!_running) break;
                Thread.Sleep(backoff);
                backoff = Math.Min(backoff * 2, 60000);
            }
            SetState(false, null);
        }

        void HandleLine(string line)
        {
            Dictionary<string, object> o = _json.DeserializeObject(line) as Dictionary<string, object>;
            if (o == null) return;
            string ev = Str(o, "event");
            if (ev != "message") return;
            string id = Str(o, "id");
            if (id.Length > 0) _lastId = id;
            string msg = Str(o, "message");
            string url = null;
            long size = 0;
            object att;
            if (o.TryGetValue("attachment", out att))
            {
                Dictionary<string, object> a = att as Dictionary<string, object>;
                if (a != null)
                {
                    url = Str(a, "url");
                    object sz;
                    if (a.TryGetValue("size", out sz)) { try { size = Convert.ToInt64(sz); } catch { } }
                }
            }
            byte[] frame;
            try { frame = Crypto.Unseal(_enc, _mac, Convert.FromBase64String(msg)); }
            catch { return; } // annan kod, skräp eller ntfy:s egna meddelanden
            Action<byte[], string, long> h = Received;
            if (h != null) h(frame, url, size);
        }

        static string Str(Dictionary<string, object> o, string key)
        {
            object v;
            return o.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        void SetState(bool connected, string message)
        {
            bool changed = connected != _connected;
            _connected = connected;
            Action<bool, string> h = StateChanged;
            if (h != null && (changed || message != null)) h(connected, message);
        }
    }
}
