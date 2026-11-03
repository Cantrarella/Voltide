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
                    RunTest();
                    return;
                }

                if (args != null && args.Length > 0 && args[0] == "--make-icon")
                {
                    string outPath = args.Length > 1 ? args[1] : "app.ico";
                    MakeIcon(outPath);
                    return;
                }

                bool autoShow = false;
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] == "--show") autoShow = true;
                    }
                }

                bool createdNew;
                using (Mutex mutex = new Mutex(true, "Voltide-SingleInstance", out createdNew))
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

                    using (TrayApp app = new TrayApp())
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

        private static void RunTest()
        {
            DeviceManager m = new DeviceManager();
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
            int[] sizes = new int[] { 16, 20, 24, 32, 48, 64, 128, 256 };
            UI.IconFactory.WriteAppIco(outPath, sizes);
            Console.WriteLine("wrote " + outPath);
        }
    }
}
