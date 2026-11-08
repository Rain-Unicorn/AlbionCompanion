using System;
using AlbionCompanion.Core.Income;

namespace AlbionCompanion.Core
{
    /// <summary>本地玩家身份的三态（设计 §3.2）。</summary>
    public enum IdentityState { Unknown, Candidate, Confirmed }

    /// <summary>把身份推进到 Confirmed 的最后一条证据来源。</summary>
    public enum ConfirmSource { JoinResponse, UpdateMoney, ManualName, NewCharacterMatch }

    /// <summary>evt=62 拾取银币的入账裁决（设计 §3.5）。</summary>
    public enum TakeSilverDisposition
    {
        /// <summary>已确认是我捡的 → IncomeTracker.AddConfirmed。</summary>
        CountAsMine,
        /// <summary>候选期间挂起 → IncomeTracker.AddPending。</summary>
        Pending,
        /// <summary>不是我（Confirmed 下他人拾取 / Candidate 下第三者拾取）→ 不入账。</summary>
        Ignore
    }

    /// <summary>
    /// 本地玩家身份状态机（R1 / 方案 4，设计 §3.1-§3.4）。
    /// 唯一写入入口、唯一判定出口；「我是谁」不再由散落的 static 字段承载。
    /// 证据强度：JoinResponse（服务器权威）&gt; evt=81 余额严格匹配 &gt; evt=62 拾取（最弱）&gt; 手选名字。
    /// </summary>
    /// <remarks>
    /// 与设计 §3.3 表格的一处刻意偏离（依 L1 风险规则与 T-03/T-04 测试口径）：
    /// evt=81 的 p0（ObjectId）**不单独**构成确认证据——只有「候选 id 与 p0 相同，
    /// 且余额差与候选期间挂起的银币严格相等」才晋升。evt=81 在 Confirmed 态遇到不同 p0
    /// 只记冲突日志、不改状态（L1：推翻只允许由已确认的自身特征触发，其余一律不改）。
    /// 原因：evt=81 若对他人余额更新也可见，凭 p0 单独确认/推翻会让身份在多人场景来回跳。
    /// </remarks>
    public static class LocalPlayerIdentity
    {
        private static readonly object _gate = new object();

        private static IdentityState _state = IdentityState.Unknown;
        private static long? _confirmedId;
        private static string? _confirmedName;         // 可为空：允许「有 id 无名」的确认态
        private static ConfirmSource? _source;
        private static string? _lastSelfName;          // 最近一次已知自己名字（推翻降级后仍保留，供 evt=29 匹配）
        private static string? _manualName;            // 设置页「我的角色」
        private static long? _candidateId;
        private static DateTime _candidateSince;
        private static int _candidateHits;             // 候选拾取次数
        private static long _candidateSilver;          // 挂起银币合计（原始 FixPoint 值）
        private static long _accSinceMoney;            // 自上一条 evt=81 以来候选拾取累计（原始 FixPoint，余额比对窗口）
        private static long? _lastBalance;             // evt=81 银币余额基线（原始 FixPoint）

        /// <summary>状态变化 / 冲突日志的对外通知（ChatSniffer 订阅转发到状态栏）。</summary>
        public static event Action<string>? OnStatus;

        // ---- 状态读取（任意线程；内部加锁、不触发事件） ----

        public static IdentityState State { get { lock (_gate) { return _state; } } }

        public static long? ConfirmedObjectId { get { lock (_gate) { return _confirmedId; } } }

        public static string? ConfirmedName { get { lock (_gate) { return _confirmedName; } } }

        /// <summary>当前确认的证据来源；未确认时为 null（T-03 断言用）。</summary>
        public static ConfirmSource? CurrentSource { get { lock (_gate) { return _source; } } }

        public static long? CandidateObjectId { get { lock (_gate) { return _candidateId; } } }

        public static int CandidateSilverHits { get { lock (_gate) { return _candidateHits; } } }

        /// <summary>候选期间挂起的银币额（显示单位：原始值 ÷ 10000），供 UI 三态徽标。</summary>
        public static long CandidateSilverTotal { get { lock (_gate) { return _candidateSilver / 10000; } } }

        public static string? ManualName { get { lock (_gate) { return _manualName; } } }

        /// <summary>
        /// 「我」的展示名（R2 负键空间 key=0 的显示名）：已确认且有名字用真名，
        /// 否则「我（识别中）」——不占用「未知玩家」语义。
        /// </summary>
        public static string DisplaySelfName
        {
            get { lock (_gate) { return string.IsNullOrEmpty(_confirmedName) ? "我（识别中）" : _confirmedName; } }
        }

