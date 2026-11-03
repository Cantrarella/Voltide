using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Voltide.Core;

namespace Voltide.UI
{
    public class TrayApp : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly DeviceManager _manager;
        private readonly NotifyIcon _tray;
        private readonly MainForm _form;
        private readonly System.Windows.Forms.Timer _timer;

        private bool _refreshing;
        private bool _refreshQueued;

        private readonly LowBatteryGuard _lowBattery = new LowBatteryGuard();
        private int _lowBatteryThreshold;
        private int _lowBatteryRemembered = LowBatterySettings.DefaultThreshold;
        private readonly string _settingsKey;

        // ShowBalloonTip replaces whatever is already on screen, so several notices
        // in one refresh used to leave only the last one visible. These wait their
        // turn. Low-battery entries are inserted ahead of full-charge ones.
        private sealed class PendingNotice
        {
            public int TimeoutMs;
            public string Title;
            public string Text;
            public ToolTipIcon Icon;
            public bool LowBattery;
        }

        private readonly List<PendingNotice> _notices = new List<PendingNotice>();
        private bool _noticeShowing;
        private int _noticeGen;
        private DateTime _noticeShownAtUtc;
        private System.Windows.Forms.Timer _noticeTimer;

        // A second right-click replaces the popup instead of stacking another copy.
        private TrayMenuForm _menuForm;


        internal static readonly Color MenuMuted = Color.FromArgb(135, 135, 135); // card status line

