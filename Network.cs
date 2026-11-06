using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiniChat
{
    /// <summary>En direkt TCP-förbindelse till en annan MiniChat.</summary>
    public class Peer
    {
        public string Id;
        public string Name;
        public byte[] Avatar;
        public string Status = "online";
        public IPAddress Address;
        public int ListenPort;
        public bool Outbound;
        public string ManualAddress;
        public TcpClient Client;
        public NetworkStream Stream;
        public byte[] EncKey, MacKey;
        public DateTime LastSeen;
        public List<Participant> Roster = new List<Participant>();
        public readonly object SendLock = new object();
        public string EndPoint { get { return Address + ":" + ListenPort; } }
    }

    /// <summary>Någon som är med i chatten – direkt ansluten eller via någon annan.</summary>
    public class Participant
    {
        public string Id;
        public string Name;
        public byte[] Avatar;
        public string Status;
        public bool Direct;
        public bool Cloud;   // nås bara via molnbryggan
    }

    public class ChatMessage
    {
        public string Id;
        public string FromId;
        public string FromName;
        public DateTime Time;
        public string Text;
    }

    /// <summary>En filöverföring. State: offer, waiting, active, done, declined, error.</summary>
    public class FileTransfer
    {
        public string Id;
        public string FromId, FromName;
        public string ToId, ToName;
        public string FileName;
        public string FilePath;
        public long Size;
        public long Done;
        public bool Outgoing;
        public string State;
        public string Error;
        public string Via;          // null = direkt (P2P), "cloud" = via molnbryggan
        public DateTime Started = DateTime.Now;
        internal string CloudUrl;

        internal FileStream Stream;
        internal SemaphoreSlim Window;
        internal TaskCompletionSource<bool> Accepted;
        internal int Acked;
        internal bool DoneRequested, Finalized;
        internal DateTime LastActivity = DateTime.UtcNow;
        internal DateTime LastReport = DateTime.MinValue;
        internal readonly object IoLock = new object();

        public int Percent { get { return Size <= 0 ? 100 : (int)(Done * 100 / Size); } }
    }

    /// <summary>
    /// Serverlös peer-to-peer-chatt.
    ///  - UDP-broadcast på LAN för att hitta varandra (port 48556)
    ///  - Direkta TCP-förbindelser (standardport 48555), manuell adress för andra nät
    ///  - Varje förbindelse krypteras: ECDH (P-256) ger en gemensam nyckel, trafiken
    ///    går som AES-256-CBC + HMAC-SHA256. Ingen kod behöver delas.
    ///  - Alla berättar för varandra vem de känner (roster) och vidarebefordrar
    ///    meddelanden och filer, så två som inte når varandra direkt når varandra via en tredje.
    /// </summary>
    public class Network : IDisposable
    {
        public const int DiscoveryPort = 48556;
        public const int DefaultTcpPort = 48555;
        const string Magic = "MC2";
        const int MaxFrame = 4 * 1024 * 1024;
        const int ChunkSize = 64 * 1024;
        const int WindowSize = 8;
        const int MaxAvatar = 96 * 1024;
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        public string SelfId { get; private set; }
        public string SelfName { get; private set; }
        public byte[] SelfAvatar { get; private set; }
        public string SelfStatus { get; private set; }
        public int ListenPort { get; private set; }
        public bool IsRunning { get { return _running; } }
        public bool AutoAccept = true;
        public string DownloadFolder;

        public event Action<Peer> PeerJoined;
        public event Action<Peer> PeerLeft;
        public event Action RosterChanged;
        public event Action<ChatMessage> MessageReceived;
        public event Action<FileTransfer> TransferUpdated;
        public event Action<string, string> SoundReceived;          // (avsändarens namn, ljudets namn)
        public event Action<string, string, string, byte[]> SoundDataReceived; // (namn, ljudets namn, filändelse, ljuddata)
        public event Action<string, string, bool> TypingChanged;      // (avsändarens id, namn, skriver?)
        public event Action<string, string> NudgeReceived;           // (avsändarens id, namn)
        public event Action<string, string, string> SeenReceived;     // (avsändarens id, namn, meddelande-id)
        public event Action<string, string, string, string, bool> ReactionReceived; // (id, namn, meddelande-id, emoji, på?)
        public event Action<string> Log;
        public event Action<bool, string> CloudStateChanged;        // (ansluten?, meddelande eller null)

        class CloudPeer { public string Id, Name, Status; public byte[] Avatar; public DateTime LastSeen; }
        const int CloudFileLimit = 5 * 1024 * 1024;
        CloudBridge _cloud;
        readonly Dictionary<string, CloudPeer> _cloudPeers = new Dictionary<string, CloudPeer>();
        DateTime _lastCloudHello = DateTime.MinValue, _lastCloudAvatar = DateTime.MinValue;
        string _cloudSeenPending;

        public bool CloudEnabled { get { return _cloud != null; } }
        public bool CloudConnected { get { return _cloud != null && _cloud.Connected; } }

        readonly object _lock = new object();
        readonly Dictionary<string, Peer> _peers = new Dictionary<string, Peer>();
        readonly HashSet<string> _connecting = new HashSet<string>();
        readonly HashSet<string> _seenIds = new HashSet<string>();
        readonly Queue<string> _seenQueue = new Queue<string>();
        readonly List<string> _manual = new List<string>();
        readonly Dictionary<string, FileTransfer> _transfers = new Dictionary<string, FileTransfer>();

        TcpListener _listener;
        UdpClient _udp;
        System.Threading.Timer _timer;
        int _tick, _inTimer;
        bool _rosterPending;
        volatile bool _running;
        List<IPAddress> _broadcastCache;
        DateTime _broadcastCacheTime;

        public List<Peer> Peers
        {
            get { lock (_lock) return _peers.Values.ToList(); }
        }

        public List<string> ManualAddresses
        {
            get { lock (_lock) return new List<string>(_manual); }
        }

        /// <summary>Alla i chatten utom jag själv: direkta kompisar plus de som kompisarna känner.</summary>
        public List<Participant> Participants
        {
            get
            {
                Dictionary<string, Participant> d = new Dictionary<string, Participant>();
                lock (_lock)
                {
                    foreach (Peer p in _peers.Values)
                        d[p.Id] = new Participant { Id = p.Id, Name = p.Name, Avatar = p.Avatar, Status = p.Status, Direct = true };
                    foreach (Peer p in _peers.Values)
                        foreach (Participant r in p.Roster)
                            if (r.Id != SelfId && !d.ContainsKey(r.Id))
                                d[r.Id] = new Participant { Id = r.Id, Name = r.Name, Avatar = r.Avatar, Status = r.Status, Direct = false };
                    DateTime cutoff = DateTime.UtcNow.AddMinutes(-25);
                    foreach (CloudPeer cp in _cloudPeers.Values)
                        if (cp.Id != SelfId && cp.LastSeen > cutoff && !d.ContainsKey(cp.Id))
                            d[cp.Id] = new Participant { Id = cp.Id, Name = cp.Name ?? "Okänd", Avatar = cp.Avatar, Status = cp.Status ?? "online", Direct = false, Cloud = true };
                }
                return d.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        }

        public FileTransfer GetTransfer(string id)
        {
            lock (_lock)
            {
                FileTransfer t;
                return _transfers.TryGetValue(id, out t) ? t : null;
            }
        }

        // ================================================================ start/stopp

        public void Start(string id, string name, string status, byte[] avatar, int preferredPort, IEnumerable<string> manualAddresses)
        {
            if (_running) return;
            SelfId = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
            SelfName = CleanName(name);
            SelfStatus = CleanStatus(status);
            SelfAvatar = avatar;
            lock (_lock)
            {
                _manual.Clear();
                foreach (string m in manualAddresses)
                    if (!string.IsNullOrEmpty(m) && !_manual.Contains(m)) _manual.Add(m);
            }

            _listener = BindListener(preferredPort);
            ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;

            try
            {
                _udp = new UdpClient();
                _udp.ExclusiveAddressUse = false;
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _udp.EnableBroadcast = true;
                Task ignored = UdpLoop();
            }
            catch (Exception ex)
            {
                _udp = null;
                Emit("Kunde inte starta upptäckt på lokala nätet: " + ex.Message);
            }

            Task ignored2 = AcceptLoop();
            _timer = new System.Threading.Timer(OnTimer, null, 300, 3000);
        }

        public void Stop()
        {
            if (!_running) return;
            DisableCloud();
            _running = false;
            try { if (_timer != null) _timer.Dispose(); } catch { }
            foreach (Peer p in Peers) SendFrame(p, Frame("BYE", null));
            try { _listener.Stop(); } catch { }
            try { if (_udp != null) _udp.Close(); } catch { }
            foreach (Peer p in Peers)
            {
                try { p.Client.Close(); } catch { }
            }
            lock (_lock)
            {
                _peers.Clear();
                _connecting.Clear();
                foreach (FileTransfer t in _transfers.Values)
                {
                    try { if (t.Stream != null) t.Stream.Close(); } catch { }
                }
            }
        }

        public void Dispose() { Stop(); }

        static TcpListener BindListener(int preferred)
        {
            int[] tries = preferred > 0 ? new[] { preferred, 0 } : new[] { 0 };
            Exception last = null;
            foreach (int p in tries)
            {
                try
                {
                    TcpListener l = new TcpListener(IPAddress.Any, p);
                    l.Start();
                    return l;
                }
                catch (SocketException ex) { last = ex; }
            }
            throw last;
        }

        // ================================================================ publikt: skicka, namn, bild, adresser

        public ChatMessage Send(string text)
        {
            ChatMessage m = new ChatMessage
            {
                Id = NewId(),
                FromId = SelfId,
                FromName = SelfName,
                Time = DateTime.Now,
                Text = text
            };
            MarkSeen(m.Id);
            byte[] frame = Frame("MSG|" + m.Id + "|" + SelfId + "|" + SelfName + "|" + DateTime.UtcNow.Ticks, Utf8.GetBytes(text));
            Task ignored = Task.Run(() => BroadcastAll(frame));
            return m;
        }

        /// <summary>Skickar ett ljud (t.ex. "prutt") som spelas upp hos alla andra utan att synas i chatten.</summary>
        public void SendSound(string sound)
        {
            string id = NewId();
            MarkSeen(id);
            byte[] frame = Frame("SOUND|" + id + "|" + SelfId + "|" + SelfName, Utf8.GetBytes(sound));
            Task ignored = Task.Run(() => BroadcastAll(frame));
        }

        /// <summary>Eget ljud (datat följer med så att mottagaren inte behöver ha filen). ext t.ex. ".mp3".</summary>
        public void SendSoundData(string sound, string ext, byte[] data)
        {
            string id = NewId();
            MarkSeen(id);
            byte[] frame = Frame("SOUNDX|" + id + "|" + SelfId + "|" + SelfName + "|" + CleanName(sound) + "|" + CleanName(ext), data);
            Task ignored = Task.Run(() => BroadcastAll(frame));
        }

        public void SetStatus(string status)
        {
            status = CleanStatus(status);
            if (status == SelfStatus) return;
            SelfStatus = status;
            byte[] frame = Frame("STATUS|" + status, null);
            Task ignored = Task.Run(() => Broadcast(frame, null));
            CloudHello(false);
        }

        public void SendTyping(bool on)
        {
            string id = NewId();
            MarkSeen(id);
            byte[] frame = Frame("TYPING|" + id + "|" + SelfId + "|" + SelfName + "|" + (on ? "1" : "0"), null);
            Task ignored = Task.Run(() => Broadcast(frame, null));
        }

        public void SendNudge()
        {
            string id = NewId();
            MarkSeen(id);
            byte[] frame = Frame("NUDGE|" + id + "|" + SelfId + "|" + SelfName, null);
            Task ignored = Task.Run(() => BroadcastAll(frame));
        }

        public void SendSeen(string msgId)
        {
            string id = NewId();
            MarkSeen(id);
            byte[] frame = Frame("SEEN|" + id + "|" + SelfId + "|" + SelfName + "|" + msgId, null);
            Task ignored = Task.Run(() => Broadcast(frame, null));
            if (_cloud != null) lock (_lock) _cloudSeenPending = msgId; // molnet: samlat, högst en gång per minut
        }

        public void SendReaction(string msgId, string emoji, bool on)
        {
            string id = NewId();
            MarkSeen(id);
            byte[] frame = Frame("REACT|" + id + "|" + SelfId + "|" + SelfName + "|" + msgId + "|" + (on ? "1" : "0"), Utf8.GetBytes(emoji));
            Task ignored = Task.Run(() => BroadcastAll(frame));
        }

        public void SetName(string name)
        {
            SelfName = CleanName(name);
            byte[] frame = Frame("NAME|" + SelfName, null);
            Task ignored = Task.Run(() => Broadcast(frame, null));
            CloudHello(true);
        }

        public void SetAvatar(byte[] avatar)
        {
            SelfAvatar = avatar;
            byte[] frame = Frame("AVATAR", avatar);
            Task ignored = Task.Run(() => Broadcast(frame, null));
            _lastCloudAvatar = DateTime.MinValue;
            CloudAvatar();
        }

        // ================================================================ molnbryggan

        /// <summary>Slår på reservvägen via ntfy. Koden ger ämne och nyckel; servern ser bara krypterade blobbar.</summary>
        public void EnableCloud(string familyCode, string server)
        {
            DisableCloud();
            string code = CloudBridge.NormalizeCode(familyCode);
            if (code.Length == 0) return;
            CloudBridge c = new CloudBridge(server, code);
            c.StateChanged += (ok, msg) =>
            {
                if (ok) { CloudHello(true); CloudAvatar(); }
                Action<bool, string> ev = CloudStateChanged;
                if (ev != null) ev(ok, msg);
            };
            c.Received += OnCloudEvent;
            _cloud = c;
            c.Start();
        }

        public void DisableCloud()
        {
            CloudBridge c = _cloud;
            if (c == null) return;
            _cloud = null;
            try { c.TrySendNow(Frame("CBYE|" + SelfId, null)); } catch { }
            c.Stop();
            lock (_lock) _cloudPeers.Clear();
            RaiseRoster();
        }

        /// <summary>Molnet används bara när det behövs: ingen direkt kompis alls, eller någon som bara nås via molnet.</summary>
        bool ShouldCloudSend()
        {
            if (_cloud == null) return false;
            List<Participant> ps = Participants;
            return ps.Count == 0 || ps.Any(x => x.Cloud);
        }

        void BroadcastAll(byte[] frame)
        {
            Broadcast(frame, null);
            CloudBridge c = _cloud;
            if (c != null && ShouldCloudSend()) c.Publish(frame);
        }

        /// <summary>Vidarebefordrar en mottagen ram: till alla direkta kompisar utom avsändaren, och vid behov till molnet.</summary>
        void Relay(byte[] plain, Peer from, bool cloudToo)
        {
            Broadcast(plain, from);
            CloudBridge c = _cloud;
            if (cloudToo && from != null && c != null && Participants.Any(x => x.Cloud)) c.Publish(plain);
        }

        void CloudHello(bool force)
        {
            CloudBridge c = _cloud;
            if (c == null) return;
            lock (_lock)
            {
                if (!force && (DateTime.UtcNow - _lastCloudHello).TotalSeconds < 60) return;
                _lastCloudHello = DateTime.UtcNow;
            }
            c.Publish(Frame("CHELLO|" + SelfId + "|" + SelfName + "|" + SelfStatus, null));
        }

        void CloudAvatar()
        {
            CloudBridge c = _cloud;
            if (c == null) return;
            lock (_lock)
            {
                if ((DateTime.UtcNow - _lastCloudAvatar).TotalMinutes < 30) return;
                _lastCloudAvatar = DateTime.UtcNow;
            }
            byte[] header = Frame("CAVATAR|" + SelfId + "|" + SelfName, null);
            byte[] av = SelfAvatar;
            if (av == null) c.Publish(header);
            else
            {
                Task ignored = Task.Run(() => { try { c.PutAttachment(header, av, null); } catch { } });
            }
        }

        /// <summary>Uppdaterar vad vi vet om en molndeltagare. Returnerar true om den var ny eller länge tyst.</summary>
        bool TouchCloudPeer(string id, string name, string status)
        {
            if (id == SelfId) return false;
            bool fresh;
            lock (_lock)
            {
                CloudPeer cp;
                bool known = _cloudPeers.TryGetValue(id, out cp);
                fresh = !known || (DateTime.UtcNow - cp.LastSeen).TotalMinutes > 10;
                if (!known)
                {
                    cp = new CloudPeer { Id = id, Avatar = LoadCachedAvatar(id) };
                    _cloudPeers[id] = cp;
                }
                if (name != null) cp.Name = name;
                if (status != null) cp.Status = status;
                cp.LastSeen = DateTime.UtcNow;
            }
            RaiseRoster();
            return fresh;
        }

        void OnCloudEvent(byte[] frame, string url, long size)
        {
            CloudBridge c = _cloud;
            if (c == null || !_running) return;
            try
            {
                int nl = Array.IndexOf(frame, (byte)10);
                string header = Utf8.GetString(frame, 0, nl < 0 ? frame.Length : nl);
                string[] p = header.Split('|');
                switch (p[0])
                {
                    case "CHELLO":
                        if (p.Length < 4) return;
                        if (TouchCloudPeer(p[1], CleanName(p[2]), CleanStatus(p[3]))) CloudHello(false);
                        return;
                    case "CBYE":
                        if (p.Length < 2 || p[1] == SelfId) return;
                        lock (_lock) _cloudPeers.Remove(p[1]);
                        RaiseRoster();
                        return;
                    case "CAVATAR":
                        {
                            if (p.Length < 3 || p[1] == SelfId) return;
                            string id = p[1], name = CleanName(p[2]);
                            if (url == null) { SetCloudAvatar(id, name, null); return; }
                            Task ignored = Task.Run(() =>
                            {
                                byte[] av = null;
                                try { av = c.Download(url, size, null); } catch { }
                                if (av != null && av.Length > MaxAvatar) av = null;
                                SetCloudAvatar(id, name, av);
                            });
                            return;
                        }
                    case "CBLOB":
                        {
                            if (url == null) return;
                            Task ignored = Task.Run(() =>
                            {
                                try { HandleFrame(null, c.Download(url, size, null)); } catch { }
                            });
                            return;
                        }
                    case "CFILE":
                        HandleCloudFile(p, frame, nl, url);
                        return;
                    default:
                        HandleFrame(null, frame);
                        return;
                }
            }
            catch { }
        }

        void SetCloudAvatar(string id, string name, byte[] avatar)
        {
            TouchCloudPeer(id, name, null);
            lock (_lock)
            {
                CloudPeer cp;
                if (_cloudPeers.TryGetValue(id, out cp)) cp.Avatar = avatar;
            }
            SaveCachedAvatar(id, avatar);
            RaiseRoster();
        }

        void HandleCloudFile(string[] p, byte[] frame, int nl, string url)
        {
            // CFILE|tid|from|fromName|storlek ; kropp = filnamn ; bilagan = filen (krypterad)
            long fsize;
            if (p.Length < 5 || url == null || !long.TryParse(p[4], out fsize) || fsize < 0) return;
            if (p[2] == SelfId || GetTransfer(p[1]) != null) return;
            string name = nl < 0 ? "fil" : Utf8.GetString(frame, nl + 1, frame.Length - nl - 1);
            FileTransfer t = new FileTransfer
            {
                Id = p[1], FromId = p[2], FromName = CleanName(p[3]), ToId = SelfId, ToName = SelfName,
                FileName = CleanFileName(name), Size = fsize, Outgoing = false, State = "offer", Via = "cloud", CloudUrl = url
            };
            lock (_lock) _transfers[t.Id] = t;
            TouchCloudPeer(p[2], t.FromName, null);
            Report(t, true);
            if (AutoAccept) AcceptFile(t.Id);
        }

        async Task RunCloudOutgoing(FileTransfer t)
        {
            CloudBridge c = _cloud;
            try
            {
                Report(t, true);
                if (c == null) { Fail(t, "Molnbryggan är av.", false); return; }
                if (t.Size > CloudFileLimit) { Fail(t, "För stor för molnbryggan (max 5 MB) – skicka när ni är på samma nät.", false); return; }
                byte[] data = File.ReadAllBytes(t.FilePath);
                t.State = "active";
                t.LastActivity = DateTime.UtcNow;
                Report(t, true);
                byte[] header = Frame("CFILE|" + t.Id + "|" + SelfId + "|" + SelfName + "|" + t.Size, Utf8.GetBytes(t.FileName));
                bool ok = await Task.Run(() => c.PutAttachment(header, data, delegate(long done, long total)
                {
                    t.Done = Math.Min(t.Size, (long)(t.Size * (double)done / Math.Max(1, total)));
                    t.LastActivity = DateTime.UtcNow;
                    Report(t, false);
                }));
                if (!ok) { Fail(t, "Molnbryggan tog inte emot filen.", false); return; }
                t.Done = t.Size;
                t.State = "done";
                Report(t, true);
            }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                Fail(t, r != null && (int)r.StatusCode == 413 ? "För stor för molnbryggan." : "Molnbryggan: " + ex.Message, false);
            }
            catch (Exception ex)
            {
                Fail(t, "Fel: " + ex.Message, false);
            }
        }

        async Task RunCloudIncoming(FileTransfer t)
        {
            CloudBridge c = _cloud;
            try
            {
                t.State = "active";
                t.LastActivity = DateTime.UtcNow;
                Report(t, true);
                if (c == null) throw new IOException("molnbryggan är av");
                byte[] data = await Task.Run(() => c.Download(t.CloudUrl, t.Size, delegate(long done, long total)
                {
                    t.Done = Math.Min(t.Size, (long)(t.Size * (double)done / Math.Max(1, total)));
                    t.LastActivity = DateTime.UtcNow;
                    Report(t, false);
                }));
                string dir = DownloadFolder;
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "MiniChat");
                Directory.CreateDirectory(dir);
                string final = UniquePath(dir, t.FileName);
                File.WriteAllBytes(final, data);
                t.FilePath = final;
                t.Done = t.Size;
                t.State = "done";
                t.Finalized = true;
                Report(t, true);
            }
            catch (Exception ex)
            {
                Fail(t, "Kunde inte hämta filen: " + ex.Message, false);
            }
        }

        static string AvatarCacheDir { get { return Path.Combine(Settings.Folder, "avatars"); } }

        static byte[] LoadCachedAvatar(string id)
        {
            try
            {
                string f = Path.Combine(AvatarCacheDir, SafeId(id) + ".jpg");
                return File.Exists(f) ? File.ReadAllBytes(f) : null;
            }
            catch { return null; }
        }

        static void SaveCachedAvatar(string id, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(AvatarCacheDir);
                string f = Path.Combine(AvatarCacheDir, SafeId(id) + ".jpg");
                if (data == null) { if (File.Exists(f)) File.Delete(f); }
                else File.WriteAllBytes(f, data);
            }
            catch { }
        }

        static string SafeId(string id)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in id) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.Length > 0 ? sb.ToString() : "x";
        }

        public void AddManual(string hostPort)
        {
            hostPort = (hostPort ?? "").Trim();
            if (hostPort.Length == 0) return;
            lock (_lock) if (!_manual.Contains(hostPort)) _manual.Add(hostPort);
            TryManual(hostPort, true);
        }

        public void ClearManual()
        {
            lock (_lock) _manual.Clear();
        }

        // ================================================================ filer

        /// <summary>Skickar filen till alla som är online just nu – en överföring per mottagare.</summary>
        public List<FileTransfer> SendFile(string path)
        {
            FileInfo fi = new FileInfo(path);
            List<FileTransfer> list = new List<FileTransfer>();
            foreach (Participant pt in Participants)
            {
                FileTransfer t = new FileTransfer
                {
                    Id = NewId(),
                    FromId = SelfId,
                    FromName = SelfName,
                    ToId = pt.Id,
                    ToName = pt.Name,
                    FileName = fi.Name,
                    FilePath = fi.FullName,
                    Size = fi.Length,
                    Outgoing = true,
                    State = "waiting",
                    Window = new SemaphoreSlim(WindowSize, WindowSize),
                    Accepted = new TaskCompletionSource<bool>()
                };
                lock (_lock) _transfers[t.Id] = t;
                list.Add(t);
                Task ignored = RunOutgoing(t);
            }
            if (_cloud != null)
            {
                List<Participant> cloudOnly = Participants.Where(x => x.Cloud).ToList();
                if (cloudOnly.Count > 0)
                {
                    FileTransfer ct = new FileTransfer
                    {
                        Id = NewId(), FromId = SelfId, FromName = SelfName, ToId = "cloud",
                        ToName = string.Join(", ", cloudOnly.Select(x => x.Name).ToArray()),
                        FileName = fi.Name, FilePath = fi.FullName, Size = fi.Length, Outgoing = true, State = "waiting", Via = "cloud"
                    };
                    lock (_lock) _transfers[ct.Id] = ct;
                    list.Add(ct);
                    Task ignored2 = RunCloudOutgoing(ct);
                }
            }
            return list;
        }

        public void AcceptFile(string id)
        {
            FileTransfer t = GetTransfer(id);
            if (t == null || t.Outgoing || t.State != "offer") return;
            if (t.Via == "cloud") { Task ignoredCloud = RunCloudIncoming(t); return; }
            try
            {
                string dir = DownloadFolder;
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "MiniChat");
                Directory.CreateDirectory(dir);
                string final = UniquePath(dir, t.FileName);
                t.FilePath = final + ".part";
                t.Stream = new FileStream(t.FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                if (t.Size > 0) t.Stream.SetLength(t.Size);
                t.State = "active";
                t.LastActivity = DateTime.UtcNow;
                Report(t, true);
                byte[] frame = Frame("FACCEPT|" + t.Id + "|" + SelfId + "|" + t.FromId, null);
                Task ignored = Task.Run(() => Route(t.FromId, frame, null));
                if (t.Size == 0 || t.DoneRequested) FinalizeIncoming(t);
            }
            catch (Exception ex)
            {
                Fail(t, "Kunde inte spara filen: " + ex.Message, true);
            }
        }

        public void DeclineFile(string id)
        {
            FileTransfer t = GetTransfer(id);
            if (t == null || t.Outgoing || t.State != "offer") return;
            t.State = "declined";
            Report(t, true);
            byte[] frame = Frame("FDECLINE|" + t.Id + "|" + SelfId + "|" + t.FromId, null);
            Task ignored = Task.Run(() => Route(t.FromId, frame, null));
        }

        async Task RunOutgoing(FileTransfer t)
        {
            try
            {
                Report(t, true);
                Route(t.ToId, Frame("FOFFER|" + t.Id + "|" + SelfId + "|" + SelfName + "|" + t.ToId + "|" + t.Size, Utf8.GetBytes(t.FileName)), null);
                Task done = await Task.WhenAny(t.Accepted.Task, Task.Delay(90000));
                if (done != t.Accepted.Task) { Fail(t, "Mottagaren svarade inte.", false); return; }
                if (!t.Accepted.Task.Result) { t.State = "declined"; Report(t, true); return; }

                t.State = "active";
                t.LastActivity = DateTime.UtcNow;
                Report(t, true);
                int total = (int)((t.Size + ChunkSize - 1) / ChunkSize);
                using (FileStream fs = new FileStream(t.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
                {
                    byte[] buf = new byte[ChunkSize];
                    for (int i = 0; i < total; i++)
                    {
                        if (t.State != "active") return;
                        if (!await t.Window.WaitAsync(45000)) { Fail(t, "Överföringen avbröts – ingen kontakt.", true); return; }
                        int n = await fs.ReadAsync(buf, 0, ChunkSize);
                        if (n <= 0) break;
                        byte[] body = new byte[n];
                        Buffer.BlockCopy(buf, 0, body, 0, n);
                        Route(t.ToId, Frame("FCHUNK|" + t.Id + "|" + SelfId + "|" + t.ToId + "|" + i, body), null);
                        t.Done += n;
                        Report(t, false);
                    }
                }
                while (t.Acked < total && t.State == "active")
                {
                    if ((DateTime.UtcNow - t.LastActivity).TotalSeconds > 45) { Fail(t, "Mottagaren svarar inte.", true); return; }
                    await Task.Delay(50);
                }
                if (t.State != "active") return;
                Route(t.ToId, Frame("FDONE|" + t.Id + "|" + SelfId + "|" + t.ToId, null), null);
                t.State = "done";
                Report(t, true);
            }
            catch (Exception ex)
            {
                Fail(t, "Fel: " + ex.Message, true);
            }
        }

        void HandleFileFrame(Peer from, string[] p, byte[] body, byte[] plain)
        {
            string type = p[0];
            if (p.Length < 4) return;
            string tid = p[1];
            string fromId = p[2];
            string to = type == "FOFFER" ? (p.Length > 4 ? p[4] : "") : p[3];

            // Dubbletter (samma ram via två vägar) kastas; ramar till någon annan skickas vidare.
            string key = type + ":" + tid + ":" + p[p.Length - 1];
            if (!MarkSeen(key)) return;
            if (to != SelfId)
            {
                Route(to, plain, from);
                return;
            }

            FileTransfer t = GetTransfer(tid);
            switch (type)
            {
                case "FOFFER":
                    {
                        long size;
                        if (p.Length < 6 || !long.TryParse(p[5], out size) || size < 0) return;
                        if (t != null) return;
                        t = new FileTransfer
                        {
                            Id = tid,
                            FromId = fromId,
                            FromName = CleanName(p[3]),
                            ToId = SelfId,
                            ToName = SelfName,
                            FileName = CleanFileName(Utf8.GetString(body)),
                            Size = size,
                            Outgoing = false,
                            State = "offer"
                        };
                        lock (_lock) _transfers[tid] = t;
                        Report(t, true);
                        if (AutoAccept) AcceptFile(tid);
                        break;
                    }
                case "FACCEPT":
                    if (t != null && t.Outgoing) t.Accepted.TrySetResult(true);
                    break;
                case "FDECLINE":
                    if (t != null && t.Outgoing) t.Accepted.TrySetResult(false);
                    break;
                case "FCHUNK":
                    {
                        int index;
                        if (t == null || t.Outgoing || t.State != "active" || p.Length < 5 || !int.TryParse(p[4], out index)) return;
                        try
                        {
                            lock (t.IoLock)
                            {
                                if (t.Finalized) return;
                                t.Stream.Seek((long)index * ChunkSize, SeekOrigin.Begin);
                                t.Stream.Write(body, 0, body.Length);
                            }
                            t.Done += body.Length;
                            t.LastActivity = DateTime.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            Fail(t, "Kunde inte skriva filen: " + ex.Message, true);
                            return;
                        }
                        Route(fromId, Frame("FACK|" + tid + "|" + SelfId + "|" + fromId + "|" + index, null), null);
                        Report(t, false);
                        if (t.Done >= t.Size && t.DoneRequested) FinalizeIncoming(t);
                        break;
                    }
                case "FACK":
                    if (t != null && t.Outgoing)
                    {
                        t.LastActivity = DateTime.UtcNow;
                        Interlocked.Increment(ref t.Acked);
                        try { t.Window.Release(); } catch { }
                    }
                    break;
                case "FDONE":
                    if (t != null && !t.Outgoing)
                    {
                        t.DoneRequested = true;
                        if (t.State == "active" && t.Done >= t.Size) FinalizeIncoming(t);
                    }
                    break;
                case "FCANCEL":
                    if (t != null && (t.State == "active" || t.State == "waiting" || t.State == "offer"))
                        Fail(t, body.Length > 0 ? Utf8.GetString(body) : "Avbruten av andra sidan.", false);
                    break;
            }
        }

        void FinalizeIncoming(FileTransfer t)
        {
            lock (t.IoLock)
            {
                if (t.Finalized) return;
                t.Finalized = true;
                try { if (t.Stream != null) t.Stream.Close(); } catch { }
            }
            try
            {
                string final = t.FilePath.EndsWith(".part") ? t.FilePath.Substring(0, t.FilePath.Length - 5) : t.FilePath;
                if (File.Exists(final)) final = UniquePath(Path.GetDirectoryName(final), Path.GetFileName(final));
                File.Move(t.FilePath, final);
                t.FilePath = final;
            }
            catch (Exception ex)
            {
                t.State = "error";
                t.Error = "Kunde inte spara: " + ex.Message;
                Report(t, true);
                return;
            }
            t.State = "done";
            Report(t, true);
        }

        void Fail(FileTransfer t, string reason, bool tellOther)
        {
            if (t.State == "done" || t.State == "error" || t.State == "declined") return;
            t.State = "error";
            t.Error = reason;
            lock (t.IoLock)
            {
                t.Finalized = true;
                try { if (t.Stream != null) t.Stream.Close(); } catch { }
            }
            if (!t.Outgoing && t.FilePath != null && t.FilePath.EndsWith(".part"))
            {
                try { File.Delete(t.FilePath); } catch { }
            }
            if (t.Accepted != null) t.Accepted.TrySetResult(false);
            if (tellOther)
            {
                string other = t.Outgoing ? t.ToId : t.FromId;
                byte[] frame = Frame("FCANCEL|" + t.Id + "|" + SelfId + "|" + other, Utf8.GetBytes(reason));
                Task ignored = Task.Run(() => Route(other, frame, null));
            }
            Report(t, true);
        }

        void Report(FileTransfer t, bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && (now - t.LastReport).TotalMilliseconds < 120) return;
            t.LastReport = now;
            Action<FileTransfer> ev = TransferUpdated;
            if (ev != null && _running) ev(t);
        }

        static string UniquePath(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path) && !File.Exists(path + ".part")) return path;
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; i < 10000; i++)
            {
                path = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(path) && !File.Exists(path + ".part")) return path;
            }
            return Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
        }

        static string CleanFileName(string name)
        {
            name = (name ?? "").Trim();
            int i = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (i >= 0) name = name.Substring(i + 1);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.TrimEnd('.', ' ');
            if (name.Length == 0 || name == ".." ) name = "fil";
            if (name.Length > 150) name = name.Substring(0, 150);
            return name;
        }

        // ================================================================ timer: hello, återanslutning, ping, timeouts

        void OnTimer(object state)
        {
            if (!_running) return;
            if (Interlocked.CompareExchange(ref _inTimer, 1, 0) != 0) return;
            try
            {
                _tick++;
                SendHello();
                if (_tick % 3 == 1)
                    foreach (string m in ManualAddresses) TryManual(m, false);
                if (_tick % 5 == 0)
                {
                    DateTime now = DateTime.UtcNow;
                    foreach (Peer p in Peers)
                    {
                        if ((now - p.LastSeen).TotalSeconds > 60) { try { p.Client.Close(); } catch { } }
                        else SendFrame(p, Frame("PING", null));
                    }
                }
                List<FileTransfer> stale;
                lock (_lock)
                    stale = _transfers.Values.Where(t => !t.Outgoing && t.State == "active" && t.Via == null && (DateTime.UtcNow - t.LastActivity).TotalSeconds > 60).ToList();
                foreach (FileTransfer t in stale) Fail(t, "Överföringen avbröts – ingen kontakt.", true);

                CloudBridge cloud = _cloud;
                if (cloud != null)
                {
                    if (_tick % 400 == 0) CloudHello(true); // puls var 20:e minut
                    if (_tick % 20 == 0)
                    {
                        string seenId;
                        lock (_lock) { seenId = _cloudSeenPending; _cloudSeenPending = null; }
                        if (seenId != null && ShouldCloudSend())
                        {
                            string id = NewId();
                            MarkSeen(id);
                            cloud.Publish(Frame("SEEN|" + id + "|" + SelfId + "|" + SelfName + "|" + seenId, null));
                        }
                        bool removed = false;
                        lock (_lock)
                        {
                            DateTime cutoff = DateTime.UtcNow.AddMinutes(-25);
                            foreach (string k in _cloudPeers.Keys.ToList())
                                if (_cloudPeers[k].LastSeen < cutoff) { _cloudPeers.Remove(k); removed = true; }
                        }
                        if (removed) RaiseRoster();
                    }
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _inTimer, 0); }
        }

        // ================================================================ UDP-upptäckt

        void SendHello()
        {
            if (_udp == null) return;
            byte[] data = Utf8.GetBytes(Magic + "|HELLO|" + SelfId + "|" + SelfName + "|" + ListenPort);
            foreach (IPAddress bc in BroadcastAddresses())
            {
                try { _udp.Send(data, data.Length, new IPEndPoint(bc, DiscoveryPort)); } catch { }
            }
        }

        List<IPAddress> BroadcastAddresses()
        {
            if (_broadcastCache != null && (DateTime.UtcNow - _broadcastCacheTime).TotalSeconds < 30) return _broadcastCache;
            List<IPAddress> list = new List<IPAddress>();
            list.Add(IPAddress.Broadcast);
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] a = ua.Address.GetAddressBytes();
                        byte[] m = ua.IPv4Mask.GetAddressBytes();
                        byte[] b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | (byte)(~m[i]));
                        IPAddress bc = new IPAddress(b);
                        if (!list.Contains(bc)) list.Add(bc);
                    }
                }
            }
            catch { }
            _broadcastCache = list;
            _broadcastCacheTime = DateTime.UtcNow;
            return list;
        }

        public static List<string> LocalAddresses()
        {
            List<string> list = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address.ToString());
                }
            }
            catch { }
            return list;
        }

        async Task UdpLoop()
        {
            while (_running)
            {
                UdpReceiveResult r;
                bool failed = false;
                try { r = await _udp.ReceiveAsync(); }
                catch { failed = true; r = default(UdpReceiveResult); }
                if (failed)
                {
                    if (!_running) return;
                    await Task.Delay(500);
                    continue;
                }

                string text;
                try { text = Utf8.GetString(r.Buffer); } catch { continue; }
                string[] p = text.Split('|');
                if (p.Length < 5 || p[0] != Magic) continue;
                if (p[1] != "HELLO" && p[1] != "HELLO-REPLY") continue;
                string id = p[2];
                int port;
                if (id == SelfId || !int.TryParse(p[4], out port)) continue;

                if (p[1] == "HELLO")
                {
                    // Svara direkt till avsändaren så att den hittar oss även om dess
                    // brandvägg stoppar inkommande broadcast (svar på egen trafik släpps igenom).
                    byte[] data = Utf8.GetBytes(Magic + "|HELLO-REPLY|" + SelfId + "|" + SelfName + "|" + ListenPort);
                    try { _udp.Send(data, data.Length, r.RemoteEndPoint); } catch { }
                }
                ConsiderPeer(id, r.RemoteEndPoint.Address, port);
            }
        }

        void ConsiderPeer(string id, IPAddress addr, int port)
        {
            string key = addr + ":" + port;
            lock (_lock)
            {
                if (_peers.ContainsKey(id)) return;
                if (_connecting.Contains(key)) return;
                _connecting.Add(key);
            }
            // Den med lägst id ringer först; den andra väntar lite och ringer bara om det behövs.
            int delay = string.CompareOrdinal(SelfId, id) < 0 ? 0 : 2500;
            Task ignored = Task.Run(async () =>
            {
                try
                {
                    if (delay > 0)
                    {
                        await Task.Delay(delay);
                        bool already;
                        lock (_lock) already = _peers.ContainsKey(id);
                        if (already) return;
                    }
                    await ConnectAsync(addr, port, null, false);
                }
                finally
                {
                    lock (_lock) _connecting.Remove(key);
                }
            });
        }

        void ConsiderEndpoint(IPAddress addr, int port)
        {
            string key = addr + ":" + port;
            lock (_lock)
            {
                if (_peers.Values.Any(x => x.Address.Equals(addr) && x.ListenPort == port)) return;
                if (_connecting.Contains(key)) return;
                _connecting.Add(key);
            }
            Task ignored = Task.Run(async () =>
            {
                try { await ConnectAsync(addr, port, null, false); }
                finally { lock (_lock) _connecting.Remove(key); }
            });
        }

        void TryManual(string hostPort, bool verbose)
        {
            string key = "manual:" + hostPort;
            lock (_lock)
            {
                if (_peers.Values.Any(x => x.ManualAddress == hostPort)) return;
                if (_connecting.Contains(key)) return;
                _connecting.Add(key);
            }
            Task ignored = Task.Run(async () =>
            {
                try
                {
                    string host = hostPort;
                    int port = DefaultTcpPort;
                    int i = hostPort.LastIndexOf(':');
                    if (i > 0)
                    {
                        host = hostPort.Substring(0, i);
                        int pp;
                        if (int.TryParse(hostPort.Substring(i + 1), out pp) && pp > 0 && pp < 65536) port = pp;
                    }
                    IPAddress addr;
                    if (!IPAddress.TryParse(host, out addr))
                    {
                        IPAddress[] all = await Dns.GetHostAddressesAsync(host);
                        addr = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                        if (addr == null)
                        {
                            if (verbose) Emit("Hittar ingen adress för " + host + ".");
                            return;
                        }
                    }
                    if (verbose) Emit("Ansluter till " + hostPort + "…");
                    await ConnectAsync(addr, port, hostPort, verbose);
                }
                catch (Exception ex)
                {
                    if (verbose) Emit("Kunde inte ansluta till " + hostPort + ": " + ex.Message);
                }
                finally
                {
                    lock (_lock) _connecting.Remove(key);
                }
            });
        }

        // ================================================================ TCP

        async Task AcceptLoop()
        {
            while (_running)
            {
                TcpClient c = null;
                bool failed = false;
                try { c = await _listener.AcceptTcpClientAsync(); }
                catch { failed = true; }
                if (failed)
                {
                    if (!_running) return;
                    await Task.Delay(200);
                    continue;
                }
                Task ignored = HandleConnection(c, false, null);
            }
        }

        async Task ConnectAsync(IPAddress addr, int port, string manualAddr, bool verbose)
        {
            TcpClient client = new TcpClient(AddressFamily.InterNetwork);
            bool connected = false;
            try
            {
                Task connect = client.ConnectAsync(addr, port);
                Task done = await Task.WhenAny(connect, Task.Delay(5000));
                if (done != connect) throw new TimeoutException("inget svar inom 5 sekunder");
                await connect;
                connected = true;
            }
            catch (Exception ex)
            {
                try { client.Close(); } catch { }
                if (verbose) Emit("Kunde inte ansluta till " + (manualAddr ?? (addr + ":" + port)) + ": " + ex.Message);
            }
            if (connected) await HandleConnection(client, true, manualAddr);
        }

        async Task HandleConnection(TcpClient client, bool outbound, string manualAddr)
        {
            Peer peer = null;
            try
            {
                client.NoDelay = true;
                client.SendTimeout = 15000;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                NetworkStream stream = client.GetStream();
                IPAddress remoteAddr = ((IPEndPoint)client.Client.RemoteEndPoint).Address;

                // ---- Handskakning i klartext: id, namn, port och ECDH-publik nyckel
                byte[] theirPub;
                string theirId, theirName, theirStatus;
                int theirPort;
                byte[] encKey, macKey;
                using (ECDiffieHellmanCng ecdh = new ECDiffieHellmanCng(256))
                {
                    ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
                    ecdh.HashAlgorithm = CngAlgorithm.Sha256;
                    byte[] myPub = ecdh.PublicKey.ToByteArray();
                    WriteRaw(stream, Utf8.GetBytes("HELLO|" + SelfId + "|" + SelfName + "|" + ListenPort + "|" + Convert.ToBase64String(myPub) + "|" + SelfStatus));

                    byte[] first = await ReadFrameAsync(stream, 8000);
                    if (first == null) throw new IOException("stängd");
                    string[] p = Utf8.GetString(first).Split('|');
                    if (p.Length < 5 || p[0] != "HELLO" || !int.TryParse(p[3], out theirPort)) throw new IOException("ogiltig handskakning");
                    theirId = p[1];
                    theirName = CleanName(p[2]);
                    theirPub = Convert.FromBase64String(p[4]);
                    theirStatus = CleanStatus(p.Length > 5 ? p[5] : "online");
                    if (theirId == SelfId) return; // vi ringde oss själva

                    byte[] shared = ecdh.DeriveKeyMaterial(ECDiffieHellmanCngPublicKey.FromByteArray(theirPub, CngKeyBlobFormat.EccPublicBlob));
                    encKey = Hmac(shared, "MiniChat enc");
                    macKey = Hmac(shared, "MiniChat mac");
                }

                peer = new Peer
                {
                    Id = theirId,
                    Name = theirName,
                    Status = theirStatus,
                    Address = remoteAddr,
                    ListenPort = theirPort,
                    Outbound = outbound,
                    ManualAddress = manualAddr,
                    Client = client,
                    Stream = stream,
                    EncKey = encKey,
                    MacKey = macKey,
                    LastSeen = DateTime.UtcNow
                };

                // Dubbletter: behåll förbindelsen som startades av den med lägst id (båda räknar lika).
                bool preferred = outbound
                    ? string.CompareOrdinal(SelfId, theirId) < 0
                    : string.CompareOrdinal(theirId, SelfId) < 0;
                Peer old = null;
                lock (_lock)
                {
                    if (_peers.TryGetValue(theirId, out old))
                    {
                        if (!preferred) { peer = null; return; }
                        if (old.ManualAddress != null && peer.ManualAddress == null) peer.ManualAddress = old.ManualAddress;
                        peer.Avatar = old.Avatar;
                        peer.Roster = old.Roster;
                    }
                    _peers[theirId] = peer;
                }
                if (old != null)
                {
                    try { old.Client.Close(); } catch { }
                }

                // ---- Nu är allt krypterat
                if (SelfAvatar != null) SendFrame(peer, Frame("AVATAR", SelfAvatar));
                SendRoster(peer);
                string known;
                lock (_lock)
                    known = string.Join(",", _peers.Values.Where(x => x != peer).Select(x => x.EndPoint).ToArray());
                if (known.Length > 0) SendFrame(peer, Frame("PEERS|" + known, null));

                if (old == null)
                {
                    Raise(PeerJoined, peer);
                    RaiseRoster();
                    ScheduleRoster();
                }

                while (_running)
                {
                    byte[] data = await ReadFrameAsync(stream, 0);
                    if (data == null) break;
                    byte[] plain = Unseal(peer, data);
                    peer.LastSeen = DateTime.UtcNow;
                    HandleFrame(peer, plain);
                }
            }
            catch (Exception)
            {
                // tappad förbindelse eller ogiltig data – städas nedan
            }
            finally
            {
                try { client.Close(); } catch { }
                if (peer != null)
                {
                    bool removed = false;
                    lock (_lock)
                    {
                        Peer cur;
                        if (_peers.TryGetValue(peer.Id, out cur) && cur == peer)
                        {
                            _peers.Remove(peer.Id);
                            removed = true;
                        }
                    }
                    if (removed)
                    {
                        Raise(PeerLeft, peer);
                        RaiseRoster();
                        ScheduleRoster();
                    }
                }
            }
        }

        void HandleFrame(Peer from, byte[] plain)
        {
            int nl = Array.IndexOf(plain, (byte)10);
            string header = Utf8.GetString(plain, 0, nl < 0 ? plain.Length : nl);
            byte[] body;
            if (nl < 0) body = new byte[0];
            else
            {
                body = new byte[plain.Length - nl - 1];
                Buffer.BlockCopy(plain, nl + 1, body, 0, body.Length);
            }
            string[] p = header.Split('|');

            switch (p[0])
            {
                case "MSG":
                    {
                        if (p.Length < 5) return;
                        if (!MarkSeen(p[1])) return;
                        long ticks;
                        long.TryParse(p[4], out ticks);
                        ChatMessage m = new ChatMessage
                        {
                            Id = p[1],
                            FromId = p[2],
                            FromName = CleanName(p[3]),
                            Time = ticks > 0 && ticks < DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime() : DateTime.Now,
                            Text = Utf8.GetString(body)
                        };
                        if (from == null) TouchCloudPeer(p[2], CleanName(p[3]), null);
                        if (m.FromId != SelfId) Raise(MessageReceived, m);
                        Relay(plain, from, true);
                        break;
                    }
                case "SOUND":
                    {
                        if (p.Length < 4) return;
                        if (!MarkSeen(p[1])) return;
                        if (from == null) TouchCloudPeer(p[2], CleanName(p[3]), null);
                        if (p[2] != SelfId)
                        {
                            Action<string, string> ev = SoundReceived;
                            if (ev != null && _running) ev(CleanName(p[3]), Utf8.GetString(body));
                        }
                        Relay(plain, from, true);
                        break;
                    }
                case "NAME":
                    if (from == null) break;
                    if (p.Length >= 2)
                    {
                        from.Name = CleanName(p[1]);
                        RaiseRoster();
                        ScheduleRoster();
                    }
                    break;
                case "STATUS":
                    if (from == null) break;
                    if (p.Length >= 2)
                    {
                        from.Status = CleanStatus(p[1]);
                        RaiseRoster();
                        ScheduleRoster();
                    }
                    break;
                case "SOUNDX":
                    {
                        if (p.Length < 5 || !MarkSeen(p[1])) return;
                        if (p[2] != SelfId && body.Length > 0 && body.Length <= 3 * 1024 * 1024)
                        {
                            Action<string, string, string, byte[]> ev = SoundDataReceived;
                            if (ev != null && _running) ev(CleanName(p[3]), CleanName(p[4]), p.Length > 5 ? p[5] : ".wav", body);
                        }
                        if (from == null) TouchCloudPeer(p[2], CleanName(p[3]), null);
                        Relay(plain, from, true);
                        break;
                    }
                case "TYPING":
                    {
                        if (p.Length < 5 || !MarkSeen(p[1])) return;
                        if (p[2] != SelfId)
                        {
                            Action<string, string, bool> ev = TypingChanged;
                            if (ev != null && _running) ev(p[2], CleanName(p[3]), p[4] == "1");
                        }
                        Relay(plain, from, false);
                        break;
                    }
                case "NUDGE":
                    {
                        if (p.Length < 4 || !MarkSeen(p[1])) return;
                        if (p[2] != SelfId)
                        {
                            Action<string, string> ev = NudgeReceived;
                            if (ev != null && _running) ev(p[2], CleanName(p[3]));
                        }
                        if (from == null) TouchCloudPeer(p[2], CleanName(p[3]), null);
                        Relay(plain, from, true);
                        break;
                    }
                case "SEEN":
                    {
                        if (p.Length < 5 || !MarkSeen(p[1])) return;
                        if (p[2] != SelfId)
                        {
                            Action<string, string, string> ev = SeenReceived;
                            if (ev != null && _running) ev(p[2], CleanName(p[3]), p[4]);
                        }
                        if (from == null) TouchCloudPeer(p[2], CleanName(p[3]), null);
                        Relay(plain, from, false);
                        break;
                    }
                case "REACT":
                    {
                        if (p.Length < 6 || !MarkSeen(p[1])) return;
                        if (p[2] != SelfId)
                        {
                            Action<string, string, string, string, bool> ev = ReactionReceived;
                            if (ev != null && _running) ev(p[2], CleanName(p[3]), p[4], Utf8.GetString(body), p[5] == "1");
                        }
                        if (from == null) TouchCloudPeer(p[2], CleanName(p[3]), null);
                        Relay(plain, from, true);
                        break;
                    }
                case "AVATAR":
                    if (from == null) break;
                    from.Avatar = body.Length > 0 && body.Length <= MaxAvatar ? body : null;
                    SaveCachedAvatar(from.Id, from.Avatar);
                    RaiseRoster();
                    ScheduleRoster();
                    break;
                case "ROSTER":
                    {
                        if (from == null) break;
                        List<Participant> list = new List<Participant>();
                        foreach (string line in Utf8.GetString(body).Split('\n'))
                        {
                            string[] f = line.Split('|');
                            if (f.Length < 2 || f[0].Length == 0) continue;
                            Participant r = new Participant { Id = f[0], Name = CleanName(f[1]), Status = CleanStatus(f.Length > 3 ? f[3] : "online") };
                            if (f.Length > 2 && f[2].Length > 0)
                            {
                                try { r.Avatar = Convert.FromBase64String(f[2]); } catch { }
                                if (r.Avatar != null && r.Avatar.Length > MaxAvatar) r.Avatar = null;
                            }
                            list.Add(r);
                        }
                        from.Roster = list;
                        RaiseRoster();
                        break;
                    }
                case "PEERS":
                    {
                        if (from == null || p.Length < 2) return;
                        foreach (string ep in p[1].Split(','))
                        {
                            int i = ep.LastIndexOf(':');
                            if (i <= 0) continue;
                            IPAddress a;
                            int port;
                            if (!IPAddress.TryParse(ep.Substring(0, i), out a)) continue;
                            if (!int.TryParse(ep.Substring(i + 1), out port)) continue;
                            if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                            ConsiderEndpoint(a, port);
                        }
                        break;
                    }
                case "PING":
                    if (from != null) SendFrame(from, Frame("PONG", null));
                    break;
                case "PONG":
                    break;
                case "BYE":
                    if (from != null) { try { from.Client.Close(); } catch { } }
                    break;
                case "FOFFER":
                case "FACCEPT":
                case "FDECLINE":
                case "FCHUNK":
                case "FACK":
                case "FDONE":
                case "FCANCEL":
                    if (from != null) HandleFileFrame(from, p, body, plain);
                    break;
            }
        }

        // ================================================================ roster

        void SendRoster(Peer to)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Peer p in Peers)
            {
                if (p == to) continue;
                sb.Append(p.Id).Append('|').Append(p.Name).Append('|')
                  .Append(p.Avatar != null ? Convert.ToBase64String(p.Avatar) : "").Append('|').Append(p.Status).Append('\n');
            }
            SendFrame(to, Frame("ROSTER", Utf8.GetBytes(sb.ToString())));
        }

        void ScheduleRoster()
        {
            lock (_lock)
            {
                if (_rosterPending) return;
                _rosterPending = true;
            }
            Task ignored = Task.Run(async () =>
            {
                await Task.Delay(400);
                lock (_lock) _rosterPending = false;
                if (!_running) return;
                foreach (Peer p in Peers) SendRoster(p);
            });
        }

        // ================================================================ ramar & kryptering

        public static byte[] Frame(string header, byte[] body)
        {
            byte[] h = Utf8.GetBytes(header);
            byte[] f = new byte[h.Length + 1 + (body == null ? 0 : body.Length)];
            Buffer.BlockCopy(h, 0, f, 0, h.Length);
            f[h.Length] = 10;
            if (body != null) Buffer.BlockCopy(body, 0, f, h.Length + 1, body.Length);
            return f;
        }

        static void WriteRaw(Stream s, byte[] payload)
        {
            byte[] outp = new byte[4 + payload.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, outp, 0, 4);
            Buffer.BlockCopy(payload, 0, outp, 4, payload.Length);
            s.Write(outp, 0, outp.Length);
        }

        /// <summary>Krypterar och skickar en ram till en kompis. Trådsäker, blockerar tills skickad.</summary>
        void SendFrame(Peer p, byte[] plain)
        {
            try
            {
                byte[] boxed = Seal(p, plain);
                lock (p.SendLock) WriteRaw(p.Stream, boxed);
            }
            catch
            {
                try { p.Client.Close(); } catch { }
            }
        }

        void Broadcast(byte[] plain, Peer except)
        {
            foreach (Peer p in Peers)
                if (p != except) SendFrame(p, plain);
        }

        /// <summary>Skickar till en viss deltagare: direkt om vi känner den, annars till alla (någon vet vägen).</summary>
        void Route(string toId, byte[] plain, Peer except)
        {
            List<Peer> peers = Peers;
            Peer direct = peers.FirstOrDefault(x => x.Id == toId);
            if (direct != null)
            {
                if (direct != except) SendFrame(direct, plain);
                return;
            }
            foreach (Peer p in peers)
                if (p != except) SendFrame(p, plain);
        }

        static async Task<byte[]> ReadFrameAsync(Stream s, int timeoutMs)
        {
            byte[] lenBuf = new byte[4];
            Task<bool> head = ReadExact(s, lenBuf, 4);
            if (timeoutMs > 0 && await Task.WhenAny(head, Task.Delay(timeoutMs)) != head) throw new TimeoutException("handskakning");
            if (!await head) return null;
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0 || len > MaxFrame) throw new IOException("ogiltig ramstorlek");
            byte[] buf = new byte[len];
            Task<bool> bodyTask = ReadExact(s, buf, len);
            if (timeoutMs > 0 && await Task.WhenAny(bodyTask, Task.Delay(timeoutMs)) != bodyTask) throw new TimeoutException("handskakning");
            if (!await bodyTask) return null;
            return buf;
        }

        static async Task<bool> ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = await s.ReadAsync(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        static byte[] Seal(Peer p, byte[] plain) { return Crypto.Seal(p.EncKey, p.MacKey, plain); }
        static byte[] Unseal(Peer p, byte[] data) { return Crypto.Unseal(p.EncKey, p.MacKey, data); }
        static byte[] Hmac(byte[] key, string label) { return Crypto.Hmac(key, label); }

        // ================================================================ hjälp

        bool MarkSeen(string id)
        {
            lock (_lock)
            {
                if (!_seenIds.Add(id)) return false;
                _seenQueue.Enqueue(id);
                while (_seenQueue.Count > 50000) _seenIds.Remove(_seenQueue.Dequeue());
                return true;
            }
        }

        static string NewId() { return Guid.NewGuid().ToString("N"); }

        public static string CleanName(string name)
        {
            name = (name ?? "").Replace("|", "").Replace("\n", " ").Replace("\r", " ").Trim();
            if (name.Length > 32) name = name.Substring(0, 32);
            return name.Length == 0 ? "Okänd" : name;
        }

        public static string CleanStatus(string s)
        {
            return s == "busy" || s == "away" ? s : "online";
        }

        void Raise(Action<Peer> ev, Peer p) { if (ev != null && _running) ev(p); }
        void Raise(Action<ChatMessage> ev, ChatMessage m) { if (ev != null && _running) ev(m); }
        void RaiseRoster() { Action ev = RosterChanged; if (ev != null && _running) ev(); }
        void Emit(string s) { Action<string> ev = Log; if (ev != null) ev(s); }
    }

    /// <summary>AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC). Används både för P2P-länkar och molnbryggan.</summary>
    public static class Crypto
    {
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static byte[] Seal(byte[] encKey, byte[] macKey, byte[] plain)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = encKey;
                aes.GenerateIV();
                byte[] ct;
                using (ICryptoTransform t = aes.CreateEncryptor()) ct = t.TransformFinalBlock(plain, 0, plain.Length);
                byte[] outp = new byte[16 + ct.Length + 32];
                Buffer.BlockCopy(aes.IV, 0, outp, 0, 16);
                Buffer.BlockCopy(ct, 0, outp, 16, ct.Length);
                using (HMACSHA256 h = new HMACSHA256(macKey))
                {
                    byte[] mac = h.ComputeHash(outp, 0, 16 + ct.Length);
                    Buffer.BlockCopy(mac, 0, outp, 16 + ct.Length, 32);
                }
                return outp;
            }
        }

        public static byte[] Unseal(byte[] encKey, byte[] macKey, byte[] data)
        {
            if (data.Length < 16 + 16 + 32) throw new IOException("för kort ram");
            int ctLen = data.Length - 48;
            using (HMACSHA256 h = new HMACSHA256(macKey))
            {
                byte[] mac = h.ComputeHash(data, 0, 16 + ctLen);
                int diff = 0;
                for (int i = 0; i < 32; i++) diff |= mac[i] ^ data[16 + ctLen + i];
                if (diff != 0) throw new IOException("MAC-fel");
            }
            using (Aes aes = Aes.Create())
            {
                aes.Key = encKey;
                byte[] iv = new byte[16];
                Buffer.BlockCopy(data, 0, iv, 0, 16);
                aes.IV = iv;
                using (ICryptoTransform t = aes.CreateDecryptor()) return t.TransformFinalBlock(data, 16, ctLen);
            }
        }

        public static byte[] Hmac(byte[] key, string label)
        {
            using (HMACSHA256 h = new HMACSHA256(key)) return h.ComputeHash(Utf8.GetBytes(label));
        }
    }
}
