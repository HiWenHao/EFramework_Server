/*
 * ================================================
 * Describe:      KCP loopback tests (fragmentation, ordering, large messages).
 * CreationTime:  2026-11-13 13:57:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using EFramework.Tests.Harness;
using EasyFramework.Systems.Net;

namespace EFramework.Tests.Tests
{
    /// <summary>
    /// KCP 回环测试：用两条内存管道把两个 <see cref="Kcp"/> 实例对接，验证分片、重排与顺序。
    /// <para>KCP loopback tests: two <see cref="Kcp"/> instances joined by in-memory pipes, verifying
    /// fragmentation, reordering and ordering.</para>
    /// </summary>
    internal static class KcpLoopbackTests
    {
        #region Methods

        public static void Run(TestRunner runner)
        {
            TestRunner.Section("KCP (reliable transport)");

            runner.Run("Delivers 200 variable-size messages in order", () =>
            {
                var aToB = new DatagramPipe();
                var bToA = new DatagramPipe();
                var settings = new KcpSettings { Interval = 5 };

                var sender = new Kcp(7, aToB.Write);
                var receiver = new Kcp(7, bToA.Write);
                settings.ApplyTo(sender);
                settings.ApplyTo(receiver);

                const int messageCount = 200;
                var expected = new List<byte[]>(messageCount);

                for (int i = 0; i < messageCount; i++)
                {
                    // 覆盖极小包、单分片、跨多分片（mss = 512 - 24 = 488）
                    int size = 1 + i % 1500;
                    var payload = new byte[size];
                    for (int j = 0; j < size; j++) payload[j] = (byte)(i + j);

                    expected.Add(payload);
                    sender.Send(payload, 0, payload.Length);
                }

                var received = new List<byte[]>(messageCount);
                var buffer = new byte[4096];
                var stopwatch = Stopwatch.StartNew();

                while (received.Count < messageCount && stopwatch.ElapsedMilliseconds < 10000)
                {
                    Consume(receiver, aToB);
                    Consume(sender, bToA);

                    while (true)
                    {
                        int size = receiver.Recv(buffer, 0);
                        if (size <= 0) break;

                        var copy = new byte[size];
                        Buffer.BlockCopy(buffer, 0, copy, 0, size);
                        received.Add(copy);
                    }

                    Thread.Sleep(1);
                }

                Assert.Equal(messageCount, received.Count, "received message count");

                for (int i = 0; i < messageCount; i++)
                {
                    Assert.Equal(expected[i].Length, received[i].Length, $"message {i} length");
                    Assert.BytesEqual(expected[i], received[i], expected[i].Length, $"message {i} content");
                }
            });

            runner.Run("A single message larger than the window still completes", () =>
            {
                var aToB = new DatagramPipe();
                var bToA = new DatagramPipe();
                var settings = new KcpSettings { Interval = 5 };

                var sender = new Kcp(11, aToB.Write);
                var receiver = new Kcp(11, bToA.Write);
                settings.ApplyTo(sender);
                settings.ApplyTo(receiver);

                // 48KB 单条消息：分片数远超发送窗口(256 段 * 488B ≈ 122KB)，验证窗口推进不死锁
                var payload = new byte[48 * 1024];
                for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;

                Assert.Equal(0, sender.Send(payload, 0, payload.Length), "send should be accepted");

                var buffer = new byte[payload.Length];
                int received = -1;
                var stopwatch = Stopwatch.StartNew();

                while (stopwatch.ElapsedMilliseconds < 10000)
                {
                    Consume(receiver, aToB);
                    Consume(sender, bToA);

                    received = receiver.Recv(buffer, 0);
                    if (received > 0) break;

                    Thread.Sleep(1);
                }

                Assert.Equal(payload.Length, received, "large message size");
                Assert.BytesEqual(payload, buffer, payload.Length, "large message content");
            });

            runner.Run("Dead link is detected when the peer stops acking", () =>
            {
                var aToB = new DatagramPipe();
                var settings = new KcpSettings { Interval = 5, DeadLink = 3 };

                // 只发不收：无人回 ACK，重传次数超限后应判定链路死亡
                var sender = new Kcp(13, aToB.Write);
                settings.ApplyTo(sender);

                sender.Send(new byte[] { 1, 2, 3 }, 0, 3);

                var stopwatch = Stopwatch.StartNew();
                while (!sender.IsDead && stopwatch.ElapsedMilliseconds < 5000)
                {
                    sender.Update();
                    Thread.Sleep(5);
                }

                Assert.True(sender.IsDead, "link should be marked dead after DeadLink retransmits");
            });

            runner.Run("KcpSettings rejects values KCP would silently ignore or truncate", () =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new KcpSettings { Mtu = 0 }.Validate(), "Mtu 0");
                Assert.Throws<ArgumentOutOfRangeException>(() => new KcpSettings { Mtu = 2000 }.Validate(), "Mtu 2000");

                // 窗口在段头里是 uint16，70000 会被截断成错误的窗口通告
                Assert.Throws<ArgumentOutOfRangeException>(() => new KcpSettings { RcvWnd = 70000 }.Validate(), "RcvWnd 70000");

                // 这几个值 KCP 会静默保留默认值，配置写错将毫无反馈
                Assert.Throws<ArgumentOutOfRangeException>(() => new KcpSettings { SndWnd = 0 }.Validate(), "SndWnd 0");
                Assert.Throws<ArgumentOutOfRangeException>(() => new KcpSettings { Interval = 0 }.Validate(), "Interval 0");
                Assert.Throws<ArgumentOutOfRangeException>(() => new KcpSettings { DeadLink = 0 }.Validate(), "DeadLink 0");
                Assert.Throws<ArgumentOutOfRangeException>(() => new KcpSettings { FastResend = -1 }.Validate(), "FastResend -1");

                // 默认值必须通过
                new KcpSettings().Validate();
            });

            runner.Run("Clone isolates the shared default from later mutation", () =>
            {
                KcpSettings template = new KcpSettings();
                KcpSettings copy = template.Clone();

                template.Mtu = 100;
                template.RcvWnd = 8;

                Assert.Equal(512, copy.Mtu, "clone keeps its own MTU");
                Assert.Equal(256, copy.RcvWnd, "clone keeps its own window");
                Assert.Equal(100, template.Mtu, "template keeps the mutation (proves they are separate objects)");
            });
        }

        // 把管道里的数据报全部喂给接收方，并推进它
        private static void Consume(Kcp receiver, DatagramPipe pipe)
        {
            while (pipe.TryRead(out byte[] datagram))
            {
                receiver.Input(datagram, 0, datagram.Length);
            }

            receiver.Update();
        }

        // 内存数据报管道：模拟 UDP 的有界报文语义（一次写入 = 一条数据报）
        private sealed class DatagramPipe
        {
            private readonly Queue<byte[]> _queue = new Queue<byte[]>();

            public void Write(byte[] data, int size)
            {
                var copy = new byte[size];
                Buffer.BlockCopy(data, 0, copy, 0, size);
                _queue.Enqueue(copy);
            }

            public bool TryRead(out byte[] datagram)
            {
                return _queue.TryDequeue(out datagram);
            }
        }

        #endregion
    }
}
