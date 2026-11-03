using System;
using System.Collections.Generic;
using Voltide.Core;

namespace Voltide.UI
{
    /// <summary>
    /// Low-battery settings. One number does both jobs: 0 means "off", any other
    /// value is the percentage at which a notification fires.
    ///
    /// Stored next to the refresh interval in HKCU\Software\Voltide, so the choice
    /// survives a restart just like the other preferences.
    /// </summary>
    internal static class LowBatterySettings
    {
        private const string SettingsKey = @"Software\Voltide";
        private const string ValueName = "LowBatteryThreshold";

        public static readonly int[] Choices = new int[] { 0, 10, 20, 30 };

        public const int DefaultThreshold = 20;

        public static int Load()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    if (key == null) return DefaultThreshold;
                    object raw = key.GetValue(ValueName);
                    if (raw == null) return DefaultThreshold;
                    return Normalize(Convert.ToInt32(raw));
                }
            }
            catch
            {
                return DefaultThreshold;
            }
        }

        public static void Save(int threshold)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key != null) key.SetValue(ValueName, Normalize(threshold));
                }
            }
            catch
            {
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
        /// charge cycle. Unplugging resets the state, so the next charge to full
        /// notifies again — one toast per charge session.
        /// </summary>
        public List<DeviceSnapshot> EvaluateFullCharge(IList<DeviceSnapshot> snapshots, bool enabled)
        {
            List<DeviceSnapshot> due = new List<DeviceSnapshot>();
            if (!enabled || snapshots == null) return due;

            foreach (DeviceSnapshot s in snapshots)
            {
                if (s == null || s.Info == null || s.Reading == null) continue;
                if (string.IsNullOrEmpty(s.Info.Id)) continue;
                if (!s.Reading.Online || !s.Reading.Percent.HasValue) continue;

                State state = GetState(s.Info.Id);

                // Off the cable (or the receiver cannot tell): the episode is over.
                if (!s.Reading.Charging)
                {
                    state.WarnedFull = false;
                    continue;
                }

                if (s.Reading.Percent.Value >= FullChargePercent && !state.WarnedFull)
                {
                    state.WarnedFull = true;
                    due.Add(s);
                }
            }

            return due;
        }

        /// <summary>
        /// Returns the devices that newly crossed the threshold on this refresh.
        /// Call once per refresh, with the threshold the user currently has selected.
        /// </summary>
        public List<DeviceSnapshot> Evaluate(IList<DeviceSnapshot> snapshots, int threshold)
        {
            List<DeviceSnapshot> due = new List<DeviceSnapshot>();
            threshold = LowBatterySettings.Normalize(threshold);
            if (threshold == 0 || snapshots == null) return due;

            foreach (DeviceSnapshot s in snapshots)
            {
                if (s == null || s.Info == null || s.Reading == null) continue;
                if (string.IsNullOrEmpty(s.Info.Id)) continue;
                if (!s.Reading.Online || !s.Reading.Percent.HasValue) continue;

                int percent = s.Reading.Percent.Value;
                State state = GetState(s.Info.Id);

                // Charging cancels the whole episode: nobody wants to be warned about
                // a device that is already on the cable.
                if (s.Reading.Charging)
                {
                    state.Warned = false;
                    continue;
                }

                if (percent > threshold + RecoveryMargin)
                {
                    state.Warned = false;
                    continue;
                }

                if (percent <= threshold && !state.Warned)
                {
                    state.Warned = true;
                    due.Add(s);
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
