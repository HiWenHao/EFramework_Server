/*
 * ================================================
 * Describe:      Session registry and datagram router (endpoint -> KCP session).
 * CreationTime:  2026-11-13 13:10:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Net;
using EasyFramework.Systems.Net;

namespace EasyFramework.Server.Sessions
{
    /// <summary>
    /// 会话路由：把入站数据报派发到对应会话，并管理会话的建立、上限、空闲回收与握手门禁。
    /// <para>Session router: routes inbound datagrams to their session and owns creation, capacity,
    /// idle reclamation and the handshake gate.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>为什么按远端端点建会话，而不是按 conv</b>：客户端 <c>NetworkManager</c> 的 conv 是
    /// 进程内自增（每个客户端进程都从 1 开始），两个玩家会撞同一个 conv。端点（ip:port）才是网络层
    /// 唯一的身份，conv 只用于「同一端点内检测通道重建」。</para>
    /// <para><b>Why key by endpoint instead of conv</b>: the client allocates convs per process starting
    /// at 1, so two players collide on conv 1. The endpoint (ip:port) is the only unique identity at the
    /// network layer; conv is used only to detect a channel rebuild on the same endpoint.</para>
    /// <para><b>遍历安全</b>：派发处理器可能关闭/新建会话（改字典），故 Tick/Sweep 一律遍历复用缓冲区，
    /// 不直接枚举字典。</para>
    /// <para><b>Iteration safety</b>: handlers may close or create sessions (mutating the dictionaries),
    /// so Tick/Sweep iterate a reused buffer rather than enumerating the dictionaries.</para>
    /// </remarks>
    internal sealed class SessionRouter
    {
        #region Fields

        // KCP 段头长度：短于此长度的数据报不可能是有效 KCP 段（多为端口扫描）
        private const int MinKcpDatagramSize = 24;

        private readonly Dictionary<IPEndPoint, NetSession> _byEndPoint = new Dictionary<IPEndPoint, NetSession>();
        private readonly Dictionary<int, NetSession> _byId = new Dictionary<int, NetSession>();

        // Tick/Sweep 的复用遍历缓冲：稳态下零分配
        private readonly List<NetSession> _iterationBuffer = new List<NetSession>();

        private readonly ServerOptions _options;
        private readonly KcpSettings _settings;
        private readonly SessionLimits _limits;
        private readonly Transport.IServerTransport _transport;
        private readonly ServerDispatcher _dispatcher;
        private readonly IServerLogger _logger;
        private readonly Action<NetSession> _onOpened;
        private readonly Action<NetSession, string> _onClosed;

        private int _nextSessionId = 1;
        private long _rejectedByLimit;

        #endregion

        #region Properties

        /// <summary>
        /// 当前会话数（含已关闭待回收的会话）
        /// <para>Current session count (including closed sessions awaiting reclamation)</para>
        /// </summary>
        public int Count => _byEndPoint.Count;

        /// <summary>
        /// 因超过会话上限被拒绝的端点数
        /// <para>Endpoints rejected because the session ceiling was hit</para>
        /// </summary>
        public long RejectedByLimit => System.Threading.Interlocked.Read(ref _rejectedByLimit);

        #endregion

        #region Methods

        /// <summary>
        /// 构造会话路由。
        /// <para>Create the session router.</para>
        /// </summary>
        /// <param name="options">服务端参数<para>Server options</para></param>
        /// <param name="settings">KCP 调参数<para>KCP tuning</para></param>
        /// <param name="transport">传输层（会话出站出口）<para>Transport (session outbound sink)</para></param>
        /// <param name="dispatcher">消息分发器<para>Message dispatcher</para></param>
        /// <param name="logger">日志输出<para>Log output</para></param>
        /// <param name="onOpened">会话建立回调<para>Session-opened callback</para></param>
        /// <param name="onClosed">会话关闭回调（会话, 原因）<para>Session-closed callback (session, reason)</para></param>
        public SessionRouter(ServerOptions options, KcpSettings settings, Transport.IServerTransport transport,
            ServerDispatcher dispatcher, IServerLogger logger, Action<NetSession> onOpened,
            Action<NetSession, string> onClosed)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _settings = settings ?? KcpSettings.Default;
            // 会话上限从宿主参数派生一次，所有会话共用同一份只读值
            _limits = new SessionLimits(options.MaxSendBacklogSegments, options.MaxMessagesPerSecond);
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = logger;
            _onOpened = onOpened;
            _onClosed = onClosed;
        }

        /// <summary>
        /// 按会话 Id 取会话。
        /// <para>Get a session by id.</para>
        /// </summary>
        /// <param name="sessionId">会话 Id<para>Session id</para></param>
        /// <param name="session">取到的会话；不存在为 null<para>The session; null when absent</para></param>
        /// <returns>是否存在<para>Whether it exists</para></returns>
        public bool TryGet(int sessionId, out NetSession session)
        {
            return _byId.TryGetValue(sessionId, out session);
        }

        /// <summary>
        /// 取当前会话快照（用于广播与外部遍历，调用方拿到的是独立数组）。
        /// <para>Snapshot the current sessions (for broadcast and external iteration; the caller gets
        /// an independent array).</para>
        /// </summary>
        /// <returns>会话数组<para>Array of sessions</para></returns>
        public NetSession[] Snapshot()
        {
            if (_byEndPoint.Count == 0) return Array.Empty<NetSession>();

            var snapshot = new NetSession[_byEndPoint.Count];
            _byEndPoint.Values.CopyTo(snapshot, 0);
            return snapshot;
        }

        /// <summary>
        /// 路由一条入站数据报：定位或建立会话，然后投喂 KCP。
        /// <para>Route one inbound datagram: locate or create the session, then feed KCP.</para>
        /// </summary>
        /// <param name="datagram">入站数据报<para>Inbound datagram</para></param>
        /// <param name="now">当前单调时刻（秒）<para>Current monotonic timestamp in seconds</para></param>
        public void Route(Transport.Datagram datagram, double now)
        {
            if (datagram.Length < MinKcpDatagramSize) return;

            uint conv = ReadConv(datagram.Buffer);

            if (!_byEndPoint.TryGetValue(datagram.RemoteEndPoint, out NetSession session) || session.Conv != conv)
            {
                // conv 变化 = 同一端点重建了 KCP 通道（正常重连会换本地端口，走不到这里）
                if (session != null) Remove(session, $"KCP conv changed ({session.Conv} -> {conv})");

                if (_byEndPoint.Count >= _options.MaxSessions)
                {
                    long rejected = System.Threading.Interlocked.Increment(ref _rejectedByLimit);
                    if (rejected == 1 || rejected % 1000 == 0)
                    {
                        _logger?.Warn($"[ROUTER] session ceiling {_options.MaxSessions} reached; " +
                                      $"dropped datagram from {datagram.RemoteEndPoint} (total rejected={rejected})");
                    }

                    return;
                }

                session = Create(datagram.RemoteEndPoint, conv, now);
            }

            session.OnDatagram(datagram.Buffer, datagram.Length, now);
        }

        /// <summary>
        /// 一个节拍内推进全部会话并顺手回收：关闭的、空闲超时的、以及 Tick 中自关的。
        /// <para>Advance every session and reclaim in the same pass: closed ones, idle-timed-out ones,
        /// and those that closed themselves during Tick.</para>
        /// </summary>
        /// <remarks>
        /// 合并成一次遍历是为了只填一次复用缓冲（每节拍两次全量拷贝没有必要）。
        /// 遍历的是复用缓冲而非字典本身，因此 <see cref="Remove"/> 在循环内改字典是安全的。
        /// <para>Merged into one pass to fill the reused buffer once (two full copies per tick are
        /// pointless). The loop walks the reused buffer, not the dictionary, so Remove is safe inside.</para>
        /// </remarks>
        /// <param name="now">当前单调时刻（秒）<para>Current monotonic timestamp in seconds</para></param>
        public void Update(double now)
        {
            if (_byEndPoint.Count == 0) return;

            double idleTimeout = _options.SessionIdleTimeoutSeconds;

            FillIterationBuffer();
            for (int i = 0; i < _iterationBuffer.Count; i++)
            {
                NetSession session = _iterationBuffer[i];

                // 上一轮已被关闭（例如处理器里主动 Close），直接回收
                if (session.IsClosed)
                {
                    Remove(session, session.CloseReason);
                    continue;
                }

                try
                {
                    session.Tick(now);
                }
                catch (Exception e)
                {
                    // 单个会话的异常不能拖垮整个服务端：关掉它，立即回收
                    _logger?.Error($"[SESSION {session.SessionId}] tick failed, closing session.", e);
                    Remove(session, "Session tick failed");
                    continue;
                }

                // Tick 内部可能触发了延迟关闭
                if (session.IsClosed)
                {
                    Remove(session, session.CloseReason);
                    continue;
                }

                if (idleTimeout > 0d && now - session.LastReceiveTime > idleTimeout)
                {
                    Remove(session, $"Idle timeout ({idleTimeout:F0}s)");
                }
            }
        }

        /// <summary>
        /// 立即移除并关闭指定会话。
        /// <para>Remove and close the given session immediately.</para>
        /// </summary>
        /// <param name="session">目标会话<para>Target session</para></param>
        /// <param name="reason">关闭原因<para>Close reason</para></param>
        public void Remove(NetSession session, string reason)
        {
            if (session == null) return;

            // 以会话 Id 的移除结果作为唯一出口判据，避免同一会话重复通知
            if (!_byId.Remove(session.SessionId)) return;

            if (_byEndPoint.TryGetValue(session.RemoteEndPoint, out NetSession mapped) && ReferenceEquals(mapped, session))
                _byEndPoint.Remove(session.RemoteEndPoint);

            if (!session.IsClosed) session.Close(reason);

            _logger?.Debug($"[SESSION-] {session} reason={reason}");
            _onClosed?.Invoke(session, reason);
        }

        /// <summary>
        /// 关闭并移除全部会话（停机路径）。
        /// <para>Close and remove every session (shutdown path).</para>
        /// </summary>
        /// <param name="reason">关闭原因<para>Close reason</para></param>
        public void CloseAll(string reason)
        {
            if (_byEndPoint.Count == 0) return;

            NetSession[] snapshot = Snapshot();
            for (int i = 0; i < snapshot.Length; i++)
            {
                Remove(snapshot[i], reason);
            }
        }

        // 建立会话并按握手策略设置初始状态
        private NetSession Create(IPEndPoint remoteEndPoint, uint conv, double now)
        {
            var session = new NetSession(_nextSessionId++, conv, remoteEndPoint, _transport, _settings, _limits,
                HandlePacket, _logger, now)
            {
                State = _options.RequireHandshake ? NetSessionState.Handshaking : NetSessionState.Ready,
            };

            _byEndPoint[remoteEndPoint] = session;
            _byId[session.SessionId] = session;

            _logger?.Debug($"[SESSION+] {session} handshakeRequired={_options.RequireHandshake}");
            _onOpened?.Invoke(session);
            return session;
        }

        // 握手门禁：未就绪的会话只放行握手包
        private void HandlePacket(NetSession session, NetPacket packet)
        {
            if (session.State == NetSessionState.Handshaking && packet.Opcode != NetOpcode.Handshake)
            {
                if (_logger?.IsEnabled(LogLevel.Warn) == true)
                {
                    _logger.Warn($"[SESSION {session.SessionId}] opcode {packet.Opcode} dropped: handshake pending");
                }

                return;
            }

            _dispatcher.Dispatch(session, packet);
        }

        // 把字典的当前值填入复用缓冲（ValueCollection 走 ICollection.CopyTo，不产生枚举器分配）
        private void FillIterationBuffer()
        {
            _iterationBuffer.Clear();
            _iterationBuffer.AddRange(_byEndPoint.Values);
        }

        // 读取数据报前 4 字节的 conv（KCP 段头为小端序）
        private static uint ReadConv(byte[] datagram)
        {
            return (uint)datagram[0]
                   | ((uint)datagram[1] << 8)
                   | ((uint)datagram[2] << 16)
                   | ((uint)datagram[3] << 24);
        }

        #endregion
    }
}
