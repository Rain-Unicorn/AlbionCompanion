using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 物品数据库：从 indexedItems.json 加载数字 Index -> UniqueName 映射
    /// 直接复用 SAT 的物品索引数据，不猜映射
    /// </summary>
    public static class ItemDatabase
    {
        private static Dictionary<int, string> _indexToName = new Dictionary<int, string>();
        private static bool _loaded = false;
        private static readonly object _lock = new object();

        public static void Load(string path)
        {
            lock (_lock)
            {
                if (_loaded) return;
                if (!File.Exists(path)) return;

                try
                {
                    string json = File.ReadAllText(path);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };
                    var items = JsonSerializer.Deserialize<List<ItemEntry>>(json, options);

                    if (items != null)
                    {
                        int count = 0;
                        foreach (var item in items)
                        {
                            if (item != null && !string.IsNullOrEmpty(item.Index) && !string.IsNullOrEmpty(item.UniqueName))
                            {
                                int idx;
                                if (int.TryParse(item.Index, out idx))
                                {
                                    _indexToName[idx] = item.UniqueName;
                                    count++;
                                }
                            }
                        }
                        System.Diagnostics.Debug.WriteLine("ItemDatabase loaded: " + count + " items");
                    }
                    _loaded = true;
                }
                catch
                {
                    // 加载失败就用空映射
                }
            }
        }

        public static string GetUniqueName(int index)
        {
            string name;
            if (_indexToName.TryGetValue(index, out name))
                return name;
            return null;
        }

        public static string GetDisplayName(int index)
        {
            string uniqueName = GetUniqueName(index);
            if (string.IsNullOrEmpty(uniqueName))
                return "Item " + index;

            // 从 UniqueName 提取资源类型和 Tier
            // 格式: T4_ORE, T5_ORE@1, T3_FIBER, T8_HIDE@3 等
            string cleanName = uniqueName.Split('@')[0];
            string[] parts = cleanName.Split('_');
            if (parts.Length >= 2)
            {
                string tier = parts[0]; // T1-T8
                string resourceType = parts[1]; // ORE/FIBER/WOOD/HIDE/STONE

                string typeName;
                switch (resourceType)
                {
                    case "ORE": typeName = "矿石"; break;
                    case "FIBER": typeName = "纤维"; break;
                    case "WOOD": typeName = "木材"; break;
                    case "HIDE": typeName = "皮革"; break;
                    case "STONE": typeName = "石头"; break;
                    default: typeName = resourceType; break;
                }

                // 检查 enchantment level
                string ench = "";
                if (uniqueName.Contains("@"))
                {
                    string enchStr = uniqueName.Split('@')[1];
                    ench = "." + enchStr;
                }

                return tier + ench + " " + typeName;
            }

            return uniqueName;
        }

        public static bool IsLoaded
        {
            get { return _loaded; }
        }

        public static int GetLoadedCount()
        {
            return _indexToName.Count;
        }

        private class ItemEntry
        {
            public string Index;
            public string UniqueName;
        }
    }
}
