using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI
{
    internal static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }

    /// <summary>仅用于接收全局热键的隐藏消息窗口。</summary>
    public class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0x0001;
        private const uint ModAlt = 0x0001;
        private const uint ModCtrl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;

        private bool _registered;
        public event EventHandler Pressed;

        public HotkeyWindow()
        {
            var cp = new CreateParams();
            cp.Caption = "AlbionCompanionHotkeySink";
            CreateHandle(cp);
        }

        public bool Register(string text)
        {
            Unregister();
            uint mods;
            uint vk;
            if (!TryParse(text, out mods, out vk)) return false;
            _registered = NativeMethods.RegisterHotKey(Handle, HotkeyId, mods, vk);
            return _registered;
        }

        public void Unregister()
        {
            if (_registered)
            {
                NativeMethods.UnregisterHotKey(Handle, HotkeyId);
                _registered = false;
            }
        }

        public static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('+');
            foreach (string raw in parts)
            {
                string token = raw.Trim();
                if (token.Length == 0) return false;
                string lower = token.ToLowerInvariant();
                if (lower == "ctrl" || lower == "control") { mods |= ModCtrl; continue; }
                if (lower == "alt") { mods |= ModAlt; continue; }
                if (lower == "shift") { mods |= ModShift; continue; }
                if (lower == "win") { mods |= ModWin; continue; }

                if (token.Length == 1)
                {
                    char c = char.ToUpperInvariant(token[0]);
                    if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                    {
                        vk = (uint)c;
                        continue;
                    }
                    return false;
                }
                if (lower.Length >= 2 && lower[0] == 'f')
                {
                    int n;
                    if (int.TryParse(lower.Substring(1), out n) && n >= 1 && n <= 12)
                    {
                        vk = (uint)(0x70 + n - 1);
                        continue;
                    }
                }
                return false;
            }
            return mods != 0 && vk != 0;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                var handler = Pressed;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    /// <summary>应用容器：托盘图标、全局热键、悬浮条与面板的联动。</summary>
    public class CompanionApp : ApplicationContext
    {
        private readonly Runtime _rt;
        private readonly OverlayForm _overlay;
        private readonly PanelForm _panel;
        private readonly NotifyIcon _tray;
        private readonly HotkeyWindow _hotkey;
        private readonly Icon _icon;

        public CompanionApp(Runtime runtime)
        {
            _rt = runtime;
            _icon = BuildIcon();

            _panel = new PanelForm(_rt);
            _panel.CollapseRequested += delegate { Collapse(); };

            _overlay = new OverlayForm(_rt);
            _overlay.ExpandRequested += delegate { Expand(); };
            _overlay.ExitRequested += delegate { Quit(); };
            PlaceOverlay();
            _overlay.Show();

            _tray = new NotifyIcon();
            _tray.Icon = _icon;
            _tray.Text = "Albion Companion";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { Expand(); };
            _tray.ContextMenuStrip = BuildTrayMenu();

            _hotkey = new HotkeyWindow();
            _hotkey.Pressed += delegate { ToggleByHotkey(); };
            if (!_hotkey.Register(_rt.Settings.Hotkey))
            {
                _tray.ShowBalloonTip(3000, "热键注册失败",
                    "热键 " + _rt.Settings.Hotkey + " 可能已被占用，可在「设置」页更换。",
                    ToolTipIcon.Warning);
            }

            _rt.SettingsApplied += delegate
            {
                _overlay.ApplySettings();
                _panel.TopMost = _rt.Settings.AlwaysOnTop;
                if (!_hotkey.Register(_rt.Settings.Hotkey))
                {
                    _tray.ShowBalloonTip(2500, "热键未生效",
                        "热键 " + _rt.Settings.Hotkey + " 被占用，请换一个组合。", ToolTipIcon.Warning);
                }
            };
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = Th.Card;
            menu.ForeColor = Th.Text;
            menu.Font = Th.F(9.5f);
            menu.ShowImageMargin = false;

            menu.Items.Add("展开详细面板", null, delegate { Expand(); });
            menu.Items.Add("显示悬浮条", null, delegate
            {
                _panel.Hide();
                _overlay.Show();
                _overlay.BringToFront();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("重置本次会话", null, delegate { _rt.ResetSession(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Quit(); });
            return menu;
        }

        private void PlaceOverlay()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            _overlay.Location = new Point(area.Right - _overlay.Width - 28, area.Top + 96);
        }

        private void Expand()
        {
            _overlay.Hide();
            _panel.Show();
            _panel.BringToFront();
            _panel.Activate();
        }

        private void Collapse()
        {
            _panel.Hide();
            _overlay.Show();
            _overlay.BringToFront();
        }

        private void ToggleByHotkey()
        {
            if (_panel.Visible)
            {
                Collapse();
                return;
            }
            if (_overlay.Visible)
            {
                _overlay.Hide();
                return;
            }
            _overlay.Show();
            _overlay.BringToFront();
        }

        public void Quit()
        {
            _rt.Save();
            _tray.Visible = false;
            _tray.Dispose();
            _hotkey.Dispose();
            ExitThread();
        }

        private static Icon BuildIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Th.FillRound(g, new Rectangle(2, 2, 28, 28), 8, Color.FromArgb(23, 26, 32));
                Th.FillRound(g, new Rectangle(2, 2, 28, 28), 8, Color.FromArgb(40, 216, 169, 74));
                Th.StrokeRound(g, new Rectangle(2, 2, 27, 27), 8, Th.Accent);
                TextRenderer.DrawText(g, "A", Th.FB(14f), new Rectangle(2, 3, 28, 27), Th.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