        public TrayApp()
        {
            _settingsKey = LowBatterySettings.RealKey;
            _manager = new DeviceManager();

            _form = new MainForm(_manager);
            _form.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    _form.Hide();
                }
            };
            _form.RefreshRequested += delegate { RefreshNow(); };

            // force handle creation so cross-thread marshalling works immediately
            IntPtr handle = _form.Handle;

            _tray = new NotifyIcon();
            _tray.Icon = IconFactory.ToLightningIcon(32);
            _tray.Text = "Voltide";   // Windows tooltip on hover
            _tray.Visible = true;
            _tray.BalloonTipClosed += delegate { OnBalloonClosed(); };

            _tray.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { _form.ShowPanel(); return; }
                ShowMenuAboveRight();
            };

            _form.RefreshSeconds = LoadRefreshSeconds();
            _lowBatteryThreshold = LowBatterySettings.Load(_settingsKey);
            _lowBatteryRemembered = LowBatterySettings.LoadLast(_settingsKey);
            if (_lowBatteryThreshold != 0) _lowBatteryRemembered = _lowBatteryThreshold;
            LowBatterySettings.EnsureLast(_settingsKey, _lowBatteryThreshold);
            _form.RememberLowBatteryChoice(_lowBatteryRemembered);
            _form.LowBatteryThreshold = _lowBatteryThreshold;
            _form.RefreshRateChanged += delegate
            {
                SaveRefreshSeconds(_form.RefreshSeconds);
                ApplyInterval();
            };
            _form.LowBatteryChanged += delegate
            {
                _lowBatteryThreshold = LowBatterySettings.Normalize(_form.LowBatteryThreshold);
                if (_lowBatteryThreshold != 0) _lowBatteryRemembered = _lowBatteryThreshold;
                LowBatterySettings.Save(_settingsKey, _lowBatteryThreshold);
            };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _form.RefreshSeconds * 1000;
            _timer.Tick += delegate
            {
                // Keep the footprint small: the receiver shares its input pipe with
                // G HUB, and every request we send is broadcast to it too. It also
                // stops answering for a while if it is polled too hard, which showed
                // up as cards flapping between "97%" and "asleep".
                ApplyInterval();
                RefreshNow();
            };
            _timer.Start();

            RefreshNow();
        }

        /// <summary>
        /// The panel's own cadence while it is open; a slow trickle while hidden in the
        /// tray so a sleeping mouse is not woken every 15 seconds for nothing.
        /// </summary>
        private void ApplyInterval()
        {
            int seconds = _form.Visible ? _form.RefreshSeconds : 300;
            if (_timer.Interval != seconds * 1000) _timer.Interval = seconds * 1000;
        }

        // ------------------------------------------------------- refresh rate

        private int LoadRefreshSeconds()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_settingsKey, false))
                {
                    if (key == null) return 90;
                    object raw = key.GetValue("RefreshSeconds");
                    if (raw == null) return 90;

                    int seconds = Convert.ToInt32(raw);
                    return (seconds == 15 || seconds == 30 || seconds == 90) ? seconds : 90;
                }
            }
            catch
            {
                return 30;
            }
        }

        private void SaveRefreshSeconds(int seconds)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_settingsKey))
                {
                    if (key != null) key.SetValue("RefreshSeconds", seconds);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Opens the popup to the upper-right of the cursor, in a borderless window of
        /// our own so the size, the corners and the dismissal are all under our control.
        /// </summary>
        private void ShowMenuAboveRight()
        {
            // One popup at a time. Without this, each right-click built a new window
            // and the old one stayed alive until it lost focus — repeated clicks piled
            // up as a stack of menus.
            if (_menuForm != null && !_menuForm.IsDisposed)
            {
                try { _menuForm.Close(); } catch { }
                _menuForm = null;
            }

            MenuPanel panel = BuildMenu();
            if (panel == null) return;

            TrayMenuForm form = new TrayMenuForm();
            form.Panel = panel;
            form.ClientSize = panel.Size;

            panel.Dock = DockStyle.Fill;
            panel.QuitRequested += delegate { Application.Exit(); };

            panel.AutostartEnabled = Autostart.IsSupported();
            panel.AutostartOn = Autostart.IsSupported() && Autostart.IsEnabled();

            panel.LowBatteryThreshold = _lowBatteryThreshold;

            MenuPanel captured = panel;
            panel.AutostartToggled += delegate { Autostart.Set(captured.AutostartOn); };
            panel.LowBatteryChanged += delegate
            {
                _lowBatteryThreshold = LowBatterySettings.Normalize(captured.LowBatteryThreshold);
                if (_lowBatteryThreshold != 0) _lowBatteryRemembered = _lowBatteryThreshold;
                LowBatterySettings.Save(_settingsKey, _lowBatteryThreshold);
                try { _form.LowBatteryThreshold = _lowBatteryThreshold; } catch { }
            };

            form.Controls.Add(panel);

            Point p = Cursor.Position;
            Rectangle screen = Screen.FromPoint(p).WorkingArea;

            int x = p.X + 4;
            int y = p.Y - panel.Height - 4;

            if (x + panel.Width > screen.Right) x = p.X - panel.Width - 4;
            if (y < screen.Top) y = p.Y + 4;

            form.Location = new Point(x, y);
            _menuForm = form;
            form.Show();
            form.Activate();
            panel.Focus();
        }

        // ---------------------------------------------------------------- menu

        /// <summary>
        /// Rebuilt on every open so the list is never stale. Order matches the panel:
        /// reporting a level, then awake-but-waiting, then asleep.
        /// </summary>
        /// <summary>
        /// Whole-menu shrink on top of DPI scaling. Every size in TrayMenu rides the
        /// one scale factor — rows, gaps, fonts, the pill — so this single constant
        /// compacts the menu proportionally without touching its layout code.
        /// </summary>
        private const float MenuShrink = 1.0f;

        private MenuPanel BuildMenu()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                float scale = g.DpiX / 96f * MenuShrink;

                // Unified 16 px across the whole menu: names, percentages and the
                // Chinese action rows share one size so nothing looks oversized.
                Font nameFont = FontManager.Get(FontKind.MontserratMedium, 16f * scale);
                Font valueFont = FontManager.Get(FontKind.MontserratMedium, 16f * scale);
                Font cnFont = FontManager.Get(FontKind.Chinese, 16f * scale);
                Font markFont = FontManager.Get(FontKind.MontserratSemiBold, 9f * scale);

                List<MenuRow> rows = new List<MenuRow>();

                foreach (DeviceSnapshot s in OrderedForMenu())
                {
                    MenuRow row = new MenuRow();

                    // The tray row is narrow, so devices may publish a shorter label
                    // than the one the card uses.
                    if (!string.IsNullOrEmpty(s.Info.ShortModel)) row.Name = s.Info.ShortModel;
                    else if (!string.IsNullOrEmpty(s.Info.Model)) row.Name = s.Info.Model;
                    else row.Name = s.Info.Brand;

                    if (HasLevel(s))
                    {
                        row.Kind = MenuRowKind.Level;
                        row.Percent = s.Reading.Percent.Value;
                        row.Value = s.Reading.Percent.Value + "%";
                    }
                    else
                    {
                        row.Kind = MenuRowKind.Asleep;

                        int? last = _form.LastPercent(s.Info.Id);
                        if (last.HasValue)
                        {
                            // Still paired, just idle: show the last known level in grey
                            // instead of a blank, same rule as the card.
                            row.Percent = last.Value;
                            row.Value = last.Value >= 100 ? "Full" : last.Value + "%";
                            row.GreyValue = true;
                        }
                        else
                        {
                            row.Value = "休眠中";
                        }
                    }

                    rows.Add(row);
                }

                return new MenuPanel(rows, scale, nameFont, valueFont, cnFont, markFont);
            }
        }
        private List<DeviceSnapshot> OrderedForMenu()
        {
            List<DeviceSnapshot> live = new List<DeviceSnapshot>();
            List<DeviceSnapshot> asleep = new List<DeviceSnapshot>();

            foreach (DeviceSnapshot s in _manager.Snapshot())
            {
                if (s.Reading == null) { asleep.Add(s); continue; }

                if (s.Reading.Online && s.Reading.Percent.HasValue) live.Add(s);
                else asleep.Add(s);
            }

            live.AddRange(asleep);
            return live;
        }

        private static bool HasLevel(DeviceSnapshot s)
        {
            return s != null && s.Reading != null && s.Reading.Online && s.Reading.Percent.HasValue;
        }

        // ------------------------------------------------------------- refresh

        private void RefreshNow()
        {
            if (_refreshing)
            {
                // A read is already running. Queue one more instead of dropping the
                // request — dropping it is what made a click on the refresh icon spin
                // the arrow and change nothing at all.
                _refreshQueued = true;
                return;
            }

            _refreshing = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _manager.Refresh();
                }
                catch
                {
                }
                finally
                {
                    _refreshing = false;
                }

                try
                {
                    _form.BeginInvoke(new MethodInvoker(delegate
                    {
                        _form.UpdateSnapshot(_manager.Snapshot());
                        NotifyLowBattery();
                        NotifyFullCharge();
                        UpdateTrayIcon();
                    }));
                }
                catch
                {
                }

                if (_refreshQueued)
                {
                    _refreshQueued = false;
                    RefreshNow();
                }
            });
        }

        /// <summary>
        /// Runs on the UI thread after each refresh. One balloon per device that just
        /// crossed the threshold; the guard makes sure it only happens once per episode.
        /// </summary>
        private void NotifyLowBattery()
        {
            List<DeviceSnapshot> due;
            try
            {
                due = _lowBattery.Evaluate(_manager.Snapshot(), _lowBatteryThreshold, _lowBatteryRemembered);
            }
            catch
            {
                return;
            }

            if (due == null || due.Count == 0) return;

            int shown = 0;
            foreach (DeviceSnapshot s in due)
            {
                if (shown >= 3) break;
                shown++;

                string name = s.Info.ShortModel;
                if (string.IsNullOrEmpty(name)) name = s.Info.Model;
                if (string.IsNullOrEmpty(name)) name = s.Info.Name;

                int percent = s.Reading.Percent.HasValue ? s.Reading.Percent.Value : 0;

                EnqueueNotice(
                    8000,
                    "Voltide · 低电量提醒",
                    name + " 电量 " + percent + "%，该充电了",
                    ToolTipIcon.Warning,
                    true);
            }
        }

        /// <summary>
        /// "Full" counterpart of NotifyLowBattery, same refresh tick. Fires once per
        /// charge session per device, and only while the low-battery switch is on —
        /// one preference covers both ends of the battery story.
        /// </summary>
        private void NotifyFullCharge()
        {
            List<DeviceSnapshot> due;
            try
            {
                due = _lowBattery.EvaluateFullCharge(_manager.Snapshot(), _lowBatteryThreshold != 0);
            }
            catch
            {
                return;
            }

            if (due == null || due.Count == 0) return;

            foreach (DeviceSnapshot s in due)
            {
                string name = s.Info.ShortModel;
                if (string.IsNullOrEmpty(name)) name = s.Info.Model;
                if (string.IsNullOrEmpty(name)) name = s.Info.Name;

                EnqueueNotice(
                    8000,
                    "Voltide · 充电完成",
                    name + " 电量已充满",
                    ToolTipIcon.Info,
                    false);
            }
        }

        /// <summary>
        /// One balloon at a time. Low-battery notices are queued ahead of any
        /// full-charge notice that has not been shown yet, so a wired pad's
        /// "fully charged" tip cannot replace the warning the user needs.
        /// </summary>
        private void EnqueueNotice(int timeoutMs, string title, string text, ToolTipIcon icon, bool lowBattery)
        {
            const int cap = 6;
            PendingNotice notice = new PendingNotice();
            notice.TimeoutMs = timeoutMs;
            notice.Title = title;
            notice.Text = text;
            notice.Icon = icon;
            notice.LowBattery = lowBattery;

            if (_notices.Count >= cap)
            {
                if (!lowBattery) return;
                for (int i = _notices.Count - 1; i >= 0; i--)
                {
                    if (_notices[i].LowBattery) continue;
                    _notices.RemoveAt(i);
                    break;
                }
                if (_notices.Count >= cap) return;
            }

            if (!lowBattery)
            {
                _notices.Add(notice);
            }
            else
            {
                int i = 0;
                while (i < _notices.Count && _notices[i].LowBattery) i++;
                _notices.Insert(i, notice);
            }

            if (!_noticeShowing) ShowNextNotice();
        }

        private void ShowNextNotice()
        {
            if (_notices.Count == 0)
            {
                _noticeShowing = false;
                return;
            }

            PendingNotice notice = _notices[0];
            _notices.RemoveAt(0);
            _noticeShowing = true;
            int gen = ++_noticeGen;
            _noticeShownAtUtc = DateTime.UtcNow;

            try
            {
                _tray.ShowBalloonTip(notice.TimeoutMs, notice.Title, notice.Text, notice.Icon);
            }
            catch
            {
                _noticeShowing = false;
                ShowNextNotice();
                return;
            }

            if (_noticeTimer == null)
            {
                _noticeTimer = new System.Windows.Forms.Timer();
                _noticeTimer.Tick += delegate
                {
                    int pending = (int)_noticeTimer.Tag;
                    _noticeTimer.Stop();
                    FinishNotice(pending);
                };
            }

            _noticeTimer.Tag = gen;
            _noticeTimer.Interval = notice.TimeoutMs < 1000 ? 1000 : notice.TimeoutMs;
            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        private void OnBalloonClosed()
        {
            // Wine can signal closed as soon as the tip is posted. Advancing
            // then collapses the queue and only the last notice stays visible.
            if ((DateTime.UtcNow - _noticeShownAtUtc).TotalMilliseconds < 1500) return;
            FinishNotice(_noticeGen);
        }

        private void FinishNotice(int gen)
        {
            if (gen != _noticeGen) return;
            _noticeShowing = false;
            ShowNextNotice();
        }

        public void ShowPanel()
        {
            _form.ShowPanel();
        }

        // ------------------------------------------------------------- tray icon

        private int _trayPercent = -1;   // -1 = default logo, otherwise the battery digit

        /// <summary>
        /// Swaps the tray icon for a live battery digit: the lowest online device's
        /// percentage, framed by a corner bracket. No device keeps the transparent
        /// lightning glyph. 100% uses the app icon, drawn at the tray's real pixel
        /// size (16/20/24/32) so high DPI does not scale a single bitmap. Runs on
        /// the UI thread after each refresh.
        ///
        /// The old icon is disposed before the swap — NotifyIcon copies the handle, so
        /// leaving the previous Icon for the GC leaks a GDI object per refresh.
        /// </summary>
        private void UpdateTrayIcon()
        {
            int percent = -1;

            foreach (DeviceSnapshot s in _manager.Snapshot())
            {
                if (s == null || s.Reading == null) continue;
                if (!s.Reading.Online || !s.Reading.Percent.HasValue) continue;

                int p = s.Reading.Percent.Value;
                if (p < 0) p = 0;
                if (p > 100) p = 100;

                if (percent < 0 || p < percent) percent = p;
            }

            if (percent == _trayPercent) return;
            _trayPercent = percent;

            Icon next;
            if (percent < 0) next = IconFactory.ToLightningIcon(32);
            else if (percent >= 100) next = IconFactory.ToAppIcon(IconFactory.SystemSmallIconSize());
            else next = IconFactory.ToBatteryIcon(percent, 32);

            Icon previous = _tray.Icon;
            _tray.Icon = next;
            if (previous != null) previous.Dispose();

            _tray.Text = percent < 0 ? "Voltide" : "Voltide · 最低电量 " + percent + "%";
        }

        public void Dispose()
        {
            // The popup is a separate top-level window: closing the tray app must not
            // leave it on screen.
            if (_menuForm != null && !_menuForm.IsDisposed)
            {
                try { _menuForm.Close(); } catch { }
                _menuForm = null;
            }

            if (_noticeTimer != null)
            {
                _noticeTimer.Stop();
                _noticeTimer.Dispose();
                _noticeTimer = null;
            }

            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
            }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }

        }
    }
}
