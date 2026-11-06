using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MiniChat
{
    /// <summary>Sparas som enkel text i %APPDATA%\MiniChat\settings.txt. Profilbilden ligger bredvid som avatar.jpg.</summary>
    public class Settings
    {
        public string Id = "";          // beständigt id så att historik och profilbilder hänger ihop mellan starter
        public string Name = "";
        public string Status = "online"; // vald status: online eller busy
        public bool Sound = true;
        public bool Notify = true;
        public bool AutoAccept = true;
        public int Port = Network.DefaultTcpPort;
        public List<string> Manual = new List<string>();
        public string CloudCode = "";                        // familjekod för molnbryggan (tom = av)
        public string CloudServer = CloudBridge.DefaultServer;

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniChat"); }
        }

        static string FilePath { get { return Path.Combine(Folder, "settings.txt"); } }
        public static string AvatarPath { get { return Path.Combine(Folder, "avatar.jpg"); } }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                foreach (string raw in File.Exists(FilePath) ? File.ReadAllLines(FilePath) : new string[0])
                {
                    int i = raw.IndexOf('=');
                    if (i <= 0) continue;
                    string k = raw.Substring(0, i).Trim();
                    string v = raw.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "id": s.Id = v; break;
                        case "name": s.Name = v; break;
                        case "status": s.Status = Network.CleanStatus(v); break;
                        case "sound": s.Sound = v == "1"; break;
                        case "notify": s.Notify = v == "1"; break;
                        case "autoaccept": s.AutoAccept = v == "1"; break;
                        case "port":
                            {
                                int p;
                                if (int.TryParse(v, out p) && p > 0 && p < 65536) s.Port = p;
                                break;
                            }
                        case "manual":
                            s.Manual = v.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
                            break;
                        case "cloudcode": s.CloudCode = v; break;
                        case "cloudserver": if (v.Length > 0) s.CloudServer = v; break;
                    }
                }
            }
            catch { }
            if (s.Id.Length == 0) { s.Id = Guid.NewGuid().ToString("N"); s.Save(); }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(FilePath, new[]
                {
                    "id=" + Id,
                    "name=" + Name,
                    "status=" + Status,
                    "sound=" + (Sound ? "1" : "0"),
                    "notify=" + (Notify ? "1" : "0"),
                    "autoaccept=" + (AutoAccept ? "1" : "0"),
                    "port=" + Port,
                    "manual=" + string.Join(";", Manual.ToArray()),
                    "cloudcode=" + CloudCode,
                    "cloudserver=" + CloudServer
                });
            }
            catch { }
        }

        public static byte[] LoadAvatar()
        {
            try { return File.Exists(AvatarPath) ? File.ReadAllBytes(AvatarPath) : null; } catch { return null; }
        }

        public static void SaveAvatar(byte[] data)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                if (data == null) { if (File.Exists(AvatarPath)) File.Delete(AvatarPath); }
                else File.WriteAllBytes(AvatarPath, data);
            }
            catch { }
        }
    }

    /// <summary>Autostart via HKCU\...\Run – kräver inga administratörsrättigheter.</summary>
    static class Startup
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "MiniChat";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                    return k != null && k.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static bool Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (k == null) return false;
                    if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Får den inbyggda webbläsarkontrollen att köra i IE11-läge (annars IE7-läge utan
        /// rundade hörn och färgemojis). Per användare, inga adminrättigheter.
        /// </summary>
        public static void EnsureBrowserMode()
        {
            try
            {
                string exe = Path.GetFileName(Application.ExecutablePath);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    if (k == null) return;
                    object cur = k.GetValue(exe);
                    if (!(cur is int) || (int)cur != 11001) k.SetValue(exe, 11001, RegistryValueKind.DWord);
                }
            }
            catch { }
        }
    }
}
