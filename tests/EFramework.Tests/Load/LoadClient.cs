/*
 * ================================================
 * Describe:      One load-test client: protocol-accurate, rate-limited, RTT-tracking.
 * CreationTime:  2026-11-13 15:16:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Text;
using EFramework.Tests.Harness;
using EasyFramework.Systems.Net;

namespace EFramework.Tests.Load
{
    /// <summary>
    /// 压测用客户端：复用与服务端对端完全一致的协议栈（<see cref="ProtocolClient"/>），只多了限速与 RTT 统计。
    /// <para>A load-test client that reuses the exact protocol stack of the end-to-end probe, adding only rate
    /// limiting and RTT tracking.</para>
    /// </summary>
    /// <remarks>
    /// <para>刻意不用"裸发 UDP"：那样测到的是"网络能跑多快"，不是"这套服务端能处理多快"。复用真实客户端栈
    /// 意味着 KCP 的分片、重传、窗口都在参与，结果才有意义。</para>
    /// <para>Deliberately not raw UDP: that would measure the network, not the server. Reusing the real client
    /// stack keeps KCP fragmentation, retransmission and windowing in the loop, which is what makes the numbers
    /// meaningful.</para>
    /// </remarks>
    internal sealed class LoadClient : IDisposable
    {
        #region Fields

        private const int MaxBurstPerService = 64;
        private const double MaxCatchUpSeconds = 1d;
        private const int SequencePayloadSize = 8;

        private readonly ProtocolClient _protocol;
        private readonly LoadOptions _options;
        private readonly Action<double> _onRttSample;
        private readonly Dictionary<int, double> _inflight = new Dictionary<int, double>();
        private readonly double _intervalSeconds;
        private readonly byte[] _payload;

        private int _nextId = 1;
        private double _nextSendAt;

        #endregion

        #region Properties

        public long Sent { get; private set; }

        public long Received { get; private set; }

        /// <summary>
        /// 是否还有未回来的请求（有则生成器必须继续自旋，否则会把自身的唤醒延迟算进 RTT）
        /// <para>Whether requests are still outstanding (if so the generator must keep spinning, otherwise its
        /// own wake-up latency lands in the measured RTT)</para>
        /// </summary>
        public bool HasInflight => _inflight.Count > 0;

        public bool HandshakeSettled { get; private set; }

        public int HandshakeCode { get; private set; }

        public string HandshakeReason { get; private set; }

        #endregion

        #region Methods

        public LoadClient(int index, LoadOptions options, Action<double> onRttSample)
        {
            _options = options;
            _onRttSample = onRttSample;
            _intervalSeconds = 1d / options.QpsPerClient;

            // conv 必须每个客户端不同：服务端按端点建会话，但相同的 conv 会让"同端点重建"的语义更难排查
            _protocol = new ProtocolClient(options.Host, options.Port, (uint)(index + 1));
            _payload = BuildPayload(options);
        }

        /// <summary>
        /// 发送握手（未配置票据时视为不需要握手）。
        /// <para>Send the handshake (no token configured means authentication is off).</para>
        /// </summary>
        public void SendHandshake()
        {
            if (string.IsNullOrEmpty(_options.Token))
            {
                HandshakeSettled = true;
                return;
            }

            _protocol.Send(NetOpcode.Handshake, 0, Encoding.UTF8.GetBytes(_options.Token));
        }

        /// <summary>
        /// 驱动一次收发：先收包（顺手记录 RTT），再把到期的发送补上。
        /// <para>Drive one round: drain inbound (recording RTT), then send whatever is due.</para>
        /// </summary>
        /// <param name="now">当前单调时刻（秒）<para>Current monotonic timestamp (seconds)</para></param>
        /// <returns>本轮是否有实际收发（供调用方决定要不要让出 CPU）<para>Whether this round did any work</para></returns>
        public bool Service(double now)
        {
            bool worked = false;

            int drained = _protocol.Drain(packet =>
            {
                worked = true;
                OnPacket(packet, now);
            });

            if (drained > 0) worked = true;

            if (!HandshakeSettled)
            {
                // 握手阶段只收不发，等结果
                return worked;
            }

            int burst = 0;
            while (now >= _nextSendAt && burst < MaxBurstPerService)
            {
                SendOne(now);
                _nextSendAt += _intervalSeconds;
                burst++;
            }

            if (burst > 0) worked = true;

            // 被调度打断导致严重落后时放弃补发：否则会喷出一串"补偿风暴"，把测出来的延迟搞脏
            if (now - _nextSendAt > MaxCatchUpSeconds) _nextSendAt = now;

            return worked;
        }

        public void Begin(double now)
        {
            _nextSendAt = now;
        }

        /// <summary>
        /// 停止发送新消息，仅继续收在途回包（用于计时结束后把尾巴收干净）。
        /// <para>Stop sending new messages but keep draining in-flight replies (used to finish the tail after
        /// the timed window).</para>
        /// </summary>
        public void StopSending()
        {
            _nextSendAt = double.PositiveInfinity;
        }

        public void Dispose()
        {
            _protocol.Dispose();
        }

        private void SendOne(double now)
        {
            // id 用正数，既能当 rpcId 也能写进载荷
            int id = _nextId++;
            if (_nextId < 0) _nextId = 1;

            _inflight[id] = now;

            if (_options.EchoMode)
            {
                // 业务路径：rpcId 由服务端原样回显，直接用它配对
                _protocol.Send(LoadRunner.EchoReqOpcode, id, _payload);
            }
            else
            {
                // 协议路径：Ping 原样回显载荷，把序号写在载荷里配对
                BitConverter.TryWriteBytes(new Span<byte>(_payload, 0, SequencePayloadSize), (long)id);
                _protocol.Send(NetOpcode.Ping, 0, _payload);
            }

            Sent++;
        }

        private void OnPacket(NetPacket packet, double now)
        {
            if (packet.Opcode == NetOpcode.HandshakeResult)
            {
                HandshakeCode = packet.PayloadLength > 0 ? packet.Payload[0] : -1;
                HandshakeReason = packet.PayloadLength > 1
                    ? Encoding.UTF8.GetString(packet.Payload, 1, packet.PayloadLength - 1)
                    : string.Empty;
                HandshakeSettled = true;
                return;
            }

            int id;
            if (packet.Opcode == LoadRunner.EchoResOpcode)
            {
                id = packet.RpcId;
            }
            else if (packet.Opcode == NetOpcode.Pong && packet.PayloadLength >= SequencePayloadSize)
            {
                id = (int)BitConverter.ToInt64(packet.Payload, 0);
            }
            else
            {
                return;
            }

            if (!_inflight.Remove(id, out double sentAt)) return;

            Received++;
            _onRttSample((now - sentAt) * 1000d);
        }

        // 业务模式：构造一条合法 JSON（服务端要反序列化），填充长度由 --size 控制
        private static byte[] BuildPayload(LoadOptions options)
        {
            if (!options.EchoMode) return new byte[Math.Max(SequencePayloadSize, options.PayloadSize)];

            string padding = options.PayloadSize > 0 ? new string('x', options.PayloadSize) : string.Empty;
            return Encoding.UTF8.GetBytes($"{{\"Content\":\"{padding}\"}}");
        }

        #endregion
    }
}
