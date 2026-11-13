/*
 * ================================================
 * Describe:      Per-message context handed to business handlers (reply / push helpers).
 * CreationTime:  2026-11-13 13:15:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Text;
using EasyFramework.Server.Sessions;
using EasyFramework.Systems.Net;

namespace EasyFramework.Server
{
    /// <summary>
    /// 消息上下文：把「来源会话 + 已解码消息包」包成业务可直接使用的对象，并提供回包/推送入口。
    /// <para>Message context: wraps the source session and the decoded packet into something business code
    /// can use directly, with reply and push helpers.</para>
    /// </summary>
    /// <remarks>
    /// <para>上下文仅在本次处理器调用期间有效（底层载荷是本次消息的独立拷贝），不要跨线程或延后使用。</para>
    /// <para>The context is valid only for the duration of this handler call (the payload is a per-message
    /// copy); do not cache it or hand it to another thread.</para>
    /// </remarks>
    public sealed class MessageContext
    {
        #region Fields

        private readonly IMessageSerializer _serializer;

        #endregion

        #region Properties

        /// <summary>
        /// 来源会话（需要会话级信息或主动推送时使用）
        /// <para>Source session (for session-level info or server-initiated pushes)</para>
        /// </summary>
        public NetSession Session { get; }

        /// <summary>
        /// 原始消息包
        /// <para>Raw decoded packet</para>
        /// </summary>
        public NetPacket Packet { get; }

        /// <summary>
        /// 来源会话 Id
        /// <para>Source session id</para>
        /// </summary>
        public int SessionId => Session.SessionId;

        /// <summary>
        /// 消息操作码
        /// <para>Message opcode</para>
        /// </summary>
        public int Opcode => Packet.Opcode;

        /// <summary>
        /// RPC 配对号（0 表示单向消息）
        /// <para>RPC pairing id (0 means a one-way message)</para>
        /// </summary>
        public int RpcId => Packet.RpcId;

        /// <summary>
        /// 是否为请求（带配对号，客户端在等响应）
        /// <para>Whether this is a request (carries a pairing id; the client awaits a response)</para>
        /// </summary>
        public bool IsRequest => Packet.RpcId != 0;

        /// <summary>
        /// 载荷字节（无载荷时为 null）
        /// <para>Payload bytes (null when empty)</para>
        /// </summary>
        public byte[] Payload => Packet.Payload;

        /// <summary>
        /// 载荷长度
        /// <para>Payload length</para>
        /// </summary>
        public int PayloadLength => Packet.PayloadLength;

        /// <summary>
        /// 来源端点（ip:port）
        /// <para>Source endpoint (ip:port)</para>
        /// </summary>
        public string RemoteEndPoint => Session.RemoteEndPointText;

        /// <summary>
        /// 当前配置的序列化器（未配置时为 null）
        /// <para>Configured serializer (null when none is configured)</para>
        /// </summary>
        public IMessageSerializer Serializer => _serializer;

        #endregion

        #region Methods

        /// <summary>
        /// 构造消息上下文。
        /// <para>Create a message context.</para>
        /// </summary>
        /// <param name="session">来源会话<para>Source session</para></param>
        /// <param name="packet">已解码消息包<para>Decoded packet</para></param>
        /// <param name="serializer">序列化器，可为 null<para>Serializer; may be null</para></param>
        public MessageContext(NetSession session, NetPacket packet, IMessageSerializer serializer)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Packet = packet;
            _serializer = serializer;
        }

        /// <summary>
        /// 把载荷按 UTF-8 解码为字符串（无载荷时返回空串）。
        /// <para>Decode the payload as UTF-8 (returns an empty string when there is no payload).</para>
        /// </summary>
        /// <returns>载荷文本<para>Payload text</para></returns>
        public string GetString()
        {
            return PayloadLength > 0 ? Encoding.UTF8.GetString(Payload, 0, PayloadLength) : string.Empty;
        }

        /// <summary>
        /// 反序列化载荷为消息对象（需已配置序列化器）。
        /// <para>Deserialize the payload into a message object (a serializer must be configured).</para>
        /// </summary>
        /// <typeparam name="TMessage">消息类型<para>Message type</para></typeparam>
        /// <returns>反序列化结果；载荷为空时返回 default<para>The result; default when the payload is empty</para></returns>
        public TMessage Deserialize<TMessage>() where TMessage : class
        {
            RequireSerializer();
            return PayloadLength > 0 ? _serializer.Deserialize<TMessage>(Payload, 0, PayloadLength) : null;
        }

        /// <summary>
        /// 回包（带原始载荷，回显请求的 rpcId 以完成客户端的 RPC 等待）。
        /// <para>Reply with a raw payload (echoes the request's rpcId so the client's RPC completes).</para>
        /// </summary>
        /// <param name="opcode">响应操作码<para>Response opcode</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        public void Reply(int opcode, byte[] payload)
        {
            Session.Send(opcode, Packet.RpcId, payload);
        }

        /// <summary>
        /// 回包（按响应类型的 <see cref="NetOpcodeAttribute"/> 取操作码并序列化）。
        /// <para>Reply using the response type's <see cref="NetOpcodeAttribute"/> for the opcode and the
        /// configured serializer for the body.</para>
        /// </summary>
        /// <typeparam name="TResponse">响应消息类型<para>Response message type</para></typeparam>
        /// <param name="response">响应消息<para>Response message</para></param>
        public void Reply<TResponse>(TResponse response) where TResponse : class
        {
            RequireSerializer();
            int opcode = NetOpcodeInfo.RequireOpcode(typeof(TResponse));
            Reply(opcode, _serializer.Serialize(response));
        }

        /// <summary>
        /// 单向推送（不带配对号，客户端按 opcode 路由到处理器）。
        /// <para>One-way push (no pairing id; the client routes it to a handler by opcode).</para>
        /// </summary>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <param name="payload">载荷字节，可为 null<para>Payload bytes; may be null</para></param>
        public void Push(int opcode, byte[] payload)
        {
            Session.Send(opcode, 0, payload);
        }

        /// <summary>
        /// 单向推送（按消息类型的 <see cref="NetOpcodeAttribute"/> 取操作码并序列化）。
        /// <para>One-way push using the message type's <see cref="NetOpcodeAttribute"/> for the opcode.</para>
        /// </summary>
        /// <typeparam name="TMessage">消息类型<para>Message type</para></typeparam>
        /// <param name="message">消息对象<para>Message object</para></param>
        public void Push<TMessage>(TMessage message) where TMessage : class
        {
            RequireSerializer();
            int opcode = NetOpcodeInfo.RequireOpcode(typeof(TMessage));
            Push(opcode, _serializer.Serialize(message));
        }

        // 序列化器缺失是配置错误，直接抛出比静默丢包更容易定位
        private void RequireSerializer()
        {
            if (_serializer != null) return;

            throw new InvalidOperationException(
                "No IMessageSerializer configured. Assign ServerDispatcher.Serializer " +
                "(e.g. new JsonMessageSerializer()) before using typed replies or pushes.");
        }

        #endregion
    }
}
