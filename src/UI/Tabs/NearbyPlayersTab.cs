using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>附近玩家：实时显示抓包到的附近玩家。</summary>
    public class NearbyPlayersTab : TabBase
    {
        private readonly Label _summary;
        private readonly SimpleTable _table;
        private readonly Timer _refresh;

        public NearbyPlayersTab(Runtime runtime)
            : base(runtime)
        {
            Card(0, 0, 580, 56);
            CardText("附近玩家（实时抓包）", 14, 8, 300, 20, Th.Text, 11f, true);
            _summary = CardText("玩家 0 人", 14, 30, 552, 18, Th.Sub, 9f, false);

            Card(0, 64, 580, 0);

            Heads(new string[] { "玩家名", "公会", "首次出现", "最近出现" },
                  new float[] { 1.4f, 1.2f, 0.9f, 0.9f },
                  new bool[] { false, false, false, false },
                  0, 80, 580, 36);
            _table = Table(new float[] { 1.4f, 1.2f, 0.9f, 0.9f },
                           new bool[] { false, false, false, false },
                           0, 104, 580, 520);
            _table.EmptyText = "等待玩家数据...";

            GameEvents.OnUpdate += delegate { try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(Render)); } catch { } };

            _refresh = new Timer();
            _refresh.Interval = 2000;
            _refresh.Tick += delegate { Render(); };
            _refresh.Start();
            Render();
        }

        private void Render()
        {
            if (IsDisposed) return;

            // 清理超过 60 秒没活动的玩家
            // CD 枚举期间其它线程可写，因此先收集 key，再用 TryRemove 原子删除；
            // 删除前复核 LastSeen，避免把刚被刷新的条目误删。
            var expired = new List<long>();
            foreach (var kv in GameEvents.Players)
            {
                if ((DateTime.Now - kv.Value.LastSeen).TotalSeconds > 60)
                    expired.Add(kv.Key);
            }
            foreach (var id in expired)
            {
                if (GameEvents.Players.TryGetValue(id, out var p) &&
                    (DateTime.Now - p.LastSeen).TotalSeconds > 60)
                {
                    GameEvents.Players.TryRemove(id, out _);
                }
            }

            var list = new List<GameEvents.PlayerEntry>(GameEvents.Players.Values);
            list.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));

            _summary.Text = "玩家 " + list.Count + " 人";

            var rows = new List<string[]>();
            foreach (var p in list)
            {
                rows.Add(new string[]
                {
                    p.Name ?? "-",
                    string.IsNullOrEmpty(p.Guild) ? "-" : p.Guild,
                    p.FirstSeen.ToString("HH:mm:ss"),
                    p.LastSeen.ToString("HH:mm:ss")
                });
            }
            _table.SetRows(rows, new List<Image>());
        }
    }
}
