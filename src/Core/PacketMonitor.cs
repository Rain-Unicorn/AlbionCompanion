using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace AlbionCompanion.Core
{
    /// <summary>实时连接监控：用 IP Helper API 枚举 TCP 连接，识别 Albion 服务器流量。</summary>
    public static class PacketMonitor
    {
        // 已知 Albion 服务器网段（社区整理）
        private static readonly string[] KnownPrefixes = new[]
        {
            "92.223.84.",     // Europe
            "144.217.50.",    // US/East
            "206.166.108.",   // Asia
            "206.166.109.",
            "206.166.110.",
            "206.166.111.",
            "193.169.180.",   // Europe
            "193.169.181.",
            "193.169.182.",
            "193.169.183.",
        };

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize,
            bool bOrder, int ulAf, int TableClass, int Reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort;
            public uint RemoteAddr;
            public uint RemotePort;
            public uint OwningPid;
        }

        public class ConnectionInfo
        {
            public string LocalAddr;
            public int LocalPort;
            public string RemoteAddr;
            public int RemotePort;
            public int Pid;
            public string ProcessName;
            public bool IsAlbion;
        }

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;

        /// <summary>枚举所有 IPv4 TCP 连接，标记 Albion 服务器流量。</summary>
        public static List<ConnectionInfo> GetConnections()
        {
            var result = new List<ConnectionInfo>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                uint ret = GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0) return result;

                int num = Marshal.ReadInt32(buf);
                IntPtr rowPtr = (IntPtr)((long)buf + 4);
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));

                for (int i = 0; i < num; i++)
                {
                    var row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_TCPROW_OWNER_PID));
                    rowPtr = (IntPtr)((long)rowPtr + rowSize);

                    string remote = UintToIp(row.RemoteAddr);
                    if (row.RemoteAddr == 0) continue;

                    var info = new ConnectionInfo();
                    info.LocalAddr = UintToIp(row.LocalAddr);
                    info.LocalPort = (int)((row.LocalPort >> 8) & 0xFF) | (int)((row.LocalPort & 0xFF) << 8);
                    info.RemoteAddr = remote;
                    info.RemotePort = (int)((row.RemotePort >> 8) & 0xFF) | (int)((row.RemotePort & 0xFF) << 8);
                    info.Pid = (int)row.OwningPid;
                    info.IsAlbion = IsAlbionServer(remote);
                    try
                    {
                        var proc = System.Diagnostics.Process.GetProcessById(info.Pid);
                        info.ProcessName = proc.ProcessName;
                    }
                    catch { info.ProcessName = "?"; }
                    result.Add(info);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return result;
        }

        public static bool IsAlbionServer(string ip)
        {
            foreach (string prefix in KnownPrefixes)
            {
                if (ip.StartsWith(prefix)) return true;
            }
            return false;
        }

        private static string UintToIp(uint addr)
        {
            byte[] b = BitConverter.GetBytes(addr);
            return b[0] + "." + b[1] + "." + b[2] + "." + b[3];
        }

        /// <summary>检测 Albion 游戏进程是否在运行，并返回其 UDP 本地端口。</summary>
        public static bool IsGameRunning(out int[] udpPorts)
        {
            var ports = new List<int>();
            udpPorts = new int[0];
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("Albion-Online");
                if (procs.Length == 0) return false;
                foreach (var p in procs)
                {
                    try
                    {
                        // 读 UDP 表（简单方式：枚举进程的 UDP 端点）
                        var udpTable = GetUdpTable(p.Id);
                        ports.AddRange(udpTable);
                    }
                    catch { }
                }
                udpPorts = ports.ToArray();
                return procs.Length > 0;
            }
            catch { return false; }
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder);

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDPROW
        {
            public uint LocalAddr;
            public uint LocalPort;
        }

        private static List<int> GetUdpTable(int pid)
        {
            var result = new List<int>();
            int size = 0;
            GetUdpTable(IntPtr.Zero, ref size, true);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                uint ret = GetUdpTable(buf, ref size, true);
                if (ret != 0) return result;
                int num = Marshal.ReadInt32(buf);
                IntPtr rowPtr = (IntPtr)((long)buf + 4);
                for (int i = 0; i < num; i++)
                {
                    var row = (MIB_UDPROW)Marshal.PtrToStructure(rowPtr, typeof(MIB_UDPROW));
                    rowPtr = (IntPtr)((long)rowPtr + 8);
                    // UDP 表不带 PID，只能猜；这里返回所有端口，调用方自己过滤
                    int port = (int)((row.LocalPort >> 8) & 0xFF) | (int)((row.LocalPort & 0xFF) << 8);
                    result.Add(port);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return result;
        }
        public static void StartPolling(int intervalMs, Action<List<ConnectionInfo>> onTick)
        {
            var t = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        var list = GetConnections();
                        var albion = list.FindAll(delegate(ConnectionInfo c) { return c.IsAlbion; });
                        if (onTick != null) onTick(albion);
                    }
                    catch { }
                    Thread.Sleep(intervalMs);
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
