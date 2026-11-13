/*
 * ================================================
 * Describe:      Opcode-based dispatcher with exception containment.
 * CreationTime:  2026-11-13 13:17:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Threading;
using EasyFramework.Server.Sessions;
using EasyFramework.Systems.Net;

namespace EasyFramework.Server
{
    /// <summary>
    /// 服务端消息分发器：Opcode → 处理器，处理器异常一律就地捕获，绝不冒泡进消息泵。
    /// <para>Server dispatcher: opcode to handler, with handler exceptions contained locally so they never
    /// bubble into the pump.</para>
    /// </summary>
    /// <remarks>
    /// <para>处理器在消息泵线程上同步执行：可以安全读写会话状态，但<b>禁止阻塞</b>（不要做同步 IO、
    /// 不要 Thread.Sleep）。耗时逻辑请自行投递到线程池，并用 <c>MessageContext.Session</c> 回包。</para>
    /// <para>Handlers run synchronously on the pump thread: they may read and write session state safely but
    /// <b>must not block</b> (no synchronous IO, no Thread.Sleep). Offload slow work and reply through
    /// <c>MessageContext.Session</c>.</para>
    /// </remarks>
    public sealed class ServerDispatcher
    {
        #region Fields

        private readonly Dictionary<int, Action<MessageContext>> _handlers = new Dictionary<int, Action<MessageContext>>();
        private readonly IServerLogger _logger;

        private long _handledCount;
        private long _failedCount;

        #endregion

        #region Properties

        /// <summary>
        /// 序列化器：类型化处理器与 <c>Reply&lt;T&gt;</c> 依赖它；只用原始字节通信时可留空。
        /// <para>Serializer required by typed handlers and <c>Reply&lt;T&gt;</c>; may stay null when only raw
        /// byte payloads are used.</para>
        /// </summary>
        public IMessageSerializer Serializer { get; set; }

        /// <summary>
        /// 已注册的处理器数量
        /// <para>Number of registered handlers</para>
        /// </summary>
        public int HandlerCount => _handlers.Count;

        /// <summary>
        /// 累计成功处理的处理器调用次数
        /// <para>Total handler invocations that completed successfully</para>
        /// </summary>
        public long HandledCount => Interlocked.Read(ref _handledCount);

        /// <summary>
        /// 累计抛出异常的处理器调用次数
        /// <para>Total handler invocations that threw</para>
        /// </summary>
        public long FailedCount => Interlocked.Read(ref _failedCount);

        #endregion

        #region Methods

        /// <summary>
        /// 构造分发器。
        /// <para>Create the dispatcher.</para>
        /// </summary>
        /// <param name="logger">日志输出<para>Log output</para></param>
        /// <param name="serializer">序列化器，可为 null<para>Serializer; may be null</para></param>
        public ServerDispatcher(IServerLogger logger = null, IMessageSerializer serializer = null)
        {
            _logger = logger;
            Serializer = serializer;
        }

        /// <summary>
        /// 注册原始处理器（直接拿到已解码的消息包，不经过序列化）。
        /// <para>Register a raw handler (receives the decoded packet, no serialization).</para>
        /// </summary>
        /// <param name="opcode">消息操作码，须大于 0<para>Message opcode; must be greater than 0</para></param>
        /// <param name="handler">处理回调<para>Handle callback</para></param>
        public void Register(int opcode, Action<MessageContext> handler)
        {
            if (opcode <= 0)
                throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Opcode must be greater than 0.");
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            bool replaced = _handlers.ContainsKey(opcode);
            _handlers[opcode] = handler;

            if (replaced) _logger?.Warn($"[DISPATCH] handler for opcode {opcode} replaced");
        }

        /// <summary>
        /// 注册类型化处理器：载荷按 <see cref="Serializer"/> 反序列化后交给 <typeparamref name="THandler"/>。
        /// <para>Register a typed handler: the payload is deserialized via <see cref="Serializer"/> before
        /// being passed to <typeparamref name="THandler"/>.</para>
        /// </summary>
        /// <typeparam name="TMessage">消息类型<para>Message type</para></typeparam>
        /// <typeparam name="THandler">处理器类型（需可无参构造）<para>Handler type (must be constructible without arguments)</para></typeparam>
        /// <param name="handler">处理器实例；传 null 时反射创建<para>Handler instance; created via reflection when null</para></param>
        public void Register<TMessage, THandler>(THandler handler = null)
            where TMessage : class
            where THandler : MessageHandler<TMessage>
        {
            handler ??= Activator.CreateInstance<THandler>();

            int opcode = handler.Opcode;
            if (opcode <= 0)
            {
                throw new ArgumentException(
                    $"Handler {typeof(THandler).Name} resolved opcode 0. " +
                    $"Annotate {typeof(TMessage).Name} with [NetOpcode(n)] or override Opcode.", nameof(handler));
            }

            Register(opcode, context =>
            {
                TMessage message = context.Deserialize<TMessage>();
                if (message == null)
                {
                    _logger?.Warn($"[DISPATCH] opcode {opcode} carried an empty payload, skipped (session {context.SessionId})");
                    return;
                }

                handler.Handle(message, context);
            });

            _logger?.Debug($"[DISPATCH] {typeof(THandler).Name} -> opcode {opcode} ({typeof(TMessage).Name})");
        }

        /// <summary>
        /// 注销处理器。
        /// <para>Unregister a handler.</para>
        /// </summary>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <returns>是否注销成功<para>Whether a handler was removed</para></returns>
        public bool Unregister(int opcode)
        {
            return _handlers.Remove(opcode);
        }

        /// <summary>
        /// 判断某操作码是否已注册处理器。
        /// <para>Whether a handler is registered for the opcode.</para>
        /// </summary>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        /// <returns>是否已注册<para>Whether it is registered</para></returns>
        public bool IsRegistered(int opcode)
        {
            return _handlers.ContainsKey(opcode);
        }

        /// <summary>
        /// 分发一条消息：未注册的 Opcode 记 Debug 日志并丢弃，处理器异常记 Error 日志并计入失败数。
        /// <para>Dispatch one message: unregistered opcodes are logged at Debug and dropped; handler
        /// exceptions are logged at Error and counted.</para>
        /// </summary>
        /// <param name="session">来源会话<para>Source session</para></param>
        /// <param name="packet">已解码消息包<para>Decoded packet</para></param>
        public void Dispatch(NetSession session, NetPacket packet)
        {
            if (!_handlers.TryGetValue(packet.Opcode, out Action<MessageContext> handler))
            {
                _logger?.Debug($"[DISPATCH] no handler for opcode {packet.Opcode} (session {session.SessionId})");
                return;
            }

            var context = new MessageContext(session, packet, Serializer);
            try
            {
                handler(context);
                Interlocked.Increment(ref _handledCount);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failedCount);
                _logger?.Error($"[DISPATCH] handler threw (opcode={packet.Opcode}, rpcId={packet.RpcId}, " +
                               $"session={session.SessionId})", e);
            }
        }

        #endregion
    }
}
