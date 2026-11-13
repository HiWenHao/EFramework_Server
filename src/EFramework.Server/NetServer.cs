/*
 * ================================================
 * Describe:      Server host: wires transport, session router, dispatcher and pump; owns lifecycle.
 * CreationTime:  2026-11-13 13:28:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
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
        private readonly PumpWorkQueue _pumpWork;
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

        /// <summary>
        /// 框架版本，与 Unity 包 cn.efefef.net 的 package.json version 一致（由 Directory.Build.props 注入）
        /// <para>Framework version, aligned with the Unity package cn.efefef.net package.json version
        /// (injected by Directory.Build.props)</para>
        /// </summary>
        public static string Version { get; } = ResolveVersion();

        /// <summary>
        /// 当前会话快照（<b>仅泵线程可安全调用</b>；其它线程请用 <see cref="PostAsync{T}"/> 取一致快照）
        /// <para>Snapshot of the current sessions (<b>pump thread only</b>; other threads should use
        /// <see cref="PostAsync{T}"/> for a consistent view)</para>
        /// </summary>
        /// <remarks>
        /// 每次访问都会新建一个数组，属管理面接口（HTTP 健康检查/后台列表）用，不要放在每帧路径上。
        /// <para>Each access allocates an array; it is a management-plane API (health checks, admin listings)
        /// and must not sit on a per-frame path.</para>
        /// </remarks>
        public IReadOnlyList<NetSession> Sessions => _router.Snapshot();

        /// <summary>
        /// 实测节拍间隔（毫秒，指数滑动平均）。与 <see cref="ServerOptions.TickIntervalMs"/> 的差距说明
        /// 系统调度/计时器精度在拖后腿（Windows 默认约 15.6ms，配置 10ms 实际会睡 15ms 左右）。
        /// <para>Actual tick interval in ms (EMA). A gap versus the configured value means scheduler/timer
        /// resolution is the bottleneck (Windows defaults to ~15.6ms, so a 10ms tick really sleeps ~15ms).</para>
        /// </summary>
        public double ActualTickIntervalMs => _loop.ActualTickIntervalMs;

        /// <summary>
        /// 每拍的实际工作耗时（毫秒，滑动平均）。接近节拍间隔即表示泵是计算瓶颈。
        /// <para>Actual per-tick work time in ms (EMA). Approaching the tick interval means the pump is
        /// compute-bound.</para>
        /// </summary>
        public double ActualTickWorkMs => _loop.ActualTickWorkMs;

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

            // 参数在构造期校验并持有副本：既让配置错误立刻暴露，也避免外部后续改动影响已建会话
            KcpSettings tuning = kcpSettings ?? KcpSettings.Default;
            tuning.Validate();
            tuning = tuning.Clone();

            _router = new SessionRouter(_options, tuning, _transport, _dispatcher, _logger,
                RaiseSessionOpened, RaiseSessionClosed);
            _pumpWork = new PumpWorkQueue(_logger);
            _loop = new ServerLoop(_options, _transport, _router, _pumpWork, _logger);

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

            _logger?.Info($"[SERVER v{Version}] listening on {_transport.LocalEndPoint} " +
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
        /// 把一段工作投递到消息泵线程执行（任意线程可调用）。
        /// <para>Post work to be executed on the message-pump thread (safe from any thread).</para>
        /// </summary>
        /// <remarks>
        /// 会话路由与会话字典是泵线程独占的，<see cref="Send(int,int,byte[])"/>、<see cref="Broadcast"/>、
        /// <see cref="CloseSession"/> 都只能在泵线程调用。定时器、线程池或其它线程要操作服务端时，
        /// 一律经此投递，具体执行发生在下一个节拍开头。
        /// <para>The router and its dictionaries are pump-owned, so Send/Broadcast/CloseSession must run on the
        /// pump thread. Timers, thread-pool callbacks or other threads must go through this method; work runs
        /// at the start of the next tick.</para>
        /// </remarks>
        /// <param name="work">待执行动作，参数为当前服务端实例<para>Work to run; receives the server instance</para></param>
        public void Post(Action<NetServer> work)
        {
            if (work == null) return;

            NetServer self = this;
            _pumpWork.Post(() => work(self));
        }

        /// <summary>
        /// 把一段「取值」工作投递到消息泵线程执行，并等待它的返回值（任意线程可调用）。
        /// <para>Post a query onto the pump thread and await its result (safe from any thread).</para>
        /// </summary>
        /// <remarks>
        /// 用于管理面接口（HTTP 健康检查、后台会话列表）：这些数据只有泵线程能安全读取，
        /// 又必须同步返回给调用方。<see cref="Post"/> 是单向投递，取不到返回值，故补这个方法。
        /// 代价是一次任务分配 + 最多一个节拍的等待，不适合放在高频路径上。
        /// <para>For management-plane reads (health checks, admin session listings) whose state is
        /// pump-thread-owned but must be returned synchronously. Costs one task allocation and up to one
        /// tick of latency; not for hot paths.</para>
        /// </remarks>
        /// <typeparam name="T">返回值类型<para>Result type</para></typeparam>
        /// <param name="work">在泵线程上执行的取值逻辑<para>Query executed on the pump thread</para></param>
        /// <param name="timeoutMilliseconds">超时毫秒数（泵已停机时避免调用方永久挂起）<para>Timeout in ms (keeps callers from hanging after shutdown)</para></param>
        /// <returns>取值结果<para>The query result</para></returns>
        public Task<T> PostAsync<T>(Func<NetServer, T> work, int timeoutMilliseconds = 2000)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (!IsRunning)
            {
                throw new InvalidOperationException(
                    "PostAsync requires a running server; the pump must be alive to execute posted work.");
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            NetServer self = this;

            _pumpWork.Post(() =>
            {
                try
                {
                    completion.TrySetResult(work(self));
                }
                catch (Exception e)
                {
                    completion.TrySetException(e);
                }
            });

            return completion.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMilliseconds)));
        }

        /// <summary>
        /// 向指定会话发送单向消息（仅限泵线程；跨线程请用 <see cref="Post"/>）。
        /// <para>Send a one-way message to a session (pump thread only; use Post from other threads).</para>
        /// </summary>
        /// <param name="sessionId">会话 Id<para>Session id</para></param>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        /// <returns>会话是否存在（不保证已送达）<para>Whether the session exists (delivery is not guaranteed)</para></returns>
        public bool Send(int sessionId, int opcode, byte[] payload = null)
        {
            EnsurePumpThread(nameof(Send));

            if (!_router.TryGet(sessionId, out NetSession session)) return false;
            session.Send(opcode, payload);
            return true;
        }

        /// <summary>
        /// 向指定会话发送带配对号的消息（客户端按 rpcId 配对到挂起调用时会用到；仅限泵线程）。
        /// <para>Send a message with a pairing id to a session (only the client pairs it by rpcId; pump thread
        /// only).</para>
        /// </summary>
        /// <param name="sessionId">会话 Id<para>Session id</para></param>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <param name="rpcId">配对号<para>Pairing id</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        /// <returns>会话是否存在<para>Whether the session exists</para></returns>
        public bool Send(int sessionId, int opcode, int rpcId, byte[] payload)
        {
            EnsurePumpThread(nameof(Send));

            if (!_router.TryGet(sessionId, out NetSession session)) return false;
            session.Send(opcode, rpcId, payload);
            return true;
        }

        /// <summary>
        /// 向全部会话广播单向消息（仅限泵线程；跨线程请用 <see cref="Post"/>）。
        /// <para>Broadcast a one-way message to every session (pump thread only; use Post from other threads).</para>
        /// </summary>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        /// <returns>实际投递的会话数<para>Number of sessions the message was queued for</para></returns>
        public int Broadcast(int opcode, byte[] payload = null)
        {
            EnsurePumpThread(nameof(Broadcast));

            NetSession[] sessions = _router.Snapshot();
            for (int i = 0; i < sessions.Length; i++)
            {
                sessions[i].Send(opcode, payload);
            }

            return sessions.Length;
        }

        /// <summary>
        /// 关闭指定会话（终态，由对端重连重建；仅限泵线程）。
        /// <para>Close a session (terminal; the peer rebuilds it on reconnect; pump thread only).</para>
        /// </summary>
        /// <param name="sessionId">会话 Id<para>Session id</para></param>
        /// <param name="reason">关闭原因<para>Close reason</para></param>
        /// <returns>是否找到并关闭<para>Whether the session was found and closed</para></returns>
        public bool CloseSession(int sessionId, string reason = "Closed by server")
        {
            EnsurePumpThread(nameof(CloseSession));

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

            // 在泵线程里 Dispose 等于自己等自己：直接死锁。此时只发停机信号，把释放交给进程退出。
            if (_loop.IsPumpThread)
            {
                _logger?.Error("[SERVER] Dispose() called on the pump thread; signalling shutdown only. " +
                               "Dispose from another thread (e.g. the host's shutdown path).");
                _cancellation.Cancel();
                return;
            }

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

        // 取信息版本号；若将来接入 SourceLink 会带 +<hash> 后缀，这里只取主版本段
        private static string ResolveVersion()
        {
            string informational = typeof(NetServer).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrEmpty(informational)) return "0.0.0";

            int suffix = informational.IndexOf('+');
            return suffix >= 0 ? informational.Substring(0, suffix) : informational;
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

        // 会话路由与会话字典是泵线程独占的：跨线程直接调用会撕坏字典，宁可显式失败也不静默出错
        private void EnsurePumpThread(string operation)
        {
            if (_loop.IsPumpThread) return;

            throw new InvalidOperationException(
                $"{operation} must be called on the message-pump thread. " +
                "To act from another thread use NetServer.Post(...); NetSession.Send is safe from any thread.");
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
