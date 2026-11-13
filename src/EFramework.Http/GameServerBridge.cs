/*
 * ================================================
 * Describe:      HTTP -> game-server bridge (the ONLY place the two hosts touch).
 * CreationTime:  2026-11-13 14:42:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EasyFramework.Server;
using EasyFramework.Server.Sessions;

namespace EasyFramework.Http
{
    /// <summary>
    /// HTTP 与游戏服之间唯一的桥：把「读取统计 / 广播 / 踢人」翻译成消息泵线程上的操作。
    /// <para>The only bridge between HTTP and the game server: translates "read stats / broadcast / kick"
    /// into operations on the message-pump thread.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要这一层</b>：会话状态与 KCP 都归消息泵线程独占，HTTP 线程直接读会撕坏字典。
    /// 读走 <see cref="NetServer.PostAsync{T}"/>（带返回值的投递），写走 <see cref="NetServer.Post"/>
    /// （单向投递），HTTP 线程因此永远不直接触碰会话对象。</para>
    /// <para><b>Why this layer exists</b>: session state and KCP belong to the pump thread; reading them from
    /// an HTTP thread tears the dictionaries. Reads go through <see cref="NetServer.PostAsync{T}"/> (posted
    /// query) and writes through <see cref="NetServer.Post"/> (fire-and-forget), so an HTTP thread never
    /// touches a session object directly.</para>
    /// <para><b>不要在这里写业务</b>：业务留在 UDP 侧的 handler 里。HTTP 只负责「看」和「发号施令」。</para>
    /// <para><b>No business logic here</b>: business stays in the UDP-side handlers. HTTP only observes and
    /// issues commands.</para>
    /// </remarks>
    public sealed class GameServerBridge
    {
        #region Fields

        private readonly NetServer _server;
        private readonly IServerLogger _logger;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

        #endregion

        #region Properties

        /// <summary>
        /// 被桥接的游戏服是否在运行
        /// <para>Whether the bridged game server is running</para>
        /// </summary>
        public bool IsRunning => _server.IsRunning;

        #endregion

        #region Methods

        /// <summary>
        /// 构造桥接层。
        /// <para>Create the bridge.</para>
        /// </summary>
        /// <param name="server">被桥接的游戏服<para>The game server being bridged</para></param>
        /// <param name="logger">日志输出<para>Log output</para></param>
        public GameServerBridge(NetServer server, IServerLogger logger = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _logger = logger;
        }

        /// <summary>
        /// 取统计快照（在泵线程上读取，返回一致的值）。
        /// <para>Read a consistent statistics snapshot on the pump thread.</para>
        /// </summary>
        /// <returns>统计快照<para>Statistics snapshot</para></returns>
        public Task<ServerStats> GetStatsAsync()
        {
            return _server.PostAsync(server => new ServerStats
            {
                Version = NetServer.Version,
                UptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                Sessions = server.Sessions.Count,
                RejectedByLimit = server.RejectedByLimit,
                Handled = server.Dispatcher.HandledCount,
                Failed = server.Dispatcher.FailedCount,
                TickIntervalMs = server.Options.TickIntervalMs,
                ActualTickIntervalMs = server.ActualTickIntervalMs,
                ActualTickWorkMs = server.ActualTickWorkMs,
            });
        }

        /// <summary>
        /// 取会话列表（在泵线程上读取）。
        /// <para>Read the session list on the pump thread.</para>
        /// </summary>
        /// <returns>会话摘要数组<para>Session summaries</para></returns>
        public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync()
        {
            return _server.PostAsync<IReadOnlyList<SessionInfo>>(server =>
            {
                IReadOnlyList<NetSession> sessions = server.Sessions;
                var result = new SessionInfo[sessions.Count];

                for (int i = 0; i < sessions.Count; i++)
                {
                    NetSession session = sessions[i];
                    result[i] = new SessionInfo
                    {
                        SessionId = session.SessionId,
                        RemoteEndPoint = session.RemoteEndPointText,
                        Conv = session.Conv,
                        State = session.State.ToString(),
                        Received = session.ReceivedMessages,
                        Sent = session.SentMessages,
                        WaitSnd = session.WaitSnd,
                        Dropped = session.DroppedMessages,
                    };
                }

                return result;
            });
        }

        /// <summary>
        /// 向全部会话广播一条单向消息（投递到泵线程执行，不阻塞 HTTP 请求）。
        /// <para>Broadcast a one-way message to every session (posted to the pump; does not block the HTTP
        /// request).</para>
        /// </summary>
        /// <param name="opcode">消息操作码（须大于 0）<para>Message opcode (must be greater than 0)</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        /// <returns>是否已投递（不代表已送达）<para>Whether it was posted (not proof of delivery)</para></returns>
        public bool Broadcast(int opcode, byte[] payload)
        {
            if (opcode <= 0) return false;

            _server.Post(server => server.Broadcast(opcode, payload));

            if (_logger?.IsEnabled(LogLevel.Info) == true)
            {
                _logger.Info($"[HTTP] broadcast queued: opcode={opcode}, {payload?.Length ?? 0}B");
            }

            return true;
        }

        /// <summary>
        /// 关闭指定会话（投递到泵线程执行）。
        /// <para>Close a session (posted to the pump).</para>
        /// </summary>
        /// <param name="sessionId">会话 Id<para>Session id</para></param>
        /// <param name="reason">关闭原因<para>Close reason</para></param>
        /// <returns>是否已投递（不代表会话一定存在）<para>Whether it was posted (the session may not exist)</para></returns>
        public bool CloseSession(int sessionId, string reason)
        {
            if (sessionId <= 0) return false;

            string closeReason = string.IsNullOrEmpty(reason) ? "Closed by admin" : reason;
            _server.Post(server => server.CloseSession(sessionId, closeReason));

            if (_logger?.IsEnabled(LogLevel.Info) == true)
            {
                _logger.Info($"[HTTP] close queued: session={sessionId}, reason={closeReason}");
            }

            return true;
        }

        #endregion
    }
}
