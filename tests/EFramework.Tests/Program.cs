/*
 * ================================================
 * Describe:      Verification entry point: test suites plus a manual connectivity probe.
 * CreationTime:  2026-11-13 14:05:00
 * ScriptVersion: 0.2
 * ===============================================
 */

using System;
using System.Text;
using EFramework.Tests.Harness;
using EFramework.Tests.Tests;
using EasyFramework.Server;
using EasyFramework.Systems.Net;

namespace EFramework.Tests
{
    /// <summary>
    /// 验证入口：无参运行全部套件；<c>probe &lt;host&gt; [port]</c> 作为手工联调探针直连一个运行中的服务端。
    /// <para>Verification entry point: run all suites with no arguments, or use
    /// <c>probe &lt;host&gt; [port]</c> as a manual probe against a running server.</para>
    /// </summary>
    internal static class Program
    {
        #region Fields

        // 示例业务的 Opcode（与 EFramework.App 的 EchoReq/EchoRes 一致）
        private const int DemoEchoReqOpcode = 1001;
        private const int DemoEchoResOpcode = 1002;

        #endregion

        #region Methods

        private static int Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "probe", StringComparison.OrdinalIgnoreCase))
            {
                return RunProbe(args);
            }

            Console.WriteLine("EFramework.Server verification");
            Console.WriteLine($"runtime: {Environment.Version}, os: {Environment.OSVersion}");

            var runner = new TestRunner();

            MessagePipeTests.Run(runner);
            KcpLoopbackTests.Run(runner);
            ServerEndToEndTests.Run(runner);

            return runner.Summarize();
        }

        // 手工探针：可选握手 → ping/pong → 示例回声，验证「启动即可被客户端直连」
        private static int RunProbe(string[] args)
        {
            string host = args.Length > 1 ? args[1] : "127.0.0.1";
            int port = args.Length > 2 && int.TryParse(args[2], out int parsed) ? parsed : ServerOptions.DefaultPort;
            string token = args.Length > 3 ? args[3] : null;

            Console.WriteLine($"Probe -> {host}:{port}{(string.IsNullOrEmpty(token) ? string.Empty : " (with handshake token)")}");

            try
            {
                using var client = new ProtocolClient(host, port, 1);

                if (!string.IsNullOrEmpty(token))
                {
                    client.Send(NetOpcode.Handshake, 0, Encoding.UTF8.GetBytes(token));

                    NetPacket handshake = client.WaitFor(NetOpcode.HandshakeResult, 3000);
                    int code = handshake.PayloadLength > 0 ? handshake.Payload[0] : -1;
                    string reason = handshake.PayloadLength > 1
                        ? Encoding.UTF8.GetString(handshake.Payload, 1, handshake.PayloadLength - 1)
                        : string.Empty;

                    Console.WriteLine($"  [{(code == 0 ? "OK" : "FAIL")}] Handshake code={code} reason=\"{reason}\"");

                    if (code != 0)
                    {
                        Console.WriteLine("Probe FAILED: handshake rejected");
                        return 1;
                    }
                }

                long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                client.Send(NetOpcode.Ping, 0, BitConverter.GetBytes(stamp));
                NetPacket pong = client.WaitFor(NetOpcode.Pong, 3000);
                long rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp;

                Console.WriteLine($"  [OK] Pong     opcode={pong.Opcode} rtt={rtt}ms localPort={client.LocalPort}");

                // 直接用 JSON 字节发送，证明业务层不依赖共享 C# 类型（客户端可为任意语言）
                byte[] payload = Encoding.UTF8.GetBytes("{\"Content\":\"hello from probe\"}");
                client.Send(DemoEchoReqOpcode, 1, payload);

                NetPacket response = client.WaitFor(
                    packet => packet.Opcode == DemoEchoResOpcode && packet.RpcId == 1,
                    $"opcode {DemoEchoResOpcode} (rpcId 1)", 3000);

                Console.WriteLine($"  [OK] EchoRes  opcode={response.Opcode} rpcId={response.RpcId} " +
                                  $"body={Encoding.UTF8.GetString(response.Payload, 0, response.PayloadLength)}");

                Console.WriteLine("Probe PASSED");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Probe FAILED: {e.GetType().Name}: {e.Message}");
                return 1;
            }
        }

        #endregion
    }
}
