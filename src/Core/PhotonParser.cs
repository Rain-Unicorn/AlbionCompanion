using System;
using System.Collections.Generic;
using System.Text;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// Photon Protocol 18 解析器，完整移植自 SAT (StatisticsAnalysisTool) 源码。
    /// </summary>
    public static class PhotonParser
    {
        public class GameEvent
        {
            public byte PhotonEventCode;  // Photon 协议层事件码（通常是 3）
            public int GameEventCode;     // 真正的游戏事件码（parameters[252]）
            public byte MsgType;
            public byte OperationCode;
            public Dictionary<byte, object> Parameters;
        }

        private const int CommandHeaderLength = 12;
        private const int PhotonHeaderLength = 12;

        public static event Action<string> OnDebug;

        /// <summary>OperationResponse 解析/校验失败累计（诊断用，原子递增）。</summary>
        public static long OperationResponseFailures => System.Threading.Interlocked.Read(ref _opRespParseFail);

        /// <summary>
        /// JoinResponse 的权威 OperationCode（P0-1）。**当前为 null = 白名单为空**。
        /// </summary>
        /// <remarks>
        /// 设计文档自相矛盾：§2.2.2 / §3.3 写「Join = OperationCode==0」，
        /// 而 §9.1 / §10.3(T-01) / §9.8(V1) 要求「比较值必须是真实 JoinResponse 码，且不为 0」。
        /// 本实现采信后者（依据见 PhotonParser 类注释与交付说明），并要求**实机抓包取证**后才填值。
        ///
        /// 为什么默认 null 而不是先填 0 或某个猜测值：
        ///   - 填 0 会让「任何解析失败 → opCode 默认 0」的退化路径再次变成确认入口；
        ///   - 填猜测的非零值会让所有真实 Join 响应被丢弃（主路径永不触发，用户看不到自己的数据）；
        ///   - null = 完全不确认，是唯一**不会产生错误结论**的状态，且身份仍有 evt=81 / evt=29 / 手选名字兜底。
        ///
        /// 取值方式（V1）：实机连开 5 次，从 events.log 的 `[OpResp] opCode=..` 观测行取到稳定值，
        /// 填进本字段并把对应用例（T-01）改成该值；**禁止**为了让用例变绿而放宽成「非零即接受」。
        /// </remarks>
        public static byte? JoinResponseOperationCode = null;

        // 最近一次观测到的非零 OperationCode 与其参数键（实机 V1 取证用，不做判定）。
        public static string LastOperationCodeSample = "";

        // 复核 P0：OperationResponse 原始字节留存计数。实机日志出现「9 条不同 keys 的响应 opCode 全为 1」
        // —— 说明 opCode 读到的不是真实操作码（或协议布局与实现不符）。留原始字节才能定位真实布局；
        // 只留前 30 条，避免日志膨胀。
        private static int _opRespDumpCount;

        private static int _udpCount;
        private static int _evtCount;
        private static int _cmd6, _cmd7, _cmd8, _other;
        private static int _msgCalls;
        private static long _opRespParseFail; // OperationResponse 反序列化/校验失败次数（原子递增）

        // CommandType enum from SAT
        private const byte CmdDisconnect = 1;
        private const byte CmdSendUnreliable = 7;
        private const byte CmdSendReliable = 6;
        private const byte CmdSendFragment = 8;

        // MessageType enum from SAT
        private const byte MsgOperationRequest = 2;
        private const byte MsgOperationResponse = 3;
        private const byte MsgEvent = 4;

        public static List<GameEvent> ParsePacket(byte[] frame)
        {
            if (frame == null || frame.Length < 42) return new List<GameEvent>();
            int ipHdrLen = (frame[14] & 0x0F) * 4;
            int proto = frame[14 + 9];
            if (proto != 17) return new List<GameEvent>();
            int udpOff = 14 + ipHdrLen;
            int payloadOff = udpOff + 8;
            return ParsePacketAt(frame, payloadOff);
        }

        /// <summary>直接从 UDP payload 开始解析（Socket 模式用）</summary>
        public static List<GameEvent> ParseUdpPayload(byte[] payload)
        {
            if (payload == null || payload.Length < 12) return new List<GameEvent>();
            return ParsePacketAt(payload, 0);
        }

        public static List<GameEvent> ParsePacketAt(byte[] frame, int payloadOff)
        {
            var result = new List<GameEvent>();
            if (frame == null || frame.Length < 4) return result;

            // 前 3 个包打印 hex
            if (_udpCount < 3 && OnDebug != null)
            {
                var sb = new StringBuilder();
                int showLen = Math.Min(32, frame.Length - payloadOff);
                for (int i = 0; i < showLen; i++) sb.Append(frame[payloadOff + i].ToString("X2") + " ");
                OnDebug("包#" + _udpCount + " hex=" + sb.ToString().Trim());
            }

            ReceivePacket(frame, payloadOff, frame.Length, result);

            _udpCount++;
            return result;
        }

        // ==== Ported from SAT PhotonParser.ReceivePacket ====
        private static void ReceivePacket(byte[] payload, int start, int end, List<GameEvent> result)
        {
            int packetOffset = start;
            while (packetOffset < end)
            {
                int remaining = end - packetOffset;
                int packetLength;
                if (!TryReadPacketLength(payload, packetOffset, remaining, out packetLength))
                {
                    if (_udpCount < 3 && OnDebug != null)
                        OnDebug("  framer失败 at offset " + packetOffset);
                    return;
                }

                ReceiveSinglePacket(payload, packetOffset, packetOffset + packetLength, result);
                packetOffset += packetLength;
            }
        }

        // ==== Ported from SAT PhotonPacketFramer.TryReadPacketLength ====
        private static bool TryReadPacketLength(byte[] payload, int off, int len, out int packetLength)
        {
            packetLength = 0;
            if (len < PhotonHeaderLength) return false;

            byte flags = payload[off + 2];
            if (flags == 1)
            {
                packetLength = len;
                return true;
            }

            int offset = off + PhotonHeaderLength;
            if (flags == 0xCC) offset += 4; // CRC

            if (offset > off + len) return false;

            byte commandCount = payload[off + 3];
            for (int i = 0; i < commandCount; i++)
            {
                if (off + len - offset < CommandHeaderLength) return false;
                int commandLength = ReadInt32BE(payload, offset + 4);
                if (commandLength < CommandHeaderLength || commandLength > off + len - offset)
                    return false;
                offset += commandLength;
            }

            packetLength = offset - off;
            return true;
        }

        // ==== Ported from SAT PhotonParser.ReceiveSinglePacket ====
        private static void ReceiveSinglePacket(byte[] d, int start, int end, List<GameEvent> result)
        {
            int len = end - start;
            if (len < PhotonHeaderLength) return;

            int off = start;

            // peerId (2 bytes BE short)
            short peerId = (short)((d[off] << 8) | d[off + 1]);
            off += 2;

            byte flags = d[off++];
            byte commandCount = d[off++];

            // timestamp (4 bytes BE) - skip
            off += 4;
            // challenge (4 bytes BE) - skip
            off += 4;

            if (_udpCount < 3 && OnDebug != null)
                OnDebug("  peerId=" + peerId + " flags=" + flags + " cc=" + commandCount);

            // flags==1 = encrypted, skip
            if (flags == 1) return;

            for (int i = 0; i < commandCount; i++)
            {
                if (!HandleCommand(d, ref off, end, result))
                    break;
            }

            if (_udpCount % 200 == 0 && OnDebug != null)
                OnDebug("统计: UDP=" + _udpCount + " evt=" + _evtCount + " c6=" + _cmd6 + " c7=" + _cmd7 + " c8=" + _cmd8);
        }

        // ==== Ported from SAT PhotonParser.HandleCommand ====
        private static bool HandleCommand(byte[] d, ref int off, int end, List<GameEvent> result)
        {
            int commandStart = off;
            if (commandStart < 0 || commandStart > end - CommandHeaderLength)
                return false;

            byte commandType = d[off++];
            byte channelId = d[off++];
            off++; // cmdFlags
            off++; // reserved

            int commandLength = ReadInt32BE(d, off);
            off += 4; // length
            off += 4; // seq

            if (commandLength < CommandHeaderLength || commandLength > end - commandStart)
                return false;

            if (commandType == CmdSendReliable) _cmd6++;
            else if (commandType == CmdSendUnreliable) _cmd7++;
            else if (commandType == CmdSendFragment) _cmd8++;
            else _other++;

            int commandEnd = commandStart + commandLength;
            int commandPayloadLength = commandLength - CommandHeaderLength;

            switch (commandType)
            {
                case CmdSendUnreliable:
                    if (commandPayloadLength < 4) { off = commandEnd; return true; }
                    off += 4; // skip 4-byte header for unreliable
                    commandPayloadLength -= 4;
                    HandleSendReliable(d, ref off, off + commandPayloadLength, result);
                    break;
                case CmdSendReliable:
                    HandleSendReliable(d, ref off, off + commandPayloadLength, result);
                    break;
                case CmdSendFragment:
                    // skip fragment reassembly for now
                    break;
            }

            off = commandEnd;
            return true;
        }

        // ==== Ported from SAT PhotonParser.HandleSendReliable ====
        private static void HandleSendReliable(byte[] d, ref int off, int end, List<GameEvent> result)
        {
            _msgCalls++;
            if (end - off < 2) return;

            off++; // skip 1 byte signal (Protocol18)
            if (off >= end) return;

            byte messageType = d[off++];
            if (messageType > 128) return; // encrypted

            int operationLength = end - off;

            if (messageType == MsgEvent)
            {
                // 用 SAT 的完整 Protocol18 解析器提取真正的游戏事件码（parameters[252]）
                try
                {
                    var span = new ReadOnlySpan<byte>(d, off, operationLength);
                    var eventData = Protocol18Deserializer.DeserializeEventData(span);
                    if (eventData != null && eventData.Parameters != null)
                    {
                        int gameEventCode = -1;
                        if (eventData.Parameters.TryGetValue(252, out var evCode))
                        {
                            try { gameEventCode = Convert.ToInt32(evCode); } catch { }
                        }

                        var evt = new GameEvent();
                        evt.PhotonEventCode = eventData.Code;
                        evt.GameEventCode = gameEventCode;
                        evt.MsgType = messageType;
                        evt.Parameters = eventData.Parameters;
                        result.Add(evt);
                        _evtCount++;

                        if (_evtCount <= 50 && OnDebug != null && gameEventCode != -1)
                            OnDebug("  [gameEvt " + gameEventCode + "] params=" + FormatParams(eventData.Parameters));
                    }
                }
                catch { /* 解析失败就跳过这个包 */ }
            }
            else if (messageType == MsgOperationResponse)
            {
                // R0 修复：OperationResponse 与 EventData 的字节布局不同
                // （opCode(1B) + returnCode(2B LE) + debugMessage(1B 长度 + N) + 参数表），
                // 原先用 DeserializeEventData 会整体错位，parameters[253] 永远取不到，
                // opCode 恒为默认值 0，导致每个响应都走 Join 分支并把本地玩家清成 null。
                try
                {
                    var span = new ReadOnlySpan<byte>(d, off, operationLength);
                    RecordOperationBytes(span);
                    Protocol18.Photon.OperationResponse? opData;
                    try
                    {
                        opData = Protocol18Deserializer.DeserializeOperationResponse(span);
                    }
                    catch
                    {
                        // 解析失败必须显式计数并跳过，不得退化为「opCode 默认 0 → 视为 Join」
                        System.Threading.Interlocked.Increment(ref _opRespParseFail);
                        opData = null;
                    }

                    if (opData != null)
                    {
                        // V1 取证：记录响应码与参数键（只记录，不参与判定）
                        RecordOperationCodeSample(opData);

                        if (ValidateJoinResponse(opData, out long localObjectId, out string? localUsername))
                        {
                            if (OnDebug != null)
                                OnDebug("  [JoinResponse] LocalPlayer ObjectId=" + localObjectId + " Name=" + localUsername);

                            // R1/C3：身份唯一写入入口是状态机（强证据，确认或纠正）
                            LocalPlayerIdentity.OnJoinResponse(localObjectId, localUsername);
                        }
                    }
                }
                catch
                {
                    // 兜底：任何意外都只计数，不影响同包其它内容
                    System.Threading.Interlocked.Increment(ref _opRespParseFail);
                }
            }
            // TODO: OperationRequest parsing
        }


        /// <summary>
        /// R0 内核（可注入原始字节，供单测构造输入）：
        /// 用 OperationResponse 的真实布局反序列化，并按「JoinResponse 精确码 + 有效身份」双重校验。
        ///
        /// **判定规则（P0-1 定案）**：
        ///   - `ReturnCode != 0`（服务器失败响应）→ 不写身份；
        ///   - `OperationCode` 必须**精确等于** <see cref="JoinResponseOperationCode"/> → 否则不写身份；
        ///   - 参数 0（UserObjectId）必须可解析且 &gt; 0 才提交；
        ///   - 参数 2（Username）可为空（允许「有 id 无名」）。
        ///
        /// **已删除的旧判定**：`OperationCode == 0 → return false`，其余非零值一律放行。
        /// 两个方向都有害：
        ///   ① 若真实 Join 码落在 0，主路径永不触发（用户永远看不到自己的数据）；
        ///   ② 非零即放行，会让任意一条非 Join 的 OperationResponse（只要恰好带 p0&gt;0 与 p2）确认身份，
        ///      而 `LocalPlayerIdentity.OnJoinResponse` 在身份冲突时会 `DamageTracker.RemovePlayer(0)`——
        ///      一次无关响应就能把用户自己的累计伤害清零。
        /// 错误确认比不确认危害大得多，故一律收紧为精确白名单匹配。
        ///
        /// **白名单为空时的行为**：若 <see cref="JoinResponseOperationCode"/> 为 null（实机码值未采集到），
        /// 本方法**永远返回 false**，即完全不确认身份——这是有意的保守行为；
        /// 身份仍可由 evt=81 余额严格匹配 / evt=29 名字匹配 / 手选名字兜底。
        /// </summary>
        public static bool TryParseJoinResponse(ReadOnlySpan<byte> operationBody, out long objectId, out string? name)
        {
            objectId = 0;
            name = null;

            Protocol18.Photon.OperationResponse opData;
            try
            {
                opData = Protocol18Deserializer.DeserializeOperationResponse(operationBody);
            }
            catch
            {
                // 截断/非法字节：计入失败计数（供诊断），绝不抛给调用方
                System.Threading.Interlocked.Increment(ref _opRespParseFail);
                return false;
            }
            if (opData == null) return false;

            return ValidateJoinResponse(opData, out objectId, out name);
        }

        /// <summary>
        /// JoinResponse 判定内核（纯校验，不解析字节）。调用点已解析出的 opData 直接复用，
        /// 避免在热路径上重复反序列化。
        /// </summary>
        public static bool ValidateJoinResponse(Protocol18.Photon.OperationResponse opData,
                                                out long objectId, out string? name)
        {
            objectId = 0;
            name = null;
            if (opData == null || opData.Parameters == null) return false;

            if (opData.ReturnCode != 0) return false;          // 服务器返回失败，不写状态

            byte? expected = JoinResponseOperationCode;
            if (expected == null) return false;                // 白名单为空 → 绝不确认（不可放宽）
            if (opData.OperationCode != expected.Value) return false; // 精确等值匹配

            object objIdObj;
            if (!opData.Parameters.TryGetValue(0, out objIdObj)) return false;
            long parsedId;
            try { parsedId = Convert.ToInt64(objIdObj); }
            catch { return false; }
            if (parsedId <= 0) return false;                   // ObjectId 必须为正

            object usernameObj;
            if (opData.Parameters.TryGetValue(2, out usernameObj))
                name = usernameObj?.ToString();

            objectId = parsedId;
            return true;
        }

        /// <summary>
        /// 复核 P0 取证：把 OperationResponse 的**原始字节**（前 40 字节）落盘，用于判定真实
        /// operationCode 位置/协议布局。只在启动后前 30 条响应时记录，不影响解析、不参与身份判定。
        /// </summary>
        private static void RecordOperationBytes(ReadOnlySpan<byte> body)
        {
            if (System.Threading.Interlocked.Increment(ref _opRespDumpCount) > 30) return;
            var sb = new StringBuilder();
            sb.Append("[OpRespBytes] len=").Append(body.Length).Append(" hex=");
            int n = Math.Min(body.Length, 40);
            for (int i = 0; i < n; i++) sb.Append(body[i].ToString("X2")).Append(' ');
            string line = sb.ToString().TrimEnd();

            if (OnDebug != null) try { OnDebug("  " + line); } catch { }
            try
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "events.log");
                System.IO.File.AppendAllText(logPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
            }
            catch { /* 取证日志写失败不影响解析 */ }
        }

        /// <summary>
        /// 实机取证辅助（V1）：把已解析响应的「opCode / returnCode / 参数键」记进
        /// <see cref="LastOperationCodeSample"/> 与 events.log。**只记录，不参与身份判定**。
        /// 目的：让「真实 Join 的 opCode 到底是多少」这个未知量由实机数据给出，而不是靠猜。
        /// </summary>
        private static void RecordOperationCodeSample(Protocol18.Photon.OperationResponse opData)
        {
            if (opData == null) return;

            var sb = new StringBuilder();
            sb.Append("opCode=").Append(opData.OperationCode)
              .Append(" ret=").Append(opData.ReturnCode)
              .Append(" keys=[");
            if (opData.Parameters != null)
            {
                int i = 0;
                foreach (var kv in opData.Parameters)
                {
                    if (i++ > 0) sb.Append(',');
                    sb.Append(kv.Key);
                }
            }
            sb.Append(']');
            string sample = sb.ToString();

            // 只在码值或参数键组合变化时打一行，避免刷屏；始终保留最近一条供 UI/LiveTab 读取
            string prev = LastOperationCodeSample;
            LastOperationCodeSample = sample;
            if (prev == sample) return;

            if (OnDebug != null) OnDebug("  [OpResp] " + sample);

            // P0-1 复核：取证行必须落盘。旧实现只走 OnDebug → UI（面板未展开时根本看不到），
            // events.log 里没有任何 [OpResp] 行 —— 注释要求的「从 events.log 取 opCode」实际不可能完成，
            // 导致 JoinResponse 白名单永远填不上、身份主路径永久禁用。
            try
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "events.log");
                System.IO.File.AppendAllText(logPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " [OpResp] " + sample + Environment.NewLine);
            }
            catch { /* 取证日志写失败不影响解析 */ }
        }

        private static int ReadInt32BE(byte[] d, int o)
        {
            if (o + 4 > d.Length) return 0;
            return (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
        }

        private static string FormatParams(Dictionary<byte, object> p)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            int i = 0;
            foreach (var kv in p)
            {
                if (i++ > 0) sb.Append(", ");
                string val;
                if (kv.Value is byte[])
                {
                    var arr = (byte[])kv.Value;
                    val = "[" + arr.Length + "b]";
                }
                else if (kv.Value is object[])
                {
                    val = "[" + ((object[])kv.Value).Length + "items]";
                }
                else
                {
                    val = kv.Value == null ? "null" : kv.Value.ToString();
                }
                sb.Append(kv.Key + ":" + val);
            }
            sb.Append("}");
            return sb.ToString();
        }
    }
}
