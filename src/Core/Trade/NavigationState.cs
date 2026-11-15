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

        /// <summary>
        /// 出口八向（N/NE/E/SE/S/SW/W/NW），导航卡罗盘用。
        /// **空串 = 方位未知**：地图拓扑数据来源未知（见 <see cref="TradeRouteCatalog"/>），
        /// 当前实现一律给空串，不猜方位；罗盘全部不高亮并显式标注「方位未知」。
        /// </summary>
        public string Direction;

        /// <summary>本段是否红区；仅在 <see cref="RedKnown"/> 为 true 时才有意义。</summary>
        public bool RedZone;

        /// <summary>红区属性是否已知（false = 不臆造，UI 显示「红区待核实」而不是「安全」）。</summary>
        public bool RedKnown = true;

        public string Note;
    }

    /// <summary>一条候选路线（静态路单；实时地图跟随未解锁前由用户手动下一步，规范 §5）。</summary>
    public class NavigationRoute
    {
        public string OriginCity;
        public string DestCity;

        /// <summary>走法标签。当前一律「城市级骨架」——中间地图拓扑未接入，不编造走法名。</summary>
        public string FormLabel;

        /// <summary>风险等级：高危 / 中危 / 低危 / **未知**（不折进收益，规范 §5）。</summary>
        public string RiskLevel;

        /// <summary>红区段数；仅在 <see cref="RedKnown"/> 为 true 时才有意义。</summary>
        public int RedSegments;

        /// <summary>红区属性是否已知（false = UI 显示「红区待核实」，不得显示成「安全」）。</summary>
        public bool RedKnown = true;

        public string RiskNote;

        /// <summary>静态路单的预估耗时（分钟）；**负数 = 未知**，UI 显示「耗时未知」不编造数字。</summary>
        public int Minutes = -1;

        /// <summary>耗时是否已知（false 时不显示具体分钟数）。</summary>
        public bool MinutesKnown;

        public List<NavigationStep> Steps = new List<NavigationStep>();

        public string Title
        {
            get { return TradeCities.Zh(OriginCity) + " → " + TradeCities.Zh(DestCity); }
        }
    }

    /// <summary>
    /// 静态路单目录（规范 §5：实时跟随未解锁前用静态路单，手动下一步）。
    /// 收益由调用方传入的当前腿/装载方案决定，风险不折进收益。
    /// </summary>
    /// <remarks>
    /// **地图拓扑数据来源未知**：仓库内没有任何 portal / zone / 地图数据（`data/` 为空，
    /// AODP 只提供 prices / history / gold 三端点，均不含拓扑），设计稿 §3.4 要求的
    /// 「当前图 → 出口八向 → 下一图」五步路单因此**没有数据基础**。
    /// 按规格「不许臆造地图拓扑、不许猜 zone id 对应关系」，本目录只给**城市级骨架**：
    /// 只声明两件可从城市常量与交易数据确定的事——
    /// ① 黑市位于凯尔莱恩（`Caerleon` 与 `Black Market` 是同城两个市场）；
    /// ② 路线的起点城与终点城。
    /// 中间地图名、出口方位、红区属性、耗时一律置**未知**（`Direction=""` / `RedKnown=false` /
    /// `MinutesKnown=false`），由用户按「下一步」手动推进。接入真实拓扑后只需改本类的
    /// <see cref="Candidates"/>，调用方（UI / 悬浮条）不感知。
    /// </remarks>
    public static class TradeRouteCatalog
    {
        /// <summary>红区后果文案（唯一的既知事实：红区被击倒会掉货，与拓扑无关）。</summary>
        public const string RedZoneNote = "此段为红区，被击倒将失去全部货物";

        /// <summary>走法标签：当前无中间地图数据，统一标为城市级骨架。</summary>
        private const string CitySkeletonLabel = "城市级骨架";

        /// <summary>风险等级：拓扑未知时不得标成高危/低危，只能标未知。</summary>
        private const string UnknownRisk = "未知";

        /// <summary>
        /// 为一条腿生成候选路线。
        /// 黑市腿 = 2 段城市级骨架（起点城 → 凯城 → 黑市）；王城互跑 / 布雷西里恩 = 1 段。
        /// 每段的方向、红区属性、耗时均未知，不编造。
        /// </summary>
        public static List<NavigationRoute> Candidates(string origin, string dest)
        {
            var list = new List<NavigationRoute>();
            if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(dest)) return list;

            if (TradeCities.IsBlackMarket(dest))
            {
                // 唯一的结构性事实：黑市在凯城，所以必须经过凯城一次。
                list.Add(CityRoute(origin, dest,
                    "城市级骨架：起点城 → 凯城 → 黑市。中间地图、出口方位、红区属性与耗时均未接入数据，需手动逐步推进。",
                    Step(origin, TradeCities.Caerleon),
                    Step(TradeCities.Caerleon, dest)));
                return list;
            }

            if (string.Equals(dest, TradeCities.Brecilien, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(CityRoute(origin, dest,
                    "布雷西里恩日均成交量约为黑市 1/10，只做可行性提示，不建议作为主力路线；中间地图与耗时未接入数据。",
                    Step(origin, dest)));
                return list;
            }

            // 王城 ↔ 王城：城市级单段骨架
            list.Add(CityRoute(origin, dest,
                "城市级骨架：起点城 → 目的城。中间地图、出口方位、红区属性与耗时均未接入数据，需手动逐步推进。",
                Step(origin, dest)));
            return list;
        }

        /// <summary>组装一条「城市级骨架」路线：风险/耗时/方位全部标未知。</summary>
        private static NavigationRoute CityRoute(string origin, string dest, string note,
            params NavigationStep[] steps)
        {
            var route = new NavigationRoute
            {
                OriginCity = origin,
                DestCity = dest,
                FormLabel = CitySkeletonLabel,
                RiskLevel = UnknownRisk,
                RedSegments = 0,
                RedKnown = false,
                RiskNote = note,
                Minutes = -1,
                MinutesKnown = false
            };
            if (steps != null)
            {
                for (int i = 0; i < steps.Length; i++)
                {
                    route.Steps.Add(steps[i]);
                    if (steps[i].RedKnown && steps[i].RedZone) route.RedSegments++;
                }
            }
            return route;
        }

        /// <summary>
        /// 构造一段。方位留空（未知）、红区属性标未知——不猜 E/SE/S，不猜红区与否。
        /// </summary>
        private static NavigationStep Step(string from, string to)
        {
            return new NavigationStep
            {
                FromMap = from,
                ToMap = to,
                Direction = "",
                RedZone = false,
                RedKnown = false,
                Note = ""
            };
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

        /// <summary>红区属性是否已知（false = 全程显示「红区待核实」，不得显示成安全）。</summary>
        public bool RedKnown = true;

        /// <summary>耗时是否已知（false = 显示「耗时未知」，不编造分钟数）。</summary>
        public bool MinutesKnown;

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

        /// <summary>导航卡当前段文案：当前图 · 出口方位 → 下一图（方位未知时显式标注，不显示空方位）。</summary>
        public string CurrentText
        {
            get
            {
                var step = CurrentStep;
                if (step == null) return "";
                string dir = string.IsNullOrEmpty(step.Direction) ? "方位未知" : "出口 " + step.Direction;
                return TradeCities.Zh(step.FromMap) + " · " + dir + " → " + TradeCities.Zh(step.ToMap);
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

        /// <summary>下一步提示：红区段必须给出后果，不写「可能/大概」；方位未知时明说未知。</summary>
        public string NextHint
        {
            get
            {
                if (!IsActive) return "";
                if (Arrived) return "已抵达 " + TradeCities.Zh(DestCity) + "，导航完成";
                var current = CurrentStep;
                // 红区只在「已知是红区」时才给后果；属性未知时不得假装安全，也不得假装红区。
                if (current != null && current.RedKnown && current.RedZone)
                    return "红区：" + (string.IsNullOrEmpty(current.Note)
                        ? TradeRouteCatalog.RedZoneNote : current.Note);
                if (HasNext)
                {
                    var next = Steps[StepIndex + 1];
                    string dir = string.IsNullOrEmpty(next.Direction) ? "" : " 往 " + next.Direction;
                    return "下一步：从 " + TradeCities.Zh(next.FromMap) + dir
                           + " 前往 " + TradeCities.Zh(next.ToMap) + "（中间地图未接入，按游戏内路线走）";
                }
                return "按「下一步」完成本段";
            }
        }

        /// <summary>当前段是否红区（导航卡/悬浮条/候选行三处同时变红用）。未知不算红。</summary>
        public bool IsCurrentRed
        {
            get
            {
                var step = CurrentStep;
                return step != null && step.RedKnown && step.RedZone;
            }
        }

        /// <summary>当前段红区属性是否未知（UI 据此显示「红区待核实」，避免把未知读成安全）。</summary>
        public bool IsCurrentRedUnknown
        {
            get
            {
                var step = CurrentStep;
                return step != null && !step.RedKnown;
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
            RedKnown = route.RedKnown;
            Minutes = route.Minutes;
            MinutesKnown = route.MinutesKnown;
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
