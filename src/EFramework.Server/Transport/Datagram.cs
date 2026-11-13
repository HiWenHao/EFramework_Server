/*
 * ================================================
 * Describe:      One inbound UDP datagram (payload buffer + source endpoint).
 * CreationTime:  2026-11-13 13:00:00
 * ScriptVersion: 0.2
 * ===============================================
 */

using System.Net;

namespace EasyFramework.Server.Transport
{
    /// <summary>
    /// 一条入站数据报：载荷缓冲 + 有效长度 + 来源端点，生命周期由消费方通过
    /// <see cref="IServerTransport.Release"/> 结束。
    /// <para>One inbound datagram: payload buffer, valid length and source endpoint. Its lifetime ends when
    /// the consumer calls <see cref="IServerTransport.Release"/>.</para>
    /// </summary>
    /// <remarks>
    /// <see cref="Buffer"/> 可能大于 <see cref="Length"/>（池化缓冲按桶大小返回，例如 512 字节的包可能拿到
    /// 1024 字节的数组），因此<b>必须用 <see cref="Length"/> 而不是 <c>Buffer.Length</c></b> 界定有效数据。
    /// <para><see cref="Buffer"/> may be larger than <see cref="Length"/> (pooled buffers come back in
    /// bucket sizes), so <b>always use <see cref="Length"/> rather than <c>Buffer.Length</c></b>.</para>
    /// </remarks>
    public readonly struct Datagram
    {
        #region Properties

        /// <summary>
        /// 数据报内容
        /// <para>Datagram payload</para>
        /// </summary>
        public byte[] Buffer { get; }

        /// <summary>
        /// 有效字节数
        /// <para>Valid byte count</para>
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// 来源端点
        /// <para>Source endpoint</para>
        /// </summary>
        public IPEndPoint RemoteEndPoint { get; }

        /// <summary>
        /// 缓冲是否来自池：为 true 时 <see cref="IServerTransport.Release"/> 会把它归还池
        /// <para>Whether the buffer is pooled: when true, <see cref="IServerTransport.Release"/> returns it</para>
        /// </summary>
        internal bool IsPooled { get; }

        #endregion

        #region Methods

        /// <summary>
        /// 构造一条入站数据报（调用方自行管理缓冲生命周期）。
        /// <para>Create an inbound datagram (the caller owns the buffer lifetime).</para>
        /// </summary>
        /// <param name="buffer">数据报内容<para>Datagram payload</para></param>
        /// <param name="length">有效字节数<para>Valid byte count</para></param>
        /// <param name="remoteEndPoint">来源端点<para>Source endpoint</para></param>
        public Datagram(byte[] buffer, int length, IPEndPoint remoteEndPoint)
            : this(buffer, length, remoteEndPoint, false)
        {
        }

        /// <summary>
        /// 构造一条入站数据报，并标记缓冲是否由传输层池化提供。
        /// <para>Create an inbound datagram, marking whether the transport pooled the buffer.</para>
        /// </summary>
        /// <param name="buffer">数据报内容<para>Datagram payload</para></param>
        /// <param name="length">有效字节数<para>Valid byte count</para></param>
        /// <param name="remoteEndPoint">来源端点<para>Source endpoint</para></param>
        /// <param name="isPooled">缓冲是否来自池<para>Whether the buffer is pooled</para></param>
        internal Datagram(byte[] buffer, int length, IPEndPoint remoteEndPoint, bool isPooled)
        {
            Buffer = buffer;
            Length = length;
            RemoteEndPoint = remoteEndPoint;
            IsPooled = isPooled;
        }

        #endregion
    }
}
