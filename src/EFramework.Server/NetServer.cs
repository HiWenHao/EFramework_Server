/*
 * ================================================
 * Describe:      Server host: wires transport, session router, dispatcher and pump; owns lifecycle.
 * CreationTime:  2026-11-13 13:28:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EasyFramework.Server.Handshake;
using EasyFramework.Server.Sessions;
using EasyFramework.Server.Transport;
using EasyFramework.Systems.Net;

namespace EasyFramework.Server
{
    /// <summary>
    /// 服务端宿主：组合根 + 生命周期。装配「传输 → 会话路由 → 分发器 → 消息泵」，并注册框架内置处理器。
    /// <para>Server host: composition root plus lifecycle. Wires transport, session router, dispatcher and
    /// pump, and registers the built-in handlers.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>最小用法</b>：</para>
    /// <code>
    /// var dispatcher = new ServerDispatcher(logger, new JsonMessageSerializer());
    /// dispatcher.Register&lt;EchoReq, EchoHandler&gt;();
    /// using var server = new NetServer(new ServerOptions(), dispatcher, logger: logger);
    /// server.Start();
    /// </code>
    /// <para><b>内置处理器</b>：<c>NetOpcode.Ping</c>（回显时间戳的 Pong）与 <c>NetOpcode.Handshake</c>
    /// （交给 <see cref="IHandshakeValidator"/> 裁决）。业务可用同 Opcode 覆盖。</para>
    /// <para><b>Built-in handlers</b>: <c>NetOpcode.Ping</c> (echoes the timestamp as Pong) and
    /// <c>NetOpcode.Handshake</c> (delegated to <see cref="IHandshakeValidator"/>). Business code may
    /// override them by registering the same opcode.</para>
    /// <para><b>单次生命周期</b>：实例不可重启（停机后传输通道已关闭），需要重启请新建实例。</para>
    /// <para><b>Single lifecycle</b>: an instance cannot be restarted (the transport channel is closed on
    /// shutdown); create a new instance to restart.</para>
    /// </remarks>
    public sealed class NetServer : IDisposable
    {
        #region Fields

        private readonly ServerOptions _options;
        private readonly ServerDispatcher _dispatcher;
        private readonly IServerTransport _transport;
        private readonly IServerLogger _logger;
        private readonly IHandshakeValidator _handshakeValidator;
        private readonly SessionRouter _router;
        private readonly ServerLoop _loop;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        private Task _loopTask;
        private bool _started;
        private bool _disposed;

        #endregion

        #region Properties

        /// <summary>
        /// 是否正在运行
        /// <para>Whether the server is running</para>
        /// </summary>
        public bool IsRunning => _started && !_disposed;

        /// <summary>
        /// 实际监听端点（端口配 0 时这里给出系统分配的真实端口）
        /// <para>Effective listen endpoint (reports the OS-assigned port when configured with 0)</para>
        /// </summary>
        public IPEndPoint LocalEndPoint => _transport.LocalEndPoint;

        /// <summary>
        /// 当前会话数
        /// <para>Current session count</para>
        /// </summary>
        public int SessionCount => _router.Count;

        /// <summary>
        /// 因超过会话上限被拒绝的端点数
        /// <para>Endpoints rejected because the session ceiling was hit</para>
        /// </summary>
        public long RejectedByLimit => _router.RejectedByLimit;

        /// <summary>
        /// 启动参数
        /// <para>Startup options</para>
        /// </summary>
        public ServerOptions Options => _options;

        /// <summary>
        /// 消息分发器（可继续注册处理器）
        /// <para>Message dispatcher (handlers can still be registered)</para>
        /// </summary>
        public ServerDispatcher Dispatcher => _dispatcher;

        #endregion

        #region Methods

        /// <summary>
        /// 构造服务端宿主（尚未监听，调用 <see cref="Start"/> 生效）。
        /// <para>Create the server host (not listening yet; call <see cref="Start"/>).</para>
        /// </summary>
        /// <param name="options">启动参数<para>Startup options</para></param>
        /// <param name="dispatcher">消息分发器（业务处理器在调用前注册好）<para>Message dispatcher (register business handlers before Start)</para></param>
        /// <param name="transport">自定义传输层；null 使用 <see cref="UdpServerTransport"/><para>Custom transport; null uses <see cref="UdpServerTransport"/></para></param>
        /// <param name="logger">日志输出；null 使用控制台日志<para>Log output; null uses the console logger</para></param>
        /// <param name="handshakeValidator">握手校验器；null 使用放行实现<para>Handshake validator; null uses the permissive one</para></param>
        /// <param name="kcpSettings">KCP 调参数；null 使用与客户端对齐的默认值<para>KCP tuning; null uses the client-aligned defaults</para></param>
        public NetServer(ServerOptions options, ServerDispatcher dispatcher, IServerTransport transport = null,
            IServerLogger logger = null, IHandshakeValidator handshakeValidator = null, KcpSettings kcpSettings = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();

            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = logger ?? new ConsoleServerLogger { Level = options.LogLevel };
            _transport = transport ?? new UdpServerTransport(_options, _logger);
            _handshakeValidator = handshakeValidator ?? new AcceptAllHandshakeValidator();

            _router = new SessionRouter(_options, kcpSettings ?? KcpSettings.Default, _transport, _dispatcher, _logger,
                RaiseSessionOpened, RaiseSessionClosed);
            _loop = new ServerLoop(_options, _transport, _router, _logger);

            // 协议层处理器与业务处理器分开装配，业务可用同 Opcode 覆盖
            new BuiltInMessageHandlers(_handshakeValidator, _logger).Register(_dispatcher);
        }

        /// <summary>
        /// 启动监听并运行消息泵；绑定失败抛出 <see cref="InvalidOperationException"/>。
        /// <para>Start listening and run the pump; throws <see cref="InvalidOperationException"/> when the
        /// bind fails.</para>
        /// </summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NetServer));
            if (_started) throw new InvalidOperationException("Server is already started.");

            if (!_transport.Start())
            {
                throw new InvalidOperationException(
                    $"Transport failed to start on {_options.BindAddress}:{_options.Port}. See logs for details.");
            }

            if (_dispatcher.Serializer == null)
            {
                _logger?.Warn("[SERVER] no IMessageSerializer configured: typed handlers and typed replies are unavailable.");
            }

            _started = true;
            _loopTask = Task.Factory.StartNew(
                () => RunLoop(_cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            _logger?.Info($"[SERVER] listening on {_transport.LocalEndPoint} " +
                          $"(tick={_options.TickIntervalMs}ms, handshake={(_options.RequireHandshake ? "required" : "optional")}, " +
                          $"maxSessions={_options.MaxSessions})");
        }

        /// <summary>
        /// 优雅停机：停止收包 → 关闭全部会话 → 等待泵退出。
        /// <para>Graceful shutdown: stop receiving, close all sessions, then wait for the pump to exit.</para>
        /// </summary>
        /// <param name="reason">停机原因（会写进会话关闭日志）<para>Shutdown reason (recorded in session-close logs)</para></param>
        /// <returns>停机完成的任务<para>A task that completes when shutdown finishes</para></returns>
        public async Task StopAsync(string reason = "Server stopped")
        {
            if (!_started) return;
            _started = false;

            _cancellation.Cancel();

            Task loopTask = _loopTask;
            _loopTask = null;
            if (loopTask != null)
            {
                try
                {
                    await loopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 停机路径的正常退出
                }
            }

            _router.CloseAll(reason);
            _transport.Stop();

            _logger?.Info($"[SERVER] stopped ({reason})");
        }

        /// <summary>
        /// 向指定会话发送单向消息。
        /// <para>Send a one-way message to a session.</para>
        /// </summary>
        /// <param name="sessionId">会话 Id<para>Session id</para></param>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        /// <returns>会话是否存在（不保证已送达）<para>Whether the session exists (delivery is not guaranteed)</para></returns>
        public bool Send(int sessionId, int opcode, byte[] payload = null)
        {
            if (!_router.TryGet(sessionId, out NetSession session)) return false;
            session.Send(opcode, payload);
            return true;
        }

        /// <summary>
        /// 向指定会话发送带配对号的消息（客户端按 rpcId 配对到挂起调用时会用到）。
        /// <para>Send a message with a pairing id to a session (the client pairs it to a pending call by rpcId).</para>
        /// </summary>
        /// <param name="sessionId">会话 Id<para>Session id</para></param>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <param name="rpcId">配对号<para>Pairing id</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        /// <returns>会话是否存在<para>Whether the session exists</para></returns>
        public bool Send(int sessionId, int opcode, int rpcId, byte[] payload)
        {
            if (!_router.TryGet(sessionId, out NetSession session)) return false;
            session.Send(opcode, rpcId, payload);
            return true;
        }

        /// <summary>
        /// 向全部会话广播单向消息。
        /// <para>Broadcast a one-way message to every session.</para>
        /// </summary>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        /// <returns>实际投递的会话数<para>Number of sessions the message was queued for</para></returns>
        public int Broadcast(int opcode, byte[] payload = null)
        {
            NetSession[] sessions = _router.Snapshot();
            for (int i = 0; i < sessions.Length; i++)
            {
                sessions[i].Send(opcode, payload);
            }

            return sessions.Length;
        }

        /// <summary>
        /// 关闭指定会话（终态，由对端重连重建）。
        /// <para>Close a session (terminal; the peer rebuilds it on reconnect).</para>
        /// </summary>
        /// <param name="sessionId">会话 Id<para>Session id</para></param>
        /// <param name="reason">关闭原因<para>Close reason</para></param>
        /// <returns>是否找到并关闭<para>Whether the session was found and closed</para></returns>
        public bool CloseSession(int sessionId, string reason = "Closed by server")
        {
            if (!_router.TryGet(sessionId, out NetSession session)) return false;
            _router.Remove(session, reason);
            return true;
        }

        /// <summary>
        /// 停止服务端并释放传输资源（同步等待停机完成）。
        /// <para>Stop the server and release transport resources (blocks until shutdown completes).</para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                StopAsync("Server disposed").GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _logger?.Error("[SERVER] dispose failed.", e);
            }

            _cancellation.Dispose();
            _transport.Dispose();
        }

        // 专用长任务线程：阻塞式泵不能占用线程池
        private void RunLoop(CancellationToken cancellationToken)
        {
            try
            {
                _loop.Run(cancellationToken);
            }
            catch (Exception e)
            {
                _logger?.Error("[SERVER] pump terminated unexpectedly.", e);
            }
        }

        // 订阅者异常不能影响收发主循环
        private void RaiseSessionOpened(NetSession session)
        {
            Action<NetSession> handler = SessionOpened;
            if (handler == null) return;

            try
            {
                handler(session);
            }
            catch (Exception e)
            {
                _logger?.Error("[SERVER] SessionOpened subscriber threw.", e);
            }
        }

        // 订阅者异常不能影响收发主循环
        private void RaiseSessionClosed(NetSession session, string reason)
        {
            Action<NetSession, string> handler = SessionClosed;
            if (handler == null) return;

            try
            {
                handler(session, reason);
            }
            catch (Exception e)
            {
                _logger?.Error("[SERVER] SessionClosed subscriber threw.", e);
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// 会话建立时触发（在消息泵线程上调用，回调内不要阻塞）。
        /// <para>Raised when a session is created (called on the pump thread; do not block inside).</para>
        /// </summary>
        public event Action<NetSession> SessionOpened;

        /// <summary>
        /// 会话关闭时触发（参数为会话与原因，在消息泵线程上调用）。
        /// <para>Raised when a session closes (session plus reason, on the pump thread).</para>
        /// </summary>
        public event Action<NetSession, string> SessionClosed;

        #endregion
    }
}
