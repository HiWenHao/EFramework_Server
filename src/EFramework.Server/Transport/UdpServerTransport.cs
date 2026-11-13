/*
 * ================================================
 * Describe:      UDP server transport: async receive loop feeding a bounded channel.
 * CreationTime:  2026-11-13 13:02:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EasyFramework.Server.Transport
{
    /// <summary>
    /// 基于 <see cref="Socket"/> 的 UDP 服务端传输：一个后台异步收包循环 → 有界通道 → 消息泵。
    /// <para>Socket-based UDP server transport: one background async receive loop feeding a bounded
    /// channel drained by the pump.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不让消息泵直接收包</b>：<c>Socket.Available</c> 轮询在低流量下是空转、在高流量下
    /// 会漏掉「刚好在两次轮询之间到达」的包；异步收包交给内核通知，泵只做无锁出队。</para>
    /// <para><b>Why the pump does not receive directly</b>: polling <c>Socket.Available</c> busy-waits when
    /// idle and can miss datagrams arriving between polls when busy; the async loop lets the kernel
    /// notify, and the pump only dequeues.</para>
    /// <para><b>线程模型</b>：收包循环是唯一写入方，消息泵是唯一读取方，因此通道无需锁；
    /// 发送在泵线程上同步完成，保证同一会话的出站顺序。</para>
    /// <para><b>Threading</b>: the receive loop is the only writer and the pump the only reader, so the
    /// channel needs no lock; sends happen synchronously on the pump thread, preserving per-session
    /// outbound order.</para>
    /// </remarks>
    public sealed class UdpServerTransport : IServerTransport
    {
        #region Fields

        // UDP 数据报理论上限（IPv4 65507 / IPv6 65527），按 64KB 留足，避免截断
        private const int MaxDatagramSize = 65536;

        // 内核接收缓冲下限：突发流量下减少内核态丢包
        private const int MinReceiveBufferBytes = 1024 * 1024;

        private readonly ServerOptions _options;
        private readonly IServerLogger _logger;
        private readonly Channel<Datagram> _inbound;

        private Socket _socket;
        private CancellationTokenSource _receiveCancellation;
        private Task _receiveTask;
        private long _droppedDatagrams;
        private bool _disposed;

        #endregion

        #region Properties

        /// <summary>
        /// 是否已启动
        /// <para>Whether the transport is running</para>
        /// </summary>
        public bool IsRunning => _socket != null;

        /// <summary>
        /// 实际监听端点
        /// <para>Effective listen endpoint</para>
        /// </summary>
        public IPEndPoint LocalEndPoint => _socket?.LocalEndPoint as IPEndPoint;

        /// <summary>
        /// 因队列满被丢弃的入站数据报数
        /// <para>Inbound datagrams dropped because the queue was full</para>
        /// </summary>
        public long DroppedDatagrams => Interlocked.Read(ref _droppedDatagrams);

        #endregion

        #region Methods

        /// <summary>
        /// 构造 UDP 传输（尚未监听，调用 <see cref="Start"/> 生效）。
        /// <para>Create the UDP transport (not listening yet; call <see cref="Start"/>).</para>
        /// </summary>
        /// <param name="options">服务端参数（取绑定地址、端口与队列容量）<para>Server options (bind address, port, queue capacity)</para></param>
        /// <param name="logger">日志输出<para>Log output</para></param>
        public UdpServerTransport(ServerOptions options, IServerLogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;

            _inbound = Channel.CreateBounded<Datagram>(new BoundedChannelOptions(Math.Max(64, options.ReceiveQueueCapacity))
            {
                // 队列满时丢弃新包而不是阻塞内核收包循环：宁可丢包也不让 UDP 接收停摆
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = true,
            });
        }

        /// <summary>
        /// 绑定端口并启动后台收包循环；绑定失败返回 false。
        /// <para>Bind the port and start the background receive loop; returns false on bind failure.</para>
        /// </summary>
        /// <returns>是否启动成功<para>Whether it started</para></returns>
        public bool Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UdpServerTransport));
            if (IsRunning) return false;

            IPAddress address = ResolveBindAddress(_options.BindAddress);
            var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

            try
            {
                // IPv6 全地址时开启双栈，让 IPv4 客户端也能连进来
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    socket.DualMode = true;

                socket.Bind(new IPEndPoint(address, _options.Port));
                socket.ReceiveBufferSize = Math.Max(socket.ReceiveBufferSize, MinReceiveBufferBytes);
            }
            catch (Exception e)
            {
                _logger?.Error($"[UDP] bind {_options.BindAddress}:{_options.Port} failed.", e);
                socket.Dispose();
                return false;
            }

            _socket = socket;
            _receiveCancellation = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, _receiveCancellation.Token));

            _logger?.Info($"[UDP] bound {socket.LocalEndPoint}");
            return true;
        }

        /// <summary>
        /// 非阻塞取出一条入站数据报。
        /// <para>Try to dequeue one inbound datagram without blocking.</para>
        /// </summary>
        /// <param name="datagram">取出的数据报<para>The dequeued datagram</para></param>
        /// <returns>是否有数据<para>Whether a datagram was available</para></returns>
        public bool TryReceive(out Datagram datagram)
        {
            return _inbound.Reader.TryRead(out datagram);
        }

        /// <summary>
        /// 向指定端点发送一段数据（同步发送，保证同会话出站顺序）。
        /// <para>Send a byte range to the endpoint synchronously, preserving per-session ordering.</para>
        /// </summary>
        /// <param name="data">数据缓冲<para>Data buffer</para></param>
        /// <param name="offset">起始偏移<para>Start offset</para></param>
        /// <param name="count">字节数<para>Byte count</para></param>
        /// <param name="remoteEndPoint">目标端点<para>Target endpoint</para></param>
        /// <returns>实际发送字节数；失败返回 -1<para>Bytes sent; -1 on failure</para></returns>
        public int Send(byte[] data, int offset, int count, IPEndPoint remoteEndPoint)
        {
            Socket socket = _socket;
            if (socket == null || remoteEndPoint == null) return -1;

            try
            {
                return socket.SendTo(data, offset, count, SocketFlags.None, remoteEndPoint);
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }
            catch (SocketException e)
            {
                // 对端已关闭 / 路由不可达属常态噪音，降级为 Debug 避免刷屏
                _logger?.Debug($"[UDP] send to {remoteEndPoint} failed ({e.SocketErrorCode}): {e.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 停止监听并释放 IO 资源（可重复调用）。
        /// <para>Stop listening and release IO resources (idempotent).</para>
        /// </summary>
        public void Stop()
        {
            CancellationTokenSource cancellation = _receiveCancellation;
            _receiveCancellation = null;
            cancellation?.Cancel();

            // 先关套接字打断挂起的 ReceiveFromAsync，再等收包循环退出
            Socket socket = _socket;
            _socket = null;
            socket?.Dispose();

            Task receiveTask = _receiveTask;
            _receiveTask = null;
            if (receiveTask != null)
            {
                try
                {
                    receiveTask.Wait(TimeSpan.FromMilliseconds(500));
                }
                catch (AggregateException)
                {
                    // 收包循环内部的异常已在循环里记录，这里只做退出同步
                }
            }

            cancellation?.Dispose();
            _inbound.Writer.TryComplete();

            // 丢弃停机后残留的数据报，避免残留引用拖住内存
            while (_inbound.Reader.TryRead(out _))
            {
            }
        }

        /// <summary>
        /// 停止传输并释放资源。
        /// <para>Stop the transport and release resources.</para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        // 后台收包循环：唯一的入站写者
        private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[MaxDatagramSize];
            EndPoint remoteTemplate = socket.AddressFamily == AddressFamily.InterNetworkV6
                ? new IPEndPoint(IPAddress.IPv6Any, 0)
                : new IPEndPoint(IPAddress.Any, 0);

            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteTemplate, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException e)
                {
                    // Windows 上对端端口不可达会把 ICMP 报错转成 10054 抛给接收端，属正常噪音，不能退出循环
                    _logger?.Debug($"[UDP] receive error ({e.SocketErrorCode}): {e.Message}");
                    continue;
                }
                catch (Exception e)
                {
                    _logger?.Error("[UDP] receive loop terminated unexpectedly.", e);
                    break;
                }

                if (result.ReceivedBytes <= 0) continue;

                byte[] payload = new byte[result.ReceivedBytes];
                Buffer.BlockCopy(buffer, 0, payload, 0, result.ReceivedBytes);

                if (_inbound.Writer.TryWrite(new Datagram(payload, result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint)))
                    continue;

                Interlocked.Increment(ref _droppedDatagrams);
                _logger?.Debug($"[UDP] inbound queue full, dropped {result.ReceivedBytes}B from {result.RemoteEndPoint}");
            }
        }

        // 绑定地址解析：通配符 → Any，其余按 IP 或主机名解析
        private static IPAddress ResolveBindAddress(string bindAddress)
        {
            if (string.IsNullOrWhiteSpace(bindAddress) || bindAddress == "*" || bindAddress == "0.0.0.0")
                return IPAddress.Any;
            if (bindAddress == "::")
                return IPAddress.IPv6Any;
            if (IPAddress.TryParse(bindAddress, out IPAddress parsed))
                return parsed;

            IPAddress[] addresses = Dns.GetHostAddresses(bindAddress);
            return addresses.Length > 0 ? addresses[0] : IPAddress.Any;
        }

        #endregion
    }
}
