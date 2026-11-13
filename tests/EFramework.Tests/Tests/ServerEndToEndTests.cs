/*
 * ================================================
 * Describe:      End-to-end tests over a real UDP loopback (server + protocol probe client).
 * CreationTime:  2026-11-13 14:02:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using EFramework.Tests.Harness;
using EasyFramework.Server;
using EasyFramework.Server.Handshake;
using EasyFramework.Server.Serialization;
using EasyFramework.Server.Sessions;
using EasyFramework.Systems.Net;

namespace EFramework.Tests.Tests
{
    /// <summary>
    /// 端到端测试：真实 UDP 回环上跑「服务端 + 协议探针」，覆盖心跳、RPC、多客户端、握手与回收。
    /// <para>End-to-end tests: run the server plus the protocol probe over a real UDP loopback, covering
    /// heartbeat, RPC, multiple clients, handshake and reclamation.</para>
    /// </summary>
    internal static class ServerEndToEndTests
    {
        #region Fields

        private const int EchoReqOpcode = 2001;
        private const int EchoResOpcode = 2002;
        private const int OffThreadReqOpcode = 2003;
        private const int OffThreadResOpcode = 2004;
        private const int ThrowingOpcode = 2009;
        private const int UnknownOpcode = 9999;

        private static readonly JsonMessageSerializer Json = new JsonMessageSerializer();

        #endregion

        #region Methods

        public static void Run(TestRunner runner)
        {
            TestRunner.Section("Server end-to-end (real UDP loopback)");

            runner.Run("Ping is echoed back as Pong within the tick budget", () =>
            {
                using var fixture = new ServerFixture();
                using var client = new ProtocolClient("127.0.0.1", fixture.Port, 1);

                long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                client.Send(NetOpcode.Ping, 0, BitConverter.GetBytes(stamp));

                NetPacket pong = client.WaitFor(NetOpcode.Pong);

                Assert.Equal(8, pong.PayloadLength, "pong payload length");
                Assert.Equal(stamp, BitConverter.ToInt64(pong.Payload, 0), "pong must echo the client timestamp");
            });

            runner.Run("Typed request is answered on the matching rpcId", () =>
            {
                using var fixture = new ServerFixture();
                using var client = new ProtocolClient("127.0.0.1", fixture.Port, 1);

                client.Send(EchoReqOpcode, 55, Json.Serialize(new TestEchoReq { Content = "你好 EF.Net" }));

                NetPacket response = client.WaitFor(p => p.Opcode == EchoResOpcode && p.RpcId == 55, "echo response on rpcId 55");
                var body = Json.Deserialize<TestEchoRes>(response.Payload, 0, response.PayloadLength);

                Assert.NotNull(body, "response body");
                Assert.Equal("你好 EF.Net", body.Content, "echoed content");
                Assert.True(body.SessionId > 0, "server must assign a positive session id");
            });

            runner.Run("Two clients sharing conv=1 get independent sessions", () =>
            {
                using var fixture = new ServerFixture();

                // 客户端 conv 是进程内自增的，两个玩家进程都会用 1：服务端必须按端点区分
                using var first = new ProtocolClient("127.0.0.1", fixture.Port, 1);
                using var second = new ProtocolClient("127.0.0.1", fixture.Port, 1);

                first.Send(EchoReqOpcode, 1, Json.Serialize(new TestEchoReq { Content = "first" }));
                second.Send(EchoReqOpcode, 2, Json.Serialize(new TestEchoReq { Content = "second" }));

                NetPacket firstBody = first.WaitFor(p => p.Opcode == EchoResOpcode && p.RpcId == 1, "first response");
                NetPacket secondBody = second.WaitFor(p => p.Opcode == EchoResOpcode && p.RpcId == 2, "second response");

                var firstRes = Json.Deserialize<TestEchoRes>(firstBody.Payload, 0, firstBody.PayloadLength);
                var secondRes = Json.Deserialize<TestEchoRes>(secondBody.Payload, 0, secondBody.PayloadLength);

                Assert.Equal("first", firstRes.Content, "first client content");
                Assert.Equal("second", secondRes.Content, "second client content");
                Assert.True(firstRes.SessionId != secondRes.SessionId, "the two clients must land on different sessions");
                Assert.Equal(2, fixture.Server.SessionCount, "session count");
            });

            runner.Run("Accepted handshake unlocks business messages", () =>
            {
                using var fixture = new ServerFixture(options => options.RequireHandshake = true, new TokenValidator("secret"));
                using var client = new ProtocolClient("127.0.0.1", fixture.Port, 1);

                client.Send(NetOpcode.Handshake, 0, Encoding.UTF8.GetBytes("secret"));

                NetPacket result = client.WaitFor(NetOpcode.HandshakeResult);
                Assert.Equal(0, result.Payload[0], "handshake code 0 means accepted");

                client.Send(EchoReqOpcode, 3, Json.Serialize(new TestEchoReq { Content = "after-handshake" }));
                NetPacket response = client.WaitFor(p => p.Opcode == EchoResOpcode && p.RpcId == 3, "echo after handshake");

                var body = Json.Deserialize<TestEchoRes>(response.Payload, 0, response.PayloadLength);
                Assert.Equal("after-handshake", body.Content, "business message after handshake");
            });

            runner.Run("Rejected handshake reports a code and reclaims the session", () =>
            {
                using var fixture = new ServerFixture(options => options.RequireHandshake = true, new TokenValidator("secret"));
                using var client = new ProtocolClient("127.0.0.1", fixture.Port, 1);

                client.Send(NetOpcode.Handshake, 0, Encoding.UTF8.GetBytes("wrong-token"));

                NetPacket result = client.WaitFor(NetOpcode.HandshakeResult);
                Assert.True(result.Payload[0] != 0, "handshake code must be non-zero on rejection");
                Assert.True(result.PayloadLength > 1, "rejection must carry a UTF-8 reason");

                Assert.True(WaitUntil(() => fixture.Server.SessionCount == 0, 3000), "rejected session must be reclaimed");
            });

            runner.Run("Idle sessions are reclaimed after the timeout", () =>
            {
                using var fixture = new ServerFixture(options => options.SessionIdleTimeoutSeconds = 1);
                using var client = new ProtocolClient("127.0.0.1", fixture.Port, 1);

                client.Send(NetOpcode.Ping, 0, BitConverter.GetBytes(1L));
                client.WaitFor(NetOpcode.Pong);

                Assert.Equal(1, fixture.Server.SessionCount, "one live session before idling");
                Assert.True(WaitUntil(() => fixture.Server.SessionCount == 0, 4000), "idle session must be reclaimed");
            });

            runner.Run("Unknown opcode and a throwing handler do not break the loop", () =>
            {
                using var fixture = new ServerFixture();
                fixture.Dispatcher.Register(ThrowingOpcode, _ => throw new InvalidOperationException("boom"));

                using var client = new ProtocolClient("127.0.0.1", fixture.Port, 1);

                client.Send(UnknownOpcode, 0, new byte[] { 1, 2, 3 });
                client.Send(ThrowingOpcode, 0, null);
                client.Send(NetOpcode.Ping, 0, BitConverter.GetBytes(7L));

                NetPacket pong = client.WaitFor(NetOpcode.Pong);
                Assert.Equal(7L, BitConverter.ToInt64(pong.Payload, 0), "server must keep answering after faults");
                Assert.True(fixture.Dispatcher.FailedCount == 1, "throwing handler must be counted");
            });

            runner.Run("Reply from a pool thread is marshalled back to the pump", () =>
            {
                using var fixture = new ServerFixture();
                using var client = new ProtocolClient("127.0.0.1", fixture.Port, 1);

                client.Send(OffThreadReqOpcode, 77, Json.Serialize(new OffThreadReq { Content = "work" }));

                NetPacket response = client.WaitFor(
                    packet => packet.Opcode == OffThreadResOpcode && packet.RpcId == 77,
                    "off-thread response on rpcId 77", 5000);

                var body = Json.Deserialize<OffThreadRes>(response.Payload, 0, response.PayloadLength);
                Assert.NotNull(body, "response body");
                Assert.Equal("work@pool", body.Content, "content produced on the pool thread");
                Assert.True(body.HandlerThreadId != body.ReplyThreadId,
                    "handler and reply must run on different threads for this case to prove marshalling");
            });

            runner.Run("A burst of 200 pings is answered without loss or duplication", () =>
            {
                using var fixture = new ServerFixture();
                using var client = new ProtocolClient("127.0.0.1", fixture.Port, 1);

                const int count = 200;
                for (int i = 0; i < count; i++)
                {
                    client.Send(NetOpcode.Ping, 0, BitConverter.GetBytes((long)i));
                }

                var seen = new HashSet<long>();
                var stopwatch = Stopwatch.StartNew();

                while (seen.Count < count && stopwatch.ElapsedMilliseconds < 8000)
                {
                    client.Drain(packet =>
                    {
                        if (packet.Opcode == NetOpcode.Pong && packet.PayloadLength >= 8)
                            seen.Add(BitConverter.ToInt64(packet.Payload, 0));
                    });

                    Thread.Sleep(1);
                }

                Assert.Equal(count, seen.Count, "every ping must be answered exactly once");

                for (int i = 0; i < count; i++)
                {
                    Assert.True(seen.Contains(i), $"missing pong for ping {i}");
                }
            });

            runner.Run("NetServer.Send from a foreign thread fails fast", () =>
            {
                using var fixture = new ServerFixture();

                Exception captured = null;
                var worker = new Thread(() =>
                {
                    try
                    {
                        fixture.Server.Send(1, NetOpcode.Ping);
                    }
                    catch (Exception e)
                    {
                        captured = e;
                    }
                });

                worker.Start();
                worker.Join(2000);

                Assert.True(captured is InvalidOperationException,
                    $"expected InvalidOperationException, got {captured?.GetType().Name ?? "no exception"}");
            });

            runner.Run("StopAsync closes live sessions with a reason", () =>
            {
                var reasons = new List<string>();

                using (var fixture = new ServerFixture())
                {
                    fixture.Server.SessionClosed += (session, reason) => reasons.Add(reason);

                    using var client = new ProtocolClient("127.0.0.1", fixture.Port, 1);
                    client.Send(NetOpcode.Ping, 0, BitConverter.GetBytes(1L));
                    client.WaitFor(NetOpcode.Pong);

                    fixture.Server.StopAsync("test-shutdown").GetAwaiter().GetResult();
                }

                Assert.Equal(1, reasons.Count, "session-closed event count");
                Assert.Equal("test-shutdown", reasons[0], "shutdown reason must be propagated");
            });
        }

        // 轮询等待条件成立（用于验证异步回收路径）
        private static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }

            return condition();
        }

        #endregion

        #region Test doubles

        /// <summary>
        /// 测试用服务端夹具：端口 0（系统分配）、静默日志、注册回声处理器。
        /// <para>Test fixture: port 0 (OS-assigned), silent logging, echo handler registered.</para>
        /// </summary>
        private sealed class ServerFixture : IDisposable
        {
            #region Properties

            public NetServer Server { get; }

            public ServerDispatcher Dispatcher { get; }

            public int Port { get; }

            #endregion

            #region Methods

            public ServerFixture(Action<ServerOptions> configure = null, IHandshakeValidator validator = null)
            {
                var options = new ServerOptions { Port = 0, LogLevel = LogLevel.None };
                configure?.Invoke(options);

                var logger = new ConsoleServerLogger { Level = LogLevel.None };
                Dispatcher = new ServerDispatcher(logger, Json);
                Dispatcher.Register<TestEchoReq, TestEchoHandler>();
                Dispatcher.Register<OffThreadReq, OffThreadHandler>();

                Server = new NetServer(options, Dispatcher, logger: logger, handshakeValidator: validator);
                Server.Start();
                Port = Server.LocalEndPoint.Port;
            }

            public void Dispose()
            {
                Server.Dispose();
            }

            #endregion
        }

        /// <summary>
        /// 测试用握手校验器：只接受固定 Token。
        /// <para>Test validator: accepts one fixed token only.</para>
        /// </summary>
        private sealed class TokenValidator : IHandshakeValidator
        {
            #region Fields

            private readonly string _expected;

            #endregion

            #region Methods

            public TokenValidator(string expected)
            {
                _expected = expected;
            }

            public HandshakeResult Validate(ReadOnlySpan<byte> token, IPEndPoint remoteEndPoint)
            {
                string value = token.Length == 0 ? string.Empty : Encoding.UTF8.GetString(token);
                return value == _expected ? HandshakeResult.Success : HandshakeResult.Reject(2, "invalid token");
            }

            #endregion
        }

        /// <summary>
        /// 测试用回声处理器：原样回传内容与会话 Id。
        /// <para>Test echo handler: returns the content and the session id.</para>
        /// </summary>
        private sealed class TestEchoHandler : MessageHandler<TestEchoReq>
        {
            #region Methods

            public override void Handle(TestEchoReq message, MessageContext context)
            {
                context.Reply(new TestEchoRes
                {
                    Content = message.Content,
                    SessionId = context.SessionId,
                });
            }

            #endregion
        }

        /// <summary>
        /// 测试用跨线程处理器：模拟「处理器把耗时逻辑丢到线程池，再从池线程回包」。
        /// <para>Test cross-thread handler: simulates offloading slow work to the pool and replying from there.</para>
        /// </summary>
        private sealed class OffThreadHandler : MessageHandler<OffThreadReq>
        {
            #region Methods

            public override void Handle(OffThreadReq message, MessageContext context)
            {
                // 只把「会话 + 配对号 + 业务数据」带到别的线程，上下文本身不跨线程使用
                NetSession session = context.Session;
                int rpcId = context.RpcId;
                string content = message.Content;
                int handlerThreadId = Environment.CurrentManagedThreadId;

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(5); // 模拟查库 / 调第三方

                    session.Send(OffThreadResOpcode, rpcId, Json.Serialize(new OffThreadRes
                    {
                        Content = content + "@pool",
                        SessionId = session.SessionId,
                        HandlerThreadId = handlerThreadId,
                        ReplyThreadId = Environment.CurrentManagedThreadId,
                    }));
                });
            }

            #endregion
        }

        #endregion
    }

    /// <summary>
    /// 测试用请求消息。
    /// <para>Test request message.</para>
    /// </summary>
    [NetOpcode(2001)]
    internal sealed class TestEchoReq
    {
        #region Properties

        public string Content { get; set; }

        #endregion
    }

    /// <summary>
    /// 测试用响应消息。
    /// <para>Test response message.</para>
    /// </summary>
    [NetOpcode(2002)]
    internal sealed class TestEchoRes
    {
        #region Properties

        public string Content { get; set; }

        public int SessionId { get; set; }

        #endregion
    }

    /// <summary>
    /// 测试用跨线程请求消息。
    /// <para>Test cross-thread request message.</para>
    /// </summary>
    [NetOpcode(2003)]
    internal sealed class OffThreadReq
    {
        #region Properties

        public string Content { get; set; }

        #endregion
    }

    /// <summary>
    /// 测试用跨线程响应消息（带两个线程 Id，用于证明回包确实发生在别的线程上）。
    /// <para>Test cross-thread response (carries both thread ids to prove the reply happens off-pump).</para>
    /// </summary>
    [NetOpcode(2004)]
    internal sealed class OffThreadRes
    {
        #region Properties

        public string Content { get; set; }

        public int SessionId { get; set; }

        public int HandlerThreadId { get; set; }

        public int ReplyThreadId { get; set; }

        #endregion
    }
}
