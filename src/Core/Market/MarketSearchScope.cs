#nullable disable // 与仓库既有 Core 文件风格一致
using System.Collections.Generic;

namespace AlbionCompanion.Core
{
    /// <summary>搜索框的三种作用域（渲染分支的唯一依据）。</summary>
    public enum MarketSearchState
    {
        /// <summary>没有输入搜索词：不按搜索限制（分类/品质/城市筛选照常）。</summary>
        NoQuery,
        /// <summary>有搜索词且解析出命中项：只显示命中项。</summary>
        HasMatches,
        /// <summary>有搜索词但解析为空：必须显示空表 + 提示，**绝不回落到全量缓存**。</summary>
        NoMatches
    }

    /// <summary>
    /// 搜索作用域（P0 修复：「搜索无结果时渲染全部缓存」）。
    ///
    /// 旧实现 <c>scoped = 输入非空 &amp;&amp; 命中集非空</c>：输入了找不到的词 → 命中集空 → scoped=false，
    /// 于是把缓存里的所有行全渲染出来，用户看到一屏无关行情，和注释写的「不要兜底全部」正好相反。
    /// 现在把三态收敛成纯函数：NoQuery 不限制、HasMatches 只显示命中、NoMatches 渲染空表+提示。
    /// </summary>
    public static class MarketSearchScope
    {
        public static MarketSearchState Of(string searchText, ICollection<string> resolvedIds)
        {
            bool hasQuery = !string.IsNullOrEmpty(searchText) && searchText.Trim().Length > 0;
            if (!hasQuery) return MarketSearchState.NoQuery;
            if (resolvedIds == null || resolvedIds.Count == 0) return MarketSearchState.NoMatches;
            return MarketSearchState.HasMatches;
        }

        /// <summary>命中集转 HashSet（大小写不敏感；渲染时 O(1) 判定，且天然兼容小写输入）。</summary>
        public static HashSet<string> ToScopeSet(ICollection<string> resolvedIds)
        {
            var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (resolvedIds == null) return set;
            foreach (var id in resolvedIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                set.Add(MarketDb.BaseOf(id));
            }
            return set;
        }
    }
}
