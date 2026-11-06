using System;
using System.Threading;
using System.Windows.Forms;

namespace MiniChat
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool startInTray = false, allowMulti = false;
            foreach (string a in args)
            {
                if (a == "--tray" || a == "/tray") startInTray = true;
                if (a == "--multi" || a == "/multi") allowMulti = true; // flera instanser på samma dator (för test)
            }

            // Bara en instans per inloggad användare. Startas en andra, väcker den den första.
            bool createdNew;
            using (Mutex mutex = new Mutex(true, @"Local\MiniChat_Instance", out createdNew))
            using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MiniChat_Show"))
            {
                if (!createdNew && !allowMulti)
                {
                    showEvent.Set();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Startup.EnsureBrowserMode(); // måste ske innan webbläsarkontrollen skapas

                MainForm form = new MainForm(startInTray);
                ThreadPool.RegisterWaitForSingleObject(showEvent, (state, timedOut) =>
                {
                    try
                    {
                        if (form.IsHandleCreated && !form.IsDisposed)
                            form.BeginInvoke((Action)form.ShowFromTray);
                    }
                    catch { }
                }, null, -1, false);

                Application.Run(form);
                GC.KeepAlive(mutex);
            }
        }
    }
}
