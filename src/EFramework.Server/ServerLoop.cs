/*
 * ================================================
 * Describe:      Fixed-tick pump: posted work -> drain transport -> update sessions.
 * CreationTime:  2026-11-13 13:26:00
 * ScriptVersion: 0.2
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
    /// 定频消息泵：每个节拍先执行跨线程投递的动作，再把入站队列抽干，最后推进并回收全部会话。
    /// <para>Fixed-tick pump: run posted cross-thread work, drain the inbound queue, then advance and
    /// reclaim every session.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>单线程模型</b>：所有 KCP 状态与业务处理器都在本线程执行，因此会话与业务状态无需加锁；
    /// 换来的约束是处理器不能阻塞（详见 <see cref="ServerDispatcher"/>）。</para>
    /// <para><b>Single-threaded model</b>: all KCP state and business handlers run on this thread, so no
    /// locking is needed. The cost is that handlers must not block (see <see cref="ServerDispatcher"/>).</para>
    /// <para><b>数据报生命周期</b>：每个数据报路由完立刻 <see cref="IServerTransport.Release"/>（用 finally
    /// 保证），池化缓冲因此能在同一节拍内复用。</para>
    /// <para><b>Datagram lifetime</b>: each datagram is released right after routing (guaranteed by finally),
    /// so pooled buffers are reused within the same tick.</para>
    /// <para><b>异常隔离</b>：单个节拍的异常只记录不外抛；连续失败时限流日志，避免一条坏路径把
    /// 日志刷爆（10ms 一节拍 = 每秒 100 条）。</para>
    /// <para><b>Fault isolation</b>: a failing tick is logged, not rethrown, and repeated failures are
    /// rate-limited so a bad path cannot flood the log at 100 lines per second.</para>
    /// </remarks>
    internal sealed class ServerLoop
    {
        #region Fields

        private readonly ServerOptions _options;
        private readonly IServerTransport _transport;
        private readonly SessionRouter _router;
        private readonly PumpWorkQueue _pumpWork;
        private readonly IServerLogger _logger;

        private int _pumpThreadId;
        private int _consecutiveFailures;

        #endregion

        #region Properties

        /// <summary>
        /// 当前线程是否是消息泵线程（跨线程校验用；泵启动前恒为 false）
        /// <para>Whether the current thread is the pump thread (used for cross-thread checks; always false
        /// before the pump starts)</para>
        /// </summary>
        public bool IsPumpThread
        {
            get
            {
                int pumpThreadId = _pumpThreadId;
                return pumpThreadId != 0 && Environment.CurrentManagedThreadId == pumpThreadId;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// 构造消息泵。
        /// <para>Create the pump.</para>
        /// </summary>
        /// <param name="options">服务端参数（取节拍间隔）<para>Server options (tick interval)</para></param>
        /// <param name="transport">传输层<para>Transport</para></param>
        /// <param name="router">会话路由<para>Session router</para></param>
        /// <param name="pumpWork">跨线程工作队列<para>Cross-thread work queue</para></param>
        /// <param name="logger">日志输出<para>Log output</para></param>
        public ServerLoop(ServerOptions options, IServerTransport transport, SessionRouter router,
            PumpWorkQueue pumpWork, IServerLogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _pumpWork = pumpWork ?? throw new ArgumentNullException(nameof(pumpWork));
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
            // 先认领线程身份：会话在 Tick 中创建时会把当前线程记为唯一可写线程
            _pumpThreadId = Environment.CurrentManagedThreadId;

            int intervalMs = Math.Max(1, _options.TickIntervalMs);

            // 复用取消标记的等待句柄做可中断休眠，避免 Thread.Sleep 拖慢停机
            WaitHandle cancellationWait = cancellationToken.WaitHandle;

            while (!cancellationToken.IsCancellationRequested)
            {
                long startTicks = Stopwatch.GetTimestamp();

                try
                {
                    PumpOnce();
                    _consecutiveFailures = 0;
                }
                catch (Exception e)
                {
                    _consecutiveFailures++;
                    ReportTickFailure(e);
                }

                double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency;
                int sleepMs = intervalMs - (int)elapsedMs;
                if (sleepMs > 0) cancellationWait.WaitOne(sleepMs);
            }
        }

        // 单个节拍：投递动作 → 收包 → 推进会话
        private void PumpOnce()
        {
            _pumpWork.Drain();

            double now = ServerClock.Now;

            while (_transport.TryReceive(out Datagram datagram))
            {
                try
                {
                    _router.Route(datagram, now);
                }
                finally
                {
                    // KCP 在 Input 时已把数据拷进自己的段，这里可以安全归还缓冲
                    _transport.Release(datagram);
                }
            }

            _router.Update(now);
        }

        // 连续失败时限流：前 3 次全打，之后每 300 次打一条；恢复正常后计数归零
        private void ReportTickFailure(Exception exception)
        {
            if (_logger == null) return;

            int failures = _consecutiveFailures;
            if (failures > 3 && failures % 300 != 0) return;

            _logger.Error($"[LOOP] tick failed (consecutive={failures}), continuing with the next tick.", exception);
        }

        #endregion
    }
}
