using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// Razer 2.4G receivers (Viper Ultimate and siblings).
    ///
    /// The receiver exposes a 91-byte Feature Report control channel. The wire
    /// format is the 90-byte razer_report known from OpenRazer:
    ///
    ///   byte 0      status        (0 new, 1 busy, 2 success, 3 failure, 4 timeout, 5 unsupported)
    ///   byte 1      transaction id (1 byte, replies must be matched against it)
    ///   byte 2..3   remaining packets
    ///   byte 4      protocol type
    ///   byte 5      data size
    ///   byte 6      command class
    ///   byte 7      command id
    ///   byte 8..87  arguments
    ///   byte 88     CRC = XOR of bytes 2..87
    ///   byte 89     reserved
    ///
    /// Windows prepends the report id (0x00), hence the 91-byte window buffer.
    /// Battery query is class 0x07 / id 0x80; arguments[1] is the level, either
    /// 0..100 percent or a 0..255 raw value (the Viper Ultimate reports 132 for
    /// about half charge, so anything above 100 is scaled by 255).
    ///
    /// Two hardware quirks drive the code below:
    ///   - The mouse collection refuses GENERIC_READ, so the handle is opened
    ///     write-only and replies are pulled with HidD_GetFeature (control
    ///     transfers are not blocked, only the interrupt pipe).
    ///   - A sleeping mouse swallows the query; the receiver only answers with
    ///     its own class 0x0F status frames. Re-sending every 250 ms wakes it.
    /// </summary>
    public class RazerProvider : IDeviceProvider
    {
        private const ushort RazerVid = 0x1532;
        private const int ReportLength = 91;
        private const byte StatusOk = 0x02;
        private const byte StatusFailure = 0x03;
        private const byte StatusTimeout = 0x04;
        private const byte StatusUnsupported = 0x05;

        // Razer ships headsets, docks and wired mice that expose the same 91-byte
        // control channel but have no battery. Wireless receivers are listed
        // explicitly; anything unknown is probed once (a sleeping mouse simply
        // stays silent, which counts as "keep it").
        private static readonly HashSet<ushort> WirelessPids = new HashSet<ushort>
        {
            0x007B, // Viper Ultimate (receiver)
            0x00C1, // Viper V3 Pro (receiver)
            0x00DF, // Viper V3 Pro SE (receiver)
            0x00B7, // DeathAdder V3 Pro (receiver)
            0x00C3, // DeathAdder V3 Pro alt (receiver)
            0x00BF, // DeathAdder V4 Pro (receiver)
            0x00E6, // Viper V4 Pro (receiver)
            0x00AB, // Basilisk V3 Pro (receiver)
            0x00CD, // Basilisk V3 Pro 35K (receiver)
            0x00D7, // Basilisk V3 Pro 35K Phantom (receiver)
            0x00C5, // DeathAdder V3 HyperSpeed (receiver)
            0x00D1, // Pro Click V2 (receiver)
            0x00C8, // Pro Click V2 Vertical (receiver)
            0x00D4, // Basilisk Mobile (receiver)
            0x00B0, // Cobra Pro (receiver)
            0x00DB, // Cobra HyperSpeed (receiver)
            0x00B4, // Naga V2 HyperSpeed (receiver)
            0x00B8, // Viper V3 HyperSpeed
            0x00B9, // Basilisk V3 X HyperSpeed
            0x00B3, // HyperPolling Wireless Dongle
            0x009A, // Pro Click Mini (receiver)
            0x005A, // Lancehead (receiver)
            0x0045  // Mamba Chroma (receiver)
        };

        private static readonly Dictionary<string, bool> ProbeCache = new Dictionary<string, bool>();

        public string Name { get { return "Razer HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != RazerVid) continue;
                if (h.FeatureReportLength != ReportLength) continue;   // the control channel

                string key = h.ProductId.ToString("X4");
                if (!candidates.ContainsKey(key)) candidates.Add(key, h);
            }

            foreach (KeyValuePair<string, HidDeviceInfo> kv in candidates)
            {
                HidDeviceInfo h = kv.Value;
                if (!ShouldInclude(h)) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "razer-" + kv.Key;
                d.Brand = "Razer";
                d.Model = ResolveModel(h.ProductId, h.ProductName);
                d.Name = d.Model;
                d.ShortModel = "Razer " + d.Model;
                d.Kind = DeviceKind.Mouse;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                found.Add(d);
            }

            return found;
        }

        /// <summary>
        /// Known wireless receivers pass straight through. Anything else gets one
        /// battery query: an explicit "not supported" reply drops it, silence
        /// (sleeping device) keeps it.
        /// </summary>
        private static bool ShouldInclude(HidDeviceInfo h)
        {
            if (WirelessPids.Contains(h.ProductId)) return true;

            lock (ProbeCache)
            {
                if (ProbeCache.ContainsKey(h.Path)) return ProbeCache[h.Path];
            }

            bool keep = ProbeBattery(h.Path) == 1;
            lock (ProbeCache) { ProbeCache[h.Path] = keep; }
            return keep;
        }

        /// <summary>1 = answers with a value, 0 = explicitly unsupported, -1 = silent.</summary>
        private static int ProbeBattery(string path)
        {
            IntPtr handle = HidNative.CreateFile(path, HidNative.GENERIC_WRITE,
                HidNative.FILE_SHARE_RW, IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return -1;

            try
            {
                byte[] request = BuildReport(0x07, 0x80, new byte[] { 0x00, 0x00 }, 0xEE);
                if (!HidNative.HidD_SetFeature(handle, request, ReportLength)) return -1;

                byte[] reply = WaitFor(handle, 0xEE, 200);
                if (reply == null) return -1;
                if (reply[0] == StatusOk && reply[5] >= 2) return 1;
                return 0;
            }
            finally
            {
                HidNative.CloseHandle(handle);
            }
        }

        public DeviceReading Read(DeviceInfo d)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;

            IntPtr handle = HidNative.CreateFile(
                d.HidPath,
                HidNative.GENERIC_WRITE,          // read access is refused for mouse collections
                HidNative.FILE_SHARE_RW,
                IntPtr.Zero,
                HidNative.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle == new IntPtr(-1))
            {
                r.Error = "打开失败（错误 " + Marshal.GetLastWin32Error() + "）";
                return r;
            }

            try
            {
                byte lastStatus = 0;

                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (attempt > 0) System.Threading.Thread.Sleep(120);

                    byte tx = (byte)(0x20 + attempt);
                    byte[] request = BuildReport(0x07, 0x80, new byte[] { 0x00, 0x00 }, tx);

                    if (!HidNative.HidD_SetFeature(handle, request, ReportLength))
                    {
                        r.Error = "发送失败（错误 " + Marshal.GetLastWin32Error() + "）";
                        return r;
                    }

                    byte[] reply = WaitFor(handle, tx, 160);
                    if (reply == null) continue;

                    lastStatus = reply[0];

                    if (reply[0] == StatusFailure || reply[0] == StatusTimeout)
                    {
                        r.Sleeping = true;
                        r.Error = "设备未应答";
                        continue;
                    }
                    if (reply[0] == StatusUnsupported)
                    {
                        r.Error = "该型号不支持电量查询";
                        return r;
                    }
                    if (reply[0] != StatusOk) continue;
                    if (reply[5] < 2)
                    {
                        r.Error = "应答长度异常";
                        return r;
                    }

                    int raw = reply[8 + 1];
                    int percent = raw <= 100 ? raw : (int)Math.Round(raw / 255.0 * 100.0);
                    if (percent > 100) percent = 100;
                    if (percent < 0) percent = 0;

                    r.Percent = percent;
                    r.Level = BatteryMath.FromPercent(percent);
                    r.Charging = reply[8] != 0;
                    r.Online = true;
                    r.Sleeping = false;
                    r.Error = null;
                    r.Detail = "2.4G · 雷蛇协议";
                    r.Raw = Hex(reply, 14);
                    return r;
                }

                if (lastStatus == 0)
                {
                    r.Sleeping = true;
                    r.Error = "设备休眠";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            finally
            {
                HidNative.CloseHandle(handle);
            }

            return r;
        }

        private static byte[] BuildReport(byte cls, byte cmd, byte[] args, byte tx)
        {
            byte[] buf = new byte[ReportLength];
            buf[0] = 0x00;                 // report id
            buf[1] = 0x00;                 // status: new command
            buf[2] = tx;                   // transaction id
            buf[3] = 0x00;                 // remaining packets hi
            buf[4] = 0x00;                 // remaining packets lo
            buf[5] = 0x00;                 // protocol type
            buf[6] = (byte)args.Length;    // data size
            buf[7] = cls;
            buf[8] = cmd;
            for (int i = 0; i < args.Length; i++) buf[9 + i] = args[i];

            byte crc = 0;
            for (int i = 3; i <= 88; i++) crc ^= buf[i];
            buf[89] = crc;

            return buf;
        }

        /// <summary>
        /// Pull reports until one belongs to our transaction. The receiver also
        /// queues its own status frames here, so mismatches are simply dropped.
        /// </summary>
        private static byte[] WaitFor(IntPtr handle, byte tx, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            byte[] buf = new byte[ReportLength];

            while (Environment.TickCount < deadline)
            {
                if (HidNative.HidD_GetFeature(handle, buf, ReportLength))
                {
                    byte status = buf[1];
                    bool terminal = status == StatusOk || status == StatusFailure ||
                                    status == StatusTimeout || status == StatusUnsupported;
                    if (buf[2] == tx && terminal)
                    {
                        byte[] reply = new byte[90];
                        Array.Copy(buf, 1, reply, 0, 90);
                        return reply;
                    }
                }
                System.Threading.Thread.Sleep(8);
            }
            return null;
        }

        private static string Hex(byte[] b, int count)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < count && i < b.Length; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        private static string ResolveModel(ushort pid, string productName)
        {
            switch (pid)
            {
                case 0x007A: return "VIPER ULT";
                case 0x007B: return "VIPER ULT";
                case 0x00C0: return "VIPER V3";
                case 0x00C1: return "VIPER V3";
                case 0x00DE: return "VIPER V3 SE";
                case 0x00DF: return "VIPER V3 SE";
                case 0x00BF: return "DA V4";
                case 0x00BE: return "DA V4";
                default:
                    string name = productName;
                    if (!string.IsNullOrEmpty(name))
                    {
                        if (name.StartsWith("Razer ")) name = name.Substring(6);
                        if (name.EndsWith(" Dongle")) name = name.Substring(0, name.Length - 7);
                        if (name.EndsWith(" Receiver")) name = name.Substring(0, name.Length - 9);
                        if (name.Length > 0) return name;
                    }
                    return "Mouse";
            }
        }
    }
}
