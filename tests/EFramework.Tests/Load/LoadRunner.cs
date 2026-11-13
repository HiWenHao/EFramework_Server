/*
 * ================================================
 * Describe:      Load-test engine: warm up, drive clients (optionally multi-threaded), report RTT + loss.
 * CreationTime:  2026-11-13 15:18:00
 * ScriptVersion: 0.2
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace EFramework.Tests.Load
{
    /// <summary>
    /// 压测引擎：驱动 N 个协议级客户端，测量往返延迟分布、丢包率与实际吞吐。
    /// <para>Load engine: drive N protocol-accurate clients and measure RTT distribution, loss and achieved
    /// throughput.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>这个工具测什么</b>：服务端在真实协议栈下的处理能力 —— 每发一条是完整的
    /// 「客户端 KCP 编码 → UDP → 服务端泵 → handler → 回包 → 客户端 KCP 重组」。</para>
    /// <para><b>What it measures</b>: server capacity on the real stack — each message is a full round trip
    /// through KCP on both ends, the pump, the handler and back.</para>
    /// <para><b>它测不出什么</b>：① 真实网络（这里是 loopback，RTT 里装着两侧各一个 KCP 聚合节拍，
    /// 而 Windows 的计时器精度约 15.6ms）；② 生成器与服务端抢同一台机器。</para>
    /// <para><b>What it cannot measure</b>: real networks (loopback RTT carries a KCP flush interval on each
    /// side, and Windows' timer granularity is ~15.6ms); and it cannot avoid sharing the box with the server.</para>
    /// <para><b>为什么要多线程</b>：单线程生成器每轮都要对每个 socket 做一次系统调用，几百个 socket 时
    /// 自己就先饱和了 —— 实测 800 连接×12.5/s 的 p50 是 43ms，而同样 10k/s 用 100 连接只有 20ms。
    /// 想测服务端上限，就必须把生成器拆成多线程。</para>
    /// <para><b>Why threads</b>: a single-threaded generator makes one syscall per socket per round and
    /// saturates at a few hundred sockets — measured p50 43ms at 800×12.5/s versus 20ms at 100×100/s for the
    /// same 10k/s. To find the server's ceiling the generator must scale out.</para>
    /// </remarks>
    internal static class LoadRunner
    {
        #region Fields

        public const int EchoReqOpcode = 1001;
        public const int EchoResOpcode = 1002;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        #endregion

        #region Methods

        public static int Run(LoadOptions options)
        {
            Console.WriteLine("=== Load test ===");
            Console.WriteLine($"target  : {options.Clients} clients x {Format(options.QpsPerClient)}/s " +
                              $"= {Format(options.TotalTargetQps)}/s, {Format(options.Seconds)}s, " +
                              $"{options.Threads} generator thread(s)");
            Console.WriteLine($"mode    : {(options.EchoMode ? "echo (1001/1002, JSON + dispatcher)" : "ping (1/2, protocol only)")}" +
                              $", payload {options.PayloadSize}B");

            ServerSnapshot before = FetchStats(options.StatsUrl);

            List<List<LoadClient>> groups = CreateGroups(options);
            List<List<double>> samplesPerGroup = CreateSampleLists(options);

            if (!WarmUp(groups, samplesPerGroup, options)) return 1;

            double start = Clock.Elapsed.TotalSeconds;
            double end = start + options.Seconds;

            for (int g = 0; g < groups.Count; g++)
            {
                for (int i = 0; i < groups[g].Count; i++) groups[g][i].Begin(start);
            }

            DriveAll(groups, end);
            DrainTail(groups);

            double elapsed = Clock.Elapsed.TotalSeconds - start;

            long sent = 0;
            long received = 0;
            var samples = new List<double>();
            for (int g = 0; g < groups.Count; g++)
            {
                for (int i = 0; i < groups[g].Count; i++)
                {
                    sent += groups[g][i].Sent;
                    received += groups[g][i].Received;
                }

                samples.AddRange(samplesPerGroup[g]);
            }

            for (int g = 0; g < groups.Count; g++)
            {
                for (int i = 0; i < groups[g].Count; i++) groups[g][i].Dispose();
            }

            Report(options, sent, received, elapsed, samples, before, FetchStats(options.StatsUrl));
            return received > 0 ? 0 : 1;
        }

        // 客户端按线程分组：组内顺序驱动，组间并行，样本各存一份（无共享，无需加锁）
        private static List<List<LoadClient>> CreateGroups(LoadOptions options)
        {
            var groups = new List<List<LoadClient>>(options.Threads);
            int perGroup = options.Clients / options.Threads + 1;
            for (int g = 0; g < options.Threads; g++) groups.Add(new List<LoadClient>(perGroup));
            return groups;
        }

        private static List<List<double>> CreateSampleLists(LoadOptions options)
        {
            var lists = new List<List<double>>(options.Threads);
            int perGroup = EstimateSampleCapacity(options) / options.Threads + 1024;

            for (int g = 0; g < options.Threads; g++) lists.Add(new List<double>(perGroup));
            return lists;
        }

        // 建会话 + 可选握手；任一客户端握手失败就整体放弃（否则测的是"全被门禁丢掉"）
        private static bool WarmUp(List<List<LoadClient>> groups, List<List<double>> samplesPerGroup,
            LoadOptions options)
        {
            for (int i = 0; i < options.Clients; i++)
            {
                int g = i % groups.Count;
                var client = new LoadClient(i, options, samplesPerGroup[g].Add);
                client.SendHandshake();
                groups[g].Add(client);
            }

            double deadline = Clock.Elapsed.TotalSeconds + 15d;
            while (Clock.Elapsed.TotalSeconds < deadline)
            {
                bool allSettled = true;
                double now = Clock.Elapsed.TotalSeconds;

                for (int g = 0; g < groups.Count; g++)
                {
                    List<LoadClient> group = groups[g];
                    for (int i = 0; i < group.Count; i++)
                    {
                        group[i].Service(now);
                        if (!group[i].HandshakeSettled) allSettled = false;
                    }
                }

                if (allSettled) break;
                Thread.Sleep(1);
            }

            for (int g = 0; g < groups.Count; g++)
            {
                for (int i = 0; i < groups[g].Count; i++)
                {
                    LoadClient client = groups[g][i];
                    if (!client.HandshakeSettled)
                    {
                        Console.WriteLine($"[FAIL] a client handshake did not settle within 15s");
                        DisposeAll(groups);
                        return false;
                    }

                    if (client.HandshakeCode != 0)
                    {
                        Console.WriteLine($"[FAIL] handshake rejected: code={client.HandshakeCode} " +
                                          $"reason=\"{client.HandshakeReason}\"");
                        DisposeAll(groups);
                        return false;
                    }
                }
            }

            Console.WriteLine($"warmup  : {options.Clients} sessions ready");
            return true;
        }

        private static void DriveAll(List<List<LoadClient>> groups, double end)
        {
            var threads = new List<Thread>(groups.Count);
            for (int g = 0; g < groups.Count; g++)
            {
                List<LoadClient> group = groups[g];
                var thread = new Thread(() => Drive(group, end))
                {
                    IsBackground = true,
                    Name = $"load-gen-{g}",
                };

                threads.Add(thread);
            }

            for (int i = 0; i < threads.Count; i++) threads[i].Start();
            for (int i = 0; i < threads.Count; i++) threads[i].Join();
        }

        private static void Drive(List<LoadClient> clients, double end)
        {
            while (true)
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (now >= end) return;

                bool worked = false;
                bool inflight = false;

                for (int i = 0; i < clients.Count; i++)
                {
                    LoadClient client = clients[i];
                    worked |= client.Service(now);
                    inflight |= client.HasInflight;
                }

                // 有在途消息时只能自旋（Sleep(0) 仅让出时间片）：Windows 默认计时器精度约 15.6ms，
                // 用 Sleep(1) 等待会把生成器自己的唤醒延迟算进 RTT，测出来的就不是服务端的能力了。
                if (worked || inflight) Thread.Sleep(0);
                else Thread.Sleep(1);
            }
        }

        // 停止发送后继续收一会儿，把在途回包收干净，否则"丢包率"里会混进还没回来的消息
        private static void DrainTail(List<List<LoadClient>> groups)
        {
            for (int g = 0; g < groups.Count; g++)
            {
                for (int i = 0; i < groups[g].Count; i++) groups[g][i].StopSending();
            }

            double drainUntil = Clock.Elapsed.TotalSeconds + 0.5d;
            while (Clock.Elapsed.TotalSeconds < drainUntil)
            {
                bool worked = false;
                double now = Clock.Elapsed.TotalSeconds;

                for (int g = 0; g < groups.Count; g++)
                {
                    for (int i = 0; i < groups[g].Count; i++) worked |= groups[g][i].Service(now);
                }

                if (!worked) Thread.Sleep(1);
            }
        }

        private static void Report(LoadOptions options, long sent, long received, double elapsed,
            List<double> samples, ServerSnapshot before, ServerSnapshot after)
        {
            double lossRate = sent > 0 ? (sent - received) * 100d / sent : 0d;
            double actualQps = elapsed > 0d ? received / elapsed : 0d;

            samples.Sort();

            Console.WriteLine("--------------------------------------------------------------");
            Console.WriteLine($"sent    : {sent} | recv: {received} | lost: {sent - received} " +
                              $"({Format(lossRate)}%)");
            Console.WriteLine($"actual  : {Format(actualQps)} msg/s over {Format(elapsed)}s");
            Console.WriteLine($"RTT ms  : p50 {Format(Percentile(samples, 0.50d))} | " +
                              $"p95 {Format(Percentile(samples, 0.95d))} | " +
                              $"p99 {Format(Percentile(samples, 0.99d))} | " +
                              $"max {Format(samples.Count > 0 ? samples[samples.Count - 1] : 0d)} " +
                              $"(n={samples.Count})");

            if (before.Valid && after.Valid)
            {
                Console.WriteLine($"server  : handled +{after.Handled - before.Handled} | " +
                                  $"failed +{after.Failed - before.Failed} | " +
                                  $"sessions {after.Sessions} | rejectedByLimit +{after.Rejected - before.Rejected} | " +
                                  $"tick {Format(after.ActualTickMs)}ms/{Format(after.TickWorkMs)}ms work");
            }
            else if (!string.IsNullOrEmpty(options.StatsUrl))
            {
                Console.WriteLine("server  : (stats unavailable)");
            }

            double p99 = Percentile(samples, 0.99d);
            string verdict = lossRate > 1d
                ? "不稳定：丢包 >1%，先看 MaxSendBacklogSegments / 网络"
                : p99 > 100d
                    ? "开始劣化：p99 > 100ms，检查 tick 与 KCP 聚合叠加"
                    : "稳定：丢包 <1% 且 p99 < 100ms";
            Console.WriteLine($"verdict : {verdict}");
        }

        // 单线程内收集，合并后由调用方排序；无需加锁
        private static double Percentile(List<double> sorted, double percentile)
        {
            if (sorted.Count == 0) return 0d;

            int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            if (index < 0) index = 0;
            if (index >= sorted.Count) index = sorted.Count - 1;
            return sorted[index];
        }

        private static int EstimateSampleCapacity(LoadOptions options)
        {
            double expected = options.TotalTargetQps * options.Seconds * 1.1d;
            if (expected < 1024d) return 1024;
            return expected > 5_000_000d ? 5_000_000 : (int)expected;
        }

        private static string Format(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static void DisposeAll(List<List<LoadClient>> groups)
        {
            for (int g = 0; g < groups.Count; g++)
            {
                for (int i = 0; i < groups[g].Count; i++) groups[g][i]?.Dispose();
            }
        }

        private static ServerSnapshot FetchStats(string url)
        {
            if (string.IsNullOrEmpty(url)) return default;

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                string json = http.GetStringAsync(url).GetAwaiter().GetResult();
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                return new ServerSnapshot
                {
                    Valid = true,
                    Sessions = root.TryGetProperty("sessions", out JsonElement sessions) ? sessions.GetInt32() : 0,
                    Handled = root.TryGetProperty("handled", out JsonElement handled) ? handled.GetInt64() : 0,
                    Failed = root.TryGetProperty("failed", out JsonElement failed) ? failed.GetInt64() : 0,
                    Rejected = root.TryGetProperty("rejectedByLimit", out JsonElement rejected) ? rejected.GetInt64() : 0,
                    ActualTickMs = root.TryGetProperty("actualTickIntervalMs", out JsonElement tick) ? tick.GetDouble() : 0d,
                    TickWorkMs = root.TryGetProperty("actualTickWorkMs", out JsonElement work) ? work.GetDouble() : 0d,
                };
            }
            catch (Exception e)
            {
                Console.WriteLine($"[warn] stats unavailable: {e.Message}");
                return default;
            }
        }

        #endregion

        // 服务端 /stats 的一小片快照（用于「客户端收到的」与「服务端处理的」对账）
        private struct ServerSnapshot
        {
            public bool Valid;
            public int Sessions;
            public long Handled;
            public long Failed;
            public long Rejected;
            public double ActualTickMs;
            public double TickWorkMs;
        }
    }
}
