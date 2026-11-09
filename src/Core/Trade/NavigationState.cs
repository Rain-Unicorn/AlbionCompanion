#nullable disable
using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core.Trade
{
    /// <summary>路单中的一段：当前图 → 出口八向 → 下一图。</summary>
    public class NavigationStep
    {
        public string FromMap;
        public string ToMap;
        /// <summary>出口八向（N/NE/E/SE/S/SW/W/NW），导航卡罗盘用。</summary>
        public string Direction;
        public bool RedZone;
        public string Note;
    }

    /// <summary>一条候选路线（静态路单；实时地图跟随未解锁前由用户手动下一步，规范 §5）。</summary>
    public class NavigationRoute
    {
        public string OriginCity;
        public string DestCity;
        /// <summary>走法标签：红区直穿 / 塞福绕行 / 走私者网络 / 官道直行 …</summary>
        public string FormLabel;
        /// <summary>风险等级：高危 / 中危 / 低危（不折进收益，规范 §5）。</summary>
        public string RiskLevel;
        /// <summary>红区段数。</summary>
        public int RedSegments;
        public string RiskNote;
        /// <summary>静态路单的预估耗时（分钟）。</summary>
        public int Minutes;
        public List<NavigationStep> Steps = new List<NavigationStep>();

        public string Title
        {
            get { return TradeCities.Zh(OriginCity) + " → " + TradeCities.Zh(DestCity); }
        }
    }

    /// <summary>
    /// 静态路单目录（规范 §5：实时跟随未解锁前用静态路单，手动下一步）。
    /// 只提供走法/方向/风险段这类确定性的路线骨架，不编造概率与收益；
    /// 收益由调用方传入的当前腿/装载方案决定，风险不折进收益。
    /// </summary>
    public static class TradeRouteCatalog
    {
        private const string RedZoneNote = "此段为红区，被击倒将失去全部货物";

        /// <summary>为一条腿生成候选路线：黑市 4 条、王城互跑 3 条、布雷西里恩 1 条提示路线。</summary>
        public static List<NavigationRoute> Candidates(string origin, string dest)
        {
            var list = new List<NavigationRoute>();
            if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(dest)) return list;

            if (TradeCities.IsBlackMarket(dest))
            {
                // 静态路单：走法标签/方向/红区段都是确定性骨架，风险不折进收益（规范 §5）
                list.Add(BlackMarketRoute(origin, dest, "红区直穿", "高危", 18,
                    "直穿红区走廊，路程最短、风险最高；被击倒将失去全部货物。",
                    Step(origin, "红区走廊", "E", true),
                    Step("红区走廊", "红区腹地", "SE", true),
                    Step("红区腹地", TradeCities.Caerleon, "S", true),
                    Step(TradeCities.Caerleon, dest, "E", false)));
                list.Add(BlackMarketRoute(origin, dest, "塞福绕行", "中危", 26,
                    "经塞福绕行，仅最后一段过红区关卡。",
                    Step(origin, TradeCities.Thetford, "E", false),
                    Step(TradeCities.Thetford, "红区关卡", "S", true),
                    Step("红区关卡", TradeCities.Caerleon, "W", false),
                    Step(TradeCities.Caerleon, dest, "E", false)));
                list.Add(BlackMarketRoute(origin, dest, "走私者网络", "低危", 31,
                    "走走私者网络，全程非红区，但路程最长。",
                    Step(origin, "走私者隧道", "W", false),
                    Step("走私者隧道", TradeCities.Caerleon, "S", false),
                    Step(TradeCities.Caerleon, dest, "E", false)));
                string royalTransfer = TransferCity(origin, TradeCities.Caerleon);
                list.Add(BlackMarketRoute(origin, dest, "主城转运", "中危", 24,
                    "经王城中转再进凯城，一段红区关卡。",
                    Step(origin, royalTransfer, "N", false),
                    Step(royalTransfer, "红区关卡", "E", true),
                    Step("红区关卡", TradeCities.Caerleon, "S", false),
                    Step(TradeCities.Caerleon, dest, "E", false)));
                return list;
            }

            if (string.Equals(dest, TradeCities.Brecilien, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new NavigationRoute
                {
                    OriginCity = origin,
                    DestCity = dest,
                    FormLabel = "雾门通道",
                    RiskLevel = "低危",
                    RedSegments = 0,
                    RiskNote = "布雷西里恩日均成交量约为黑市 1/10，只做可行性提示，不建议作为主力路线。",
                    Minutes = 34,
                    Steps = new List<NavigationStep>
                    {
                        Step(origin, dest, "S", false)
                    }
                });
                return list;
            }

            // 王城 ↔ 王城：官道直行 / 中转绕行 / 红区捷径
            list.Add(new NavigationRoute
            {
                OriginCity = origin,
                DestCity = dest,
                FormLabel = "官道直行",
                RiskLevel = "低危",
                RedSegments = 0,
                RiskNote = "全程非红区，可安全通行。",
                Minutes = 16,
                Steps = new List<NavigationStep>
                {
                    Step(origin, dest, "E", false)
                }
            });

            string transfer = TransferCity(origin, dest);
            list.Add(new NavigationRoute
            {
                OriginCity = origin,
                DestCity = dest,
                FormLabel = "绕行中转",
                RiskLevel = "低危",
                RedSegments = 0,
                RiskNote = "经王城中转，全程非红区，多花一段路程。",
                Minutes = 23,
                Steps = new List<NavigationStep>
                {
                    Step(origin, transfer, "N", false),
                    Step(transfer, dest, "SE", false)
                }
            });

            list.Add(new NavigationRoute
            {
                OriginCity = origin,
                DestCity = dest,
                FormLabel = "红区捷径",
                RiskLevel = "高危",
                RedSegments = 1,
                RiskNote = RedZoneNote,
                Minutes = 13,
                Steps = new List<NavigationStep>
                {
                    Step(origin, "红区走廊", "W", true),
                    Step("红区走廊", dest, "S", true)
                }
            });
            return list;
        }

        private static NavigationRoute BlackMarketRoute(string origin, string dest, string form,
            string risk, int minutes, string note, params NavigationStep[] steps)
        {
            var route = new NavigationRoute
            {
                OriginCity = origin,
                DestCity = dest,
                FormLabel = form,
                RiskLevel = risk,
                RedSegments = 0,
                RiskNote = note,
                Minutes = minutes
            };
            if (steps != null)
            {
                for (int i = 0; i < steps.Length; i++)
                {
                    route.Steps.Add(steps[i]);
                    if (steps[i].RedZone) route.RedSegments++;
                }
            }
            if (route.RedSegments == 0)
                route.RiskNote = note; // 低危路线保留走法说明
            return route;
        }

        private static NavigationStep Step(string from, string to, string direction, bool red)
        {
            return new NavigationStep
            {
                FromMap = from,
                ToMap = to,
                Direction = direction,
                RedZone = red,
                Note = red ? RedZoneNote : ""
            };
        }

        private static string TransferCity(string origin, string dest)
        {
            for (int i = 0; i < TradeCities.Royal.Length; i++)
            {
                string city = TradeCities.Royal[i];
                if (string.Equals(city, origin, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(city, dest, StringComparison.OrdinalIgnoreCase)) continue;
                return city;
            }
            return TradeCities.Thetford;
        }
    }

    /// <summary>
    /// 导航单一状态源（规范 §5/§8.9）：主窗导航卡与悬浮条**共用这一个对象**，
    /// 下一步/上一步只改它，两处各自订阅 Changed 重绘，不允许「主窗改完再推给悬浮条」。
    /// 只在 UI 线程读写。
    /// </summary>
    public class NavigationState
    {
        /// <summary>进程级共享实例（主窗导航卡与悬浮条都读它）。</summary>
        public static readonly NavigationState Shared = new NavigationState();

        public event EventHandler Changed;

        public bool IsActive;
        public string OriginCity;
        public string DestCity;
        public string FormLabel;
        public string RiskLevel;
        public string RiskNote;
        public int RedSegments;
        public int Minutes;

        public List<NavigationStep> Steps = new List<NavigationStep>();

        /// <summary>当前已走过的段下标（0 = 第一段进行中）。</summary>
        public int StepIndex;

        /// <summary>已抵达终点。</summary>
        public bool Arrived;

        /// <summary>预估剩余收益（由装载方案/腿传入；风险不折进收益）。</summary>
        public long EstimatedProfit;

        public int Pieces;

        public double WeightKg;

        public int StepCount { get { return Steps.Count; } }

        public bool HasPrev { get { return IsActive && StepIndex > 0; } }

        public bool HasNext { get { return IsActive && !Arrived; } }

        public NavigationStep CurrentStep
        {
            get
            {
                if (!IsActive || Steps.Count == 0) return null;
                if (StepIndex < 0) return Steps[0];
                if (StepIndex >= Steps.Count) return Steps[Steps.Count - 1];
                return Steps[StepIndex];
            }
        }

        public string Title
        {
            get
            {
                if (!IsActive) return "";
                return TradeCities.Zh(OriginCity) + " → " + TradeCities.Zh(DestCity);
            }
        }

        /// <summary>导航卡当前段文案：当前图 · 出口方位 → 下一图。</summary>
        public string CurrentText
        {
            get
            {
                var step = CurrentStep;
                if (step == null) return "";
                return TradeCities.Zh(step.FromMap) + " · 出口 " + step.Direction + " → " + TradeCities.Zh(step.ToMap);
            }
        }

        public string ProgressText
        {
            get
            {
                if (!IsActive || Steps.Count == 0) return "";
                if (Arrived) return "已抵达 " + TradeCities.Zh(DestCity);
                return "第 " + (StepIndex + 1) + " / " + Steps.Count + " 步";
            }
        }

        /// <summary>下一步提示：红区段必须给出后果，不写「可能/大概」。</summary>
        public string NextHint
        {
            get
            {
                if (!IsActive) return "";
                if (Arrived) return "已抵达 " + TradeCities.Zh(DestCity) + "，导航完成";
                var current = CurrentStep;
                if (current != null && current.RedZone)
                    return "红区：" + (string.IsNullOrEmpty(current.Note) ? "被击倒将失去全部货物" : current.Note);
                if (HasNext)
                {
                    var next = Steps[StepIndex + 1];
                    return "下一步：离开 " + TradeCities.Zh(next.FromMap) + " 往 " + next.Direction;
                }
                return "按「下一步」完成本段";
            }
        }

        /// <summary>当前段是否红区（导航卡/悬浮条/候选行三处同时变红用）。</summary>
        public bool IsCurrentRed
        {
            get
            {
                var step = CurrentStep;
                return step != null && step.RedZone;
            }
        }

        public void Activate(NavigationRoute route, long estimatedProfit, int pieces, double weightKg)
        {
            if (route == null) return;
            OriginCity = route.OriginCity;
            DestCity = route.DestCity;
            FormLabel = route.FormLabel;
            RiskLevel = route.RiskLevel;
            RiskNote = route.RiskNote;
            RedSegments = route.RedSegments;
            Minutes = route.Minutes;
            Steps = new List<NavigationStep>(route.Steps);
            StepIndex = 0;
            Arrived = false;
            EstimatedProfit = estimatedProfit;
            Pieces = pieces;
            WeightKg = weightKg;
            IsActive = Steps.Count > 0;
            Raise();
        }

        /// <summary>会员档/负重变化后重算收益估计（不重置进度）。</summary>
        public void UpdateEstimates(long estimatedProfit, int pieces, double weightKg)
        {
            EstimatedProfit = estimatedProfit;
            Pieces = pieces;
            WeightKg = weightKg;
            Raise();
        }

        public void Next()
        {
            if (!IsActive || Arrived) return;
            if (StepIndex + 1 < Steps.Count) StepIndex++;
            else Arrived = true;
            Raise();
        }

        public void Prev()
        {
            if (!IsActive) return;
            if (Arrived) { Arrived = false; Raise(); return; }
            if (StepIndex > 0) { StepIndex--; Raise(); }
        }

        public void Deactivate()
        {
            if (!IsActive && Steps.Count == 0) return;
            IsActive = false;
            Arrived = false;
            StepIndex = 0;
            Steps = new List<NavigationStep>();
            EstimatedProfit = 0;
            Pieces = 0;
            WeightKg = 0d;
            Raise();
        }

        private void Raise()
        {
            var handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
