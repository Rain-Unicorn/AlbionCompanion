using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 采集资源分类映射（基于实际抓包数据校准）
    /// 已知数据点：
    ///   T4.0 矿石 = 1000
    ///   T5.1 矿石 = 1006
    /// 规律：Tier 每升1级 +1，Enchantment 每升1级 +5
    /// </summary>
    public static class GatherCategories
    {
        public enum ResourceType
        {
            Ore,      // 矿石
            Fiber,    // 纤维
            Wood,     // 木材
            Hide,     // 皮革
            Stone,    // 石头
            Unknown   // 未知
        }

        // 资源类型的基础 Tier 4.0 ID
        // 矿石 = 1000（已确认）
        // 其他类型待确认，先假设按顺序排列
        private static readonly Dictionary<ResourceType, int> _baseIds = new Dictionary<ResourceType, int>
        {
            { ResourceType.Ore, 1000 },
            { ResourceType.Fiber, 1005 },  // 假设，待确认
            { ResourceType.Wood, 1010 },  // 假设，待确认
            { ResourceType.Hide, 1015 },   // 假设，待确认
            { ResourceType.Stone, 1020 },  // 假设，待确认
        };

        public static ResourceType GetResourceType(int itemId)
        {
            // 从后往前匹配，先匹配 enchantment 高的
            foreach (var kv in _baseIds)
            {
                int baseId = kv.Value;
                // 检查 0-4 级 enchantment
                for (int ench = 0; ench <= 4; ench++)
                {
                    for (int tier = 4; tier <= 8; tier++)
                    {
                        int expectedId = baseId + (tier - 4) + ench * 5;
                        if (expectedId == itemId)
                            return kv.Key;
                    }
                }
            }
            return ResourceType.Unknown;
        }

        public static string GetResourceName(int itemId)
        {
            var type = GetResourceType(itemId);
            switch (type)
            {
                case ResourceType.Ore: return "矿石";
                case ResourceType.Fiber: return "纤维";
                case ResourceType.Wood: return "木材";
                case ResourceType.Hide: return "皮革";
                case ResourceType.Stone: return "石头";
                default: return "Item " + itemId;
            }
        }

        public static string GetTier(int itemId)
        {
            // 找到对应资源类型的 baseId
            foreach (var kv in _baseIds)
            {
                int baseId = kv.Value;
                // 检查每个 enchantment 等级
                for (int ench = 0; ench <= 4; ench++)
                {
                    int enchOffset = ench * 5;
                    int tier = 4 + (itemId - baseId - enchOffset);
                    if (tier >= 4 && tier <= 8)
                    {
                        string tierStr = "T" + tier;
                        if (ench > 0)
                            tierStr += "." + ench;
                        return tierStr;
                    }
                }
            }
            return "";
        }
    }
}
