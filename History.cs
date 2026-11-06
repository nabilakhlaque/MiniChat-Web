using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MiniChat
{
    /// <summary>En rad i historiken. Kind: msg, file eller react.</summary>
    public class HistoryRecord
    {
        public string Kind;
        public string Id;        // meddelande-/överförings-id (för react: meddelandet som reagerades på)
        public string FromId;
        public string FromName;
        public long Ticks;       // lokal tid
        public bool Mine;
        public string Text;      // msg: texten, react: emoji
        public string FileName;  // file
        public string FilePath;  // file
        public long Size;        // file
        public bool On;          // react
        public DateTime Time { get { return new DateTime(Ticks); } }
    }

    /// <summary>
    /// Lokal chatthistorik. Varje post krypteras för sig med Windows DPAPI (knuten till den
    /// inloggade Windows-användaren – ingen nyckel att hantera) och läggs till sist i filen.
    /// Vid start rensas allt äldre än 30 dagar och allt utöver de 2000 senaste posterna.
    /// </summary>
    public static class History
    {
        const int MaxDays = 30;
        const int MaxRecords = 2000;
        const char Sep = '\u001f';
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MiniChat-history-v1");
        static readonly object Lock = new object();

        static string FilePath { get { return Path.Combine(Settings.Folder, "history.dat"); } }

        public static List<HistoryRecord> Load()
        {
            List<HistoryRecord> list = new List<HistoryRecord>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                DateTime oldest = DateTime.Now.AddDays(-MaxDays);
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    if (line.Length == 0) continue;
                    HistoryRecord r = Decode(line);
                    if (r == null || r.Time < oldest) continue;
                    list.Add(r);
                }
                bool pruned = list.Count > MaxRecords;
                if (pruned) list.RemoveRange(0, list.Count - MaxRecords);
                // Skriv om filen så att gammalt försvinner från disken också
                Rewrite(list);
            }
            catch { }
            return list;
        }

        public static void Append(HistoryRecord r)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Settings.Folder);
                    File.AppendAllText(FilePath, Encode(r) + Environment.NewLine);
                }
            }
            catch { }
        }

        public static void Clear()
        {
            try { lock (Lock) if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }

        static void Rewrite(List<HistoryRecord> list)
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Settings.Folder);
                StringBuilder sb = new StringBuilder();
                foreach (HistoryRecord r in list) sb.Append(Encode(r)).Append(Environment.NewLine);
                File.WriteAllText(FilePath, sb.ToString());
            }
        }

        static string Encode(HistoryRecord r)
        {
            string[] f =
            {
                r.Kind ?? "", r.Id ?? "", r.FromId ?? "", r.FromName ?? "", r.Ticks.ToString(), r.Mine ? "1" : "0",
                r.Text ?? "", r.FileName ?? "", r.FilePath ?? "", r.Size.ToString(), r.On ? "1" : "0"
            };
            for (int i = 0; i < f.Length; i++) f[i] = f[i].Replace(Sep, ' ');
            byte[] plain = Encoding.UTF8.GetBytes(string.Join(Sep.ToString(), f));
            byte[] boxed = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(boxed);
        }

        static HistoryRecord Decode(string line)
        {
            try
            {
                byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(line), Entropy, DataProtectionScope.CurrentUser);
                string[] f = Encoding.UTF8.GetString(plain).Split(Sep);
                if (f.Length < 11) return null;
                HistoryRecord r = new HistoryRecord
                {
                    Kind = f[0], Id = f[1], FromId = f[2], FromName = f[3], Mine = f[5] == "1",
                    Text = f[6], FileName = f[7], FilePath = f[8], On = f[10] == "1"
                };
                long.TryParse(f[4], out r.Ticks);
                long.TryParse(f[9], out r.Size);
                if (r.Ticks <= 0 || r.Ticks >= DateTime.MaxValue.Ticks) return null;
                return r;
            }
            catch { return null; }
        }
    }
}
