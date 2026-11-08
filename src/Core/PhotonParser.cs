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
                    if (TryParseJoinResponse(span, out long localObjectId, out string? localUsername))
                    {
                        if (OnDebug != null)
                            OnDebug("  [JoinResponse] LocalPlayer ObjectId=" + localObjectId + " Name=" + localUsername);

                        // R1/C3：身份唯一写入入口是状态机（强证据，确认或纠正）
                        LocalPlayerIdentity.OnJoinResponse(localObjectId, localUsername);
                    }
                }
                catch
                {
                    // 解析失败必须显式计数并跳过，不得退化为「opCode 默认 0 → 视为 Join」
                    System.Threading.Interlocked.Increment(ref _opRespParseFail);
                }
            }
            // TODO: OperationRequest parsing
        }


        /// <summary>
        /// R0 内核（可注入原始字节，供单测 T-01 构造输入）：
        /// 用 OperationResponse 的真实布局反序列化，并按「JoinResponse 判定 + 有效身份」双重校验。
        /// 判定规则（依据设计风险表与 T-01）：
        ///   - opCode 为 0 不视为 JoinResponse（旧代码把 0 当 Join，是反复清空身份的根因之一）；
        ///   - ReturnCode != 0（失败响应）不写状态；
        ///   - 参数 0（UserObjectId）必须可解析且 > 0 才提交；
        ///   - 参数 2（Username）可为空（允许「有 id 无名」）。
        /// 注意：真实 Join 的 opCode 码值未经实机确认（V1），当前采用「非零即候选」的宽松判定，
        /// 实机若发现漂移应改为精确码值匹配而不是继续放宽。
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
            if (opData == null || opData.Parameters == null) return false;

            if (opData.OperationCode == 0) return false;      // 未知/无效码，绝不写身份
            if (opData.ReturnCode != 0) return false;          // 服务器返回失败，不写状态

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
