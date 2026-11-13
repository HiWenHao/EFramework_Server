/*
 * ================================================
 * Describe:      Load-test options (client count, per-client qps, duration, mode).
 * CreationTime:  2026-11-13 15:14:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Globalization;
using EasyFramework.Server;

namespace EFramework.Tests.Load
{
    /// <summary>
    /// 压测参数。默认走业务路径（1001 回声），因为那才是「真实成本」；<c>--mode ping</c> 测协议栈下限。
    /// <para>Load-test options. The default exercises the business path (1001 echo) because that reflects
    /// real cost; <c>--mode ping</c> measures the protocol floor.</para>
    /// </summary>
    internal sealed class LoadOptions
    {
        #region Properties

        public string Host { get; private set; } = "127.0.0.1";

        public int Port { get; private set; } = ServerOptions.DefaultPort;

        /// <summary>并发客户端数（= 并发会话数）<para>Concurrent clients (= concurrent sessions)</para></summary>
        public int Clients { get; private set; } = 100;

        /// <summary>每个客户端每秒发多少条（总速率 = Clients × QpsPerClient）<para>Messages per second per client (total = Clients × QpsPerClient)</para></summary>
        public double QpsPerClient { get; private set; } = 10d;

        public double Seconds { get; private set; } = 10d;

        /// <summary>
        /// 生成器线程数（每个线程驱动一部分客户端）。单线程生成器在几百个 socket 时会先饱和，
        /// 那时测到的是工具上限而不是服务端上限。
        /// <para>Generator thread count (each drives a subset of clients). A single-threaded generator
        /// saturates at a few hundred sockets; past that you measure the tool, not the server.</para>
        /// </summary>
        public int Threads { get; private set; } = 1;

        /// <summary>业务模式：true = 1001/1002（走 JSON + 分发器），false = 1/2（只走协议栈）<para>Business mode: true = 1001/1002 (JSON + dispatcher), false = 1/2 (protocol only)</para></summary>
        public bool EchoMode { get; private set; } = true;

        /// <summary>请求载荷里的填充长度（字节）<para>Padding length inside the request payload</para></summary>
        public int PayloadSize { get; private set; } = 64;

        /// <summary>握手票据（服务端开启鉴权时必须给）<para>Handshake token (required when the server enforces auth)</para></summary>
        public string Token { get; private set; }

        /// <summary>服务端 /stats 地址：用于比对「客户端收到的」与「服务端处理的」<para>Server /stats URL: compares client-side receipts against server-side handling</para></summary>
        public string StatsUrl { get; private set; }

        public double TotalTargetQps => Clients * QpsPerClient;

        #endregion

        #region Methods

        public static LoadOptions Parse(string[] args)
        {
            var options = new LoadOptions();

            // args[0] == "load"
            int index = 1;
            if (index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                options.Host = args[index];
                index++;
            }

            if (index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
            {
                options.Port = port;
                index++;
            }

            for (; index < args.Length; index++)
            {
                string arg = args[index];
                string value = index + 1 < args.Length ? args[index + 1] : null;

                switch (arg.ToLowerInvariant())
                {
                    case "--clients":
                        options.Clients = ParseInt(value, options.Clients);
                        index++;
                        break;
                    case "--qps":
                        options.QpsPerClient = ParseDouble(value, options.QpsPerClient);
                        index++;
                        break;
                    case "--seconds":
                        options.Seconds = ParseDouble(value, options.Seconds);
                        index++;
                        break;
                    case "--threads":
                        options.Threads = ParseInt(value, options.Threads);
                        index++;
                        break;
                    case "--size":
                        options.PayloadSize = ParseInt(value, options.PayloadSize);
                        index++;
                        break;
                    case "--token":
                        options.Token = value;
                        index++;
                        break;
                    case "--stats-url":
                        options.StatsUrl = value;
                        index++;
                        break;
                    case "--mode":
                        options.EchoMode = string.Equals(value, "echo", StringComparison.OrdinalIgnoreCase);
                        index++;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option: {arg}");
                }
            }

            // 参数校验：报错信息直接指向命令行开关，方便复制粘贴修正
            if (options.Clients < 1)
                throw new ArgumentException("--clients must be >= 1.", nameof(args));
            if (options.QpsPerClient <= 0d)
                throw new ArgumentException("--qps must be > 0.", nameof(args));
            if (options.Seconds <= 0d)
                throw new ArgumentException("--seconds must be > 0.", nameof(args));
            if (options.Threads < 1 || options.Threads > 64)
                throw new ArgumentException("--threads must be within 1..64.", nameof(args));
            if (options.PayloadSize < 0)
                throw new ArgumentException("--size must be >= 0.", nameof(args));

            return options;
        }

        private static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
        }

        private static double ParseDouble(string value, double fallback)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;
        }

        #endregion
    }
}
