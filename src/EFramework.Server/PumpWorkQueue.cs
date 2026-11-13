/*
 * ================================================
 * Describe:      Cross-thread work queue drained by the pump at tick start.
 * CreationTime:  2026-11-13 14:15:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Concurrent;

namespace EasyFramework.Server
{
    /// <summary>
    /// 跨线程工作队列：非泵线程把动作排进来，消息泵在每个节拍开头统一执行。
    /// <para>Cross-thread work queue: non-pump callers enqueue actions, and the pump runs them at the start
    /// of each tick.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要它</b>：会话路由与两个会话字典由泵线程独占，跨线程读写会直接撕坏
    /// <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>（可能死循环或抛
    /// <c>InvalidOperationException</c>）。需要从别的线程操作服务端时就投递到这里，而不是加锁——
    /// 加锁会把并发缺陷变成吞吐问题。</para>
    /// <para><b>Why it exists</b>: the session router and its dictionaries are pump-owned; touching them from
    /// another thread tears the dictionary (infinite loop or InvalidOperationException). Hand work to this
    /// queue instead of adding locks — locks turn a concurrency bug into a throughput problem.</para>
    /// <para><b>限流</b>：单节拍最多执行 <see cref="MaxActionsPerTick"/> 个动作，避免投递方持续压入导致
    /// 节拍永远跑不完（活锁）。超出部分留到下一节拍。</para>
    /// <para><b>Throttling</b>: at most MaxActionsPerTick actions per tick, so a producer that keeps posting
    /// cannot starve the loop (livelock). The rest waits for the next tick.</para>
    /// </remarks>
    internal sealed class PumpWorkQueue
    {
        #region Fields

        // 单节拍最多执行的动作数（防止投递方压满导致节拍无法结束）
        private const int MaxActionsPerTick = 1024;

        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private readonly IServerLogger _logger;

        private bool _throttleReported;

        #endregion

        #region Properties

        /// <summary>
        /// 当前积压的动作数（诊断用）
        /// <para>Number of queued actions (diagnostics)</para>
        /// </summary>
        public int Count => _queue.Count;

        #endregion

        #region Methods

        /// <summary>
        /// 构造工作队列。
        /// <para>Create the work queue.</para>
        /// </summary>
        /// <param name="logger">日志输出<para>Log output</para></param>
        public PumpWorkQueue(IServerLogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 入队一个待执行动作（任意线程可调用）。
        /// <para>Enqueue an action to run (safe from any thread).</para>
        /// </summary>
        /// <param name="work">待执行动作<para>Action to run</para></param>
        public void Post(Action work)
        {
            if (work != null) _queue.Enqueue(work);
        }

        /// <summary>
        /// 执行当前已入队的动作（只允许泵线程调用），单个动作的异常不影响其余动作与主循环。
        /// <para>Run the queued actions (pump thread only); a failing action affects neither the others nor
        /// the loop.</para>
        /// </summary>
        public void Drain()
        {
            int executed = 0;
            while (executed < MaxActionsPerTick && _queue.TryDequeue(out Action work))
            {
                executed++;
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    _logger?.Error("[PUMP] posted work threw.", e);
                }
            }

            if (executed < MaxActionsPerTick || _queue.IsEmpty) return;

            // 只在首次触发限流时告警，避免刷屏
            if (_throttleReported) return;

            _throttleReported = true;
            if (_logger?.IsEnabled(LogLevel.Warn) == true)
            {
                _logger.Warn($"[PUMP] posted-work budget {MaxActionsPerTick}/tick exhausted; " +
                             $"backlog={_queue.Count} (a producer is posting faster than one tick can drain)");
            }
        }

        #endregion
    }
}
