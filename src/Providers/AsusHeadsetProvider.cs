using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// ASUS ROG headsets on their 2.4 GHz dongle.
    ///
    /// Protocol cross-checked against G-Helper's AsusHeadset (app/Peripherals/Headset),
    /// which is verified on hardware for the Delta II:
    ///   64-byte frame on the vendor collection (UsagePage 0xFF00), report id 0xCC.
    ///
    ///   query   : CC 12 07 00 00 ...            (64 bytes)
    ///   reply   : CC 12 07 05 4D 14 01 ...      (64 bytes, echoed on the IN endpoint)
    ///                  ^^  ^^  ^^  ^^
    ///                  |   |   |   +-- byte 8 : low-battery prompt on/off
    ///                  |   |   +------ byte 7 : low-battery warning level (%)
    ///                  |   +---------- byte 6 : charge percent   <-- what we want
    ///                  +-------------- byte 5 : sleep timer (minutes)
    ///
    ///   charging: CC 12 08 -> reply byte 5 == 1 while charging
    ///
    /// A firmware NAK comes back as .. FF AA .. and must be discarded. Replies for
    /// a different command can arrive late from the dongle, so the read loop has to
    /// match on the family/command bytes instead of trusting the first frame it sees.
    /// </summary>
    public class AsusHeadsetProvider : IDeviceProvider
    {
        private const ushort AsusVid = 0x0B05;
        private const ushort VendorPage = 0xFF00;
        private const int MinFrame = 64;

        private const byte ReportId = 0xCC;
        private const byte Family = 0x12;
        private const byte CmdBattery = 0x07;
        private const byte CmdCharging = 0x08;

        private const int BatteryIndex = 6;
        private const int ChargingIndex = 5;

        // PIDs already confirmed elsewhere (G-Helper is verified on hardware for the
        // Delta II; Pelta values come from ASUS' own device list).
        private static readonly Dictionary<ushort, string> KnownModels = new Dictionary<ushort, string>
        {
            { 0x1AFA, "DELTA II" },
            { 0x1D41, "DELTA II KJP" },
            { 0x1B82, "PELTA" },
            { 0x1B84, "PELTA" },
        };

        // Anything from ASUS that announces itself as a headset is worth a try: the
        // frame layout above is shared across the current SpeedNova generation.
        private static readonly Regex HeadsetName = new Regex(
            "DELTA|PELTA|CETRA|CLAVIS|STRIX\\s*GO|HEADSET|HATSUNE",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public string Name { get { return "ROG Audio"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            Dictionary<ushort, HidDeviceInfo> best = new Dictionary<ushort, HidDeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != AsusVid) continue;
                if (h.UsagePage != VendorPage) continue;
                if (h.InputReportLength < MinFrame) continue;
                if (h.OutputReportLength < MinFrame) continue;
                if (!IsHeadset(h)) continue;

                HidDeviceInfo current;
                if (best.TryGetValue(h.ProductId, out current))
                {
                    // Several collections can answer the same PID; keep the widest one.
                    if (h.InputReportLength + h.OutputReportLength <=
                        current.InputReportLength + current.OutputReportLength) continue;
                }
                best[h.ProductId] = h;
            }

            foreach (KeyValuePair<ushort, HidDeviceInfo> kv in best)
            {
                HidDeviceInfo h = kv.Value;
                string model = ResolveModel(h.ProductId, h.ProductName);

                DeviceInfo d = new DeviceInfo();
                d.Id = "asus-audio-" + h.ProductId.ToString("X4");
                d.Brand = "ROG";
                d.Model = model;
                d.Name = model.StartsWith("ROG", StringComparison.OrdinalIgnoreCase)
                    ? model
                    : "ROG " + model;
                d.ShortModel = model;
                d.Kind = DeviceKind.Headset;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;

                found.Add(d);
            }

            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Level = BatteryLevel.Unknown;
            r.Detail = "2.4G · ROG 音频";

            int frameIn = MinFrame;
            int frameOut = MinFrame;

            try
            {
                HidDeviceInfo caps = FindCaps(device);
                if (caps != null)
                {
                    frameIn = Math.Max(MinFrame, caps.InputReportLength);
                    frameOut = Math.Max(MinFrame, caps.OutputReportLength);
                }

                using (HidSession s = new HidSession(device.HidPath, frameIn, frameOut, false))
                {
                    s.Flush(30);

                    byte[] battery = Query(s, frameIn, frameOut, CmdBattery);
                    if (battery == null)
                    {
                        r.Sleeping = true;
                        r.Error = "耳机未应答（可能已关机）";
                        return r;
                    }

                    int percent = battery[BatteryIndex];
                    if (percent < 0 || percent > 100)
                    {
                        r.Online = true;
                        r.Error = "读数异常";
                        return r;
                    }

                    r.Raw = BitConverter.ToString(battery, 0, 12);
                    r.Online = true;
                    r.Percent = percent;
                    r.Level = BatteryMath.FromPercent(percent);

                    byte[] charging = Query(s, frameIn, frameOut, CmdCharging);
                    if (charging != null) r.Charging = charging[ChargingIndex] == 1;

                    return r;
                }
            }
            catch (Exception ex)
            {
                r.Sleeping = true;
                r.Error = "耳机未应答（可能已关机）";
                r.Detail = ex.Message;
                return r;
            }
        }

        /// <summary>
        /// The catalog is not handed to Read(), so the report lengths are looked up
        /// again here. A missing device just means the defaults are used.
        /// </summary>
        private static HidDeviceInfo FindCaps(DeviceInfo device)
        {
            try
            {
                foreach (HidDeviceInfo h in HidNative.Enumerate())
                {
                    if (h.Path != device.HidPath) continue;
                    return h;
                }
            }
            catch
            {
            }
            return null;
        }

        private static byte[] Query(HidSession s, int frameIn, int frameOut, byte command)
        {
            byte[] request = new byte[frameOut];
            request[0] = ReportId;
            request[1] = Family;
            request[2] = command;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!s.Write(request, 400)) continue;

                for (int i = 0; i < 4; i++)
                {
                    byte[] reply = s.Read(300);
                    if (reply == null) break;
                    if (reply.Length < BatteryIndex + 1) continue;

                    // Firmware NAK
                    if (reply.Length > 2 && reply[1] == 0xFF && reply[2] == 0xAA) continue;
                    if (reply.Length < 3) continue;
                    if (reply[1] != Family || reply[2] != command) continue;

                    return reply;
                }
            }

            return null;
        }

        private static bool IsHeadset(HidDeviceInfo h)
        {
            if (KnownModels.ContainsKey(h.ProductId)) return true;

            string name = h.ProductName;
            if (string.IsNullOrEmpty(name)) return false;
            return HeadsetName.IsMatch(name);
        }

        private static string ResolveModel(ushort pid, string productName)
        {
            string known;
            if (KnownModels.TryGetValue(pid, out known)) return known;

            string name = productName;
            if (string.IsNullOrEmpty(name)) return "耳机";

            name = Regex.Replace(name, "^ASUS\\s+", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, "^ROG\\s+", "", RegexOptions.IgnoreCase);
            name = name.Trim();
            return name.Length == 0 ? "耳机" : name.ToUpperInvariant();
        }
    }
}
