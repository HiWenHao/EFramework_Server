/*
 * ================================================
 * Describe:      Opcode attribute and its reflection resolver (server side).
 * Author:        EFramework.Server
 * CreationTime:  2026-11-13 12:50:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace EasyFramework.Systems.Net
{
    /// <summary>
    /// 标注消息类的 Opcode，注册类型化处理器与回复响应时自动取码。
    /// <para>Marks a message class with its opcode; typed registration and replies resolve it automatically.</para>
    /// </summary>
    /// <example>
    /// [NetOpcode(1001)] public sealed class EchoReq { public string Content { get; set; } }
    /// </example>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class NetOpcodeAttribute : Attribute
    {
        #region Properties

        /// <summary>
        /// 消息操作码
        /// <para>Message opcode</para>
        /// </summary>
        public int Opcode { get; }

        #endregion

        #region Methods

        /// <summary>
        /// 构造消息操作码标注。
        /// <para>Create the opcode marker.</para>
        /// </summary>
        /// <param name="opcode">消息操作码<para>Message opcode</para></param>
        public NetOpcodeAttribute(int opcode)
        {
            Opcode = opcode;
        }

        #endregion
    }

    /// <summary>
    /// 从类型上解析 <see cref="NetOpcodeAttribute"/>（缓存反射结果，避免热路径重复查询）。
    /// <para>Resolves <see cref="NetOpcodeAttribute"/> from a type (reflection result is cached; the lookup
    /// is off the hot path).</para>
    /// </summary>
    public static class NetOpcodeInfo
    {
        #region Fields

        // 类型 → Opcode 缓存。Reply<T>/Push<T> 每条消息都会取码，反射结果必须缓存，
        // 否则每条出站消息都要走一次 GetCustomAttribute（内部会分配 object[]）。
        private static readonly ConcurrentDictionary<Type, int> OpcodeCache = new ConcurrentDictionary<Type, int>();

        #endregion

        #region Methods

        /// <summary>
        /// 取消息类型声明的 Opcode，未标注时返回 0。
        /// <para>Returns the opcode declared on the message type, or 0 when absent.</para>
        /// </summary>
        /// <param name="messageType">消息类型<para>Message type</para></param>
        /// <returns>操作码；未标注为 0<para>Opcode; 0 when not annotated</para></returns>
        public static int GetOpcode(Type messageType)
        {
            if (messageType == null) return 0;
            if (OpcodeCache.TryGetValue(messageType, out int cached)) return cached;

            NetOpcodeAttribute attribute = messageType.GetCustomAttribute<NetOpcodeAttribute>(false);
            int opcode = attribute?.Opcode ?? 0;

            // 特性不可变，未标注的 0 也一并缓存，避免反复反射
            OpcodeCache[messageType] = opcode;
            return opcode;
        }

        /// <summary>
        /// 取消息类型声明的 Opcode，未标注时抛出异常（用于必须在注册期暴露错误的场景）。
        /// <para>Returns the declared opcode and throws when absent (for call sites that must fail fast).</para>
        /// </summary>
        /// <param name="messageType">消息类型<para>Message type</para></param>
        /// <returns>操作码<para>Opcode</para></returns>
        public static int RequireOpcode(Type messageType)
        {
            int opcode = GetOpcode(messageType);
            if (opcode <= 0)
            {
                throw new ArgumentException(
                    $"{messageType?.Name ?? "null"} has no valid [NetOpcode]. " +
                    "Annotate the message type with [NetOpcode(n)] before registering it.");
            }

            return opcode;
        }

        #endregion
    }
}
