using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AlbionCompanion.Core;
using AlbionCompanion.UI;

/// <summary>离线渲染检查：不抢焦点、不碰屏幕，把面板各页导成 PNG。</summary>
static class VisualCheck
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string outDir = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview");
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

        var settings = new AppSettings();
        var rt = new Runtime(settings, Path.Combine(outDir, "preview-settings.json"));

        // 造一点数据，避免预览全是空状态
        var now = DateTime.Now;
        rt.Income.Add("采集", "T4 木材", 320, 480, now.AddMinutes(-46));
        rt.Income.Add("采集", "T5 矿石", 88, 2100, now.AddMinutes(-38));
        rt.Income.Add("刷怪", "腐蚀地下城掉落", 14, 24000, now.AddMinutes(-26));
        rt.Income.Add("交易", "T6 背包", 6, 38000, now.AddMinutes(-12));
        rt.Income.Add("副本", "阵营营地奖励", 3, 65000, now.AddMinutes(-5));

        var members = new object[][]
        {
            new object[] { "赤炎游侠", 486000.0, 12000.0, 132000.0, 180.0 },
            new object[] { "黑塔守卫", 212000.0, 268000.0, 640000.0, 180.0 },
            new object[] { "霜语", 398000.0, 0.0, 96000.0, 180.0 },
            new object[] { "青柠", 96000.0, 512000.0, 78000.0, 180.0 }
        };
        for (int i = 0; i < members.Length; i++)
        {
            var m = new MemberStat();
            m.Name = (string)members[i][0];
            m.Damage = (double)members[i][1];
            m.Healing = (double)members[i][2];
            m.Taken = (double)members[i][3];
            m.Seconds = (double)members[i][4];
            rt.AddMember(m);
        }

        long[] costs = { 420000, 180000, 260000, 320000, 90000, 150000, 480000, 40000 };
        for (int i = 0; i < costs.Length && i < rt.Slots.Count; i++) rt.Slots[i].Cost = costs[i];

        // 给跑商页造点跨城差价数据
        rt.Db.Upsert(new MarketQuote { ItemId = "T4_BAG", City = "Caerleon", Quality = 1, SellMin = 10000, BuyMax = 2000, SellMinDate = "2026-11-05T10:00:00" });
        rt.Db.Upsert(new MarketQuote { ItemId = "T4_BAG", City = "Bridgewatch", Quality = 1, SellMin = 15000, BuyMax = 6000, SellMinDate = "2026-11-05T10:00:00" });
        rt.Db.Upsert(new MarketQuote { ItemId = "T6_MAIN", City = "Martlock", Quality = 2, SellMin = 30000, BuyMax = 12000, SellMinDate = "2026-11-05T10:00:00" });
        rt.Db.Upsert(new MarketQuote { ItemId = "T6_MAIN", City = "Lymhurst", Quality = 2, SellMin = 60000, BuyMax = 22000, SellMinDate = "2026-11-05T10:00:00" });

        PanelForm.PreviewMode = true;

        var panel = new PanelForm(rt);
        panel.StartPosition = FormStartPosition.Manual;
        panel.Location = new Point(-4000, 0);
        panel.TopMost = false;
        panel.Show();
        Application.DoEvents();

        string[] names = { "overview", "income", "combat", "market", "trade", "harvest", "localdb", "autoupdate", "build", "settings" };
        for (int i = 0; i < names.Length; i++)
        {
            panel.SelectTab(i);
            Application.DoEvents();
            Thread.Sleep(120);
            using (var bmp = new Bitmap(panel.Width, panel.Height))
            {
                panel.DrawToBitmap(bmp, new Rectangle(0, 0, panel.Width, panel.Height));
                bmp.Save(Path.Combine(outDir, "tab-" + i + "-" + names[i] + ".png"), ImageFormat.Png);
            }
        }

        panel.Hide();

        var overlay = new OverlayForm(rt);
        overlay.StartPosition = FormStartPosition.Manual;
        overlay.Location = new Point(-4000, 0);
        overlay.ShowInTaskbar = false;
        overlay.Show();
        Application.DoEvents();
        Thread.Sleep(120);
        using (var bmp = new Bitmap(overlay.Width, overlay.Height))
        {
            overlay.DrawToBitmap(bmp, new Rectangle(0, 0, overlay.Width, overlay.Height));
            bmp.Save(Path.Combine(outDir, "overlay.png"), ImageFormat.Png);
        }
        overlay.Hide();

        // 交互验证：向悬浮条窗口投递真实的鼠标按下/抬起消息，确认点击会触发展开事件
        bool expandFired = false;
        overlay.ExpandRequested += delegate { expandFired = true; };
        PostMessage(overlay.Handle, 0x0201, new IntPtr(1), IntPtr.Zero);
        PostMessage(overlay.Handle, 0x0202, IntPtr.Zero, IntPtr.Zero);
        Application.DoEvents();
        Thread.Sleep(150);
        Application.DoEvents();
        Console.WriteLine("click-expand-event=" + expandFired);

        // 热键解析与注册验证
        uint mods = 0;
        uint vk = 0;
        Console.WriteLine("hotkey-parse=" + HotkeyWindow.TryParse("Ctrl+Alt+A", out mods, out vk)
                          + " mods=" + mods + " vk=" + vk);
        var hk = new HotkeyWindow();
        Console.WriteLine("hotkey-register=" + hk.Register("Ctrl+Alt+F10"));
        hk.Dispose();

        Console.WriteLine("preview written to " + outDir);
    }
}
