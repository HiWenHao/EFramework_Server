/*
 * ================================================
 * Describe:      Console entry: one process, two hosts (UDP game server + HTTP management plane).
 * CreationTime:  2026-11-13 13:43:00
 * ScriptVersion: 0.2
 * ===============================================
 */

using System;
using System.Net;
using System.Threading.Tasks;
using EFramework.App.Handlers;
using EFramework.App.Messages;
using EasyFramework.Http;
using EasyFramework.Server;
using EasyFramework.Server.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EFramework.App
{
    /// <summary>
    /// 控制台入口：装配业务处理器，起 UDP 游戏服与 HTTP 管理面，由 .NET 通用主机统一管理生命周期。
    /// <para>Console entry: wires the business handlers, starts the UDP game server and the HTTP management
    /// plane, and lets the .NET generic host own the lifetime.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不是两套 Ctrl+C 处理</b>：一个进程只留一个生命周期权威（.NET 通用主机）。
    /// UDP 游戏服作为 <see cref="GameNetHostedService"/> 跟随启停，避免 Ctrl+C 时 HTTP 先退、
    /// 游戏会话来不及发关闭包。</para>
    /// <para><b>Why not two Ctrl+C handlers</b>: one process, one lifetime authority (the generic host). The
    /// UDP server follows as a hosted service, so a shutdown cannot stop HTTP first and leave game sessions
    /// without a disconnect notice.</para>
    /// </remarks>
    internal static class Program
    {
        #region Methods

        private static async Task<int> Main(string[] args)
        {
            int udpPort = ParseIntOption(args, "--port", ServerOptions.DefaultPort);
            int httpPort = ParseIntOption(args, "--http-port", HttpServerOptions.DefaultPort);
            LogLevel logLevel = HasFlag(args, "--verbose") ? LogLevel.Debug : LogLevel.Info;

            var logger = new ConsoleServerLogger { Level = logLevel };
            var dispatcher = new ServerDispatcher(logger, new JsonMessageSerializer());
            dispatcher.Register<EchoReq, EchoHandler>(new EchoHandler(logger));

            using var server = new NetServer(
                new ServerOptions { Port = udpPort, LogLevel = logLevel },
                dispatcher,
                logger: logger);

            server.SessionOpened += session =>
                logger.Info($"[SESSION+] id={session.SessionId} remote={session.RemoteEndPointText} conv={session.Conv}");

            server.SessionClosed += (session, reason) =>
                logger.Info($"[SESSION-] id={session.SessionId} remote={session.RemoteEndPointText} reason={reason} " +
                            $"rx={session.ReceivedMessages} tx={session.SentMessages}");

            var httpOptions = new HttpServerOptions { Port = httpPort };
            httpOptions.Validate();

            var bridge = new GameServerBridge(server, logger);

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Parse(httpOptions.BindAddress), httpOptions.Port));

            // ASP.NET 默认把每条请求都打成 Info，会把游戏服日志淹掉；策略归 HTTP 宿主自己
            builder.UseQuietLogging();

            // 注册进容器：后续新增的管理面端点可直接注入
            builder.Services.AddSingleton(bridge);
            builder.Services.AddHostedService(_ => new GameNetHostedService(server, logger));

            WebApplication web = builder.Build();
            web.MapGameServer(bridge);

            try
            {
                // 启动 Kestrel，并由托管服务顺带启动 UDP 游戏服；任一端口绑定失败都会在这里抛出
                await web.StartAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.Error("Server failed to start.", e);
                return 1;
            }

            PrintBanner(server, httpOptions, logLevel);

            // Ctrl+C / SIGTERM 由通用主机处理，这里只等它结束
            await web.WaitForShutdownAsync().ConfigureAwait(false);
            await web.StopAsync().ConfigureAwait(false);

            logger.Info("Bye.");
            return 0;
        }

        private static void PrintBanner(NetServer server, HttpServerOptions httpOptions, LogLevel logLevel)
        {
            Console.WriteLine();
            Console.WriteLine("==============================================================");
            Console.WriteLine($"  EFramework Server (EF.Net)  v{NetServer.Version}");
            Console.WriteLine("--------------------------------------------------------------");
            Console.WriteLine($"  UDP  (KCP)   : {server.LocalEndPoint}");
            Console.WriteLine($"  HTTP (mgmt)  : http://{httpOptions.BindAddress}:{httpOptions.Port}");
            Console.WriteLine($"  Tick         : {server.Options.TickIntervalMs} ms");
            Console.WriteLine($"  Log level    : {logLevel}");
            Console.WriteLine("  Built-in     : opcode 1 Ping -> 2 Pong / 4 Handshake -> 5 Result");
            Console.WriteLine("  Business     : opcode 1001 EchoReq -> 1002 EchoRes");
            Console.WriteLine("--------------------------------------------------------------");
            Console.WriteLine("  Unity client (cn.efefef.net):");
            Console.WriteLine($"    NetworkManager.Instance.Connect(\"127.0.0.1\", {server.LocalEndPoint.Port});");
            Console.WriteLine("  Admin (management plane):");
            Console.WriteLine($"    curl http://127.0.0.1:{httpOptions.Port}/health");
            Console.WriteLine($"    curl -X POST \"http://127.0.0.1:{httpOptions.Port}/admin/broadcast?opcode=1003\" --data-binary hi");
            Console.WriteLine("--------------------------------------------------------------");
            Console.WriteLine("  Press Ctrl+C to stop (stops both hosts).");
            Console.WriteLine("==============================================================");
            Console.WriteLine();
        }

        // 参数：--port <UDP 端口>、--http-port <HTTP 端口>、--verbose
        private static int ParseIntOption(string[] args, string option, int fallback)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed)) return parsed;
            }

            return fallback;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        #endregion
    }
}
