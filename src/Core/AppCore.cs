using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbionCompanion.Core
{
    public class IncomeEntry
    {
        public DateTime Time;
        public string Category;
        public string Item;
        public int Qty;
        public long UnitPrice;
        public long Total;
    }

    public class IncomeLedger
    {
        private readonly List<IncomeEntry> _items = new List<IncomeEntry>();

        public List<IncomeEntry> Items { get { return _items; } }

        public void Add(string category, string item, int qty, long unitPrice, DateTime time)
        {
            var entry = new IncomeEntry();
            entry.Time = time;
            entry.Category = string.IsNullOrEmpty(category) ? "其他" : category;
            entry.Item = item;
            entry.Qty = qty < 1 ? 1 : qty;
            entry.UnitPrice = unitPrice < 0 ? 0 : unitPrice;
            entry.Total = (long)entry.Qty * entry.UnitPrice;
            _items.Insert(0, entry);
        }

        public long Total
        {
            get
            {
                long sum = 0;
                for (int i = 0; i < _items.Count; i++) sum += _items[i].Total;
                return sum;
            }
        }

        public Dictionary<string, long> TotalsByCategory()
        {
            var map = new Dictionary<string, long>();
            for (int i = 0; i < _items.Count; i++)
            {
                var e = _items[i];
                map[e.Category] = (map.ContainsKey(e.Category) ? map[e.Category] : 0L) + e.Total;
            }
            return map;
        }

        public void Clear()
        {
            _items.Clear();
        }

        public void ExportCsv(string path)
        {
            var sb = new StringBuilder();
            sb.Append("时间,分类,物品,数量,单价,小计\r\n");
            for (int i = 0; i < _items.Count; i++)
            {
                var e = _items[i];
                sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm")).Append(',');
                sb.Append(Quote(e.Category)).Append(',');
                sb.Append(Quote(e.Item)).Append(',');
                sb.Append(e.Qty).Append(',');
                sb.Append(e.UnitPrice).Append(',');
                sb.Append(e.Total).Append("\r\n");
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Quote(string value)
        {
            if (value == null) return "";
            if (value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }

    public class LoadoutSlot
    {
        public string Name;
        public long Cost;
    }

    public static class BuildCost
    {
        public static long Total(IEnumerable<LoadoutSlot> slots)
        {
            long sum = 0;
            foreach (var s in slots) if (s != null && s.Cost > 0) sum += s.Cost;
            return sum;
        }

        public static int BreakEvenKills(long total, long lootPerKill)
        {
            if (total <= 0 || lootPerKill <= 0) return 0;
            return (int)Math.Ceiling(total / (double)lootPerKill);
        }
    }

    public static class SessionClock
    {
        public static string Format(TimeSpan elapsed)
        {
            if (elapsed == null || elapsed.TotalSeconds < 0) return "00:00";
            int total = (int)elapsed.TotalSeconds;
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            if (h > 0) return string.Format("{0}:{1:00}:{2:00}", h, m, s);
            return string.Format("{0:00}:{1:00}", m, s);
        }

        public static double PerHour(long total, TimeSpan elapsed)
        {
            if (elapsed == null || elapsed.TotalSeconds < 60) return 0.0;
            return total / (elapsed.TotalSeconds / 3600.0);
        }
    }

    public class AppSettings
    {
        public bool AlwaysOnTop;
        public int OpacityPercent;
        public string Hotkey;
        public string DataDir;
        public string LastItems;
        public string LastCities;

        public AppSettings()
        {
            AlwaysOnTop = true;
            OpacityPercent = 92;
            Hotkey = "Ctrl+Alt+A";
            DataDir = "";
            LastItems = "T4_BAG,T5_2H_BOW@2";
            LastCities = "Caerleon,Bridgewatch,Martlock,Thetford,Fortuna,Lymhurst";
        }
    }

    public static class SettingsStore
    {
        public static string DefaultDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
        }

        public static AppSettings Load(string path)
        {
            if (!File.Exists(path)) return new AppSettings();
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                var settings = JsonSerializer.Deserialize<AppSettings>(json, options);
                if (settings == null) return new AppSettings();
                if (settings.OpacityPercent < 40 || settings.OpacityPercent > 100) settings.OpacityPercent = 92;
                if (string.IsNullOrEmpty(settings.Hotkey)) settings.Hotkey = "Ctrl+Alt+A";
                if (settings.LastCities == null) settings.LastCities = "Caerleon,Bridgewatch,Martlock,Thetford,Fortuna,Lymhurst";
                return settings;
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(string path, AppSettings settings)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(settings, options), new UTF8Encoding(false));
        }
    }
}
