/*
 * ================================================
 * Describe:      Framework-level protocol handlers (heartbeat echo + handshake verdict).
 * CreationTime:  2026-11-13 13:32:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Text;
using EasyFramework.Server.Handshake;
using EasyFramework.Server.Sessions;
using EasyFramework.Systems.Net;

namespace EasyFramework.Server
{
    /// <summary>
    /// 框架内置处理器：心跳回显与握手裁决，属于协议层职责，与业务处理器分开装配。
    /// <para>Framework built-in handlers: heartbeat echo and handshake verdict. They belong to the protocol
    /// layer and are wired separately from business handlers.</para>
    /// </summary>
    /// <remarks>
    /// 业务可用相同 Opcode 重新注册以覆盖（例如自定义 Ping 语义），注册顺序决定生效者。
    /// <para>Business code may re-register the same opcode to override (for example a custom Ping
    /// semantic); the last registration wins.</para>
    /// </remarks>
    internal sealed class BuiltInMessageHandlers
    {
        #region Fields

        // 握手结果载荷的结果码占 1 字节（与客户端 OnHandshakePacket 对齐）
        private const int HandshakeCodeSize = 1;

        private readonly IHandshakeValidator _handshakeValidator;
        private readonly IServerLogger _logger;

        #endregion

        #region Methods

        /// <summary>
        /// 构造内置处理器集合。
        /// <para>Create the built-in handler set.</para>
        /// </summary>
        /// <param name="handshakeValidator">握手校验器<para>Handshake validator</para></param>
        /// <param name="logger">日志输出<para>Log output</para></param>
        public BuiltInMessageHandlers(IHandshakeValidator handshakeValidator, IServerLogger logger)
        {
            _handshakeValidator = handshakeValidator ?? throw new ArgumentNullException(nameof(handshakeValidator));
            _logger = logger;
        }

        /// <summary>
        /// 把内置处理器注册到分发器。
        /// <para>Register the built-in handlers on the dispatcher.</para>
        /// </summary>
        /// <param name="dispatcher">目标分发器<para>Target dispatcher</para></param>
        public void Register(ServerDispatcher dispatcher)
        {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));

            dispatcher.Register(NetOpcode.Ping, HandlePing);
            dispatcher.Register(NetOpcode.Handshake, HandleHandshake);
        }

        // Ping → 原样回显载荷为 Pong：客户端靠比对回显的时间戳算 RTT
        private void HandlePing(MessageContext context)
        {
            context.Session.Send(NetOpcode.Pong, context.Payload);
        }

        // 握手：校验 Token → 回执结果 → 通过则放行，拒绝则延迟关闭（先让回执冲刷出去）
        private void HandleHandshake(MessageContext context)
        {
            ReadOnlySpan<byte> token = context.PayloadLength > 0
                ? new ReadOnlySpan<byte>(context.Payload, 0, context.PayloadLength)
                : ReadOnlySpan<byte>.Empty;

            HandshakeResult result;
            try
            {
                result = _handshakeValidator.Validate(token, context.Session.RemoteEndPoint);
            }
            catch (Exception e)
            {
                _logger?.Error($"[SESSION {context.SessionId}] handshake validator threw.", e);
                result = HandshakeResult.Reject(1, "Handshake validator failed");
            }

            context.Session.Send(NetOpcode.HandshakeResult, BuildHandshakePayload(result));

            if (result.IsSuccess)
            {
                context.Session.State = NetSessionState.Ready;
                _logger?.Info($"[SESSION {context.SessionId}] handshake accepted ({context.RemoteEndPoint})");
                return;
            }

            _logger?.Warn($"[SESSION {context.SessionId}] handshake {result} ({context.RemoteEndPoint})");
            context.Session.RequestClose($"Handshake {result}");
        }

        // 结果载荷格式必须与客户端一致：[code:1][reason:UTF-8]
        private static byte[] BuildHandshakePayload(HandshakeResult result)
        {
            int reasonBytes = string.IsNullOrEmpty(result.Message) ? 0 : Encoding.UTF8.GetByteCount(result.Message);
            var payload = new byte[HandshakeCodeSize + reasonBytes];
            payload[0] = (byte)result.Code;

            if (reasonBytes > 0)
            {
                Encoding.UTF8.GetBytes(result.Message, 0, result.Message.Length, payload, HandshakeCodeSize);
            }

            return payload;
        }

        #endregion
    }
}
