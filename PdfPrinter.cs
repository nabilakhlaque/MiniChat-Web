using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MiniChat
{
    /// <summary>
    /// Tyst PDF-utskrift. Windows saknar inbyggd sådan, så vi använder i tur och ordning:
    /// 1) SumatraPDF (portabel exe bredvid MiniChat.exe eller vanlig installation) – helt tyst och pålitlig
    /// 2) Adobe Reader/Acrobat (/t)
    /// 3) Windows "printto"-kommando för den PDF-läsare som är kopplad till .pdf
    /// </summary>
    public static class PdfPrinter
    {
        public const string SumatraUrl = "https://www.sumatrapdfreader.org/download-free-pdf-viewer";

        public static List<string> Printers()
        {
            List<string> list = new List<string>();
            try { foreach (string p in PrinterSettings.InstalledPrinters) list.Add(p); } catch { }
            return list;
        }

        public static string DefaultPrinter()
        {
            try { return new PrinterSettings().PrinterName; } catch { return ""; }
        }

        public static string FindSumatra()
        {
            List<string> dirs = new List<string>();
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            dirs.Add(exeDir);
            dirs.Add(Path.Combine(exeDir, "SumatraPDF"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SumatraPDF"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SumatraPDF"));
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(pf86)) dirs.Add(Path.Combine(pf86, "SumatraPDF"));
            foreach (string d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    string exact = Path.Combine(d, "SumatraPDF.exe");
                    if (File.Exists(exact)) return exact;
                    // Portabla nedladdningen heter t.ex. SumatraPDF-3.5.2-64.exe
                    string any = Directory.GetFiles(d, "SumatraPDF*.exe").OrderByDescending(x => x).FirstOrDefault();
                    if (any != null) return any;
                }
                catch { }
            }
            return null;
        }

        static string FindAdobe()
        {
            foreach (string exe in new[] { "AcroRd32.exe", "Acrobat.exe" })
            {
                foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    foreach (string sub in new[] { @"Software\Microsoft\Windows\CurrentVersion\App Paths\", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" })
                    {
                        try
                        {
                            using (RegistryKey k = root.OpenSubKey(sub + exe))
                            {
                                string path = k != null ? k.GetValue(null) as string : null;
                                if (!string.IsNullOrEmpty(path))
                                {
                                    path = path.Trim('"');
                                    if (File.Exists(path)) return path;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            return null;
        }

        /// <summary>Vad som kommer att användas – visas i inställningarna.</summary>
        public static string EngineText(out bool good)
        {
            string s = FindSumatra();
            if (s != null) { good = true; return "SumatraPDF hittad – utskrifter sker helt tyst."; }
            string a = FindAdobe();
            if (a != null) { good = true; return "Adobe " + (a.EndsWith("Acrobat.exe", StringComparison.OrdinalIgnoreCase) ? "Acrobat" : "Reader") + " används. Fungerar, men lägg gärna SumatraPDF bredvid MiniChat.exe för helt tyst utskrift."; }
            good = false;
            return "Ingen PDF-läsare som kan skriva ut automatiskt hittades. Ladda ner SumatraPDF (portabel version) och lägg exe-filen bredvid MiniChat.exe.";
        }

        public static bool LooksLikePdf(string path)
        {
            try
            {
                if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)) return false;
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] b = new byte[1024];
                    int n = fs.Read(b, 0, b.Length);
                    // "%PDF-" ska finnas i början (tillåt lite skräp före, som specifikationen gör)
                    for (int i = 0; i + 4 < n; i++)
                        if (b[i] == '%' && b[i + 1] == 'P' && b[i + 2] == 'D' && b[i + 3] == 'F' && b[i + 4] == '-') return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Skriver ut och blockerar tills det är klart. Returnerar null om det gick bra, annars ett felmeddelande.</summary>
        public static string Print(string pdf, string printer, out string usedPrinter)
        {
            usedPrinter = printer;
            if (!File.Exists(pdf)) return "filen finns inte";
            if (!LooksLikePdf(pdf)) return "filen är ingen PDF";

            List<string> installed = Printers();
            if (string.IsNullOrEmpty(printer) || !installed.Contains(printer))
            {
                if (!string.IsNullOrEmpty(printer)) AppLog.Write("skrivare", "vald skrivare \"" + printer + "\" finns inte – använder standardskrivaren");
                printer = DefaultPrinter();
                usedPrinter = printer;
            }
            if (string.IsNullOrEmpty(printer)) return "ingen skrivare installerad";

            string sumatra = FindSumatra();
            if (sumatra != null)
            {
                AppLog.Write("skrivare", "SumatraPDF skriver ut " + Path.GetFileName(pdf) + " på " + printer);
                string args = "-print-to " + Quote(printer) + " -silent " + Quote(pdf);
                return Run(sumatra, args, 180000, true);
            }

            string adobe = FindAdobe();
            if (adobe != null)
            {
                AppLog.Write("skrivare", "Adobe skriver ut " + Path.GetFileName(pdf) + " på " + printer);
                // /t skriver ut till angiven skrivare. Adobe stänger sig inte alltid själv, så vi väntar bara en stund.
                string r = Run(adobe, "/n /s /h /t " + Quote(pdf) + " " + Quote(printer), 30000, false);
                return r;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(pdf) { UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                string[] verbs = psi.Verbs ?? new string[0];
                if (verbs.Any(v => string.Equals(v, "printto", StringComparison.OrdinalIgnoreCase)))
                {
                    AppLog.Write("skrivare", "Windows printto skriver ut " + Path.GetFileName(pdf) + " på " + printer);
                    psi.Verb = "printto";
                    psi.Arguments = Quote(printer);
                    using (Process.Start(psi)) { }
                    return null;
                }
            }
            catch (Exception ex)
            {
                return "Windows kunde inte skriva ut: " + ex.Message;
            }
            return "ingen PDF-läsare som kan skriva ut automatiskt (lägg SumatraPDF.exe bredvid MiniChat.exe)";
        }

        static string Run(string exe, string args, int timeoutMs, bool trustExitCode)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return "kunde inte starta " + Path.GetFileName(exe);
                    if (!p.WaitForExit(timeoutMs))
                    {
                        if (trustExitCode) { try { p.Kill(); } catch { } return "utskriften tog för lång tid"; }
                        return null; // Adobe ligger kvar i bakgrunden – jobbet är redan skickat
                    }
                    if (trustExitCode && p.ExitCode != 0) return Path.GetFileNameWithoutExtension(exe) + " avslutade med felkod " + p.ExitCode;
                    return null;
                }
            }
            catch (Exception ex)
            {
                return "kunde inte starta " + Path.GetFileName(exe) + ": " + ex.Message;
            }
        }

        static string Quote(string s) { return "\"" + s.Replace("\"", "") + "\""; }
    }
}
