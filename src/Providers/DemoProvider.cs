using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// Synthetic devices for eyeballing the UI without real hardware. Only wired up
    /// when the app is launched with --demo; never in the normal build path, so a
    /// shipping exe can't accidentally show fake gear.
    /// </summary>
    public sealed class DemoProvider : IDeviceProvider
    {
        public string Name { get { return "Demo"; } }

        private readonly List<DeviceInfo> _devices = new List<DeviceInfo>();
        // Per-instance canned levels. Not static: each DemoProvider owns its own set,
        // so two demo instances can never overwrite one another's values.
        private readonly Dictionary<string, int> _percent = new Dictionary<string, int>();

        public DemoProvider()
        {
            _devices.Add(Make("GPW3", "Logitech", "GPW3 鼠标", DeviceKind.Mouse, 48));
            _devices.Add(Make("K99v2", "迈从", "K99 v2 键盘", DeviceKind.Keyboard, 100));
            _devices.Add(Make("DeltaII", "ROG", "Delta II 凌镜2", DeviceKind.Headset, 100));
            _devices.Add(Make("Beitong70", "北通", "鲲鹏70 手柄", DeviceKind.Other, 100));
        }

        private DeviceInfo Make(string id, string brand, string model, DeviceKind kind, int percent)
        {
            DeviceInfo info = new DeviceInfo();
            info.Id = "demo-" + id;
            info.Name = model;
            info.Brand = brand;
            info.Model = model;
            info.ShortModel = model;
            info.Kind = kind;
            info.VendorId = 0x0000;
            info.ProductId = 0x0000;
            info.HidPath = "demo";
            info.ProviderName = "Demo";

            // Stash the canned level so Read can hand it back without a second table.
            info.IsCharging = false;
            _percent[info.Id] = percent;
            return info;
        }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            return new List<DeviceInfo>(_devices);
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.Online = true;
            r.Sleeping = false;
            r.Charging = false;
            r.UpdatedAt = DateTime.Now;

            int p;
            if (_percent.TryGetValue(device.Id, out p))
            {
                r.Percent = p;
                r.Level = BatteryMath.FromPercent(p);
            }
            else
            {
                r.Percent = 50;
                r.Level = BatteryLevel.Medium;
            }

            return r;
        }
    }
}
