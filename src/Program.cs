using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args)
            {
                LogError(args.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
            {
                LogError(args.ExceptionObject as Exception);
            };

            bool created;
            using (var mutex = new Mutex(true, "Global\\AlbionCompanionOverlay", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Albion Companion 已经在运行了。", "Albion Companion",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string dir = SettingsStore.DefaultDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "settings.json");

                var settings = SettingsStore.Load(path);
                var runtime = new Runtime(settings, path);
                runtime.DbPath = Path.Combine(dir, "market-db.csv");
                runtime.Db = MarketDb.Load(runtime.DbPath);
                runtime.Db.LoadFetchTimes(dir);
                ItemCatalog.Taxonomy.LoadOverride(Path.Combine(dir, "taxonomy-override.json"));
                // 加载完整分类树（从 exe 同目录的 data/taxonomy.json 或 AppData）
                string taxPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "taxonomy.json");
                if (!File.Exists(taxPath)) taxPath = Path.Combine(dir, "taxonomy.json");
                ItemCatalog.Taxonomy.LoadFromJson(taxPath);

                // 加载物品数据库（数字 ID -> UniqueName 映射）
                string itemsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "indexedItems.json");
                ItemDatabase.Load(itemsPath);

                // 加载怪物数据库（MobIndex -> 怪物名字 映射）
                string mobsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "mobs.json");
                string locPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "localization.xml");
                MobDatabase.Load(mobsPath, locPath);
                System.Diagnostics.Debug.WriteLine("MobDatabase loaded: " + MobDatabase.MobCount + " mobs");

                Application.Run(new CompanionApp(runtime));
            }
        }

        private static void LogError(Exception ex)
        {
            try
            {
                string dir = SettingsStore.DefaultDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] "
                              + (ex == null ? "unknown error" : ex.ToString());
                File.AppendAllText(Path.Combine(dir, "error.log"), line + Environment.NewLine);
            }
            catch
            {
                // 日志写入失败时不再抛出，避免二次崩溃
            }
        }
    }
}
