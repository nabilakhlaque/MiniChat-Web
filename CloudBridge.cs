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
        DateTime _pauseUntil = DateTime.MinValue;   // skicka inget förrän då (efter 429/fel)
        int _fails;
        bool _quotaWarned;

        /// <summary>(ram, bilage-url eller null, bilagans storlek)</summary>
        /// <summary>Ram, bilage-URL, bilagestorlek och serverns tidsstämpel för meddelandet (UTC).</summary>
        public event Action<byte[], string, long, DateTime> Received;
        /// <summary>(ansluten?, meddelande)</summary>
        public event Action<bool, string> StateChanged;

        public bool Connected { get { return _connected; } }
        public string Topic { get { return _topic; } }

        /// <summary>Kort lägesbeskrivning för menyn.</summary>
        public string StatusText
        {
            get
            {
                if (_pauseUntil > DateTime.UtcNow)
                    return "pausad till " + _pauseUntil.ToLocalTime().ToString("HH:mm") + " – servern bromsar (" + Pending + " i kö)";
                if (!_connected) return "ansluter…" + (_lastError.Length > 0 ? " (" + Short(_lastError) + ")" : "");
                string mode = _polling ? "ansluten (hämtar var 45:e sekund)" : "ansluten";
                return Pending > 0 ? mode + ", " + Pending + " i kö" : mode;
            }
        }
        volatile bool _polling;
        int Pending { get { lock (_lock) return _outbox.Count; } }
        static string Short(string s) { return s.Length > 60 ? s.Substring(0, 57) + "…" : s; }

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
                // Närvaro och läskvitton behöver bara finnas en gång i kön – byt ut äldre
                string kind = Kind(frame);
                if (kind == "CHELLO" || kind == "SEEN" || kind == "CAVATAR")
                {
                    byte[][] all = _outbox.ToArray();
                    _outbox.Clear();
                    foreach (byte[] f in all) if (Kind(f) != kind) _outbox.Enqueue(f);
                }
                _outbox.Enqueue(frame);
                while (_outbox.Count > 200) _outbox.Dequeue();
                Monitor.PulseAll(_lock);
            }
        }

        static string Kind(byte[] frame)
        {
            int n = Math.Min(frame.Length, 12);
            for (int i = 0; i < n; i++) if (frame[i] == (byte)'|' || frame[i] == 10) return Utf8.GetString(frame, 0, i);
            return "";
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

                // Paus efter fel gäller oavsett hur många nya ramar som kommer in – annars äter
                // försöken upp hela kvoten och servern fortsätter säga nej.
                if (_pauseUntil > DateTime.UtcNow)
                {
                    Thread.Sleep((int)Math.Min(5000, Math.Max(200, (_pauseUntil - DateTime.UtcNow).TotalMilliseconds + 50)));
                    continue;
                }

                bool ok = false, drop = false, daily = false;
                int status = 0;
                try
                {
                    if (frame.Length <= MaxTextFrame) ok = PostText(Convert.ToBase64String(Crypto.Seal(_enc, _mac, frame)));
                    else ok = PutAttachment(Network.Frame("CBLOB|" + Guid.NewGuid().ToString("N"), null), frame, null);
                }
                catch (WebException ex)
                {
                    HttpWebResponse r = ex.Response as HttpWebResponse;
                    status = r != null ? (int)r.StatusCode : -1;
                    string detail = r != null ? ServerError(r) : null;
                    _lastError = (status > 0 ? "HTTP " + status : ex.Message) + (detail != null ? " (" + detail + ")" : "");
                    if (status == 413 || status == 400) drop = true; // för stort / ogiltigt – ingen idé att försöka igen
                    if (status == 429 && detail != null && (detail.IndexOf("daily", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("quota", StringComparison.OrdinalIgnoreCase) >= 0)) daily = true;
                }
                catch (Exception ex) { status = -1; _lastError = ex.Message; }

                if (ok || drop)
                {
                    lock (_lock) if (_outbox.Count > 0 && _outbox.Peek() == frame) _outbox.Dequeue();
                    if (drop) { Log("släppte " + Kind(frame) + " (" + _lastError + ")"); SetState(_connected, "Molnbryggan: ett meddelande var för stort och hoppades över"); }
                    if (ok && _fails > 0) Log("skickar igen efter " + _fails + " fel");
                    if (ok) Log("skickade " + Kind(frame) + (frame.Length > MaxTextFrame ? " som bilaga" : "") + " (" + frame.Length + " B)");
                    if (ok && _quotaWarned) { _quotaWarned = false; SetState(_connected, "Molnbryggan skickar igen"); }
                    _fails = 0;
                    continue;
                }

                _fails++;
                int wait;
                if (daily)
                {
                    // Dagskvoten är slut – den fylls på vid midnatt UTC. Prova igen då, dock minst var 30:e minut
                    // ifall servern räknar annorlunda. Innan dess är varje försök bortkastat.
                    DateTime reset = DateTime.UtcNow.Date.AddDays(1).AddMinutes(1);
                    wait = (int)Math.Min(30 * 60000, Math.Max(60000, (reset - DateTime.UtcNow).TotalMilliseconds));
                }
                else if (status == 429) wait = (int)Math.Min(600000, 60000L * (1 << Math.Min(_fails - 1, 3)));   // 1, 2, 4, 8, 10, 10… minuter
                else wait = Math.Min(120000, 10000 * _fails);                                                      // nät-/serverfel: 10 s, 20 s … 2 min
                _pauseUntil = DateTime.UtcNow.AddMilliseconds(wait);
                Log("fel vid sändning: " + _lastError + " – pausar " + (wait / 1000) + " s, " + Pending + " i kö");
                if (status == 429 && !_quotaWarned)
                {
                    _quotaWarned = true;
                    if (daily)
                        SetState(_connected, "Molnbryggan: dagskvoten hos " + ServerName() + " är slut för er internetadress (alla datorer hemma delar den). Den fylls på vid midnatt UTC, kl 01 svensk tid. Det du skriver köas; datorer på samma nät når varandra direkt som vanligt.");
                    else
                        SetState(_connected, "Molnbryggan: servern bromsar (för många anrop på kort tid) – pausar " + (wait / 60000) + " min och försöker igen. Det du skriver köas.");
                }
                else if (status != 429 && _fails == 3)
                {
                    SetState(_connected, "Molnbryggan når inte servern (" + Short(_lastError) + ") – försöker igen");
                }
            }
        }

        public static void Log(string line) { AppLog.Write("moln", line); }

        string ServerName()
        {
            try { return new Uri(_server).Host; } catch { return _server; }
        }

        /// <summary>ntfy svarar med JSON {"code":42908,"http":429,"error":"limit reached: ..."} – plocka ut texten.</summary>
        static string ServerError(HttpWebResponse r)
        {
            try
            {
                using (Stream s = r.GetResponseStream())
                using (StreamReader rd = new StreamReader(s, Utf8))
                {
                    char[] buf = new char[2000];
                    int n = rd.Read(buf, 0, buf.Length);
                    if (n <= 0) return null;
                    string body = new string(buf, 0, n).Trim();
                    Dictionary<string, object> o = null;
                    try { o = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>; } catch { }
                    if (o != null)
                    {
                        string err = Str(o, "error"), code = Str(o, "code");
                        if (err.Length > 0) return err + (code.Length > 0 ? " [" + code + "]" : "");
                    }
                    return body.Length > 160 ? body.Substring(0, 160) + "…" : body;
                }
            }
            catch { return null; }
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
            int backoff = 5000;
            int badSessions = 0;              // strömmar som stängts snabbt eller aldrig gett något
            DateTime pollUntil = DateTime.MinValue;
            while (_running)
            {
                bool polling = DateTime.UtcNow < pollUntil;
                _polling = polling;
                DateTime started = DateTime.UtcNow;
                int lines = 0;
                try
                {
                    string url = _server + "/" + _topic + "/json?since=" + (_lastId ?? "12h") + (polling ? "&poll=1" : "");
                    HttpWebRequest req = NewRequest(url, "GET");
                    req.ReadWriteTimeout = polling ? 60000 : 120000; // strömmen får keepalive var 45:e sekund
                    _subReq = req;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader r = new StreamReader(resp.GetResponseStream(), Utf8))
                    {
                        if (!polling) Log("prenumeration öppen (" + _topic + ")");
                        SetState(true, null);
                        string line;
                        while (_running && (line = r.ReadLine()) != null)
                        {
                            if (line.Length == 0) continue;
                            lines++;
                            try { HandleLine(line); } catch { }
                        }
                    }
                    if (!_running) break;
                    if (polling)
                    {
                        // Vanlig hämtning: vänta 45 s och fråga igen. Ett anrop per 45 s klarar alla proxyer och gränser.
                        for (int i = 0; i < 45 && _running; i++) Thread.Sleep(1000);
                        continue;
                    }
                    // Strömmen stängdes snällt. Frisk om den levde en stund och gav minst en rad (ntfy skickar "open" direkt).
                    double alive = (DateTime.UtcNow - started).TotalSeconds;
                    if (lines > 0 && alive >= 30) { badSessions = 0; backoff = 5000; }
                    else { badSessions++; Log("strömmen stängdes efter " + (int)alive + " s med " + lines + " rader"); }
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    WebException wex = ex as WebException;
                    HttpWebResponse wr = wex != null ? wex.Response as HttpWebResponse : null;
                    int status = wr != null ? (int)wr.StatusCode : 0;
                    string detail = wr != null ? ServerError(wr) : null;
                    _lastError = (status > 0 ? "HTTP " + status : ex.Message) + (detail != null ? " (" + detail + ")" : "");
                    double alive = (DateTime.UtcNow - started).TotalSeconds;
                    Log((polling ? "hämtning" : "prenumeration") + " bröts efter " + (int)alive + " s: " + _lastError);
                    // En ström som gick i timeout utan en enda rad är en proxy som buffrar – räkna som trasig
                    if (!polling && (lines == 0 || alive < 30)) badSessions++;
                    if (status == 429) backoff = Math.Max(backoff, 120000);
                    SetState(false, null);
                }
                if (!_running) break;

                if (!polling && badSessions >= 3)
                {
                    // Det här nätet håller inte strömmen öppen. Kör vanliga hämtningar i 15 min och prova strömmen igen sedan.
                    pollUntil = DateTime.UtcNow.AddMinutes(15);
                    badSessions = 0;
                    backoff = 5000;
                    Log("går över till hämtning var 45:e sekund i 15 min");
                    SetState(_connected, "Molnbryggan: det här nätet håller inte strömmen öppen – hämtar nya meddelanden var 45:e sekund i stället");
                    continue;
                }
                for (int i = 0; i < backoff / 1000 && _running; i++) Thread.Sleep(1000);
                backoff = Math.Min(Math.Max(backoff * 2, 15000), 300000);
            }
            _polling = false;
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
            DateTime at = DateTime.UtcNow;
            object t;
            if (o.TryGetValue("time", out t))
            {
                try { at = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Convert.ToInt64(t)); } catch { }
            }
            Action<byte[], string, long, DateTime> h = Received;
            if (h != null) h(frame, url, size, at);
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
