using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MiniChat
{
    /// <summary>
    /// Samlad aktivitetslogg: nätverk, molnbrygga, filer och fel. Hålls i minnet (senaste 600 raderna)
    /// och skrivs till %APPDATA%\MiniChat\minichat.log (rullande, ca 300 kB). Visas under kugghjulet.
    /// Inga meddelandetexter loggas – bara händelser.
    /// </summary>
    public static class AppLog
    {
        const int MaxLines = 600;
        const long MaxFileBytes = 300 * 1024;
        static readonly object Lock = new object();
        static readonly List<string> Lines = new List<string>();
        static bool _loaded;

        public static event Action<string> LineAdded;

        public static string FilePath { get { return Path.Combine(Settings.Folder, "minichat.log"); } }

        public static void Write(string category, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Pad(category) + "  " + (message ?? "").Replace("\r", " ").Replace("\n", " ");
            lock (Lock)
            {
                EnsureLoaded();
                Lines.Add(line);
                if (Lines.Count > MaxLines) Lines.RemoveRange(0, Lines.Count - MaxLines);
                try
                {
                    Directory.CreateDirectory(Settings.Folder);
                    FileInfo fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxFileBytes)
                    {
                        // behåll ungefär senaste hälften
                        string[] all = File.ReadAllLines(FilePath);
                        string[] keep = new string[all.Length / 2];
                        Array.Copy(all, all.Length - keep.Length, keep, 0, keep.Length);
                        File.WriteAllLines(FilePath, keep, Encoding.UTF8);
                    }
                    File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
            Action<string> ev = LineAdded;
            if (ev != null) { try { ev(line); } catch { } }
        }

        /// <summary>Senaste raderna, äldst först.</summary>
        public static List<string> Recent(int count)
        {
            lock (Lock)
            {
                EnsureLoaded();
                int n = Math.Min(count, Lines.Count);
                return Lines.GetRange(Lines.Count - n, n);
            }
        }

        public static string AllText()
        {
            lock (Lock)
            {
                EnsureLoaded();
                return string.Join(Environment.NewLine, Lines.ToArray());
            }
        }

        public static void Clear()
        {
            lock (Lock)
            {
                Lines.Clear();
                _loaded = true;
                try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            }
        }

        // Läs in svansen av filen vid första användning så att loggen överlever omstarter
        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                string[] all = File.ReadAllLines(FilePath, Encoding.UTF8);
                int start = Math.Max(0, all.Length - 300);
                for (int i = start; i < all.Length; i++) if (all[i].Length > 0) Lines.Add(all[i]);
                Lines.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Pad("app") + "  ── ny start ──");
            }
            catch { }
        }

        static string Pad(string c)
        {
            c = (c ?? "").ToLowerInvariant();
            return c.Length >= 6 ? c.Substring(0, 6) : c.PadRight(6);
        }
    }
}
