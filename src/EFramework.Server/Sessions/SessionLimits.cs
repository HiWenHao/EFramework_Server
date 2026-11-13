/*
 * ================================================
 * Describe:      Per-session protection limits (outbound backlog cap, inbound rate limit).
 * CreationTime:  2026-11-13 15:02:00
 * ScriptVersion: 0.1
 * ===============================================
 */

namespace EasyFramework.Server.Sessions
{
    /// <summary>
    /// 单会话的保护上限：出站积压上限与入站消息速率上限，都由宿主参数派生一次、随会话下发。
    /// <para>Per-session protection limits: outbound backlog cap and inbound message rate, derived once from
    /// the host options and handed to each session.</para>
    /// </summary>
    /// <remarks>
    /// 做成独立的只读结构体而不是把 <see cref="ServerOptions"/> 传进会话，是为了让会话只依赖"与它有关的
    /// 那几个数"，而不是整个宿主配置——会话不该知道 tick 间隔、端口、日志级别这些与自己无关的事。
    /// <para>A separate readonly struct rather than passing ServerOptions keeps a session dependent only on
    /// the numbers that concern it, not on the whole host configuration (tick interval, port, log level).</para>
    /// </remarks>
    internal readonly struct SessionLimits
    {
        #region Properties

        /// <summary>
        /// 出站积压上限（KCP 待发段数，&lt;=0 表示不限制）
        /// <para>Outbound backlog cap in KCP segments (&lt;=0 disables the cap)</para>
        /// </summary>
        public int MaxSendBacklogSegments { get; }

        /// <summary>
        /// 入站消息速率上限（条/秒，&lt;=0 表示不限制）
        /// <para>Inbound message rate cap per second (&lt;=0 disables the cap)</para>
        /// </summary>
        public int MaxMessagesPerSecond { get; }

        #endregion

        #region Methods

        /// <summary>
        /// 构造会话上限。
        /// <para>Create the session limits.</para>
        /// </summary>
        /// <param name="maxSendBacklogSegments">出站积压上限（段）<para>Outbound backlog cap in segments</para></param>
        /// <param name="maxMessagesPerSecond">入站速率上限（条/秒）<para>Inbound rate cap per second</para></param>
        public SessionLimits(int maxSendBacklogSegments, int maxMessagesPerSecond)
        {
            MaxSendBacklogSegments = maxSendBacklogSegments;
            MaxMessagesPerSecond = maxMessagesPerSecond;
        }

        #endregion
    }
}