        /// <summary>判定出口：causerId 是否为已确认的本地玩家。</summary>
        public static bool IsSelf(long causerId)
        {
            lock (_gate)
            {
                return _state == IdentityState.Confirmed && causerId > 0 && _confirmedId == causerId;
            }
        }

        // ---- 写入入口（仅抓包线程 / UI 保存按钮调用） ----

        /// <summary>
        /// JoinResponse（服务器权威，最高证据强度）。确认或纠正当前身份；
        /// 与已有 confirmed 冲突时覆盖并记 [身份冲突]；与 candidate 不同则丢弃候选及其 pending。
        /// </summary>
        public static void OnJoinResponse(long? objectId, string? name)
        {
            if (objectId == null || objectId <= 0) return;
            lock (_gate)
            {
                bool wasCandidate = _state == IdentityState.Candidate;
                long? oldCandidate = _candidateId;
                long? oldConfirmed = _confirmedId;
                string? oldName = _confirmedName;

                if (_state == IdentityState.Confirmed && _confirmedId != null && _confirmedId != objectId)
                {
                    Log("[身份冲突] JoinResponse ObjectId=" + objectId + " 与已确认 " + _confirmedId + " 不同，按服务器权威覆盖");
                    // R2/C4（设计 §4.4 RemovePlayer）：旧 key=0 的累计是按旧 id 归因的，权威纠正后视为脏数据清掉
                    GameEvents.DamageTracker.RemovePlayer(0);
                }

                ConfirmCore(objectId.Value, name, ConfirmSource.JoinResponse, resolvePending: true);

                // 候选与 JoinResponse 不同 → 候选及其 pending 整体作废（ConfirmCore 已按新 id 分流）
                if (wasCandidate && oldCandidate != null && oldCandidate != objectId)
                {
                    Log("[身份] 候选 ObjectId=" + oldCandidate + " 与 JoinResponse 不符，候选与挂起收益已丢弃");
                }
                if (_state == IdentityState.Confirmed && oldConfirmed == objectId && oldName != null && string.IsNullOrEmpty(name))
                {
                    // 同 id 重下发但没带名字：保留旧名字
                    _confirmedName = oldName;
                    _lastSelfName = oldName;
                }
            }
        }

        /// <summary>
        /// evt=81 UpdateMoney：p0=ObjectId，silverRaw=当前余额（原始 FixPoint）。
        /// 只做两件事：推进余额基线；候选 id 与 p0 相同且余额差与挂起银币严格相等时晋升（方案 4 / T-03）。
        /// 不相等只记日志、不确认、候选保留（T-04）。
        /// </summary>
        public static void OnUpdateMoney(long objectId, long silverRaw)
        {
            if (objectId <= 0) return;
            lock (_gate)
            {
                long? delta = _lastBalance.HasValue ? silverRaw - _lastBalance.Value : (long?)null;
                _lastBalance = silverRaw;

                bool promoted = false;
                if (_state == IdentityState.Candidate && _candidateId == objectId
                    && delta.HasValue && _accSinceMoney > 0
                    && Math.Abs(delta.Value - _accSinceMoney) == 0)
                {
                    // 余额差与候选期间挂起银币严格相等 → 候选晋升（ConfirmSource=UpdateMoney）
                    ConfirmCore(objectId, _lastSelfName, ConfirmSource.UpdateMoney, resolvePending: true);
                    promoted = true;
                }
                else if (_state == IdentityState.Candidate && _candidateId == objectId && delta.HasValue)
                {
                    Log("[身份] evt=81 余额差 " + delta.Value + " != 候选挂起 " + _accSinceMoney
                        + "（可能含队伍分成干扰），本轮不确认，候选保留");
                }
                else if (_state == IdentityState.Confirmed && _confirmedId != null && _confirmedId != objectId)
                {
                    // L1：其余情形一律不改——不凭 evt=81 的 p0 单独确认或推翻
                    Log("[身份冲突] evt=81 ObjectId=" + objectId + " 与已确认 " + _confirmedId + " 不同（可能是队友的余额更新），不改变身份");
                }

                // 余额基线已移动：比对窗口重开（挂起记录仍留在 IncomeTracker，仅比对累计清零）
                _accSinceMoney = 0;
                if (promoted)
                    Log("[身份] 候选经 evt=81 余额严格匹配晋升为 Confirmed（ObjectId=" + objectId + "）");
            }
        }

