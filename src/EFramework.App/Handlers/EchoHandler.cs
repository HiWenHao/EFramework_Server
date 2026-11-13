/*
 * ================================================
 * Describe:      Demo echo handler (shows typed request -> typed reply).
 * CreationTime:  2026-11-13 13:41:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using EFramework.App.Messages;
using EasyFramework.Server;

namespace EFramework.App.Handlers
{
    /// <summary>
    /// 回声处理器：演示「反序列化请求 → 组装响应 → 按 rpcId 回包」的标准写法。
    /// <para>Echo handler: demonstrates the standard flow of deserialize request, build response and
    /// reply by rpcId.</para>
    /// </summary>
    /// <remarks>
    /// 处理器跑在消息泵线程上，必须快速返回；需要查库/调第三方时请投递到线程池并自行回包。
    /// <para>Handlers run on the pump thread and must return quickly; offload database or third-party calls
    /// to the thread pool and reply from there.</para>
    /// </remarks>
    public sealed class EchoHandler : MessageHandler<EchoReq>
    {
        #region Fields

        private readonly IServerLogger _logger;

        #endregion

        #region Methods

        /// <summary>
        /// 构造回声处理器。
        /// <para>Create the echo handler.</para>
        /// </summary>
        /// <param name="logger">日志输出<para>Log output</para></param>
        public EchoHandler(IServerLogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 处理回声请求并回复 <see cref="EchoRes"/>。
        /// <para>Handle the echo request and reply with <see cref="EchoRes"/>.</para>
        /// </summary>
        /// <param name="message">请求内容<para>Request content</para></param>
        /// <param name="context">消息上下文<para>Message context</para></param>
        public override void Handle(EchoReq message, MessageContext context)
        {
            _logger?.Info($"[ECHO] session={context.SessionId} rpc={context.RpcId} " +
                          $"from={context.RemoteEndPoint} content={message.Content}");

            context.Reply(new EchoRes
            {
                Content = message.Content,
                ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SessionId = context.SessionId,
            });
        }

        #endregion
    }
}
