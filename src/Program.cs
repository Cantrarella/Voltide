using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Voltide.Core;
using Voltide.UI;

namespace Voltide
{
    public static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        /// <summary>
        /// Entry point usable from an in-memory compiled assembly
        /// (Add-Type ... then [Voltide.Program]::Run(@('--show'))).
        /// </summary>
        public static void Run(string[] args)
        {
            Main(args != null ? args : new string[0]);
        }

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                if (args != null && args.Length > 0 && args[0] == "--test")
                {
                    bool demoTest = args.Length > 1 && args[1] == "--demo";
                    RunTest(demoTest);
                    return;
                }

                if (args != null && args.Length > 0 && args[0] == "--make-icon")
                {
                    string outPath = args.Length > 1 ? args[1] : "app.ico";
                    MakeIcon(outPath);
                    return;
                }

                bool autoShow = false;
                bool demo = false;
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] == "--show") autoShow = true;
                        if (args[i] == "--demo") { demo = true; autoShow = true; }
                    }
                }

                // A copy renamed to *demo*.exe enters demo mode just by being
                // double-clicked — no command-line arguments, no shortcut, no batch
                // file. That dodges every cmd.exe argument-quoting pitfall.
                if (!demo)
                {
                    string exeName = null;
                    try { exeName = System.IO.Path.GetFileName(Application.ExecutablePath); }
                    catch { }
                    if (!string.IsNullOrEmpty(exeName) &&
                        exeName.IndexOf("demo", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        demo = true;
                        autoShow = true;
                    }
                }

                bool createdNew;
                // Demo and normal builds get separate mutexes so a demo instance
                // can run alongside (or instead of) a real one without either being
                // silently dropped.
                string mutexName = demo ? "Voltide-SingleInstance-Demo" : "Voltide-SingleInstance";
                using (Mutex mutex = new Mutex(true, mutexName, out createdNew))
                {
                    if (!createdNew)
                    {
                        return;
                    }

                    SetProcessDPIAware();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                    {
                        Log("UI thread: " + e.Exception);
                    };

                    AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                    {
                        Log("Domain: " + e.ExceptionObject);
                    };

                    using (TrayApp app = new TrayApp(demo))
                    {
                        if (autoShow) app.ShowPanel();
                        Application.Run();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Main: " + ex);
                throw;
            }
        }

        private static void Log(string msg)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "Voltide-error.txt");
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static void RunTest(bool demo)
        {
            DeviceManager m = new DeviceManager(demo);
            m.Refresh();

            List<DeviceSnapshot> snaps = m.Snapshot();
            Console.WriteLine("devices: " + snaps.Count);

            foreach (DeviceSnapshot s in snaps)
            {
                Console.WriteLine("----------------------------------------");
                Console.WriteLine("name    : " + s.Info.Name);
                Console.WriteLine("brand   : " + s.Info.Brand);
                Console.WriteLine("model   : " + s.Info.Model);
                Console.WriteLine("vid/pid : " + s.Info.VendorId.ToString("X4") + ":" + s.Info.ProductId.ToString("X4"));
                Console.WriteLine("path    : " + s.Info.HidPath);
                Console.WriteLine("online  : " + s.Reading.Online);
                Console.WriteLine("percent : " + (s.Reading.Percent.HasValue ? s.Reading.Percent.Value.ToString() + "%" : "null"));
                Console.WriteLine("charging: " + s.Reading.Charging);
                Console.WriteLine("detail  : " + s.Reading.Detail);
                Console.WriteLine("raw     : " + s.Reading.Raw);
                Console.WriteLine("error   : " + s.Reading.Error);
            }
        }

        private static void MakeIcon(string outPath)
        {
            SetProcessDPIAware();
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            UI.IconFactory.WriteAppIco(outPath, sizes);
            Console.WriteLine("wrote " + outPath);
        }
    }
}