        /// <summary>
        /// evt=62 TakeSilver：弱证据。Confirmed+本人 → CountAsMine；
        /// Unknown/Candidate 首个拾取者 → 置候选并挂起（Pending）；其余 → Ignore。
        /// silverAfterTax 为原始 FixPoint 值（供余额严格比对）。
        /// </summary>
        public static TakeSilverDisposition OnTakeSilver(long pickerId, long silverAfterTax)
        {
            if (pickerId <= 0) return TakeSilverDisposition.Ignore;
            lock (_gate)
            {
                if (_state == IdentityState.Confirmed)
                {
                    if (_confirmedId == pickerId) return TakeSilverDisposition.CountAsMine;
                    return TakeSilverDisposition.Ignore; // 别人的钱
                }
                if (_state == IdentityState.Unknown)
                {
                    _state = IdentityState.Candidate;
                    _candidateId = pickerId;
                    _candidateSince = DateTime.UtcNow;
                    _candidateHits = 1;
                    _candidateSilver = Math.Max(0, silverAfterTax);
                    _accSinceMoney = Math.Max(0, silverAfterTax);
                    Log("[身份] 首个拾取者 ObjectId=" + pickerId + " 记为候选（弱证据，等待 evt=81/JoinResponse/名字确认）");
                    return TakeSilverDisposition.Pending;
                }
                // Candidate
                if (_candidateId == pickerId)
                {
                    _candidateHits++;
                    _candidateSilver += Math.Max(0, silverAfterTax);
                    _accSinceMoney += Math.Max(0, silverAfterTax);
                    return TakeSilverDisposition.Pending;
                }
                // ⑤ 候选之外的拾取者：丢弃这笔，但不动候选（可能只是队友在捡）
                return TakeSilverDisposition.Ignore;
            }
        }

        /// <summary>
        /// evt=29 NewCharacter：推翻 / 绑定。
        /// ① Candidate 且该 ObjectId 就是候选、名字又不是自己 → 推翻候选（拾取者是别人），pending 全丢弃，回 Unknown；
        /// ②/③ 名字命中 manualName（或 Confirmed 有 id 无名时补名）→ 确认并回填 pending。
        /// Confirmed 的推翻只允许「同 ObjectId 不同名」或「同名不同 ObjectId」（L1 / T-05）。
        /// </summary>
        public static void OnNewCharacter(long objectId, string? name)
        {
            if (objectId <= 0 || string.IsNullOrEmpty(name)) return;
            lock (_gate)
            {
                if (_state == IdentityState.Confirmed)
                {
                    if (_confirmedId == objectId && name != _confirmedName)
                    {
                        // 同一 ObjectId 出现不同名字：旧确认可疑 → 降级为 Candidate（T-05 分支 2）
                        DemoteToCandidate(objectId);
                        Log("[身份] NewCharacter ObjectId=" + objectId + " 名字由 " + _confirmedName + " 变为 " + name
                            + "，已确认身份被推翻，降级为候选等待再确认");
                        return;
                    }
                    if (!string.IsNullOrEmpty(_confirmedName) && name == _confirmedName && _confirmedId != objectId)
                    {
                        // 同名不同 ObjectId（同一人重连）：推翻 → 候选
                        DemoteToCandidate(objectId);
                        Log("[身份] NewCharacter 名字 " + name + " 命中已确认名字但 ObjectId 不同（" + objectId + " ≠ " + _confirmedId
                            + "，疑似重连），降级为候选");
                        return;
                    }
                    if (_confirmedId == objectId && string.IsNullOrEmpty(_confirmedName))
                    {
                        // 有 id 无名的确认态：补名字，不改状态
                        _confirmedName = name;
                        _lastSelfName = name;
                        Log("[身份] NewCharacter 补充已确认身份的名字：" + name);
                    }
                    // 其余（路人）：一律不改（T-05 分支 1）
                    return;
                }

                if (_state == IdentityState.Candidate)
                {
                    if (_candidateId == objectId && name != _manualName && name != _lastSelfName)
                    {
                        // ① 候选本人现身且名字对不上 → 候选是别人，pending 全丢弃
                        long dropped = _candidateSilver / 10000;
                        _state = IdentityState.Unknown;
                        _candidateId = null;
                        _candidateHits = 0;
                        _candidateSilver = 0;
                        _accSinceMoney = 0;
                        IncomeTracker.DiscardPending();
                        Log("[身份] NewCharacter ObjectId=" + objectId + "（" + name + "）证明候选另有其人，候选与挂起 "
                            + dropped + " 银币已丢弃，回到未识别");
                        return;
                    }
                    if (!string.IsNullOrEmpty(_manualName) && name == _manualName)
                    {
                        // ② 名字命中手选名 → 确认并回填 pending
                        ConfirmCore(objectId, name, ConfirmSource.NewCharacterMatch, resolvePending: true);
                        Log("[身份] NewCharacter（" + name + "）命中「我的角色」，已确认 ObjectId=" + objectId + " 并回填挂起收益");
                        return;
                    }
                    return; // 与候选无关的路人：不改
                }

                // Unknown
                if (!string.IsNullOrEmpty(_manualName) && name == _manualName)
                {
                    ConfirmCore(objectId, name, ConfirmSource.NewCharacterMatch, resolvePending: false);
                    Log("[身份] NewCharacter（" + name + "）命中「我的角色」，直接确认 ObjectId=" + objectId);
                }
            }
        }

