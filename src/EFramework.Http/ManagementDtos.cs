/*
 * ================================================
 * Describe:      Management-plane DTOs exposed over HTTP (stats / session listing).
 * CreationTime:  2026-11-13 14:41:00
 * ScriptVersion: 0.1
 * ===============================================
 */

namespace EasyFramework.Http
{
    /// <summary>
    /// 服务端统计快照：健康检查与监控端点返回的结构。
    /// <para>Server statistics snapshot returned by the health and monitoring endpoints.</para>
    /// </summary>
    public sealed class ServerStats
    {
        #region Properties

        /// <summary>
        /// 框架版本（与客户端包版本一致）
        /// <para>Framework version (aligned with the client package)</para>
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// 已运行秒数
        /// <para>Uptime in seconds</para>
        /// </summary>
        public long UptimeSeconds { get; set; }

        /// <summary>
        /// 当前会话数
        /// <para>Current session count</para>
        /// </summary>
        public int Sessions { get; set; }

        /// <summary>
        /// 因超过会话上限被拒的端点数
        /// <para>Endpoints rejected by the session ceiling</para>
        /// </summary>
        public long RejectedByLimit { get; set; }

        /// <summary>
        /// 已成功处理的处理器调用次数
        /// <para>Handler invocations completed successfully</para>
        /// </summary>
        public long Handled { get; set; }

        /// <summary>
        /// 抛异常的处理器调用次数
        /// <para>Handler invocations that threw</para>
        /// </summary>
        public long Failed { get; set; }

        /// <summary>
        /// 消息泵周期（毫秒）
        /// <para>Pump tick interval in milliseconds</para>
        /// </summary>
        public int TickIntervalMs { get; set; }

        #endregion
    }

    /// <summary>
    /// 会话摘要：后台列表用，只暴露排障需要的字段。
    /// <para>Session summary for admin listings; only the fields needed for triage.</para>
    /// </summary>
    public sealed class SessionInfo
    {
        #region Properties

        /// <summary>
        /// 会话 Id
        /// <para>Session id</para>
        /// </summary>
        public int SessionId { get; set; }

        /// <summary>
        /// 远端端点（ip:port）
        /// <para>Remote endpoint (ip:port)</para>
        /// </summary>
        public string RemoteEndPoint { get; set; }

        /// <summary>
        /// KCP 会话号
        /// <para>KCP conversation id</para>
        /// </summary>
        public uint Conv { get; set; }

        /// <summary>
        /// 会话状态
        /// <para>Session state</para>
        /// </summary>
        public string State { get; set; }

        /// <summary>
        /// 累计收到的完整消息数
        /// <para>Total complete messages received</para>
        /// </summary>
        public long Received { get; set; }

        /// <summary>
        /// 累计发送的完整消息数
        /// <para>Total complete messages sent</para>
        /// </summary>
        public long Sent { get; set; }

        /// <summary>
        /// KCP 待确认段数（持续增长说明对端不回 ACK）
        /// <para>Unacknowledged KCP segments (sustained growth means the peer is not acking)</para>
        /// </summary>
        public int WaitSnd { get; set; }

        #endregion
    }
}
