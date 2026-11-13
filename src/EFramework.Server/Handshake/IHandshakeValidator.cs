/*
 * ================================================
 * Describe:      Handshake validation contract (pluggable token check).
 * CreationTime:  2026-11-13 13:21:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Net;

namespace EasyFramework.Server.Handshake
{
    /// <summary>
    /// 握手校验契约：客户端带 Token 连接时由服务端裁决是否放行。
    /// <para>Handshake validation contract: decides whether a client connecting with a token is admitted.</para>
    /// </summary>
    /// <remarks>
    /// <para>实现方在消息泵线程上被调用，必须快速返回（不要做同步网络/数据库调用）。需要远程校验时
    /// 建议先放行到「待校验」状态，校验完成后再切换会话状态。</para>
    /// <para>Implementations run on the pump thread and must return quickly (no synchronous network or
    /// database calls). For remote validation, admit into a pending state and switch it once the check
    /// completes.</para>
    /// </remarks>
    public interface IHandshakeValidator
    {
        #region Methods

        /// <summary>
        /// 校验握手 Token。
        /// <para>Validate the handshake token.</para>
        /// </summary>
        /// <param name="token">客户端 Token 原文（客户端传字符串时为 UTF-8 字节）<para>Raw token (UTF-8 bytes when the client passed a string)</para></param>
        /// <param name="remoteEndPoint">客户端来源端点（可用于 IP 白名单/风控）<para>Client endpoint (for IP allow-lists or risk control)</para></param>
        /// <returns>通过或拒绝结论<para>Acceptance or rejection verdict</para></returns>
        HandshakeResult Validate(ReadOnlySpan<byte> token, IPEndPoint remoteEndPoint);

        #endregion
    }
}