        /// <summary>
        /// 设置页「我的角色」：缓存待用。已有 confirmed 且名字不符 → 只记冲突日志不覆盖
        /// （避免手滑毁掉服务器权威）；等下一次 evt=29 名字命中即绑定 ObjectId。
        /// </summary>
        public static void SetManualName(string? name)
        {
            string trimmed = name == null ? "" : name.Trim();
            lock (_gate)
            {
                _manualName = trimmed;
                if (_state == IdentityState.Confirmed && !string.IsNullOrEmpty(trimmed)
                    && !string.IsNullOrEmpty(_confirmedName) && trimmed != _confirmedName)
                {
                    Log("[身份冲突] 手选名字「" + trimmed + "」与已确认「" + _confirmedName + "」不符，保留服务器确认结果");
                }
                else if (trimmed.Length > 0)
                {
                    Log("[身份] 已记录「我的角色」：" + trimmed + "（等待 NewCharacter 事件绑定 ObjectId）");
                }
            }
        }

        /// <summary>全部清零（测试隔离 / 会话重置用）。</summary>
        public static void Reset()
        {
            lock (_gate)
            {
                _state = IdentityState.Unknown;
                _confirmedId = null;
                _confirmedName = null;
                _source = null;
                _lastSelfName = null;
                _manualName = null;
                _candidateId = null;
                _candidateSince = default(DateTime);
                _candidateHits = 0;
                _candidateSilver = 0;
                _accSinceMoney = 0;
                _lastBalance = null;
            }
        }

        // ---- 内部 ----

        /// <summary>
        /// 唯一确认入口（设计验收项：确认与 pending 分流必须在同一次调用序列内完成，原子）。
        /// C4 将在同一序列内追加 DamageStatsTracker.RenameKey；本批先覆盖 IncomeTracker 分流。
        /// 调用方必须已持有 _gate。
        /// </summary>
        private static void ConfirmCore(long objectId, string? name, ConfirmSource source, bool resolvePending)
        {
            _state = IdentityState.Confirmed;
            _confirmedId = objectId;
            if (!string.IsNullOrEmpty(name))
            {
                _confirmedName = name;
                _lastSelfName = name;
            }
            else
            {
                _confirmedName = null; // 允许「有 id 无名」
            }
            _source = source;

            long? candidateId = _candidateId;
            _candidateId = null;
            _candidateHits = 0;
            _candidateSilver = 0;
            _accSinceMoney = 0;

            if (resolvePending)
            {
                // pending 按 pickerId 分流：是我（新确认 id）→ 入账，不是我 → 丢弃；
                // 挂起条目从候选起就挂在 IncomeTracker，这里一次性裁决，无中间可读状态
                IncomeTracker.ResolvePending(objectId, candidateId);
            }

            // R2/C4（设计 §936 原子性）：确认前该 ObjectId 若已以「普通玩家」身份进了 tracker
            // （key=objectId），确认后新伤害会记到 key=0——同一人两条并列（§9.7「我出现两条」）。
            // 必须与身份写入同一次锁序列内合并，不暴露裸的确认入口给 UI 分两次调。
            GameEvents.DamageTracker.RenameKey(objectId, 0, string.IsNullOrEmpty(name) ? null : name);
        }

        /// <summary>Confirmed → Candidate 降级（T-05 推翻路径）；候选期没有 pending（Confirmed 态收益直接入账）。</summary>
        private static void DemoteToCandidate(long objectId)
        {
            _state = IdentityState.Candidate;
            _confirmedId = null;
            _source = null;
            _candidateId = objectId;
            _candidateSince = DateTime.UtcNow;
            _candidateHits = 0;
            _candidateSilver = 0;
            _accSinceMoney = 0;
            // _confirmedName 清空但 _lastSelfName 保留（供 evt=29 规则①的名字比对）
            _confirmedName = null;
        }

        private static void Log(string message)
        {
            var handler = OnStatus;
            if (handler != null)
            {
                try { handler(message); } catch { }
            }
        }
    }
}
