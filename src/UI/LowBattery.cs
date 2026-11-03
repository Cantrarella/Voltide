using System;
using System.Collections.Generic;
using Voltide.Core;

namespace Voltide.UI
{
    /// <summary>
    /// Low-battery settings. One number does both jobs: 0 means "off", any other
    /// value is the percentage at which a notification fires.
    ///
    /// Stored next to the refresh interval under HKCU\Software\Voltide.
    /// Turning the switch off writes 0, and the last 10/20/30 choice is kept
    /// beside it so a restart does not forget which one it was.
    /// </summary>
    internal static class LowBatterySettings
    {
        public const string RealKey = @"Software\Voltide";

        private const string ValueName = "LowBatteryThreshold";
        private const string LastValueName = "LowBatteryThresholdLast";

        public static readonly int[] Choices = new int[] { 0, 10, 20, 30 };

        public const int DefaultThreshold = 20;

        public static int Load(string settingsKey)
        {
            int value;
            if (!TryRead(settingsKey, ValueName, out value)) return DefaultThreshold;
            return Normalize(value);
        }

        /// <summary>
        /// Last non-zero choice. Missing, 0, or garbage falls back to the default
        /// so the switch always has a real percentage to restore.
        /// </summary>
        public static int LoadLast(string settingsKey)
        {
            int value;
            if (!TryRead(settingsKey, LastValueName, out value)) return DefaultThreshold;
            value = Normalize(value);
            return value == 0 ? DefaultThreshold : value;
        }

        /// <summary>
        /// Older builds stored only the live threshold. If that value is 10 or 30
        /// and the "last choice" value was never written, record it now so turning
        /// the switch off later can restore it.
        /// </summary>
        public static void EnsureLast(string settingsKey, int threshold)
        {
            threshold = Normalize(threshold);
            if (threshold == 0) return;
            int existing;
            if (TryRead(settingsKey, LastValueName, out existing) && Normalize(existing) != 0) return;
            Save(settingsKey, threshold);
        }

        public static void Save(string settingsKey, int threshold)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.CreateSubKey(settingsKey))
                {
                    if (key == null) return;
                    int normalized = Normalize(threshold);
                    key.SetValue(ValueName, normalized);
                    if (normalized != 0) key.SetValue(LastValueName, normalized);
                }
            }
            catch
            {
            }
        }

        private static bool TryRead(string settingsKey, string name, out int value)
        {
            value = 0;
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.OpenSubKey(settingsKey, false))
                {
                    if (key == null) return false;
                    object raw = key.GetValue(name);
                    if (raw == null) return false;
                    value = Convert.ToInt32(raw);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static int Normalize(int value)
        {
            if (value == 0 || value == 10 || value == 20 || value == 30) return value;
            return DefaultThreshold;
        }

        /// <summary>Off -> 10 -> 20 -> 30 -> off. One click, one row, no dialog.</summary>
        public static int Next(int value)
        {
            for (int i = 0; i < Choices.Length; i++)
            {
                if (Choices[i] == value) return Choices[(i + 1) % Choices.Length];
            }
            // Unknown value (stale/corrupt registry): restart the cycle just after "off"
            // so the first click always lands on a real setting.
            return Choices[1];
        }

        public static string Label(int value)
        {
            int v = Normalize(value);
            return v == 0 ? "关" : v + "%";
        }
    }

    /// <summary>
    /// Decides when to nag about a low cell, and — just as important — when not to.
    ///
    /// The rule is one notification per "low episode": a device that drops under the
    /// threshold speaks up once, then stays quiet until it has climbed back above
    /// threshold + 5% or has been plugged in. Without that hysteresis a device sitting
    /// exactly on the threshold would toast on every refresh, which is the fastest way
    /// to get the feature switched off again.
    /// </summary>
    internal sealed class LowBatteryGuard
    {
        private const int RecoveryMargin = 5;

        // "Full" starts at 95: some receivers stop reporting charge at 95–99 even
        // though the cell is done, and waiting for a literal 100 would never fire.
        private const int FullChargePercent = 95;

        private sealed class State
        {
            public bool Warned;
            public bool WarnedFull;
        }

        private readonly Dictionary<string, State> _states = new Dictionary<string, State>();

        /// <summary>
        /// Returns the devices that newly reached a full charge on this refresh.
        /// enabled rides the low-battery switch: one toggle governs both ends of the
        /// charge cycle. The latch still moves while the switch is off, so turning
        /// it back on does not replay a charge that already finished.
        /// Unplugging resets the state, so the next charge to full notifies again —
        /// one toast per charge session. Wired pads and a failed charge query are
        /// not a charge session: only Charging == true can open one.
        /// </summary>
        public List<DeviceSnapshot> EvaluateFullCharge(IList<DeviceSnapshot> snapshots, bool enabled)
        {
            List<DeviceSnapshot> due = new List<DeviceSnapshot>();
            if (snapshots == null) return due;

            foreach (DeviceSnapshot s in snapshots)
            {
                if (s == null || s.Info == null || s.Reading == null) continue;
                if (string.IsNullOrEmpty(s.Info.Id)) continue;
                if (!s.Reading.Online || !s.Reading.Percent.HasValue) continue;

                State state = GetState(s.Info.Id);

                // Known to be off the cable: the episode is over.
                // Null (query failed, nothing earlier to keep) must not clear it.
                if (s.Reading.Charging == false)
                {
                    state.WarnedFull = false;
                    continue;
                }

                if (s.Reading.Charging != true) continue;

                if (s.Reading.Percent.Value >= FullChargePercent && !state.WarnedFull)
                {
                    state.WarnedFull = true;
                    if (enabled) due.Add(s);
                }
            }

            return due;
        }

        /// <summary>
        /// Returns the devices that newly crossed the threshold on this refresh.
        /// threshold 0 means the switch is off: nothing is returned, but the latch
        /// still tracks <paramref name="remembered"/> (the last 10/20/30 choice).
        /// Otherwise a device that sat below the line the whole time the switch
        /// was off would toast the moment it was turned back on.
        /// </summary>
        public List<DeviceSnapshot> Evaluate(IList<DeviceSnapshot> snapshots, int threshold, int remembered)
        {
            List<DeviceSnapshot> due = new List<DeviceSnapshot>();
            if (snapshots == null) return due;

            threshold = LowBatterySettings.Normalize(threshold);
            int gate = threshold == 0 ? LowBatterySettings.Normalize(remembered) : threshold;
            if (gate == 0) gate = LowBatterySettings.DefaultThreshold;
            bool notify = threshold != 0;

            foreach (DeviceSnapshot s in snapshots)
            {
                if (s == null || s.Info == null || s.Reading == null) continue;
                if (string.IsNullOrEmpty(s.Info.Id)) continue;
                if (!s.Reading.Online || !s.Reading.Percent.HasValue) continue;

                int percent = s.Reading.Percent.Value;
                State state = GetState(s.Info.Id);

                // Charging cancels the whole episode: nobody wants to be warned about
                // a device that is already on the cable. Unknown is not "on the cable"
                // and must not erase a warning that is already latched.
                if (s.Reading.Charging == true)
                {
                    state.Warned = false;
                    continue;
                }

                if (percent > gate + RecoveryMargin)
                {
                    state.Warned = false;
                    continue;
                }

                if (percent <= gate && !state.Warned)
                {
                    state.Warned = true;
                    if (notify) due.Add(s);
                }
            }

            return due;
        }

        private State GetState(string id)
        {
            State state;
            if (!_states.TryGetValue(id, out state))
            {
                state = new State();
                _states[id] = state;
            }
            return state;
        }
    }
}
