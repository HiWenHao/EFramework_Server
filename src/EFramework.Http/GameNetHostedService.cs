/*
 * ================================================
 * Describe:      Bridges the UDP game server into the .NET generic host lifetime.
 * CreationTime:  2026-11-13 14:43:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using EasyFramework.Server;
using Microsoft.Extensions.Hosting;

namespace EasyFramework.Http
{
    /// <summary>
    /// 把 UDP 游戏服挂到宿主生命周期上：宿主启动 → 游戏服开泵；宿主停机 → 游戏服优雅关闭。
    /// <para>Attaches the UDP game server to the host lifetime: host starts → pump starts; host stops →
    /// the server shuts down gracefully.</para>
    /// </summary>
    /// <remarks>
    /// <para>一个进程只应有一个「生命周期权威」。HTTP 侧用 .NET 通用主机（Ctrl+C / SIGTERM / 容器 stop 都走它），
    /// 游戏服作为 <see cref="IHostedService"/> 跟随，避免两套停机逻辑互相抢（例如 Ctrl+C 时 HTTP 先退、
    /// 游戏会话来不及发关闭包）。</para>
    /// <para>One process should have a single lifetime authority. HTTP uses the .NET generic host (Ctrl+C /
    /// SIGTERM / container stop all route through it) and the game server follows as an
    /// <see cref="IHostedService"/>, so two shutdown paths cannot race each other.</para>
    /// </remarks>
    public sealed class GameNetHostedService : IHostedService
    {
        #region Fields

        private readonly NetServer _server;
        private readonly IServerLogger _logger;
        private readonly string _shutdownReason;

        #endregion

        #region Methods

        /// <summary>
        /// 构造托管服务。
        /// <para>Create the hosted service.</para>
        /// </summary>
        /// <param name="server">要托管的游戏服<para>Game server to host</para></param>
        /// <param name="logger">日志输出<para>Log output</para></param>
        /// <param name="shutdownReason">停机原因（写进会话关闭日志）<para>Shutdown reason (recorded in session-close logs)</para></param>
        public GameNetHostedService(NetServer server, IServerLogger logger = null, string shutdownReason = "Host stopping")
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _logger = logger;
            _shutdownReason = shutdownReason;
        }

        /// <summary>
        /// 启动游戏服（绑定失败会抛出，令宿主启动失败而不是带病运行）。
        /// <para>Start the game server (a bind failure throws so the host fails fast instead of running
        /// degraded).</para>
        /// </summary>
        /// <param name="cancellationToken">启动取消标记<para>Startup cancellation token</para></param>
        /// <returns>完成的任务<para>Completion task</para></returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _server.Start();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 停机：关闭全部会话并停止收包。
        /// <para>Shutdown: close every session and stop receiving.</para>
        /// </summary>
        /// <param name="cancellationToken">停机取消标记<para>Shutdown cancellation token</para></param>
        /// <returns>停机完成的任务<para>Completion task</para></returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return _server.StopAsync(_shutdownReason);
        }

        #endregion
    }
}
