/*
 * ================================================
 * Describe:      Transport abstraction for the server side (UDP today, TCP/WebSocket later).
 * CreationTime:  2026-11-13 13:00:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Net;

namespace EasyFramework.Server.Transport
{
    /// <summary>
    /// 服务端传输抽象：一条「收包队列 + 定点发送」的通道，与具体协议解耦。
    /// <para>Server transport abstraction: a "receive queue plus addressed send" channel, decoupled
    /// from the concrete protocol.</para>
    /// </summary>
    /// <remarks>
    /// <para>收包必须是<b>非阻塞</b>的：消息泵在固定节拍里把队列抽干，IO 线程只负责投递，
    /// 这样所有 KCP 与业务逻辑都跑在同一个线程上，会话状态无需加锁。</para>
    /// <para>Receiving must be <b>non-blocking</b>: the pump drains the queue on a fixed tick and the IO
    /// thread only enqueues. All KCP and business logic then runs on a single thread, so session
    /// state needs no locking.</para>
    /// <para>换成 WebSocket / TCP 时实现本接口即可，会话层与分发层零改动。</para>
    /// <para>Implement this interface for WebSocket / TCP and the session and dispatch layers stay untouched.</para>
    /// </remarks>
    public interface IServerTransport : IDisposable
    {
        #region Properties

        /// <summary>
        /// 是否已启动
        /// <para>Whether the transport is running</para>
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// 实际监听端点（端口为 0 启动后这里给出系统分配的真实端口）
        /// <para>Effective listen endpoint (after starting on port 0 this reports the OS-assigned port)</para>
        /// </summary>
        IPEndPoint LocalEndPoint { get; }

        /// <summary>
        /// 因队列满被丢弃的入站数据报数
        /// <para>Inbound datagrams dropped because the queue was full</para>
        /// </summary>
        long DroppedDatagrams { get; }

        #endregion

        #region Methods

        /// <summary>
        /// 开始监听；绑定失败返回 false（不抛异常）。
        /// <para>Start listening; returns false on bind failure (does not throw).</para>
        /// </summary>
        /// <returns>是否启动成功<para>Whether it started</para></returns>
        bool Start();

        /// <summary>
        /// 非阻塞取出一条入站数据报。
        /// <para>Try to dequeue one inbound datagram without blocking.</para>
        /// </summary>
        /// <param name="datagram">取出的数据报<para>The dequeued datagram</para></param>
        /// <returns>是否有数据<para>Whether a datagram was available</para></returns>
        bool TryReceive(out Datagram datagram);

        /// <summary>
        /// 向指定端点发送一段数据。
        /// <para>Send a byte range to the given endpoint.</para>
        /// </summary>
        /// <param name="data">数据缓冲<para>Data buffer</para></param>
        /// <param name="offset">起始偏移<para>Start offset</para></param>
        /// <param name="count">字节数<para>Byte count</para></param>
        /// <param name="remoteEndPoint">目标端点<para>Target endpoint</para></param>
        /// <returns>实际发送字节数；失败返回 -1<para>Bytes sent; -1 on failure</para></returns>
        int Send(byte[] data, int offset, int count, IPEndPoint remoteEndPoint);

        /// <summary>
        /// 归还数据报缓冲：消费完必须调用且只调用一次（池化实现把数组还池，非池化实现为空操作）。
        /// <para>Release the datagram buffer: call exactly once after consuming it (pooled implementations
        /// return the array to the pool, others are no-ops).</para>
        /// </summary>
        /// <remarks>
        /// 刻意做成显式方法而非 <see cref="IDisposable"/>：<see cref="Datagram"/> 是结构体，装箱会引入新分配。
        /// 消息泵在每个数据报处理完后用 finally 保证调用一次。
        /// <para>Deliberately an explicit method rather than <see cref="IDisposable"/>: boxing a
        /// <see cref="Datagram"/> would allocate. The pump guarantees one call via finally.</para>
        /// </remarks>
        /// <param name="datagram">待归还的数据报<para>Datagram to release</para></param>
        void Release(Datagram datagram);

        /// <summary>
        /// 停止监听并释放 IO 资源（可重复调用）。
        /// <para>Stop listening and release IO resources (idempotent).</para>
        /// </summary>
        void Stop();

        #endregion
    }
}
