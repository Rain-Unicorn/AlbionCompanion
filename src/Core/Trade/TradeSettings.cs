#nullable disable
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AlbionCompanion.Core.Trade
{
    /// <summary>
    /// 跑商模块自己的轻量设置（不碰 AppSettings，避免跨模块改动）：
    /// 会员档（销售税 8%/4%）、负重记忆、路线档、最近目的城。
    /// 落盘 %AppData%\AlbionCompanion\trade-settings.json，读取失败一律回默认值并保留可诊断状态。
    /// </summary>
    public class TradeSettings
    {
        /// <summary>true = Premium（销售税 4%）；false = F2P（8%）。默认 F2P。</summary>
        public bool Premium;

        /// <summary>用户填的最大负重（KG）。0 = 未填——未填绝不编默认值（规范 §4）。</summary>
        public double WeightLimitKg;

        /// <summary>路线档：0=主城→黑市（默认）/1=五王城/2=布雷西里恩。</summary>
        public int RouteMode;

        /// <summary>上次选择的目的城（五王城档用），空 = 由引擎按榜首腿自动选。</summary>
        public string LastDestination = "";

        /// <summary>销售税（规范 §7：F2P 8% / Premium 4%）。</summary>
        public double SellTax { get { return Premium ? 0.04 : 0.08; } }

        public const string FileName = "trade-settings.json";

        public static string DefaultDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AlbionCompanion");
        }

        public static string DefaultPath()
        {
            return Path.Combine(DefaultDir(), FileName);
        }

        /// <summary>读取设置：文件缺失/损坏/越界一律回退默认值（不抛异常）。</summary>
        public static TradeSettings Load(string path)
        {
            var settings = new TradeSettings();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return settings;
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return settings;
                var raw = JsonSerializer.Deserialize<Persisted>(json);
                if (raw == null) return settings;
                settings.Premium = raw.Premium;
                settings.WeightLimitKg = SanitizeWeight(raw.WeightLimitKg);
                settings.RouteMode = raw.RouteMode >= 0 && raw.RouteMode <= 2 ? raw.RouteMode : 0;
                settings.LastDestination = raw.LastDestination ?? "";
            }
            catch
            {
                // 坏文件不阻断跑商页：回默认值，下一次保存会覆盖
            }
            return settings;
        }

        public void Save(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var persisted = new Persisted
                {
                    Premium = Premium,
                    WeightLimitKg = WeightLimitKg,
                    RouteMode = RouteMode,
                    LastDestination = LastDestination
                };
                string json = JsonSerializer.Serialize(persisted);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                // 设置落盘失败不阻断 UI；下次操作会再试
            }
        }

        /// <summary>负重合法范围：0（未填）或 (0, 2000] KG；NaN/负/离谱值统一归 0。</summary>
        public static double SanitizeWeight(double kg)
        {
            if (double.IsNaN(kg) || double.IsInfinity(kg) || kg <= 0d || kg > 2000d) return 0d;
            return kg;
        }

        /// <summary>把用户输入的负重文本解析成 KG（"150"、"150.5"；非法返回 0 = 未填）。</summary>
        public static double ParseWeightText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0d;
            double value;
            if (!double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                return 0d;
            return SanitizeWeight(value);
        }

        /// <summary>JSON 落盘用的扁平结构（与公开字段解耦，避免序列化行为随实现变化）。</summary>
        private class Persisted
        {
            public bool Premium { get; set; }
            public double WeightLimitKg { get; set; }
            public int RouteMode { get; set; }
            public string LastDestination { get; set; }
        }
    }
}
