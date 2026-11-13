/*
 * ================================================
 * Describe:      Minimal-API endpoints for the management plane.
 * CreationTime:  2026-11-13 14:44:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.IO;
using System.Threading.Tasks;
using EasyFramework.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EasyFramework.Http
{
    /// <summary>
    /// 管理面端点：健康检查、统计、会话列表、广播与踢人。
    /// <para>Management-plane endpoints: health, stats, session listing, broadcast and kick.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>这是管理面，不是游戏协议</b>：客户端的业务请求仍走 UDP + KCP；HTTP 只给运维、后台、
    /// 健康检查用。游戏消息不要走 HTTP —— 那会把 KCP 的可靠性与顺序性丢掉，还得再实现一遍。</para>
    /// <para><b>Management plane, not the game protocol</b>: client gameplay still goes over UDP + KCP. HTTP
    /// serves ops, back office and health checks. Do not route game messages over HTTP: you would lose KCP's
    /// reliability and ordering and have to rebuild them.</para>
    /// <para><b>鉴权</b>：当前端点无鉴权，只适合内网/本机。公网暴露前必须加认证与限流（见 README）。</para>
    /// <para><b>Auth</b>: these endpoints are unauthenticated and fit LAN/local use only. Add authentication
    /// and rate limiting before exposing them publicly (see README).</para>
    /// </remarks>
    public static class HttpEndpoints
    {
        #region Fields

        // 只在根路径的索引里用；静态复用避免每次请求都新建数组
        private static readonly string[] EndpointList =
        {
            "GET  /health",
            "GET  /stats",
            "GET  /sessions",
            "POST /admin/broadcast?opcode=<n>  (request body = raw payload)",
            "POST /admin/close/{sessionId}?reason=<text>",
        };

        #endregion

        #region Methods

        /// <summary>
        /// 把管理面端点挂到应用上。
        /// <para>Map the management-plane endpoints onto the application.</para>
        /// </summary>
        /// <param name="app">HTTP 应用<para>HTTP application</para></param>
        /// <param name="bridge">游戏服桥接层<para>Game-server bridge</para></param>
        public static void MapGameServer(this WebApplication app, GameServerBridge bridge)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            if (bridge == null) throw new ArgumentNullException(nameof(bridge));

            app.MapGet("/", () => Results.Ok(new
            {
                service = "EFramework.Http",
                version = NetServer.Version,
                endpoints = EndpointList,
            }));

            app.MapGet("/health", () => Results.Ok(new
            {
                status = bridge.IsRunning ? "ok" : "stopped",
                version = NetServer.Version,
            }));

            app.MapGet("/stats", async () => Results.Ok(await bridge.GetStatsAsync()));

            app.MapGet("/sessions", async () => Results.Ok(await bridge.GetSessionsAsync()));

            // 载荷走请求体原始字节：避免 base64 让调用方多一层转码，curl --data-binary 直接可用
            app.MapPost("/admin/broadcast", async (HttpRequest request, int opcode) =>
            {
                byte[] payload = null;

                if (request.ContentLength is > 0)
                {
                    using var buffer = new MemoryStream();
                    await request.Body.CopyToAsync(buffer);
                    if (buffer.Length > 0) payload = buffer.ToArray();
                }

                if (!bridge.Broadcast(opcode, payload))
                {
                    return Results.BadRequest(new { error = "opcode must be greater than 0" });
                }

                return Results.Ok(new { queued = true, opcode, bytes = payload?.Length ?? 0 });
            });

            app.MapPost("/admin/close/{sessionId:int}", (int sessionId, string reason) =>
            {
                if (!bridge.CloseSession(sessionId, reason))
                {
                    return Results.BadRequest(new { error = "sessionId must be greater than 0" });
                }

                return Results.Ok(new { queued = true, sessionId, reason = reason ?? "Closed by admin" });
            });
        }

        #endregion
    }
}
