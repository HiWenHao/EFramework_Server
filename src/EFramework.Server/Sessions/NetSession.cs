/*
 * ================================================
 * Describe:      One server-side KCP session (transport binding + frame codec + counters).
 * CreationTime:  2026-11-13 13:06:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Concurrent;
using System.Net;
using EasyFramework.Systems.Net;

namespace EasyFramework.Server.Sessions
{
    /// <summary>
    /// 服务端会话：一条「远端端点 + conv」对应的 KCP 通道，负责可靠收发的机械部分。
    /// <para>Server session: the KCP channel for one "remote endpoint + conv" pair; owns the mechanical
    /// part of reliable send/receive.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不继承客户端 NetworkSession</b>：客户端会话是「主动连接方」——自己分配 conv、
    /// 自己做握手与重连；服务端会话是「被动接受方」——conv 由首个数据报决定，且一个传输套接字服务
    /// 所有端点。把两者合成一个类型会让两类生命周期纠缠，故只在协议层（KCP + 帧格式）共享代码。</para>
    /// <para><b>Why this is not the client's NetworkSession</b>: the client session is the active side
    /// (allocates the conv, drives handshake and reconnect); the server session is passive (the conv comes
    /// from the first datagram, and one socket serves all endpoints). Merging them would entangle two
    /// lifecycles, so only the protocol layer (KCP + frame format) is shared.</para>
    /// <para>本类型不持有套接字，所有发送都委托给 <see cref="Transport.IServerTransport"/>；
    /// 状态变更是单线程的（消息泵线程），因此无需加锁。</para>
    /// <para>This type owns no socket: sends are delegated to the transport. State mutations are
    /// single-threaded (the pump thread), so no locking is needed.</para>
    /// </remarks>
    public sealed class NetSession
    {
        // 跨线程投递的一条出站消息（队列元素用结构体，避免每次入队都新建对象）
        private readonly struct OutboundMessage
        {
            public readonly int Opcode;
            public readonly int RpcId;
            public readonly byte[] Payload;

            public OutboundMessage(int opcode, int rpcId, byte[] payload)
            {
                Opcode = opcode;
                RpcId = rpcId;
                Payload = payload;
            }
        }

        #region Fields

        // 单条完整消息的初始读取缓冲倍数（超出时自动扩容，扩容不会丢消息）
        private const int InitialReceiveBufferMultiplier = 4;

        // 出站帧缓冲初始容量（按需扩容；复用后每条出站消息不再新建数组）
        private const int MinFrameBufferSize = 256;

        private readonly Kcp _kcp;
        private readonly Transport.IServerTransport _transport;
        private readonly IPEndPoint _remoteEndPoint;
        private readonly string _remoteEndPointText;
        private readonly Action<NetSession, NetPacket> _onPacket;
        private readonly IServerLogger _logger;

        // 创建本会话的线程（即消息泵线程）：只有它能碰 KCP 内部状态
        private readonly int _pumpThreadId;

        // 跨线程出站队列：非泵线程调用 Send 时先入队，下个节拍由泵线程代发
        private readonly ConcurrentQueue<OutboundMessage> _outbox = new ConcurrentQueue<OutboundMessage>();

        private byte[] _kcpRecvBuffer;

        // 出站复用帧缓冲（按需扩容）：每条消息只做一次拷贝，不再每条新建数组
        private byte[] _frameBuffer;

        // 会话保护上限（出站积压 / 入站速率）
        private readonly SessionLimits _limits;

        // 入站限流的固定窗口计数
        private int _rateWindowMessages;
        private double _rateWindowStart;
        private long _droppedMessages;
        private double _lastReceiveTime;
        private long _receivedDatagrams;
        private long _receivedMessages;
        private long _sentMessages;
        private bool _closed;
        private string _closeReason;
        private double _requestedCloseTime;

        #endregion

        #region Properties

        /// <summary>
        /// 会话 Id（服务端分配，单调递增，全进程唯一）
        /// <para>Session id (server-assigned, monotonically increasing, unique per process)</para>
        /// </summary>
        public int SessionId { get; }

        /// <summary>
        /// KCP 会话号（由客户端首个数据报决定，同一会话内不变）
        /// <para>KCP conversation id (taken from the client's first datagram; constant for the session)</para>
        /// </summary>
        public uint Conv { get; }

        /// <summary>
        /// 远端端点
        /// <para>Remote endpoint</para>
        /// </summary>
        public IPEndPoint RemoteEndPoint => _remoteEndPoint;

        /// <summary>
        /// 远端端点文本形式（ip:port，日志用，已缓存避免重复格式化）
        /// <para>Remote endpoint as text (ip:port, cached for logging)</para>
        /// </summary>
        public string RemoteEndPointText => _remoteEndPointText;

        /// <summary>
        /// 已通过握手校验的身份主体（账号 Id）；未鉴权时为 null
        /// <para>Verified subject (account id) after the handshake; null when unauthenticated</para>
        /// </summary>
        /// <remarks>
        /// 业务层应当用这个值判断"这条连接是谁"，而不是相信客户端在消息里自报的 Id —— 后者可以随便填。
        /// <para>Business code must use this instead of an id the client claims in a message.</para>
        /// </remarks>
        public string AccountId { get; internal set; }

        /// <summary>
        /// 当前状态
        /// <para>Current state</para>
        /// </summary>
        public NetSessionState State { get; internal set; }

        /// <summary>
        /// 最近一次收到数据报的单调时刻（秒）
        /// <para>Monotonic timestamp of the last received datagram, in seconds</para>
        /// </summary>
        public double LastReceiveTime => _lastReceiveTime;

        /// <summary>
        /// 累计收到的数据报数（KCP 层）
        /// <para>Total datagrams received at the KCP layer</para>
        /// </summary>
        public long ReceivedDatagrams => _receivedDatagrams;

        /// <summary>
        /// 累计收到的完整消息数（帧层）
        /// <para>Total complete messages received at the frame layer</para>
        /// </summary>
        public long ReceivedMessages => _receivedMessages;

        /// <summary>
        /// 累计发送的完整消息数
        /// <para>Total complete messages sent</para>
        /// </summary>
        public long SentMessages => _sentMessages;

        /// <summary>
        /// 累计因入站限流被丢弃的消息数
        /// <para>Messages dropped by the inbound rate limit</para>
        /// </summary>
        public long DroppedMessages => _droppedMessages;

        /// <summary>
        /// 是否已关闭
        /// <para>Whether the session is closed</para>
        /// </summary>
        public bool IsClosed => _closed;

        /// <summary>
        /// 关闭原因（未关闭时为空）
        /// <para>Close reason (empty while open)</para>
        /// </summary>
        public string CloseReason => _closeReason;

        /// <summary>
        /// KCP 待确认段数（诊断用：持续增长说明对端不回 ACK）
        /// <para>Unacknowledged KCP segments (diagnostics: sustained growth means the peer is not acking)</para>
        /// </summary>
        public int WaitSnd => _kcp.WaitSnd;

        /// <summary>
        /// KCP 内部状态快照（排障用）
        /// <para>KCP internal state snapshot (troubleshooting)</para>
        /// </summary>
        public string DebugInfo => _kcp.DebugInfo;

        #endregion

        #region Methods

        /// <summary>
        /// 创建服务端会话（由会话路由在收到首个数据报时调用）。
        /// <para>Create a server session (called by the router on the first datagram).</para>
        /// </summary>
        /// <param name="sessionId">会话 Id<para>Session id</para></param>
        /// <param name="conv">KCP 会话号<para>KCP conversation id</para></param>
        /// <param name="remoteEndPoint">远端端点<para>Remote endpoint</para></param>
        /// <param name="transport">传输层<para>Transport</para></param>
        /// <param name="settings">KCP 调参数<para>KCP tuning</para></param>
        /// <param name="limits">会话保护上限<para>Session protection limits</para></param>
        /// <param name="onPacket">完整消息回调（在消息泵线程上同步调用）<para>Complete-message callback (invoked synchronously on the pump thread)</para></param>
        /// <param name="logger">日志输出<para>Log output</para></param>
        /// <param name="now">当前单调时刻（秒）<para>Current monotonic timestamp in seconds</para></param>
        internal NetSession(int sessionId, uint conv, IPEndPoint remoteEndPoint, Transport.IServerTransport transport,
            KcpSettings settings, SessionLimits limits, Action<NetSession, NetPacket> onPacket, IServerLogger logger,
            double now)
        {
            SessionId = sessionId;
            Conv = conv;
            _remoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
            _remoteEndPointText = remoteEndPoint.ToString();
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _onPacket = onPacket;
            _logger = logger;
            _limits = limits;
            _lastReceiveTime = now;
            _rateWindowStart = now;

            // 会话由路由在消息泵线程上创建，这里记下的就是唯一的「可写 KCP」线程
            _pumpThreadId = Environment.CurrentManagedThreadId;

            settings ??= KcpSettings.Default;
            _kcp = new Kcp(conv, SendDatagram);
            settings.ApplyTo(_kcp);

            _kcpRecvBuffer = new byte[Math.Max(1024, settings.Mtu * InitialReceiveBufferMultiplier)];
            State = NetSessionState.Handshaking;
        }

        /// <summary>
        /// 投喂一条入站数据报（KCP 在此完成分片重排与确认）。
        /// <para>Feed one inbound datagram (KCP handles reordering and acknowledgement here).</para>
        /// </summary>
        /// <param name="datagram">数据报内容<para>Datagram payload</para></param>
        /// <param name="length">有效字节数<para>Valid byte count</para></param>
        /// <param name="now">当前单调时刻（秒）<para>Current monotonic timestamp in seconds</para></param>
        internal void OnDatagram(byte[] datagram, int length, double now)
        {
            if (_closed) return;

            _receivedDatagrams++;
            _lastReceiveTime = now;

            int result = _kcp.Input(datagram, 0, length);
            if (result < 0)
            {
                // conv 不匹配或段长度非法：丢弃该数据报即可，KCP 会靠对端重传恢复
                if (_logger?.IsEnabled(LogLevel.Debug) == true)
                {
                    _logger.Debug($"[SESSION {SessionId}] malformed datagram dropped ({length}B, kcp={result})");
                }
            }
        }

        /// <summary>
        /// 推进一个节拍：冲刷发送、处理重传、取出完整消息并回调。
        /// <para>Advance one tick: flush sends, process retransmissions, drain complete messages and invoke
        /// the callback.</para>
        /// </summary>
        /// <param name="now">当前单调时刻（秒）<para>Current monotonic timestamp in seconds</para></param>
        internal void Tick(double now)
        {
            if (_closed) return;

            // 跨线程投递的消息必须在冲刷之前搬进 KCP，否则要多等一个节拍
            DrainOutbox();

            _kcp.Update();

            while (true)
            {
                int size = _kcp.Recv(_kcpRecvBuffer, 0);

                // 下一条完整消息大于缓冲：扩容后重试，消息仍留在 KCP 队列，不会丢
                if (size == -3)
                {
                    _kcpRecvBuffer = new byte[_kcpRecvBuffer.Length * 2];
                    continue;
                }

                if (size <= 0) break;

                _receivedMessages++;

                // 传真实消息长度而不是缓冲容量：伪造的超长 payloadLength 会被拒，而不是读出上一条的残留
                if (!MessagePipe.TryDecodePacket(SessionId, _kcpRecvBuffer, 0, size, out NetPacket packet))
                {
                    if (_logger?.IsEnabled(LogLevel.Warn) == true)
                    {
                        _logger.Warn($"[SESSION {SessionId}] frame decode failed, {size}B dropped");
                    }

                    continue;
                }

                // 限流：超限直接丢弃而不是排队 —— 排队等于把刷包的压力转嫁给其他玩家
                if (!TryConsumeReceiveBudget(now)) continue;

                _onPacket?.Invoke(this, packet);

                // 处理器可能已关闭本会话，后续消息不再派发
                if (_closed) return;
            }

            if (_requestedCloseTime > 0d && now >= _requestedCloseTime)
            {
                Close(_closeReason);
            }
        }

        /// <summary>
        /// 发送一条单向消息（rpcId = 0，客户端按 Opcode 路由到处理器）。
        /// <para>Send a one-way message (rpcId = 0; the client routes it to a handler by opcode).</para>
        /// </summary>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        public void Send(int opcode, byte[] payload = null)
        {
            Send(opcode, 0, payload);
        }

        /// <summary>
        /// 发送一条消息，rpcId 非 0 时客户端会把它配对给挂起的 RPC 等待方。
        /// <para>Send a message; a non-zero rpcId is paired with a pending client RPC call.</para>
        /// </summary>
        /// <remarks>
        /// <b>线程安全</b>：可从任意线程调用。非消息泵线程会先入队，由下一个节拍的泵线程代发，
        /// 因此跨线程回包的额外延迟上限是一个 <c>TickIntervalMs</c>。
        /// KCP 内部状态只能在泵线程上访问，直接跨线程写会破坏其链表与序号。
        /// <para><b>Thread safety</b>: safe from any thread. Off-pump callers are queued and flushed by the
        /// pump on its next tick, adding at most one <c>TickIntervalMs</c> of latency. KCP state is
        /// pump-thread-only; writing it cross-thread corrupts its lists and sequence numbers.</para>
        /// </remarks>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <param name="rpcId">RPC 配对号（回显请求的 rpcId）<para>RPC pairing id (echo the request's rpcId)</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        public void Send(int opcode, int rpcId, byte[] payload)
        {
            if (_closed) return;

            // 跨线程（例如处理器把耗时逻辑丢到线程池后回包）：只入队，不碰 KCP
            if (Environment.CurrentManagedThreadId != _pumpThreadId)
            {
                _outbox.Enqueue(new OutboundMessage(opcode, rpcId, payload));
                return;
            }

            SendCore(opcode, rpcId, payload);
        }

        // 真正的发送：只允许泵线程调用
        private void SendCore(int opcode, int rpcId, byte[] payload)
        {
            // 出站积压保护：对端长时间不 ACK 时，Kcp.Send 不会失败，只会让待发队列一直变长。
            // 广播一次就往每个死会话里堆一份 —— 这里把它变成「及时判定不可用并回收」。
            int backlogCap = _limits.MaxSendBacklogSegments;
            if (backlogCap > 0 && _kcp.WaitSnd >= backlogCap)
            {
                string reason = $"Send backlog overflow ({_kcp.WaitSnd} >= {backlogCap} segments)";
                if (_logger?.IsEnabled(LogLevel.Warn) == true)
                {
                    _logger.Warn($"[SESSION {SessionId}] {reason}, closing as unreachable");
                }

                Close(reason);
                return;
            }

            // 复用帧缓冲：编码直接写进去，KCP 再拷进段，全程不新建数组
            int required = MessagePipe.HeaderSize + (payload?.Length ?? 0);
            if (_frameBuffer == null || _frameBuffer.Length < required)
                _frameBuffer = new byte[Math.Max(required, MinFrameBufferSize)];

            if (!MessagePipe.Encode(opcode, rpcId, payload, _frameBuffer, out int length))
            {
                if (_logger?.IsEnabled(LogLevel.Warn) == true)
                {
                    _logger.Warn($"[SESSION {SessionId}] frame encode failed (opcode={opcode}, " +
                                 $"need={required}B, buffer={_frameBuffer.Length}B)");
                }

                return;
            }

            // KCP 在下一个节拍统一冲刷，故本条消息的实际发出延迟 <= ServerOptions.TickIntervalMs
            int result = _kcp.Send(_frameBuffer, 0, length);
            if (result < 0)
            {
                if (_logger?.IsEnabled(LogLevel.Warn) == true)
                {
                    // -2 = 超过 255 个分片（约 124KB 以上），-3 = 分片数超过对端接收窗口
                    string why = result == -2 ? "message too large (>255 fragments)" : "fragments exceed peer window";
                    _logger.Warn($"[SESSION {SessionId}] send rejected (opcode={opcode}, {length}B): {why}, dropped");
                }

                return;
            }

            _sentMessages++;
        }

        // 入站限流：固定窗口计数，超限丢弃并计数；只在首次丢弃时告警，避免刷屏
        private bool TryConsumeReceiveBudget(double now)
        {
            int limit = _limits.MaxMessagesPerSecond;
            if (limit <= 0) return true;

            if (now - _rateWindowStart >= 1d)
            {
                _rateWindowStart = now;
                _rateWindowMessages = 0;
            }

            if (_rateWindowMessages < limit)
            {
                _rateWindowMessages++;
                return true;
            }

            _droppedMessages++;
            if (_droppedMessages == 1 && _logger?.IsEnabled(LogLevel.Warn) == true)
            {
                _logger.Warn($"[SESSION {SessionId}] receive rate limit {limit}/s hit; further messages are dropped until the window resets");
            }

            return false;
        }

        // 把跨线程投递的消息搬进 KCP（必须在冲刷之前）
        private void DrainOutbox()
        {
            while (_outbox.TryDequeue(out OutboundMessage message))
            {
                if (_closed) return;
                SendCore(message.Opcode, message.RpcId, message.Payload);
            }
        }

        /// <summary>
        /// 延迟关闭：先让当前待发数据冲刷出去，再关会话（用于「拒绝握手」这类需要把结论送达对端的场景）。
        /// <para>Delayed close: let pending outbound data flush before closing (used when the verdict itself
        /// must reach the peer, e.g. a handshake rejection).</para>
        /// </summary>
        /// <param name="reason">关闭原因<para>Close reason</para></param>
        /// <param name="delaySeconds">延迟秒数（至少一个泵周期）<para>Delay in seconds (at least one pump tick)</para></param>
        public void RequestClose(string reason, double delaySeconds = 0.2d)
        {
            if (_closed || _requestedCloseTime > 0d) return;

            _closeReason = reason;
            _requestedCloseTime = ServerClock.Now + Math.Max(0d, delaySeconds);
        }

        /// <summary>
        /// 立即关闭会话（不再收发包，等待路由扫除回收）。
        /// <para>Close the session immediately (no further traffic; reclaimed by the next router sweep).</para>
        /// </summary>
        /// <param name="reason">关闭原因<para>Close reason</para></param>
        public void Close(string reason)
        {
            if (_closed) return;

            _closed = true;
            _closeReason = reason;
            State = NetSessionState.Closed;
        }

        /// <summary>
        /// 返回会话的一行摘要（日志与诊断用）。
        /// <para>Returns a single-line summary of the session (logging and diagnostics).</para>
        /// </summary>
        /// <returns>摘要文本<para>Summary text</para></returns>
        public override string ToString()
        {
            return $"#{SessionId} {_remoteEndPointText} conv={Conv} state={State} rx={_receivedMessages} tx={_sentMessages}";
        }

        // KCP 出站出口：把拼好的段直接交给传输层
        private void SendDatagram(byte[] data, int size)
        {
            _transport.Send(data, 0, size, _remoteEndPoint);
        }

        #endregion
    }
}
