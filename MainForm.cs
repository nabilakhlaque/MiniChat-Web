using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MiniChat
{
    /// <summary>Det JavaScript i app.html får anropa (window.external.*).</summary>
    [ComVisible(true)]
    public class ScriptBridge
    {
        readonly MainForm _f;
        public ScriptBridge(MainForm f) { _f = f; }
        public void send(string text) { _f.SendText(text); }
        public void typing(bool on) { _f.TypingInput(on); }
        public void nudge() { _f.SendNudge(); }
        public void react(string msgId, string emoji) { _f.React(msgId, emoji); }
        public void setStatus(string status) { _f.SetChosenStatus(status); }
        public void setCloudCode(string code) { _f.SetCloudCode(code); }
        public void setCloudServer(string url) { _f.SetCloudServer(url); }
        public void setName(string name) { _f.SetName(name); }
        public void pickAvatar() { _f.ChooseAvatar(); }
        public void removeAvatar() { _f.SetAvatar(null); }
        public void pickFiles() { _f.PickFiles(); }
        public void emoji() { _f.OpenEmojiPanel(); }
        public void addAddress(string addr) { _f.AddAddress(addr); }
        public void forgetAddresses() { _f.ForgetAddresses(); }
        public void setPort(string port) { _f.SetPort(port); }
        public void toggle(string key) { _f.Toggle(key); }
        public void openDownloads() { _f.OpenDownloads(); }
        public void openSounds() { _f.OpenSoundsFolder(); }
        public void clearChat() { _f.ClearChat(); }
        public void exit() { _f.ExitApp(); }
        public void openUrl(string url) { _f.OpenUrl(url); }
        public void openFile(string id) { _f.OpenTransferFile(id, false); }
        public void showFile(string id) { _f.OpenTransferFile(id, true); }
        public void acceptFile(string id) { _f.AcceptTransfer(id, true); }
        public void declineFile(string id) { _f.AcceptTransfer(id, false); }
        public void dragIn() { _f.ShowDropOverlay(); }
    }

    /// <summary>Minimal JSON-byggare för anropen in i vyn.</summary>
    class Json
    {
        readonly StringBuilder _sb = new StringBuilder("{");
        bool _first = true;

        public Json Add(string key, object value)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            _sb.Append('"').Append(key).Append("\":");
            if (value == null) _sb.Append("null");
            else if (value is bool) _sb.Append((bool)value ? "true" : "false");
            else if (value is int || value is long || value is double)
                _sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            else Str(value.ToString());
            return this;
        }

        void Str(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }

        public override string ToString() { return _sb.ToString() + "}"; }
    }

    /// <summary>
    /// Tunt WinForms-skal: fönster, systemfältsikon, ljud, filer, historik och dialoger.
    /// Allt synligt i fönstret ritas av app.html i den inbyggda webbvyn.
    /// </summary>
    public class MainForm : Form, IMessageFilter
    {
        static readonly Color Bg = Color.FromArgb(237, 242, 247);
        static readonly Color Blue = Color.FromArgb(60, 140, 231);
        static readonly CultureInfo Sv = new CultureInfo("sv-SE");
        static readonly Regex SoundRe = new Regex(@"\*([\p{L}\p{N}_\-]{1,32})\*", RegexOptions.Compiled);
        static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };
        const int AwayMinutes = 10;

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
        [DllImport("user32.dll")]
        static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        readonly Settings _settings = Settings.Load();
        Network _net;
        byte[] _avatar;

        WebBrowser _web;
        Panel _dropOverlay;
        NotifyIcon _tray;
        Icon _icon, _iconAlert;
        System.Windows.Forms.Timer _blink, _dropTimer, _idleTimer, _typingTimer, _seenTimer, _shakeTimer;
        bool _blinkOn;
        SoundPlayer _ding, _prutt, _skratt, _knock;
        int _unread;
        bool _exiting, _allowVisible, _trayHintShown, _initialized, _webReady;
        ToolStripMenuItem _trSound, _trNotify, _trAutostart, _trOnline, _trBusy;

        string _effStatus = "online";       // det andra ser: online, busy eller away
        bool _typingSent;
        DateTime _typingLast = DateTime.MinValue;
        readonly Dictionary<string, KeyValuePair<string, DateTime>> _typers = new Dictionary<string, KeyValuePair<string, DateTime>>();
        string _typersShown = "";
        DateTime _lastNudgeSent = DateTime.MinValue;
        readonly Dictionary<string, DateTime> _lastNudgeFrom = new Dictionary<string, DateTime>();
        Point _shakeOrigin;
        int _shakeStep;

        readonly List<string> _myMsgIds = new List<string>();
        readonly Dictionary<string, int> _seenBy = new Dictionary<string, int>();
        readonly Dictionary<string, string> _pendingSeen = new Dictionary<string, string>();
        readonly HashSet<string> _myReactions = new HashSet<string>();

        readonly Queue<KeyValuePair<string, object[]>> _jsQueue = new Queue<KeyValuePair<string, object[]>>();
        readonly List<ChatMessage> _outbox = new List<ChatMessage>();
        readonly HashSet<string> _knownTransfers = new HashSet<string>();
        readonly Dictionary<string, string> _avatarKeys = new Dictionary<string, string>();
        Dictionary<string, string> _knownOnline = new Dictionary<string, string>();
        readonly Dictionary<string, string> _knownStatus = new Dictionary<string, string>();
        DateTime _lastMsgTime = DateTime.MinValue;

        public MainForm(bool startInTray)
        {
            _allowVisible = !startInTray;

            Text = "MiniChat";
            _icon = LoadIcon("MiniChat.app.ico") ?? SystemIcons.Application;
            _iconAlert = LoadIcon("MiniChat.alert.ico") ?? SystemIcons.Information;
            Icon = _icon;
            Font = new Font("Segoe UI", 10f);
            BackColor = Bg;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 760);
            MinimumSize = new Size(430, 540);
            _avatar = Settings.LoadAvatar();
            if (_settings.Status != "busy") _settings.Status = "online"; // "away" sätts bara automatiskt
            _effStatus = _settings.Status;

            BuildView();
            BuildTray();
            BuildTimers();
            InitSounds();
            CleanTemp();
            Application.AddMessageFilter(this);

            if (Startup.IsEnabled()) Startup.Set(true); // håll sökvägen färsk om exe-filen flyttats

            Activated += delegate { MarkRead(); Js("focusInput"); };
            Resize += delegate { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        }

        // ================================================================ vy

        void BuildView()
        {
            _web = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScriptErrorsSuppressed = true,
                IsWebBrowserContextMenuEnabled = false,
                WebBrowserShortcutsEnabled = false,
                AllowWebBrowserDrop = true,
                TabStop = false
            };
            _web.ObjectForScripting = new ScriptBridge(this);
            _web.DocumentCompleted += delegate
            {
                _webReady = true;
                PushState();
                Js("showApp", _settings.Name.Trim().Length == 0);
                FlushJs();
            };
            _web.Navigating += (s, e) =>
            {
                string u = e.Url == null ? "" : e.Url.ToString();
                if (!u.StartsWith("about:")) e.Cancel = true; // inget får navigera bort från vyn
            };

            _dropOverlay = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(232, 241, 252), Visible = false, AllowDrop = true };
            _dropOverlay.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(16, 16, _dropOverlay.Width - 33, _dropOverlay.Height - 33);
                using (GraphicsPath gp = Rounded(r, 22))
                using (Pen p = new Pen(Blue, 2) { DashStyle = DashStyle.Dash, DashCap = DashCap.Round })
                    e.Graphics.DrawPath(p, gp);
                using (Font f = new Font("Segoe UI", 16f, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, "Släpp här för att skicka", f, r, Blue, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            _dropOverlay.DragEnter += OnDragEnterFiles;
            _dropOverlay.DragDrop += OnDragDropFiles;
            _dropOverlay.DragLeave += delegate { HideDropOverlay(); };
            _dropOverlay.Click += delegate { HideDropOverlay(); };

            Controls.Add(_dropOverlay);
            Controls.Add(_web);
            AllowDrop = true;
            DragEnter += OnDragEnterFiles;
            DragDrop += OnDragDropFiles;

            _web.DocumentText = LoadText("MiniChat.app.html");
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath gp = new GraphicsPath();
            int d = radius * 2;
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Öppna MiniChat", null, delegate { ShowFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            _trOnline = (ToolStripMenuItem)menu.Items.Add("Tillgänglig", null, delegate { SetChosenStatus("online"); });
            _trBusy = (ToolStripMenuItem)menu.Items.Add("Upptagen", null, delegate { SetChosenStatus("busy"); });
            menu.Items.Add(new ToolStripSeparator());
            _trSound = (ToolStripMenuItem)menu.Items.Add("Ljud", null, delegate { Toggle("sound"); });
            _trNotify = (ToolStripMenuItem)menu.Items.Add("Aviseringar", null, delegate { Toggle("notify"); });
            _trAutostart = (ToolStripMenuItem)menu.Items.Add("Starta med Windows", null, delegate { Toggle("autostart"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Avsluta", null, delegate { ExitApp(); });

            _tray = new NotifyIcon { Icon = _icon, Text = "MiniChat", Visible = true, ContextMenuStrip = menu };
            _tray.DoubleClick += delegate { ShowFromTray(); };
            _tray.BalloonTipClicked += delegate { ShowFromTray(); };
            SyncTray();
        }

        void BuildTimers()
        {
            _blink = new System.Windows.Forms.Timer { Interval = 600 };
            _blink.Tick += delegate
            {
                _blinkOn = !_blinkOn;
                _tray.Icon = _blinkOn ? _iconAlert : _icon;
            };

            _dropTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            _dropTimer.Tick += delegate { HideDropOverlay(); };

            // Ej vid datorn: inget tangentbord/mus i hela Windows på 10 minuter
            _idleTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            _idleTimer.Tick += delegate { ApplyStatus(); };
            _idleTimer.Start();

            // Andra som skriver: visas i 6 s efter senaste signal
            _typingTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _typingTimer.Tick += delegate { PushTypers(); };

            // "Sett": skickas samlat strax efter att meddelanden visats i ett aktivt fönster
            _seenTimer = new System.Windows.Forms.Timer { Interval = 700 };
            _seenTimer.Tick += delegate { FlushSeen(); };

            _shakeTimer = new System.Windows.Forms.Timer { Interval = 18 };
            _shakeTimer.Tick += delegate { ShakeStep(); };
        }

        void InitSounds()
        {
            _ding = LoadSound("MiniChat.ding.wav");
            _prutt = LoadSound("MiniChat.prutt.wav");
            _skratt = LoadSound("MiniChat.skratt.wav");
            _knock = LoadSound("MiniChat.knock.wav");
        }

        static SoundPlayer LoadSound(string resource)
        {
            try
            {
                Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
                if (s == null) return null;
                SoundPlayer p = new SoundPlayer(s);
                p.Load();
                return p;
            }
            catch { return null; }
        }

        static Icon LoadIcon(string resource)
        {
            try
            {
                Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
                if (s != null) return new Icon(s);
            }
            catch { }
            return null;
        }

        static string LoadText(string resource)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch
            {
                return "<html><body style='font-family:Segoe UI'>Kunde inte läsa vyn (app.html saknas i exe-filen).</body></html>";
            }
        }

        // ================================================================ livscykel

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_initialized) return;
            _initialized = true;
            if (_settings.Name.Trim().Length > 0) BeginInvoke((Action)StartNetwork);
        }

        // Gör att fönstret kan starta dolt (bara ikon vid klockan) utan att blinka till.
        protected override void SetVisibleCore(bool value)
        {
            if (!_allowVisible)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            Application.RemoveMessageFilter(this);
            if (_net != null) { _net.Stop(); _net = null; }
            _blink.Stop();
            _idleTimer.Stop();
            _tray.Visible = false;
            base.OnFormClosing(e);
        }

        public void ShowFromTray()
        {
            _allowVisible = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            MarkRead();
        }

        void HideToTray()
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3000, "MiniChat körs i bakgrunden",
                    "Dubbelklicka på ikonen vid klockan för att öppna. Högerklicka för att avsluta.", ToolTipIcon.Info);
            }
        }

        public void ExitApp()
        {
            _exiting = true;
            Close();
        }

        /// <summary>Ctrl+V med filer eller en bild i urklipp skickar dem direkt, var fokus än ligger i fönstret.</summary>
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_KEYDOWN = 0x100;
            if (m.Msg != WM_KEYDOWN || _net == null) return false;
            if ((int)(long)m.WParam != (int)Keys.V) return false;
            if ((ModifierKeys & Keys.Control) != Keys.Control || (ModifierKeys & Keys.Alt) == Keys.Alt) return false;
            if (Form.ActiveForm != this) return false;
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    List<string> files = new List<string>();
                    foreach (string f in Clipboard.GetFileDropList()) files.Add(f);
                    SendFiles(files);
                    return true;
                }
                if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
                {
                    using (Image img = Clipboard.GetImage())
                    {
                        string dir = Path.Combine(Path.GetTempPath(), "MiniChat");
                        Directory.CreateDirectory(dir);
                        string path = Path.Combine(dir, "Urklipp " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png");
                        img.Save(path, ImageFormat.Png);
                        SendFiles(new[] { path });
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        // ================================================================ nätverk

        void StartNetwork()
        {
            if (_net != null || _settings.Name.Trim().Length == 0) return;

            ReplayHistory();

            Network net = new Network();
            net.AutoAccept = _settings.AutoAccept;
            net.RosterChanged += () => OnUi(delegate { OnRoster(); });
            net.MessageReceived += m => OnUi(delegate { OnIncoming(m); });
            net.TransferUpdated += t => OnUi(delegate { OnTransfer(t); });
            net.SoundReceived += (who, sound) => OnUi(delegate { PlayEffect(sound); });
            net.SoundDataReceived += (who, sound, ext, data) => OnUi(delegate { PlaySoundData(ext, data); });
            net.TypingChanged += (id, name, on) => OnUi(delegate { OnTyping(id, name, on); });
            net.NudgeReceived += (id, name) => OnUi(delegate { OnNudge(id, name); });
            net.SeenReceived += (id, name, msgId) => OnUi(delegate { OnSeen(name, msgId); });
            net.ReactionReceived += (id, name, msgId, emoji, on) => OnUi(delegate { OnReaction(id, name, msgId, emoji, on, true); });
            net.Log += s => OnUi(delegate { Js("addSystem", s); });
            net.CloudStateChanged += (ok, msg) => OnUi(delegate { OnCloudState(ok, msg); });
            try
            {
                net.Start(_settings.Id, _settings.Name, _effStatus, _avatar, _settings.Port, _settings.Manual);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Kunde inte starta nätverket:\n" + ex.Message, "MiniChat", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _net = net;
            if (_settings.CloudCode.Trim().Length > 0) _net.EnableCloud(_settings.CloudCode, _settings.CloudServer);
            PushState();
            Js("addSystem", "Du är inne som " + _net.SelfName + ". Allt som skickas är krypterat.");
            if (_settings.Manual.Count > 0) Js("addSystem", "Försöker nå " + string.Join(", ", _settings.Manual.ToArray()) + "…");
            if (_net.CloudEnabled) Js("addSystem", "Molnbryggan ansluter…");
            ApplyStatus();
        }

        bool _cloudWasConnected;
        void OnCloudState(bool ok, string msg)
        {
            if (ok && !_cloudWasConnected) Js("addSystem", "☁ Molnbryggan är ansluten – ni når varandra via internet");
            else if (msg != null) Js("addSystem", "☁ " + msg);
            _cloudWasConnected = ok;
            PushState();
        }

        public void SetCloudCode(string code)
        {
            code = CloudBridge.NormalizeCode(code);
            _settings.CloudCode = code;
            _settings.Save();
            _cloudWasConnected = false;
            if (_net != null)
            {
                if (code.Length == 0) { _net.DisableCloud(); Js("addSystem", "Molnbryggan är avstängd."); }
                else { _net.EnableCloud(code, _settings.CloudServer); Js("addSystem", "Molnbryggan ansluter…"); }
            }
            PushState();
        }

        public void SetCloudServer(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) url = CloudBridge.DefaultServer;
            _settings.CloudServer = url;
            _settings.Save();
            if (_net != null && _settings.CloudCode.Length > 0) _net.EnableCloud(_settings.CloudCode, url);
            PushState();
        }

        void OnRoster()
        {
            if (_net == null) return;
            List<Participant> list = _net.Participants;
            Dictionary<string, string> now = new Dictionary<string, string>();
            foreach (Participant p in list) now[p.Id] = p.Name;
            foreach (KeyValuePair<string, string> kv in now)
                if (!_knownOnline.ContainsKey(kv.Key)) Js("addSystem", kv.Value + " är online");
            foreach (KeyValuePair<string, string> kv in _knownOnline)
                if (!now.ContainsKey(kv.Key)) { Js("addSystem", kv.Value + " gick offline"); _knownStatus.Remove(kv.Key); }
            _knownOnline = now;

            StringBuilder sb = new StringBuilder("[");
            foreach (Participant p in list)
            {
                PushAvatar(p.Id, p.Avatar);
                string st = p.Status ?? "online";
                string old;
                if (_knownStatus.TryGetValue(p.Id, out old) && old != st)
                    Js("addSystem", p.Name + (st == "busy" ? " är upptagen" : st == "away" ? " är inte vid datorn" : " är tillbaka"));
                _knownStatus[p.Id] = st;
                if (sb.Length > 1) sb.Append(',');
                sb.Append(new Json().Add("id", p.Id).Add("name", p.Name).Add("status", st).Add("via", p.Cloud ? "cloud" : "p2p").ToString());
            }
            sb.Append(']');
            Js("setRoster", sb.ToString());
            SyncTray();
            if (list.Count > 0) FlushOutbox();
        }

        void OnIncoming(ChatMessage m)
        {
            ShowMessage(m, false, null, true);
            RemoveTyper(m.FromId);
            bool quiet = _effStatus == "busy";
            if (!IsReading())
            {
                _unread++;
                UpdateTitle();
                StartBlink();
                if (_settings.Notify && !quiet) Toast(DisplayName(m.FromId, m.FromName), m.Text);
            }
            if (_settings.Sound && !quiet) Play(_ding);
            QueueSeen(m.FromId, m.Id);
        }

        void OnTransfer(FileTransfer t)
        {
            if (_net == null) return;
            if (!_knownTransfers.Contains(t.Id))
            {
                _knownTransfers.Add(t.Id);
                string sender = t.Outgoing ? _net.SelfId : t.FromId;
                string name = t.Outgoing ? _net.SelfName : DisplayName(t.FromId, t.FromName);
                DateTime now = DateTime.Now;
                Js("addFile", new Json()
                    .Add("id", t.Id).Add("mine", t.Outgoing).Add("sender", sender).Add("name", name)
                    .Add("fileName", t.FileName).Add("sizeText", SizeText(t.Size))
                    .Add("time", Epoch(now)).Add("showTime", TakeTimeSeparator(now)).Add("timeText", TimeText(now)).ToString());
                if (t.Outgoing) PushThumb(t.Id, t.FilePath);
                else
                {
                    bool quiet = _effStatus == "busy";
                    if (!IsReading())
                    {
                        _unread++;
                        UpdateTitle();
                        StartBlink();
                        if (_settings.Notify && !quiet) Toast(name, "Skickar filen " + t.FileName);
                    }
                    if (_settings.Sound && !quiet) Play(_ding);
                }
            }
            Js("fileState", new Json()
                .Add("id", t.Id).Add("mine", t.Outgoing).Add("pct", t.Percent).Add("state", t.State)
                .Add("sizeText", SizeText(t.Size)).Add("text", TransferText(t)).ToString());

            if (t.State == "done" || t.State == "error" || t.State == "declined")
            {
                string key = "hist:" + t.Id;
                if (!_knownTransfers.Contains(key))
                {
                    _knownTransfers.Add(key);
                    if (t.State == "done")
                    {
                        History.Append(new HistoryRecord
                        {
                            Kind = "file", Id = t.Id, FromId = t.Outgoing ? _net.SelfId : t.FromId, FromName = t.Outgoing ? _net.SelfName : t.FromName,
                            Ticks = DateTime.Now.Ticks, Mine = t.Outgoing, FileName = t.FileName, FilePath = t.FilePath, Size = t.Size
                        });
                        if (!t.Outgoing) PushThumb(t.Id, t.FilePath);
                    }
                }
            }
        }

        static string TransferText(FileTransfer t)
        {
            switch (t.State)
            {
                case "offer": return t.Outgoing ? "Väntar på svar…" : "vill skicka en fil till dig";
                case "waiting": return "Väntar på att " + t.ToName + " tar emot…";
                case "active": return (t.Outgoing ? "Skickar " : "Tar emot ") + t.Percent + " %";
                case "done": return t.Outgoing ? "Skickad till " + t.ToName : "Mottagen";
                case "declined": return t.Outgoing ? t.ToName + " avböjde" : "Avböjd";
                case "error": return t.Error ?? "Misslyckades";
            }
            return "";
        }

        string DisplayName(string id, string fallback)
        {
            if (_net != null)
            {
                Participant p = _net.Participants.FirstOrDefault(x => x.Id == id);
                if (p != null)
                {
                    PushAvatar(p.Id, p.Avatar);
                    return p.Name;
                }
            }
            return fallback;
        }

        void PushAvatar(string id, byte[] data)
        {
            string key = "";
            if (data != null && data.Length > 0)
            {
                long sum = data.Length;
                for (int i = 0; i < data.Length; i += 7) sum = unchecked(sum * 31 + data[i]);
                key = sum.ToString();
            }
            string old;
            if (_avatarKeys.TryGetValue(id, out old) && old == key) return;
            _avatarKeys[id] = key;
            Js("setAvatar", id, key.Length == 0 ? "" : DataUrl(data));
        }

        static string DataUrl(byte[] d)
        {
            string mime = d.Length > 2 && d[0] == 0xFF && d[1] == 0xD8 ? "image/jpeg" : "image/png";
            return "data:" + mime + ";base64," + Convert.ToBase64String(d);
        }

        // ================================================================ historik

        void ReplayHistory()
        {
            List<HistoryRecord> list = History.Load();
            if (list.Count == 0) return;
            foreach (HistoryRecord r in list)
            {
                switch (r.Kind)
                {
                    case "msg":
                        {
                            ChatMessage m = new ChatMessage { Id = r.Id, FromId = r.FromId, FromName = r.FromName, Time = r.Time, Text = r.Text };
                            ShowMessage(m, r.Mine, null, false);
                            break;
                        }
                    case "file":
                        {
                            bool exists = r.FilePath != null && File.Exists(r.FilePath);
                            Js("addFile", new Json()
                                .Add("id", r.Id).Add("mine", r.Mine).Add("sender", r.FromId).Add("name", r.FromName)
                                .Add("fileName", r.FileName).Add("sizeText", SizeText(r.Size))
                                .Add("time", Epoch(r.Time)).Add("showTime", TakeTimeSeparator(r.Time)).Add("timeText", TimeText(r.Time)).ToString());
                            Js("fileState", new Json()
                                .Add("id", r.Id).Add("mine", r.Mine).Add("pct", 100).Add("state", exists ? "done" : "error")
                                .Add("sizeText", SizeText(r.Size)).Add("text", exists ? (r.Mine ? "Skickad" : "Mottagen") : "Filen finns inte kvar").ToString());
                            if (exists)
                            {
                                _knownTransfers.Add(r.Id);
                                _knownTransfers.Add("hist:" + r.Id);
                                _historyFiles[r.Id] = r.FilePath;
                                PushThumb(r.Id, r.FilePath);
                            }
                            break;
                        }
                    case "react":
                        OnReaction(r.FromId, r.FromName, r.Id, r.Text, r.On, false);
                        break;
                }
            }
            Js("addSystem", "— äldre meddelanden ovan —");
        }
        readonly Dictionary<string, string> _historyFiles = new Dictionary<string, string>();

        // ================================================================ skicka (anropas från vyn)

        public void SendText(string text)
        {
            if (_net == null || text == null) return;
            if (_typingSent) { _typingSent = false; _net.SendTyping(false); }

            // *prutt*, *skratt* och *egetljud* blir ljud hos de andra och syns inte i chatten
            List<string> sounds = new List<string>();
            Dictionary<string, string> custom = CustomSounds();
            string rest = SoundRe.Replace(text, delegate(Match m)
            {
                string s = m.Groups[1].Value.ToLowerInvariant();
                if (s == "prutt" || s == "skratt" || custom.ContainsKey(s))
                {
                    if (!sounds.Contains(s)) sounds.Add(s);
                    return " ";
                }
                return m.Value;
            });
            rest = Regex.Replace(rest, @"[ \t]{2,}", " ").Trim();

            bool anyone = _net.Participants.Count > 0;
            if (sounds.Count > 0)
            {
                if (!anyone && !_net.CloudEnabled) Js("toast", "Ingen annan är online – ljudet skickades inte");
                else
                {
                    foreach (string s in sounds)
                    {
                        if (s == "prutt") { _net.SendSound(s); Js("toast", "💨 Prutt skickad"); }
                        else if (s == "skratt") { _net.SendSound(s); Js("toast", "😂 Skratt skickat"); }
                        else
                        {
                            try
                            {
                                FileInfo fi = new FileInfo(custom[s]);
                                if (fi.Length > 3 * 1024 * 1024) { Js("toast", "Ljudet " + s + " är för stort (max 3 MB)"); continue; }
                                _net.SendSoundData(s, fi.Extension.ToLowerInvariant(), File.ReadAllBytes(fi.FullName));
                                Js("toast", "🔊 " + s + " skickat");
                            }
                            catch (Exception ex) { Js("toast", "Kunde inte läsa ljudet " + s + ": " + ex.Message); }
                        }
                    }
                }
            }
            if (rest.Length == 0) return;

            if (!anyone && !_net.CloudEnabled)
            {
                ChatMessage pending = new ChatMessage { Id = Guid.NewGuid().ToString("N"), FromId = _net.SelfId, FromName = _net.SelfName, Time = DateTime.Now, Text = rest };
                _outbox.Add(pending);
                ShowMessage(pending, true, "Skickas när någon kommer online…", true);
                return;
            }
            ChatMessage sent = _net.Send(rest);
            ShowMessage(sent, true, !anyone ? "Ligger i molnet – levereras när den andra kommer in" : null, true);
        }

        void FlushOutbox()
        {
            if (_net == null || _outbox.Count == 0) return;
            foreach (ChatMessage pm in _outbox)
            {
                _net.Send(pm.Text);
                Js("setStatus", pm.Id, "Skickat");
            }
            _outbox.Clear();
        }

        void ShowMessage(ChatMessage m, bool mine, string status, bool record)
        {
            string name = mine ? m.FromName : DisplayName(m.FromId, m.FromName);
            Js("addMessage", new Json()
                .Add("id", m.Id).Add("mine", mine).Add("sender", m.FromId).Add("name", name).Add("text", m.Text)
                .Add("time", Epoch(m.Time)).Add("showTime", TakeTimeSeparator(m.Time)).Add("timeText", TimeText(m.Time))
                .Add("status", status).ToString());
            if (mine) _myMsgIds.Add(m.Id);
            if (record)
                History.Append(new HistoryRecord { Kind = "msg", Id = m.Id, FromId = m.FromId, FromName = m.FromName, Ticks = m.Time.Ticks, Mine = mine, Text = m.Text });
        }

        void SendFiles(IEnumerable<string> paths)
        {
            if (_net == null) return;
            if (_net.Participants.Count == 0)
            {
                Js("toast", "Ingen annan är online – filen skickades inte");
                return;
            }
            foreach (string path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Js("addSystem", "Mappar kan inte skickas (" + Path.GetFileName(path) + ") – zippa den först.");
                        continue;
                    }
                    if (!File.Exists(path)) continue;
                    foreach (FileTransfer t in _net.SendFile(path)) OnTransfer(t);
                }
                catch (Exception ex)
                {
                    Js("addSystem", "Kunde inte skicka " + Path.GetFileName(path) + ": " + ex.Message);
                }
            }
        }

        public void PickFiles()
        {
            using (OpenFileDialog d = new OpenFileDialog { Title = "Skicka fil", Multiselect = true, Filter = "Alla filer|*.*" })
            {
                if (d.ShowDialog(this) == DialogResult.OK) SendFiles(d.FileNames);
            }
            Js("focusInput");
        }

        public void OpenEmojiPanel()
        {
            try
            {
                // Windows-tangenten + punkt öppnar Windows egen emojipanel (Windows 10 1709 och senare)
                keybd_event(0x5B, 0, 0, UIntPtr.Zero);
                keybd_event(0xBE, 0, 0, UIntPtr.Zero);
                keybd_event(0xBE, 0, 2, UIntPtr.Zero);
                keybd_event(0x5B, 0, 2, UIntPtr.Zero);
            }
            catch { }
        }

        /// <summary>Bilder visas som miniatyr direkt i bubblan.</summary>
        void PushThumb(string id, string path)
        {
            if (path == null || !File.Exists(path)) return;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(ImageExts, ext) < 0) return;
            Task ignored = Task.Run(() =>
            {
                string url = null;
                try { url = MakeThumb(path); } catch { }
                if (url != null) OnUi(delegate { Js("fileThumb", id, url); });
            });
        }

        static string MakeThumb(string path)
        {
            using (Image src = Image.FromFile(path))
            {
                const int max = 420;
                double k = Math.Min(1.0, Math.Min((double)max / src.Width, (double)max / src.Height));
                int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
                using (Bitmap bmp = new Bitmap(w, h))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.White);
                        g.DrawImage(src, new Rectangle(0, 0, w, h));
                    }
                    return "data:image/jpeg;base64," + Convert.ToBase64String(Jpeg(bmp, 82L));
                }
            }
        }

        static byte[] Jpeg(Bitmap bmp, long quality)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (EncoderParameters ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                    bmp.Save(ms, jpeg, ep);
                }
                return ms.ToArray();
            }
        }

        // ================================================================ status, skriver, knacka, sett, reaktioner

        public void SetChosenStatus(string status)
        {
            status = status == "busy" ? "busy" : "online";
            _settings.Status = status;
            _settings.Save();
            ApplyStatus();
            Js("toast", status == "busy" ? "Du är nu upptagen – inga ljud eller aviseringar" : "Du är nu tillgänglig");
        }

        /// <summary>Räknar ut vad de andra ska se: vald status, eller "away" efter 10 minuters inaktivitet.</summary>
        void ApplyStatus()
        {
            string eff = _settings.Status;
            if (IdleMinutes() >= AwayMinutes) eff = "away";
            bool changed = eff != _effStatus;
            _effStatus = eff;
            if (_net != null) _net.SetStatus(eff);
            if (changed || _webReady)
                Js("setMyStatus", new Json().Add("status", _effStatus).Add("chosen", _settings.Status).ToString());
            SyncTray();
        }

        static double IdleMinutes()
        {
            try
            {
                LASTINPUTINFO lii = new LASTINPUTINFO();
                lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref lii)) return 0;
                uint idle = unchecked((uint)Environment.TickCount - lii.dwTime);
                return idle / 60000.0;
            }
            catch { return 0; }
        }

        public void TypingInput(bool on)
        {
            if (_net == null) return;
            DateTime now = DateTime.UtcNow;
            if (on)
            {
                if (!_typingSent || (now - _typingLast).TotalSeconds > 3)
                {
                    _typingSent = true;
                    _typingLast = now;
                    _net.SendTyping(true);
                }
            }
            else if (_typingSent)
            {
                _typingSent = false;
                _net.SendTyping(false);
            }
        }

        void OnTyping(string id, string name, bool on)
        {
            if (on) _typers[id] = new KeyValuePair<string, DateTime>(name, DateTime.UtcNow.AddSeconds(6));
            else _typers.Remove(id);
            PushTypers();
            if (_typers.Count > 0) _typingTimer.Start(); else _typingTimer.Stop();
        }

        void RemoveTyper(string id)
        {
            if (_typers.Remove(id)) PushTypers();
        }

        void PushTypers()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string k in _typers.Keys.ToList())
                if (_typers[k].Value < now) _typers.Remove(k);
            StringBuilder sb = new StringBuilder("[");
            foreach (KeyValuePair<string, KeyValuePair<string, DateTime>> kv in _typers)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append(new Json().Add("id", kv.Key).Add("name", kv.Value.Key).ToString());
            }
            sb.Append(']');
            string json = sb.ToString();
            if (json == _typersShown) return;
            _typersShown = json;
            Js("typing", json);
            if (_typers.Count == 0) _typingTimer.Stop();
        }

        public void SendNudge()
        {
            if (_net == null) return;
            if (_net.Participants.Count == 0) { Js("toast", "Ingen annan är online"); return; }
            if ((DateTime.UtcNow - _lastNudgeSent).TotalSeconds < 5) { Js("toast", "Lugn, vänta några sekunder 🙂"); return; }
            _lastNudgeSent = DateTime.UtcNow;
            _net.SendNudge();
            Js("addSystem", "Du knackade på 👋");
            Shake();
        }

        void OnNudge(string id, string name)
        {
            DateTime last;
            if (_lastNudgeFrom.TryGetValue(id, out last) && (DateTime.UtcNow - last).TotalSeconds < 5) return;
            _lastNudgeFrom[id] = DateTime.UtcNow;
            Js("addSystem", name + " knackade på dig 👋");
            ShowFromTray();
            Flash();
            Shake();
            Play(_knock);
        }

        void Shake()
        {
            if (!Visible || WindowState != FormWindowState.Normal || _shakeTimer.Enabled) return;
            _shakeOrigin = Location;
            _shakeStep = 0;
            _shakeTimer.Start();
        }

        void ShakeStep()
        {
            _shakeStep++;
            if (_shakeStep > 26 || WindowState != FormWindowState.Normal)
            {
                _shakeTimer.Stop();
                Location = _shakeOrigin;
                return;
            }
            int amp = Math.Max(1, 12 - _shakeStep / 2);
            Location = new Point(_shakeOrigin.X + (int)Math.Round(Math.Sin(_shakeStep * 1.9) * amp),
                                 _shakeOrigin.Y + (int)Math.Round(Math.Cos(_shakeStep * 2.4) * amp * 0.5));
        }

        void Flash()
        {
            try
            {
                FLASHWINFO fi = new FLASHWINFO();
                fi.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
                fi.hwnd = Handle;
                fi.dwFlags = 3 | 12; // FLASHW_ALL | FLASHW_TIMERNOFG
                fi.uCount = 6;
                fi.dwTimeout = 0;
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        void QueueSeen(string senderId, string msgId)
        {
            _pendingSeen[senderId] = msgId;
            if (IsReading()) { _seenTimer.Stop(); _seenTimer.Start(); }
        }

        void FlushSeen()
        {
            _seenTimer.Stop();
            if (_net == null || !IsReading() || _pendingSeen.Count == 0) return;
            foreach (string msgId in _pendingSeen.Values.ToList()) _net.SendSeen(msgId);
            _pendingSeen.Clear();
        }

        void OnSeen(string name, string msgId)
        {
            int idx = _myMsgIds.IndexOf(msgId);
            if (idx < 0) return;
            int cur;
            if (_seenBy.TryGetValue(name, out cur) && cur >= idx) return;
            _seenBy[name] = idx;
            int best = _seenBy.Values.Max();
            List<string> names = _seenBy.Where(kv => kv.Value >= best).Select(kv => kv.Key).OrderBy(x => x).ToList();
            int others = _net != null ? _net.Participants.Count : 0;
            string text = others <= 1 ? "Sett" : "Sett av " + string.Join(", ", names.ToArray());
            Js("seen", _myMsgIds[best], text);
        }

        public void React(string msgId, string emoji)
        {
            if (_net == null || string.IsNullOrEmpty(msgId) || string.IsNullOrEmpty(emoji)) return;
            string key = msgId + "|" + emoji;
            bool on = !_myReactions.Contains(key);
            if (on) _myReactions.Add(key); else _myReactions.Remove(key);
            _net.SendReaction(msgId, emoji, on);
            OnReaction(_net.SelfId, _net.SelfName, msgId, emoji, on, true);
        }

        void OnReaction(string fromId, string name, string msgId, string emoji, bool on, bool record)
        {
            if (!record && _net == null && fromId == _settings.Id)
            {
                string key = msgId + "|" + emoji;
                if (on) _myReactions.Add(key); else _myReactions.Remove(key);
            }
            Js("react", msgId, emoji, name, on);
            if (record)
                History.Append(new HistoryRecord { Kind = "react", Id = msgId, FromId = fromId, FromName = name, Ticks = DateTime.Now.Ticks, Mine = _net != null && fromId == _net.SelfId, Text = emoji, On = on });
        }

        // ================================================================ dra & släpp

        void OnDragEnterFiles(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                if (sender == _dropOverlay) _dropTimer.Stop();
            }
            else e.Effect = DragDropEffects.None;
        }

        void OnDragDropFiles(object sender, DragEventArgs e)
        {
            HideDropOverlay();
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) SendFiles(files);
        }

        public void ShowDropOverlay()
        {
            OnUi(delegate
            {
                if (_dropOverlay.Visible || _net == null) return;
                _dropOverlay.Visible = true;
                _dropOverlay.BringToFront();
                _dropTimer.Stop();
                _dropTimer.Start();
            });
        }

        void HideDropOverlay()
        {
            _dropTimer.Stop();
            _dropOverlay.Visible = false;
        }

        // ================================================================ övriga anrop från vyn

        public void SetName(string name)
        {
            if (name == null || name.Trim().Length == 0) return;
            name = Network.CleanName(name);
            bool first = _settings.Name.Trim().Length == 0;
            if (!first && name == _settings.Name) return;
            _settings.Name = name;
            _settings.Save();
            if (_net != null)
            {
                _net.SetName(name);
                Js("addSystem", "Du heter nu " + name + ".");
            }
            PushState();
            if (first || _net == null)
            {
                Js("showApp", false);
                StartNetwork();
            }
        }

        public void ChooseAvatar()
        {
            using (OpenFileDialog d = new OpenFileDialog { Title = "Välj profilbild", Filter = "Bilder|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|Alla filer|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { SetAvatar(MakeAvatar(d.FileName)); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Kunde inte läsa bilden: " + ex.Message, "MiniChat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        public void SetAvatar(byte[] data)
        {
            _avatar = data;
            Settings.SaveAvatar(data);
            if (_net != null) _net.SetAvatar(data);
            PushState();
        }

        /// <summary>Beskär till kvadrat, skala till 96×96 och spara som liten JPEG (några kB).</summary>
        static byte[] MakeAvatar(string path)
        {
            using (Image src = Image.FromFile(path))
            {
                int s = Math.Min(src.Width, src.Height);
                Rectangle crop = new Rectangle((src.Width - s) / 2, (src.Height - s) / 2, s, s);
                using (Bitmap bmp = new Bitmap(96, 96))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.White);
                        g.DrawImage(src, new Rectangle(0, 0, 96, 96), crop, GraphicsUnit.Pixel);
                    }
                    return Jpeg(bmp, 85L);
                }
            }
        }

        public void AddAddress(string addr)
        {
            if (_net == null) return;
            addr = (addr ?? "").Trim();
            if (addr.Length == 0) return;
            _net.AddManual(addr);
            _settings.Manual = _net.ManualAddresses;
            _settings.Save();
        }

        public void ForgetAddresses()
        {
            _settings.Manual = new List<string>();
            _settings.Save();
            if (_net != null) _net.ClearManual();
            Js("toast", "Sparade adresser glömda");
        }

        public void SetPort(string port)
        {
            int p;
            if (!int.TryParse((port ?? "").Trim(), out p) || p < 1 || p > 65535) return;
            _settings.Port = p;
            _settings.Save();
            Js("toast", "Port " + p + " gäller nästa gång MiniChat startas");
        }

        public void Toggle(string key)
        {
            switch (key)
            {
                case "sound": _settings.Sound = !_settings.Sound; if (_settings.Sound) Play(_ding); break;
                case "notify": _settings.Notify = !_settings.Notify; break;
                case "autoAccept": _settings.AutoAccept = !_settings.AutoAccept; if (_net != null) _net.AutoAccept = _settings.AutoAccept; break;
                case "autostart":
                    if (!Startup.Set(!Startup.IsEnabled())) Js("toast", "Kunde inte ändra autostart");
                    break;
            }
            _settings.Save();
            PushSettings();
        }

        public void OpenDownloads()
        {
            try
            {
                string dir = DownloadsDir();
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        public void OpenSoundsFolder()
        {
            try
            {
                string dir = SoundsDir();
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        static string DownloadsDir()
        {
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "MiniChat");
        }

        static string SoundsDir()
        {
            return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ljud");
        }

        /// <summary>Egna ljud: alla .wav/.mp3/.wma i mappen "ljud" bredvid exe-filen. *filnamn* i ett meddelande spelar upp det hos de andra.</summary>
        static Dictionary<string, string> CustomSounds()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            try
            {
                string dir = SoundsDir();
                if (!Directory.Exists(dir)) return d;
                foreach (string f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".wav" && ext != ".mp3" && ext != ".wma") continue;
                    d[Path.GetFileNameWithoutExtension(f).ToLowerInvariant()] = f;
                }
            }
            catch { }
            return d;
        }

        public void ClearChat()
        {
            _lastMsgTime = DateTime.MinValue;
            History.Clear();
            Js("toast", "Chatten och historiken är rensad");
        }

        public void OpenUrl(string url)
        {
            try
            {
                if (url != null && (url.StartsWith("http://") || url.StartsWith("https://"))) Process.Start(url);
            }
            catch { }
        }

        public void OpenTransferFile(string id, bool showInFolder)
        {
            string path = null;
            FileTransfer t = _net == null ? null : _net.GetTransfer(id);
            if (t != null && t.State == "done") path = t.FilePath;
            else _historyFiles.TryGetValue(id, out path);
            if (path == null || !File.Exists(path)) { Js("toast", "Filen finns inte kvar"); return; }
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                bool risky = ext == ".exe" || ext == ".bat" || ext == ".cmd" || ext == ".ps1" || ext == ".vbs" || ext == ".js" || ext == ".msi" || ext == ".scr" || ext == ".com";
                if (showInFolder || risky) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else Process.Start(path);
            }
            catch { }
        }

        public void AcceptTransfer(string id, bool accept)
        {
            if (_net == null) return;
            if (accept) _net.AcceptFile(id); else _net.DeclineFile(id);
        }

        // ================================================================ tillstånd till vyn, tray, ljud

        void PushState()
        {
            Js("setMe", new Json().Add("name", _settings.Name).Add("avatar", _avatar != null ? DataUrl(_avatar) : "").ToString());
            string cloud = _settings.CloudCode.Length == 0 ? "av" : (_net == null || !_net.CloudEnabled) ? "av" : _net.CloudConnected ? "ansluten" : "ansluter…";
            Js("setInfo", new Json()
                .Add("ip", string.Join(", ", Network.LocalAddresses().ToArray()))
                .Add("port", (_net != null ? _net.ListenPort : _settings.Port).ToString())
                .Add("cloud", cloud).Add("cloudCode", _settings.CloudCode).Add("cloudServer", _settings.CloudServer).ToString());
            Js("setMyStatus", new Json().Add("status", _effStatus).Add("chosen", _settings.Status).ToString());
            PushSettings();
        }

        void PushSettings()
        {
            Js("setSettings", new Json()
                .Add("sound", _settings.Sound).Add("notify", _settings.Notify).Add("autoAccept", _settings.AutoAccept)
                .Add("autostart", Startup.IsEnabled()).Add("hasAvatar", _avatar != null).ToString());
            SyncTray();
        }

        void SyncTray()
        {
            if (_tray == null) return;
            _trSound.Checked = _settings.Sound;
            _trNotify.Checked = _settings.Notify;
            _trAutostart.Checked = Startup.IsEnabled();
            _trOnline.Checked = _settings.Status == "online";
            _trBusy.Checked = _settings.Status == "busy";
            int n = _net != null ? _net.Participants.Count : 0;
            string st = _effStatus == "busy" ? "upptagen" : _effStatus == "away" ? "ej vid datorn" : null;
            string t = _unread > 0 ? "MiniChat – " + _unread + " olästa" : (n == 0 ? "MiniChat – ingen annan online" : "MiniChat – " + n + " online");
            if (st != null) t += " · " + st;
            _tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void UpdateTitle()
        {
            Text = _unread > 0 ? "(" + _unread + ") MiniChat" : "MiniChat";
            SyncTray();
        }

        bool IsReading()
        {
            return Visible && WindowState != FormWindowState.Minimized && Form.ActiveForm == this;
        }

        void MarkRead()
        {
            if (_pendingSeen.Count > 0) { _seenTimer.Stop(); _seenTimer.Start(); }
            if (_unread == 0 && !_blink.Enabled) return;
            _unread = 0;
            StopBlink();
            UpdateTitle();
        }

        void StartBlink()
        {
            if (_blink.Enabled) return;
            _blinkOn = false;
            _blink.Start();
        }

        void StopBlink()
        {
            _blink.Stop();
            _tray.Icon = _icon;
        }

        void Toast(string title, string body)
        {
            body = (body ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (body.Length == 0) body = "(nytt meddelande)";
            if (body.Length > 160) body = body.Substring(0, 157) + "…";
            if (string.IsNullOrEmpty(title)) title = "MiniChat";
            try { _tray.ShowBalloonTip(5000, title, body, ToolTipIcon.None); } catch { }
        }

        void PlayEffect(string sound)
        {
            if (sound == "prutt") Play(_prutt);
            else if (sound == "skratt") Play(_skratt);
        }

        /// <summary>Spelar mottaget ljud. WAV direkt ur minnet; MP3/WMA via Windows MCI från en tillfällig fil.</summary>
        static void PlaySoundData(string ext, byte[] data)
        {
            try
            {
                ext = (ext ?? "").ToLowerInvariant();
                if (ext != ".mp3" && ext != ".wma")
                {
                    SoundPlayer p = new SoundPlayer(new MemoryStream(data));
                    p.Play();
                    return;
                }
                string dir = Path.Combine(Path.GetTempPath(), "MiniChat");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "ljud_" + Guid.NewGuid().ToString("N") + ext);
                File.WriteAllBytes(path, data);
                Mci.Play(path);
            }
            catch { }
        }

        /// <summary>Städar gamla tillfälliga ljud- och urklippsfiler.</summary>
        static void CleanTemp()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "MiniChat");
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir))
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        static void Play(SoundPlayer p)
        {
            try
            {
                if (p != null) p.Play();
                else SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        // ================================================================ hjälp

        void Js(string fn, params object[] args)
        {
            if (!_webReady)
            {
                _jsQueue.Enqueue(new KeyValuePair<string, object[]>(fn, args));
                return;
            }
            try
            {
                if (_web.Document != null) _web.Document.InvokeScript(fn, args);
            }
            catch { }
        }

        void FlushJs()
        {
            while (_jsQueue.Count > 0)
            {
                KeyValuePair<string, object[]> kv = _jsQueue.Dequeue();
                try { if (_web.Document != null) _web.Document.InvokeScript(kv.Key, kv.Value); } catch { }
            }
        }

        bool TakeTimeSeparator(DateTime t)
        {
            bool show = _lastMsgTime == DateTime.MinValue || (t - _lastMsgTime).TotalMinutes > 10;
            if (t > _lastMsgTime) _lastMsgTime = t;
            return show;
        }

        static string TimeText(DateTime t)
        {
            if (t.Date == DateTime.Today) return "Idag " + t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return "Igår " + t.ToString("HH:mm");
            return t.ToString("d MMMM HH:mm", Sv);
        }

        static long Epoch(DateTime t)
        {
            return (long)(t.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        static string SizeText(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024L * 1024) return (n / 1024.0).ToString("0.#", Sv) + " kB";
            if (n < 1024L * 1024 * 1024) return (n / 1048576.0).ToString("0.#", Sv) + " MB";
            return (n / 1073741824.0).ToString("0.##", Sv) + " GB";
        }

        void OnUi(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(a); } catch { }
            }
            else a();
        }
    }

    /// <summary>Spelar mp3/wma med Windows inbyggda MCI (winmm.dll) – inga extra bibliotek.</summary>
    static class Mci
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr hwndCallback);

        static readonly object Lock = new object();
        static int _n;

        public static void Play(string path)
        {
            lock (Lock)
            {
                // Stäng föregående egna ljud om det fortfarande spelar – ett i taget räcker för familjen
                if (_n > 0) mciSendString("close snd" + _n, null, 0, IntPtr.Zero);
                _n++;
                string alias = "snd" + _n;
                int r = mciSendString("open \"" + path + "\" alias " + alias, null, 0, IntPtr.Zero);
                if (r != 0) r = mciSendString("open \"" + path + "\" type mpegvideo alias " + alias, null, 0, IntPtr.Zero);
                if (r != 0) return;
                mciSendString("play " + alias, null, 0, IntPtr.Zero);
            }
        }
    }
}
