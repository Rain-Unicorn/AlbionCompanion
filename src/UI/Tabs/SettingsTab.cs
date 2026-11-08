using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>设置：置顶、透明度、全局热键、数据目录与合规说明。</summary>
    public class SettingsTab : TabBase
    {
        private readonly CheckBox _topMost;
        private readonly TrackBar _opacity;
        private readonly Label _opacityValue;
        private readonly TextBox _hotkey;
        private readonly Label _hotkeyMsg;
        private readonly Label _dataDir;
        private readonly TextBox _charName; // R1/C3：「我的角色」
        private readonly Label _charMsg;
        private bool _syncing;

        public SettingsTab(Runtime runtime)
            : base(runtime)
        {
            Card(0, 0, 496, 150);
            CardText("显示", 14, 10, 100, 20, Th.Text, 10.5f, true);

            _topMost = Ui.Check("窗口置顶（悬浮条与面板始终位于游戏窗口之上）", 14, 36, 460,
                                Rt.Settings.AlwaysOnTop);
            _topMost.CheckedChanged += delegate
            {
                if (_syncing) return;
                Rt.Settings.AlwaysOnTop = _topMost.Checked;
                Rt.ApplySettings();
            };
            Controls.Add(_topMost);

            CardText("悬浮条透明度", 14, 64, 200, 18, Th.Sub, 8.5f, false);
            _opacity = new TrackBar();
            _opacity.Minimum = 45;
            _opacity.Maximum = 100;
            _opacity.TickStyle = TickStyle.None;
            _opacity.BackColor = Th.Bg;
            _opacity.Location = new Point(14, 84);
            _opacity.Size = new Size(300, 42);
            _opacity.Value = Math.Max(45, Math.Min(100, Rt.Settings.OpacityPercent));
            _opacity.ValueChanged += delegate
            {
                _opacityValue.Text = _opacity.Value + "%";
                if (_syncing) return;
                Rt.Settings.OpacityPercent = _opacity.Value;
                Rt.Notify();
            };
            _opacity.MouseUp += delegate
            {
                if (_syncing) return;
                Rt.ApplySettings();
            };
            Controls.Add(_opacity);

            _opacityValue = CardText(_opacity.Value + "%", 320, 94, 80, 24, Th.Text, 11f, true);

            // R1/C3：「我的角色」——无法自动识别身份（如启动晚错过 JoinResponse）时的名字兜底
            Card(0, 142, 496, 88);
            CardText("我的角色", 14, 152, 200, 20, Th.Text, 10.5f, true);
            CardText("填游戏内角色名，用于无法自动识别身份时兜底（身份确认以服务器事件为准）",
                     14, 174, 460, 16, Th.Sub, 8f, false);

            _charName = Ui.Input(14, 194, 200, 28);
            _charName.Text = Rt.Settings.LocalPlayerName;
            Controls.Add(_charName);

            _charMsg = CardText("", 318, 200, 164, 16, Th.Sub, 8f, false);

            var saveName = Ui.Primary("保存", 222, 194, 88, 30);
            saveName.Click += delegate
            {
                string text = _charName.Text.Trim();
                Rt.Settings.LocalPlayerName = text;
                LocalPlayerIdentity.SetManualName(text);
                Rt.Save();
                _charMsg.Text = text.Length == 0 ? "已清除，回到自动识别"
                                              : "已保存，等角色出现时自动绑定";
                _charMsg.ForeColor = Th.Good;
            };
            Controls.Add(saveName);

            Card(0, 240, 496, 96);
            CardText("全局热键", 14, 250, 200, 20, Th.Text, 10.5f, true);
            CardText("格式：Ctrl+Alt+A（支持 Ctrl / Alt / Shift / Win 与 A-Z、0-9、F1-F12）",
                     14, 272, 460, 18, Th.Sub, 8f, false);

            _hotkey = Ui.Input(14, 296, 140, 28);
            _hotkey.Text = Rt.Settings.Hotkey;
            Controls.Add(_hotkey);

            var apply = Ui.Primary("应用热键", 162, 296, 88, 30);
            apply.Click += delegate
            {
                string text = _hotkey.Text.Trim();
                uint mods;
                uint vk;
                if (!HotkeyWindow.TryParse(text, out mods, out vk))
                {
                    _hotkeyMsg.Text = "格式不正确，例如 Ctrl+Alt+A";
                    _hotkeyMsg.ForeColor = Th.Bad;
                    return;
                }
                Rt.Settings.Hotkey = text;
                Rt.ApplySettings();
                _hotkeyMsg.Text = "已尝试注册 " + text + "；若无反应说明被其他程序占用";
                _hotkeyMsg.ForeColor = Th.Good;
            };
            Controls.Add(apply);

            _hotkeyMsg = CardText("", 256, 302, 232, 18, Th.Sub, 8f, false);

            Card(0, 346, 496, 84);
            CardText("数据与目录", 14, 356, 200, 20, Th.Text, 10.5f, true);
            CardText("配置保存在", 14, 378, 120, 18, Th.Sub, 8.5f, false);
            _dataDir = CardText(SettingsStore.DefaultDir(), 14, 396, 356, 30, Th.Text, 8.5f, false);

            var open = Ui.Secondary("打开目录", 378, 394, 100, 30);
            open.Click += delegate
            {
                try
                {
                    string dir = SettingsStore.DefaultDir();
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    Process.Start("explorer.exe", dir);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("打开目录失败：" + ex.Message, "打开目录", MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            };
            Controls.Add(open);

            Card(0, 440, 496, 298);
            CardText("关于与边界说明", 14, 450, 200, 20, Th.Text, 10.5f, true);
            CardText(
                "本工具是本地统计与行情助手，包含：\r\n" +
                "· 悬浮条：会话计时、本次收益、时薪，单击展开详细面板；\r\n" +
                "· 收益台账：手工记录采集 / 刷怪 / 副本 / 交易收益，可导出 CSV；\r\n" +
                "· 战斗复盘：手工录入或载入示例，查看 DPS / HPS / 承伤占比；\r\n" +
                "· 市场行情：通过 albion-online-data.com 公共接口查询各城最低卖 / 最高买价；\r\n" +
                "· 配装成本：填写各部位单价，估算整套成本与回本次数。\r\n\r\n" +
                "刻意没有实现的部分：不读取游戏进程内存、不注入 DLL、不模拟键鼠、\r\n" +
                "不做反作弊规避。这些行为违反阿尔比恩使用条款，也会带来封号风险。\r\n" +
                "因此战斗数据需要由你自己录入或粘贴。",
                14, 474, 466, 256, Th.Sub, 8.5f, false);
        }

        public override void RefreshData()
        {
            _syncing = true;
            _topMost.Checked = Rt.Settings.AlwaysOnTop;
            _opacity.Value = Math.Max(45, Math.Min(100, Rt.Settings.OpacityPercent));
            _opacityValue.Text = _opacity.Value + "%";
            if (!_hotkey.Focused) _hotkey.Text = Rt.Settings.Hotkey;
            if (!_charName.Focused) _charName.Text = Rt.Settings.LocalPlayerName;
            _dataDir.Text = SettingsStore.DefaultDir();
            _syncing = false;
        }
    }
}
