/*
 * ================================================
 * Describe:      Default permissive handshake validator (accepts every token).
 * CreationTime:  2026-11-13 13:21:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Net;

namespace EasyFramework.Server.Handshake
{
    /// <summary>
    /// 放行所有握手的默认校验器：用于内网联调与「先跑通再收紧」的接入阶段。
    /// <para>Permissive default validator: for LAN debugging and the "make it work, then tighten it"
    /// integration stage.</para>
    /// </summary>
    /// <remarks>
    /// 生产环境务必替换为实现 <see cref="IHandshakeValidator"/> 的鉴权实现（校验 Token 签名 / 有效期 /
    /// 账号绑定），否则任何人猜中端口即可接入。
    /// <para>Always replace it in production with a real <see cref="IHandshakeValidator"/> (signature,
    /// expiry, account binding); otherwise anyone who guesses the port gets in.</para>
    /// </remarks>
    public sealed class AcceptAllHandshakeValidator : IHandshakeValidator
    {
        #region Methods

        /// <summary>
        /// 无条件放行。
        /// <para>Accept unconditionally.</para>
        /// </summary>
        /// <param name="token">客户端 Token（忽略）<para>Client token (ignored)</para></param>
        /// <param name="remoteEndPoint">客户端来源端点（忽略）<para>Client endpoint (ignored)</para></param>
        /// <returns>始终为通过<para>Always an acceptance</para></returns>
        public HandshakeResult Validate(ReadOnlySpan<byte> token, IPEndPoint remoteEndPoint)
        {
            return HandshakeResult.Success;
        }

        #endregion
    }
}
