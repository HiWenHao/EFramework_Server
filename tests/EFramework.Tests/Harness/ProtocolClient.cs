/*
 * ================================================
 * Describe:      Protocol probe: mirrors the Unity client's cn.efefef.net send/receive path exactly.
 * CreationTime:  2026-11-13 13:52:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EasyFramework.Systems.Net;

namespace EFramework.Tests.Harness
{
    /// <summary>
    /// 协议探针客户端：严格复刻客户端 <c>UdpSocket</c> + <c>NetworkSession</c> 的收发顺序，
    /// 用于在服务端改动后验证线上格式没有漂移。
    /// <para>Protocol probe client: reproduces the client's <c>UdpSocket</c> + <c>NetworkSession</c> send and
    /// receive order exactly, so any wire-format drift on the server side shows up here.</para>
    /// </summary>
    /// <remarks>
    /// 刻意不使用服务端任何类型：它只依赖 <c>EFramework.Net</c>（与 Unity 侧共享的协议契约层），
    /// 因此「探针通过」等价于「客户端能通」。
    /// <para>Deliberately depends on no server type: only on <c>EFramework.Net</c> (the protocol layer shared
    /// with Unity), so "the probe passes" means "the client works".</para>
    /// </remarks>
    internal sealed class ProtocolClient : IDisposable
    {
        #region Fields

        private readonly UdpClient _socket;
        private readonly Kcp _kcp;
        private readonly Queue<NetPacket> _inbox = new Queue<NetPacket>();
        private readonly byte[] _socketBuffer = new byte[65536];

        private byte[] _frameBuffer;
        private bool _disposed;

        #endregion

        #region Properties

        /// <summary>
        /// 本端 UDP 端口（服务端以 ip:port 识别会话）
        /// <para>Local UDP port (the server identifies sessions by ip:port)</para>
        /// </summary>
        public int LocalPort => ((IPEndPoint)_socket.Client.LocalEndPoint).Port;

        /// <summary>
        /// KCP 会话号（本探针显式指定，便于构造 conv 碰撞场景）
        /// <para>KCP conversation id (explicit so conv-collision scenarios can be constructed)</para>
        /// </summary>
        public uint Conv { get; }

        #endregion

        #region Methods

        /// <summary>
        /// 连接服务端并建立 KCP 通道（不发送任何数据，首包由调用方决定）。
        /// <para>Connect to the server and set up KCP (sends nothing; the first packet is up to the caller).</para>
        /// </summary>
        /// <param name="host">服务端地址<para>Server host</para></param>
        /// <param name="port">服务端端口<para>Server port</para></param>
        /// <param name="conv">KCP 会话号<para>KCP conversation id</para></param>
        /// <param name="settings">调参数；null 用默认<para>Tuning; null for defaults</para></param>
        public ProtocolClient(string host, int port, uint conv, KcpSettings settings = null)
        {
            Conv = conv;
            settings ??= KcpSettings.Default;

            // 与客户端 UdpSocket.Connect 一致：已连接套接字，只与远端通信
            _socket = new UdpClient();
            _socket.Connect(host, port);

            _kcp = new Kcp(conv, (data, size) => _socket.Send(data, size));
            settings.ApplyTo(_kcp);

            _frameBuffer = new byte[Math.Max(1024, settings.Mtu * 4)];
        }

        /// <summary>
        /// 发送一条消息（rpcId = 0 为单向，非 0 为请求）。
        /// <para>Send one message (rpcId = 0 is one-way, non-zero is a request).</para>
        /// </summary>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <param name="rpcId">RPC 配对号<para>RPC pairing id</para></param>
        /// <param name="payload">载荷字节<para>Payload bytes</para></param>
        public void Send(int opcode, int rpcId = 0, byte[] payload = null)
        {
            byte[] frame = MessagePipe.Encode(opcode, rpcId, payload);
            _kcp.Send(frame, 0, frame.Length);
        }

        /// <summary>
        /// 驱动一次收发：收 UDP → 喂 KCP → 推进 KCP → 取完整消息入队列（顺序与客户端 Update 一致）。
        /// <para>Drive one send/receive round: drain UDP, feed KCP, advance KCP, dequeue complete messages
        /// (same order as the client's Update).</para>
        /// </summary>
        public void Pump()
        {
            while (_socket.Available > 0)
            {
                IPEndPoint from = null;
                byte[] received = _socket.Receive(ref from);
                if (received.Length == 0) break;
                if (_kcp.Input(received, 0, received.Length) < 0) break;
            }

            _kcp.Update();

            while (true)
            {
                int size = _kcp.Recv(_frameBuffer, 0);

                // 与客户端一致：缓冲不足则扩容重试
                if (size == -3)
                {
                    Array.Resize(ref _frameBuffer, _frameBuffer.Length * 2);
                    continue;
                }

                if (size <= 0) break;

                if (MessagePipe.TryDecodePacket(0, _frameBuffer, out NetPacket packet))
                {
                    _inbox.Enqueue(packet);
                }
            }
        }

        /// <summary>
        /// 等待满足条件的一条消息（内部持续 Pump，超时抛出异常并打印已收消息便于定位）。
        /// <para>Wait for a matching message (keeps pumping; on timeout it throws with the received packets
        /// listed to ease triage).</para>
        /// </summary>
        /// <param name="predicate">匹配条件<para>Match predicate</para></param>
        /// <param name="description">条件描述（用于超时信息）<para>Predicate description (for the timeout message)</para></param>
        /// <param name="timeoutMilliseconds">超时毫秒数<para>Timeout in milliseconds</para></param>
        /// <returns>命中的消息包<para>The matched packet</para></returns>
        public NetPacket WaitFor(Func<NetPacket, bool> predicate, string description, int timeoutMilliseconds = 3000)
        {
            var stopwatch = Stopwatch.StartNew();
            int drained = 0;

            while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                Pump();

                while (_inbox.Count > 0)
                {
                    NetPacket packet = _inbox.Dequeue();
                    drained++;
                    if (predicate(packet)) return packet;
                }

                System.Threading.Thread.Sleep(1);
            }

            throw new TimeoutException(
                $"Timed out after {timeoutMilliseconds}ms waiting for {description} " +
                $"(port={LocalPort}, conv={Conv}, packetsSeen={drained}, kcp: {_kcp.DebugInfo})");
        }

        /// <summary>
        /// 等待指定 Opcode 的消息。
        /// <para>Wait for a message with the given opcode.</para>
        /// </summary>
        /// <param name="opcode">目标操作码<para>Target opcode</para></param>
        /// <param name="timeoutMilliseconds">超时毫秒数<para>Timeout in milliseconds</para></param>
        /// <returns>命中的消息包<para>The matched packet</para></returns>
        public NetPacket WaitFor(int opcode, int timeoutMilliseconds = 3000)
        {
            return WaitFor(packet => packet.Opcode == opcode, $"opcode {opcode}", timeoutMilliseconds);
        }

        /// <summary>
        /// 取出当前已收到的全部消息（内部先 Pump 一次），返回取出条数。
        /// <para>Take every message received so far (pumping once first); returns how many were taken.</para>
        /// </summary>
        /// <param name="onPacket">逐条回调<para>Per-packet callback</para></param>
        /// <returns>取出条数<para>Number of packets taken</para></returns>
        public int Drain(Action<NetPacket> onPacket)
        {
            Pump();

            int taken = 0;
            while (_inbox.Count > 0)
            {
                onPacket?.Invoke(_inbox.Dequeue());
                taken++;
            }

            return taken;
        }

        /// <summary>
        /// 关闭套接字。
        /// <para>Close the socket.</para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _socket.Dispose();
        }

        #endregion
    }
}
