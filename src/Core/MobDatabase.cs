using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 怪物数据库：从 mobs.json 加载怪物列表
    /// 直接复用 SAT 的怪物数据，按 MobIndex 查名字
    /// 公式：mobDataIndex = MobIndex - 16（SAT 确认的偏移）
    /// </summary>
    public static class MobDatabase
    {
        private static List<MobEntry> _mobs = new List<MobEntry>();
        private static Dictionary<string, string> _localization = new Dictionary<string, string>();
        private static bool _loaded = false;
        private static readonly object _lock = new object();
        private const int InGameMobIndexOffset = 16;

        public class MobEntry
        {
            public string uniquename;
            public int tier;
            public string namelocatag;
        }

        public static void Load(string mobsPath, string localizationPath)
        {
            lock (_lock)
            {
                if (_loaded) return;
                if (!File.Exists(mobsPath)) return;

                try
                {
                    // 1. 加载怪物列表
                    string json = File.ReadAllText(mobsPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        _mobs = new List<MobEntry>();
                        foreach (var elem in root.EnumerateArray())
                        {
                            var entry = new MobEntry();
                            if (elem.TryGetProperty("@uniquename", out var uname))
                                entry.uniquename = uname.GetString();
                            if (elem.TryGetProperty("@tier", out var tier))
                                entry.tier = tier.GetInt32();
                            if (elem.TryGetProperty("@namelocatag", out var nloc))
                                entry.namelocatag = nloc.GetString();
                            _mobs.Add(entry);
                        }
                        System.Diagnostics.Debug.WriteLine("MobDatabase loaded: " + _mobs.Count + " mobs");
                    }

                    // 2. 加载本地化翻译（中文）
                    if (File.Exists(localizationPath))
                    {
                        var xdoc = XDocument.Load(localizationPath);
                        _localization = new Dictionary<string, string>();
                        foreach (var tu in xdoc.Descendants("tu"))
                        {
                            string tuid = tu.Attribute("tuid")?.Value;
                            if (string.IsNullOrEmpty(tuid)) continue;

                            foreach (var tuv in tu.Elements("tuv"))
                            {
                                string lang = tuv.Attribute(XNamespace.Xml + "lang")?.Value;
                                if (lang == "ZH-CN")
                                {
                                    string seg = tuv.Element("seg")?.Value;
                                    if (!string.IsNullOrEmpty(seg))
                                    {
                                        _localization[tuid] = seg;
                                    }
                                    break;
                                }
                            }
                        }
                        System.Diagnostics.Debug.WriteLine("Localization loaded: " + _localization.Count + " zh-CN entries");
                    }

                    _loaded = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("MobDatabase load error: " + ex.Message);
                }
            }
        }

        public static int MobCount { get { return _mobs == null ? 0 : _mobs.Count; } }

        /// <summary>
        /// 按游戏内 MobIndex 查怪物名字（中文）
        /// </summary>
        public static string GetMobNameByIndex(int mobIndex)
        {
            if (!_loaded || _mobs == null || _mobs.Count == 0) return null;

            int mobDataIndex = mobIndex - InGameMobIndexOffset;
            if (mobDataIndex < 0 || mobDataIndex >= _mobs.Count) return null;

            var mob = _mobs[mobDataIndex];
            if (mob == null) return null;

            // 优先用本地化标签查中文名字
            if (!string.IsNullOrEmpty(mob.namelocatag) && _localization.TryGetValue(mob.namelocatag, out var zhName))
            {
                return zhName;
            }

            //  fallback：返回英文 uniquename
            string name = mob.uniquename;
            if (name.StartsWith("MOB_")) name = name.Substring(4);
            return name;
        }

        public static int GetMobTierByIndex(int mobIndex)
        {
            if (!_loaded || _mobs == null || _mobs.Count == 0) return 0;

            int mobDataIndex = mobIndex - InGameMobIndexOffset;
            if (mobDataIndex < 0 || mobDataIndex >= _mobs.Count) return 0;

            return _mobs[mobDataIndex].tier;
        }
    }
}
