using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace AlbionCompanion.Core.Party
{
    /// <summary>
    /// party-cache.json 的读写与落盘策略（设计 §7.2）：
    /// - schemaVersion 固定 1，读取时若 &gt;1 则忽略整个缓存并告警（不猜测新格式）；
    /// - 原子写：先写 .tmp 再 File.Replace（失败平台回退 File.Move overwrite）；
    /// - 2 秒防抖：PartyState 每个变更事件都会触发落盘，防抖避免高频 IO；
    /// - 解析失败降级为空名单（返回 null），由调用方决定 verifiedReason。
    /// </summary>
    public static class PartyCacheStore
    {
        public sealed class CacheMember
        {
            public long objectId { get; set; }
            public string name { get; set; } = "";
            public string guid { get; set; } = ""; // Base64，16 字节
            public string role { get; set; } = "member";
            public DateTime lastSeenUtc { get; set; }
            public string confidence { get; set; } = "confirmed"; // confirmed / pendingObjectId / pendingName
        }

        public sealed class CacheData
        {
            public int schemaVersion { get; set; } = 1;
            public DateTime savedAtUtc { get; set; }
            public bool verified { get; set; }
            public string verifiedReason { get; set; } = "";
            public long leaderObjectId { get; set; }
            public List<CacheMember> members { get; set; } = new List<CacheMember>();
        }

        private static readonly object _saveGate = new object();
        private static Timer? _debounce;
        private static string? _pendingPath;
        private static CacheData? _pendingData;
        private static readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = false,
        };

        /// <summary>读取缓存。文件缺失 / JSON 损坏 / schemaVersion&gt;1 一律返回 null（降级空名单），绝不抛异常。</summary>
        public static CacheData? Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string raw = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                CacheData? data = JsonSerializer.Deserialize<CacheData>(raw, _json);
                if (data == null) return null;
                if (data.schemaVersion > 1)
                {
                    OnStatus?.Invoke($"[队伍] party-cache schemaVersion={data.schemaVersion} 高于支持的 1，忽略缓存");
                    return null;
                }
                return data;
            }
            catch (Exception ex)
            {
                OnStatus?.Invoke($"[队伍] party-cache 解析失败，降级为空名单：{ex.Message}");
                return null;
            }
        }

        /// <summary>立即原子写（防抖定时器被取消，由本调用接管）。</summary>
        public static void FlushNow(string path, CacheData data)
        {
            lock (_saveGate)
            {
                _pendingPath = null;
                _pendingData = null;
                _debounce?.Dispose();
                _debounce = null;
            }
            WriteAtomic(path, data);
        }

        /// <summary>2 秒防抖保存：2 秒内的多次调用合并为一次写盘。</summary>
        public static void SaveDebounced(string path, CacheData data)
        {
            lock (_saveGate)
            {
                _pendingPath = path;
                _pendingData = data;
                if (_debounce == null)
                {
                    _debounce = new Timer(_ =>
                    {
                        string? p; CacheData? d;
                        lock (_saveGate)
                        {
                            p = _pendingPath; d = _pendingData;
                            _pendingPath = null; _pendingData = null;
                            _debounce?.Dispose(); _debounce = null;
                        }
                        if (p != null && d != null) WriteAtomic(p, d);
                    }, null, 2000, Timeout.Infinite);
                }
            }
        }

        /// <summary>测试辅助：构造当前时间戳的数据壳。</summary>
        public static CacheData NewData() => new CacheData { schemaVersion = 1, savedAtUtc = DateTime.UtcNow };

        private static void WriteAtomic(string path, CacheData data)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, _json));
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); }
                    catch (PlatformNotSupportedException) { File.Move(tmp, path, overwrite: true); }
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                OnStatus?.Invoke($"[队伍] party-cache 写盘失败：{ex.Message}");
            }
        }

        /// <summary>状态回调（由 ChatSniffer 接到事件日志）。</summary>
        public static event Action<string>? OnStatus;
    }
}
