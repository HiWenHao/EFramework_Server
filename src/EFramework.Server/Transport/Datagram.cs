/*
 * ================================================
 * Describe:      One inbound UDP datagram (payload copy + source endpoint).
 * CreationTime:  2026-11-13 13:00:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System.Net;

namespace EasyFramework.Server.Transport
{
    /// <summary>
    /// 一条入站数据报：载荷已从接收缓冲复制出来，生命周期独立于接收循环。
    /// <para>One inbound datagram: the payload has been copied out of the receive buffer, so its
    /// lifetime is independent of the receive loop.</para>
    /// </summary>
    /// <remarks>
    /// <see cref="Buffer"/> 按实际长度分配，因此 <see cref="Length"/> 恒等于其长度；拆出字段是为了
    /// 将来换用池化缓冲时不必改调用方。
    /// <para><see cref="Buffer"/> is allocated at the exact length, so <see cref="Length"/> always
    /// equals its size; the separate field keeps call sites stable if pooling is introduced later.</para>
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

        #endregion

        #region Methods

        /// <summary>
        /// 构造一条入站数据报。
        /// <para>Create an inbound datagram.</para>
        /// </summary>
        /// <param name="buffer">数据报内容<para>Datagram payload</para></param>
        /// <param name="length">有效字节数<para>Valid byte count</para></param>
        /// <param name="remoteEndPoint">来源端点<para>Source endpoint</para></param>
        public Datagram(byte[] buffer, int length, IPEndPoint remoteEndPoint)
        {
            Buffer = buffer;
            Length = length;
            RemoteEndPoint = remoteEndPoint;
        }

        #endregion
    }
}
