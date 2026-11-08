using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core
{
    public class MemberStat
    {
        public string Name;
        public double Damage;
        public double Healing;
        public double Taken;
        public double Seconds;

        public double Dps
        {
            get { return Seconds > 0 ? Damage / Seconds : 0; }
        }

        public double Hps
        {
            get { return Seconds > 0 ? Healing / Seconds : 0; }
        }
    }

    /// <summary>会话级运行时状态：设置、收益台账、战斗记录、配装槽位。</summary>
    public class Runtime
    {
        public AppSettings Settings;
        public string SettingsPath;
        public IncomeLedger Income = new IncomeLedger();
        public DateTime SessionStart;
        public List<MemberStat> Members = new List<MemberStat>();
        public List<LoadoutSlot> Slots = new List<LoadoutSlot>();
        public long LootPerKill = 7000;

        /// <summary>本地行情库（跨页共享：市场页写、跑商页读）。</summary>
        public MarketDb Db = new MarketDb();
        public string DbPath;

        public event EventHandler Changed;
        public event EventHandler SettingsApplied;

        public Runtime(AppSettings settings, string settingsPath)
        {
            Settings = settings;
            SettingsPath = settingsPath;
            SessionStart = DateTime.Now;
            InitSlots();
        }

        private void InitSlots()
        {
            string[] names = { "主手武器", "副手", "头部", "护甲", "鞋子", "披风", "坐骑", "背包" };
            for (int i = 0; i < names.Length; i++)
            {
                var slot = new LoadoutSlot();
                slot.Name = names[i];
                slot.Cost = 0;
                Slots.Add(slot);
            }
        }

        public TimeSpan Elapsed
        {
            get
            {
                var span = DateTime.Now - SessionStart;
                return span < TimeSpan.Zero ? TimeSpan.Zero : span;
            }
        }

        public double IncomePerHour
        {
            get { return SessionClock.PerHour(Income.Total, Elapsed); }
        }

        public long LoadoutTotal
        {
            get { return BuildCost.Total(Slots); }
        }

        public void Notify()
        {
            var handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Save()
        {
            SettingsStore.Save(SettingsPath, Settings);
        }

        public void ApplySettings()
        {
            Save();
            var handler = SettingsApplied;
            if (handler != null) handler(this, EventArgs.Empty);
            Notify();
        }

        public void ResetSession()
        {
            SessionStart = DateTime.Now;
            Income.Clear();
            Members.Clear();
            Notify();
        }

        public void AddMember(MemberStat member)
        {
            if (member == null) return;
            if (member.Seconds <= 0) member.Seconds = 1;
            Members.Add(member);
            Notify();
        }
    }
}
