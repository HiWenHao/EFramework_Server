/*
 * ================================================
 * Describe:      Server startup options (bind, tick, session policy).
 * CreationTime:  2026-11-13 12:58:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;

namespace EasyFramework.Server
{
    /// <summary>
    /// 服务端启动参数：绑定地址、泵频率、会话上限与握手策略。
    /// <para>Server startup options: bind address, pump frequency, session limits and handshake policy.</para>
    /// </summary>
    public sealed class ServerOptions
    {
        #region Fields

        /// <summary>
        /// 默认监听端口（客户端 <c>NetworkManager.Connect(host, 7777)</c> 即可直连）
        /// <para>Default listen port (a client can connect with <c>NetworkManager.Connect(host, 7777)</c>)</para>
        /// </summary>
        public const int DefaultPort = 7777;

        #endregion

        #region Properties

        /// <summary>
        /// 绑定地址：<c>0.0.0.0</c>/<c>*</c> 表示全部网卡，<c>::</c> 表示 IPv6 全部网卡，也可填具体 IP 或主机名
        /// <para>Bind address: <c>0.0.0.0</c>/<c>*</c> for all IPv4 NICs, <c>::</c> for all IPv6 NICs,
        /// or a concrete IP / host name</para>
        /// </summary>
        public string BindAddress { get; set; } = "0.0.0.0";

        /// <summary>
        /// 监听端口（0 表示由系统分配，便于测试并行跑）
        /// <para>Listen port (0 lets the OS assign one, useful for parallel tests)</para>
        /// </summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>
        /// 消息泵周期(ms)：同时是「收包处理」与「回包发出」的延迟上限
        /// <para>Pump period in ms; also the upper bound of inbound processing and outbound flush latency</para>
        /// </summary>
        public int TickIntervalMs { get; set; } = 10;

        /// <summary>
        /// 会话空闲超时(秒)：超过该时长没收到任何数据报即回收，&lt;=0 表示不回收
        /// <para>Session idle timeout in seconds: reclaimed when no datagram arrives within it;
        /// &lt;=0 disables reclamation</para>
        /// </summary>
        public int SessionIdleTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// 会话数上限：超过后新端点的数据报被丢弃（防扫描/打满内存）
        /// <para>Session ceiling: datagrams from new endpoints are dropped beyond it (anti-scan, memory guard)</para>
        /// </summary>
        public int MaxSessions { get; set; } = 4096;

        /// <summary>
        /// 是否强制握手：为 true 时未通过握手的会话除握手包外一律丢弃（客户端须带 Token 连接）
        /// <para>Whether a handshake is mandatory: when true, a session's non-handshake packets are dropped
        /// until it passes (the client must connect with a token)</para>
        /// </summary>
        public bool RequireHandshake { get; set; }

        /// <summary>
        /// 收包队列容量（内核 → 消息泵的缓冲；溢出时丢弃新包并计数）
        /// <para>Inbound queue capacity (kernel to pump buffer; new datagrams are dropped and counted on overflow)</para>
        /// </summary>
        public int ReceiveQueueCapacity { get; set; } = 8192;

        /// <summary>
        /// 单会话出站积压上限（KCP 待发段数，0 表示不限制）
        /// <para>Per-session outbound backlog cap in KCP segments (0 disables the cap)</para>
        /// </summary>
        /// <remarks>
        /// 慢客户端或已经掉线的客户端不会让 <c>Kcp.Send</c> 失败，而是让内部的待发队列一直变长 —— 广播一次
        /// 就往每个"死会话"里堆一份，直到重传次数耗尽才判定链路死亡。这个上限把"无限堆积"变成
        /// "及时判定不可用并回收"，避免单个客户端拖垮整台服务器。
        /// 健康会话的在途段数受窗口限制（默认 256），故 1024 留了充足余量。
        /// <para>A slow or already-dead client does not make <c>Kcp.Send</c> fail; its internal send queue just
        /// grows, and every broadcast adds one more copy per dead session until retransmissions exhaust. This
        /// cap turns unbounded growth into a prompt "unreachable, reclaim it". Healthy sessions are bounded by
        /// the window (256 by default), so 1024 leaves ample headroom.</para>
        /// </remarks>
        public int MaxSendBacklogSegments { get; set; } = 1024;

        /// <summary>
        /// 单会话入站消息速率上限（条/秒，0 表示不限制）
        /// <para>Per-session inbound message rate cap in messages per second (0 disables the cap)</para>
        /// </summary>
        /// <remarks>
        /// 一个刷包客户端可以在一个节拍里塞满队列，让所有玩家的延迟一起变差。超限的消息直接丢弃并计数
        /// （而不是排队），因为"排队"等于把攻击转嫁给其他玩家。
        /// 正常客户端每秒几十条；512 给合法突发（批量操作、重连补发）留足空间。
        /// <para>A flooding client can fill the queue within one tick and degrade every other player's latency.
        /// Over-limit messages are dropped and counted rather than queued, because queueing would forward the
        /// attack to everyone else. Normal clients send tens per second; 512 leaves room for legitimate bursts.</para>
        /// </remarks>
        public int MaxMessagesPerSecond { get; set; } = 512;

        /// <summary>
        /// 日志阈值
        /// <para>Logging threshold</para>
        /// </summary>
        public LogLevel LogLevel { get; set; } = LogLevel.Info;

        #endregion

        #region Methods

        /// <summary>
        /// 校验参数合法性，非法时抛出 <see cref="ArgumentOutOfRangeException"/>。
        /// <para>Validates the options and throws <see cref="ArgumentOutOfRangeException"/> when invalid.</para>
        /// </summary>
        public void Validate()
        {
            if (Port is < 0 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be within 0..65535.");
            if (TickIntervalMs < 1)
                throw new ArgumentOutOfRangeException(nameof(TickIntervalMs), TickIntervalMs, "Tick interval must be >= 1 ms.");
            if (MaxSessions < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxSessions), MaxSessions, "Max sessions must be >= 1.");
            if (ReceiveQueueCapacity < 64)
                throw new ArgumentOutOfRangeException(nameof(ReceiveQueueCapacity), ReceiveQueueCapacity, "Receive queue capacity must be >= 64.");
            if (MaxSendBacklogSegments < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxSendBacklogSegments), MaxSendBacklogSegments, "Backlog cap must be >= 0 (0 disables it).");
            if (MaxMessagesPerSecond < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxMessagesPerSecond), MaxMessagesPerSecond, "Message rate cap must be >= 0 (0 disables it).");
            if (string.IsNullOrWhiteSpace(BindAddress))
                throw new ArgumentException("Bind address must not be empty.", nameof(BindAddress));
        }

        #endregion
    }
}
