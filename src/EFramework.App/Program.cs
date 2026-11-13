/*
 * ================================================
 * Describe:      Console entry point: wire handlers, start the server, wait for Ctrl+C.
 * CreationTime:  2026-11-13 13:43:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Threading.Tasks;
using EFramework.App.Handlers;
using EFramework.App.Messages;
using EasyFramework.Server;
using EasyFramework.Server.Serialization;

namespace EFramework.App
{
    /// <summary>
    /// 控制台入口：装配分发器与示例业务，启动服务端并等待 Ctrl+C 优雅停机。
    /// <para>Console entry point: wires the dispatcher and demo business code, starts the server and waits
    /// for Ctrl+C to shut down gracefully.</para>
    /// </summary>
    internal static class Program
    {
        #region Methods

        private static async Task<int> Main(string[] args)
        {
            int port = ParsePort(args, ServerOptions.DefaultPort);
            LogLevel logLevel = HasFlag(args, "--verbose") ? LogLevel.Debug : LogLevel.Info;

            var logger = new ConsoleServerLogger { Level = logLevel };
            var dispatcher = new ServerDispatcher(logger, new JsonMessageSerializer());
            dispatcher.Register<EchoReq, EchoHandler>(new EchoHandler(logger));

            using var server = new NetServer(new ServerOptions { Port = port, LogLevel = logLevel }, dispatcher, logger: logger);

            server.SessionOpened += session =>
                logger.Info($"[SESSION+] id={session.SessionId} remote={session.RemoteEndPointText} conv={session.Conv}");

            server.SessionClosed += (session, reason) =>
                logger.Info($"[SESSION-] id={session.SessionId} remote={session.RemoteEndPointText} reason={reason} " +
                            $"rx={session.ReceivedMessages} tx={session.SentMessages}");

            try
            {
                server.Start();
            }
            catch (Exception e)
            {
                logger.Error("Server failed to start.", e);
                return 1;
            }

            PrintBanner(server, logLevel);
            await WaitForShutdownAsync().ConfigureAwait(false);
            await server.StopAsync("Console shutdown").ConfigureAwait(false);

            logger.Info("Bye.");
            return 0;
        }

        // Ctrl+C 只置位：先让泵与传输层有序停下，再由 Main 正常返回
        private static async Task WaitForShutdownAsync()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                // 阻止运行时直接杀进程，改走优雅停机
                eventArgs.Cancel = true;
                completion.TrySetResult(true);
            };

            Console.CancelKeyPress += handler;
            try
            {
                await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }

        private static void PrintBanner(NetServer server, LogLevel logLevel)
        {
            Console.WriteLine();
            Console.WriteLine("==============================================================");
            Console.WriteLine("  EFramework Server (EF.Net)");
            Console.WriteLine("--------------------------------------------------------------");
            Console.WriteLine($"  UDP endpoint : {server.LocalEndPoint}");
            Console.WriteLine($"  Tick         : {server.Options.TickIntervalMs} ms");
            Console.WriteLine($"  Log level    : {logLevel}");
            Console.WriteLine("  Built-in     : opcode 1 Ping -> 2 Pong / 4 Handshake -> 5 Result");
            Console.WriteLine("  Business     : opcode 1001 EchoReq -> 1002 EchoRes");
            Console.WriteLine("--------------------------------------------------------------");
            Console.WriteLine("  Unity client (cn.efefef.net):");
            Console.WriteLine($"    NetworkManager.Instance.Connect(\"127.0.0.1\", {server.LocalEndPoint.Port});");
            Console.WriteLine("    // 随后发送 1001 即可收到 1002 回包");
            Console.WriteLine("--------------------------------------------------------------");
            Console.WriteLine("  Press Ctrl+C to stop.");
            Console.WriteLine("==============================================================");
            Console.WriteLine();
        }

        // 参数：--port/-p <端口>、--verbose
        private static int ParsePort(string[] args, int fallback)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != "--port" && args[i] != "-p") continue;
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
