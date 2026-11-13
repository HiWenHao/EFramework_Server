/*
 * ================================================
 * Describe:      HTTP host conveniences: quiet-by-default logging and option binding.
 * CreationTime:  2026-11-13 14:52:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace EasyFramework.Http
{
    /// <summary>
    /// HTTP 宿主的默认配置：日志静音级别等「宿主自身」的策略，不掺业务。
    /// <para>HTTP host defaults: policies that belong to the host itself (log verbosity, etc.), with no
    /// business concerns.</para>
    /// </summary>
    /// <remarks>
    /// 这些方法刻意放在本工程而不是组合根：组合根里如果引入 <c>Microsoft.Extensions.Logging</c>，
    /// 会与 <c>EasyFramework.Server.LogLevel</c> 撞名（CS0104）；而且「HTTP 请求日志打多细」本来就属于
    /// HTTP 宿主的决策。
    /// <para>These live here rather than in the composition root because importing
    /// <c>Microsoft.Extensions.Logging</c> there collides with <c>EasyFramework.Server.LogLevel</c> (CS0104),
    /// and "how verbose are HTTP request logs" is an HTTP-host decision anyway.</para>
    /// </remarks>
    public static class HttpHostDefaults
    {
        #region Methods

        /// <summary>
        /// 把框架日志压到 Warning 及以上：ASP.NET 默认对每条请求打 Info（开始/路由/结果/耗时共 5~6 条），
        /// 会把游戏服的业务日志整段淹掉。
        /// <para>Raise the framework log threshold to Warning: ASP.NET logs 5-6 Info lines per request by
        /// default, which drowns the game server's business logs.</para>
        /// </summary>
        /// <param name="builder">HTTP 应用构建器<para>HTTP application builder</para></param>
        public static void UseQuietLogging(this WebApplicationBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            builder.Logging.SetMinimumLevel(LogLevel.Warning);
        }

        #endregion
    }
}
