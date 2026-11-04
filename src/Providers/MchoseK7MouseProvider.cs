using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// 查询 K7 V2 Ultra+ 接收器的电量与充电状态，仅发送只读设备信息命令 0x0900。
    /// 充电状态依据官网 0x0900 字段与实机切换报告解析。协议依据： https://github.com/mitsimi/mchose-tray/blob/main/PROTOCOL.md and
    /// https://github.com/Rapou7/offline-mchose/blob/master/REVERSE_ENGINEERING_MCHOSE.md.
    /// </summary>
    public class MchoseK7MouseProvider : IDeviceProvider
    {
        private const ushort ReceiverVid = 0x3837;
        private const ushort ReceiverPid = 0x1014;
        private const ushort UsagePage = 0xFF01;
        private const ushort Usage = 0x0001;
        private const int ReportLength = 64;
        private const int ReplyTimeoutMs = 800;

        public string Name { get { return "MCHOSE K7 Mouse"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            Dictionary<string, bool> seenPaths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDeviceInfo hid in catalog)
            {
                if (!MatchesReceiver(hid)) continue;
                if (seenPaths.ContainsKey(hid.Path)) continue;
                seenPaths.Add(hid.Path, true);

                DeviceInfo device = new DeviceInfo();
                device.Id = "mchose-mouse-" + hid.Path;
                device.Brand = "MCHOSE";
                device.Model = "K7 V2 Ultra+";
                device.Name = "MCHOSE K7 V2 Ultra+";
                device.ShortModel = "K7 V2 Ultra+";
                device.Kind = DeviceKind.Mouse;
                device.VendorId = hid.VendorId;
                device.ProductId = hid.ProductId;
                device.HidPath = hid.Path;
                device.ProviderName = Name;
                found.Add(device);
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading reading = new DeviceReading();
            reading.UpdatedAt = DateTime.Now;
            reading.Level = BatteryLevel.Unknown;

            if (device == null || device.VendorId != ReceiverVid || device.ProductId != ReceiverPid ||
                string.IsNullOrEmpty(device.HidPath) || !IsMi02Path(device.HidPath))
            {
                reading.Error = "不支持的 MCHOSE 鼠标接收器";
                return reading;
            }

            try
            {
                using (HidSession session = new HidSession(device.HidPath, ReportLength, ReportLength))
                {
                    byte[] request = MchoseMouseProtocol.CreateBatteryRequest();
                    if (!session.Write(request, 1000))
                    {
                        reading.Error = "发送电量查询失败";
                        return reading;
                    }

                    Stopwatch timer = Stopwatch.StartNew();
                    while (timer.ElapsedMilliseconds < ReplyTimeoutMs)
                    {
                        int remaining = ReplyTimeoutMs - (int)timer.ElapsedMilliseconds;
                        byte[] report = session.Read(Math.Min(100, Math.Max(1, remaining)));
                        if (report == null) continue;

                        int percent;
                        bool charging;
                        bool chargingKnown;
                        MchoseMouseReplyKind result = MchoseMouseProtocol.ParseResponse(report, out percent, out charging, out chargingKnown);
                        if (result == MchoseMouseReplyKind.Unrelated) continue;

                        reading.Raw = ToHex(report);
                        if (result == MchoseMouseReplyKind.Invalid)
                        {
                            reading.Error = "MCHOSE 电量应答格式或校验无效";
                            return reading;
                        }
                        if (result == MchoseMouseReplyKind.Unavailable)
                        {
                            reading.Sleeping = true;
                            reading.Error = "鼠标休眠或未连接";
                            return reading;
                        }

                        reading.Percent = percent;
                        reading.Level = BatteryMath.FromPercent(percent);
                        // Upstream models this as bool Charging + bool ChargingKnown;
                        // we fold it into the nullable three-state Charging.
                        reading.Charging = chargingKnown ? (bool?)charging : null;
                        reading.Online = true;
                        reading.Detail = "2.4G";
                        return reading;
                    }

                    reading.Sleeping = true;
                    reading.Error = "设备休眠";
                }
            }
            catch (Exception ex)
            {
                reading.Error = ex.Message;
            }

            return reading;
        }

        private static bool MatchesReceiver(HidDeviceInfo hid)
        {
            return hid != null &&
                hid.VendorId == ReceiverVid &&
                hid.ProductId == ReceiverPid &&
                hid.UsagePage == UsagePage &&
                hid.Usage == Usage &&
                hid.InputReportLength == ReportLength &&
                hid.OutputReportLength == ReportLength &&
                !string.IsNullOrEmpty(hid.Path) &&
                IsMi02Path(hid.Path);
        }

        private static bool IsMi02Path(string path)
        {
            return path != null && path.IndexOf("&mi_02&", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ToHex(byte[] report)
        {
            StringBuilder text = new StringBuilder(report.Length * 3);
            for (int i = 0; i < report.Length; i++)
            {
                if (i != 0) text.Append(' ');
                text.Append(report[i].ToString("X2"));
            }
            return text.ToString();
        }
    }

    internal enum MchoseMouseReplyKind
    {
        Unrelated,
        Invalid,
        Unavailable,
        Battery
    }

    /// <summary>
    /// 编解码 K7 V2 的设备信息报文，拒绝不完整、校验错误或其他设备的回复。
    /// </summary>
    internal static class MchoseMouseProtocol
    {
        private const byte ReportId = 0x4D;
        private const byte ProtocolVersion = 0x01;
        private const byte Flags = 0x01;
        private const byte PayloadLength = 0x10;
        private const ushort BatteryQuery = 0x0900;
        private const ushort MouseVid = 0x3837;
        // 实机应答中的鼠标 PID；接收器 PID 是 0x1014。
        private const ushort MousePid = 0x4026;
        private const int ChecksumOffset = 24;

        internal static byte[] CreateBatteryRequest()
        {
            byte[] request = new byte[64];
            request[0] = ReportId;
            request[1] = ProtocolVersion;
            request[2] = Flags;
            request[4] = (byte)(BatteryQuery & 0xFF);
            request[5] = (byte)(BatteryQuery >> 8);
            request[8] = (byte)(request[2] ^ request[3] ^ request[4] ^ request[5] ^ request[6] ^ request[7]);
            return request;
        }

        internal static MchoseMouseReplyKind ParseResponse(byte[] report, out int percent, out bool charging, out bool chargingKnown)
        {
            percent = 0;
            charging = false;
            chargingKnown = false;
            if (report == null || report.Length < 6 || report[4] != 0x00 || report[5] != 0x09)
                return MchoseMouseReplyKind.Unrelated;

            if (report.Length != 64 ||
                report[0] != ReportId ||
                report[1] != ProtocolVersion ||
                report[2] != Flags ||
                report[3] != PayloadLength ||
                report[6] != 0x00)
                return MchoseMouseReplyKind.Invalid;

            int checksum = 0;
            for (int i = 2; i <= 7 + PayloadLength; i++) checksum ^= report[i];
            if (report[ChecksumOffset] != (byte)checksum) return MchoseMouseReplyKind.Invalid;

            for (int i = ChecksumOffset + 1; i < report.Length; i++)
            {
                if (report[i] != 0) return MchoseMouseReplyKind.Invalid;
            }

            ushort mouseVid = (ushort)(report[8] | (report[9] << 8));
            ushort mousePid = (ushort)(report[10] | (report[11] << 8));
            if (mouseVid != MouseVid || mousePid != MousePid) return MchoseMouseReplyKind.Invalid;

            byte connection = report[18];
            byte charge = report[19];
            byte level = report[20];
            if (charge > 1 || level > 100) return MchoseMouseReplyKind.Invalid;
            if (connection == 0) return MchoseMouseReplyKind.Unavailable;
            if (connection != 2) return MchoseMouseReplyKind.Invalid;

            percent = level;
            charging = charge == 1;
            chargingKnown = true;
            return MchoseMouseReplyKind.Battery;
        }
    }
}
