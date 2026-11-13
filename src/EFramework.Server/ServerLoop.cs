/*
 * ================================================
 * Describe:      Fixed-tick pump: drain transport -> tick sessions -> sweep idle sessions.
 * CreationTime:  2026-11-13 13:26:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Diagnostics;
using System.Threading;
using EasyFramework.Server.Sessions;
using EasyFramework.Server.Transport;

namespace EasyFramework.Server
{
    /// <summary>
    /// 定频消息泵：每个节拍把入站队列抽干、推进全部会话、回收空闲会话。
    /// <para>Fixed-tick pump: each tick drains the inbound queue, advances every session and reclaims
    /// idle ones.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>单线程模型</b>：所有 KCP 状态与业务处理器都在本线程执行，因此会话与业务状态无需加锁；
    /// 换来的约束是处理器不能阻塞（详见 <see cref="ServerDispatcher"/>）。</para>
    /// <para><b>Single-threaded model</b>: all KCP state and business handlers run on this thread, so no
    /// locking is needed. The cost is that handlers must not block (see <see cref="ServerDispatcher"/>).</para>
    /// <para><b>异常隔离</b>：单个节拍的异常只记录不外抛，泵继续跑；会话级异常由会话路由关闭该会话。</para>
    /// <para><b>Fault isolation</b>: a failing tick is logged, not rethrown, so the pump keeps running;
    /// session-level faults close only that session.</para>
    /// </remarks>
    internal sealed class ServerLoop
    {
        #region Fields

        private readonly ServerOptions _options;
        private readonly IServerTransport _transport;
        private readonly SessionRouter _router;
        private readonly IServerLogger _logger;

        #endregion

        #region Methods

        /// <summary>
        /// 构造消息泵。
        /// <para>Create the pump.</para>
        /// </summary>
        /// <param name="options">服务端参数（取节拍间隔）<para>Server options (tick interval)</para></param>
        /// <param name="transport">传输层<para>Transport</para></param>
        /// <param name="router">会话路由<para>Session router</para></param>
        /// <param name="logger">日志输出<para>Log output</para></param>
        public ServerLoop(ServerOptions options, IServerTransport transport, SessionRouter router, IServerLogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _logger = logger;
        }

        /// <summary>
        /// 阻塞式运行消息泵，直到取消标记被触发（在专用长任务线程上调用）。
        /// <para>Run the pump in a blocking fashion until the token is cancelled (invoked on a dedicated
        /// long-running thread).</para>
        /// </summary>
        /// <param name="cancellationToken">停机标记<para>Shutdown token</para></param>
        public void Run(CancellationToken cancellationToken)
        {
            int intervalMs = Math.Max(1, _options.TickIntervalMs);

            // 复用取消标记的等待句柄做可中断休眠，避免 Thread.Sleep 拖慢停机
            WaitHandle cancellationWait = cancellationToken.WaitHandle;

            while (!cancellationToken.IsCancellationRequested)
            {
                long startTicks = Stopwatch.GetTimestamp();

                try
                {
                    PumpOnce();
                }
                catch (Exception e)
                {
                    _logger?.Error("[LOOP] tick failed, continuing with the next tick.", e);
                }

                double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency;
                int sleepMs = intervalMs - (int)elapsedMs;
                if (sleepMs > 0) cancellationWait.WaitOne(sleepMs);
            }
        }

        // 单个节拍：收包 → 推进会话 → 回收
        private void PumpOnce()
        {
            double now = ServerClock.Now;

            while (_transport.TryReceive(out Datagram datagram))
            {
                _router.Route(datagram, now);
            }

            _router.Tick(now);
            _router.Sweep(now);
        }

        #endregion
    }
}
