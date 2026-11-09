#nullable disable // 与仓库既有 Core 文件风格一致
using System;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// 市场屏统一时钟（设计规范 §六 / §八.8）：相对时间必须由**单调时钟**产出，
    /// 避免系统时间被 NTP 校正/用户改表后「数据时间」突然跳动或出现负年龄。
    ///
    /// 做法：首次读取时锚定 (墙钟 UtcNow, Environment.TickCount64)，之后用 TickCount64 差值推进。
    /// 精度足够市场屏（分钟级相对时间）；跨进程重启重新锚定即可。
    /// 单测只能验证「同一进程内不回退 + 与墙钟量级一致」，无法验证跨进程行为。
    /// </summary>
    public static class MarketClock
    {
        private static readonly object _sync = new object();
        private static DateTimeOffset _anchorUtc;
        private static long _anchorTick;
        private static bool _initialized;

        /// <summary>单调推进的当前时刻。</summary>
        public static DateTimeOffset UtcNow
        {
            get
            {
                lock (_sync)
                {
                    if (!_initialized)
                    {
                        _anchorUtc = DateTimeOffset.UtcNow;
                        _anchorTick = Environment.TickCount64;
                        _initialized = true;
                    }
                    long delta = Environment.TickCount64 - _anchorTick;
                    if (delta < 0) delta = 0; // TickCount64 理论不回退；防御性钳制
                    return _anchorUtc.AddMilliseconds(delta);
                }
            }
        }

        /// <summary>测试用：重新锚定（生产代码不调用）。</summary>
        internal static void ReanchorForTests()
        {
            lock (_sync) { _initialized = false; }
        }
    }
}
