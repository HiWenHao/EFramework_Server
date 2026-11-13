/*
 * ================================================
 * Describe:      Typed message handler base (opcode resolved from [NetOpcode]).
 * CreationTime:  2026-11-13 13:16:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using EasyFramework.Systems.Net;

namespace EasyFramework.Server
{
    /// <summary>
    /// 类型化消息处理器基类：操作码取自消息类上的 <see cref="NetOpcodeAttribute"/>，业务只写处理逻辑。
    /// <para>Typed handler base: the opcode comes from <see cref="NetOpcodeAttribute"/> on the message type,
    /// so business code only writes the handling logic.</para>
    /// </summary>
    /// <typeparam name="TMessage">消息类型<para>Message type</para></typeparam>
    public abstract class MessageHandler<TMessage> where TMessage : class
    {
        #region Properties

        /// <summary>
        /// 处理的消息操作码（默认取消息类上的 <see cref="NetOpcodeAttribute"/>）
        /// <para>Handled opcode (defaults to the message type's <see cref="NetOpcodeAttribute"/>)</para>
        /// </summary>
        public virtual int Opcode => NetOpcodeInfo.GetOpcode(typeof(TMessage));

        #endregion

        #region Methods

        /// <summary>
        /// 处理已反序列化的消息。
        /// <para>Handle the deserialized message.</para>
        /// </summary>
        /// <param name="message">消息对象<para>Message object</para></param>
        /// <param name="context">消息上下文（含来源会话与回包入口）<para>Message context (source session and reply entry)</para></param>
        public abstract void Handle(TMessage message, MessageContext context);

        #endregion
    }
}
