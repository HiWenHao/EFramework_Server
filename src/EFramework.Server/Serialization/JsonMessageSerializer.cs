/*
 * ================================================
 * Describe:      System.Text.Json serializer matching the client-side field/property layout.
 * CreationTime:  2026-11-13 13:23:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Text.Json;
using EasyFramework.Systems.Net;

namespace EasyFramework.Server.Serialization
{
    /// <summary>
    /// 默认 JSON 序列化实现：字段与属性都参与序列化，不做命名改写（与消息类字段名逐字符一致）。
    /// <para>Default JSON serializer: both fields and properties are included and no naming policy is
    /// applied, so wire names match the message members character for character.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>与客户端必须同构</b>：Unity 侧若用 Newtonsoft.Json，请设置
    /// <c>DefaultSettings = MemberSerialization.OptIn/OptOut</c> 且保留字段原名、不要开 camelCase；
    /// 否则会出现「服务端能解析、客户端解析为空」的静默错位。</para>
    /// <para><b>Must stay isomorphic with the client</b>: if the Unity side uses Newtonsoft.Json, keep the
    /// original member names and avoid camelCase, otherwise payloads silently deserialize into empty
    /// objects on one side.</para>
    /// </remarks>
    public sealed class JsonMessageSerializer : IMessageSerializer
    {
        #region Fields

        private readonly JsonSerializerOptions _options;

        #endregion

        #region Methods

        /// <summary>
        /// 构造 JSON 序列化器。
        /// <para>Create the JSON serializer.</para>
        /// </summary>
        /// <param name="options">自定义选项；传 null 使用与客户端对齐的默认选项<para>Custom options; null uses the client-aligned defaults</para></param>
        public JsonMessageSerializer(JsonSerializerOptions options = null)
        {
            _options = options ?? CreateDefaultOptions();
        }

        /// <summary>
        /// 创建默认选项：包含字段、忽略大小写不匹配、不做命名改写。
        /// <para>Create the default options: include fields, no case-insensitive matching, no naming policy.</para>
        /// </summary>
        /// <returns>默认选项<para>Default options</para></returns>
        public static JsonSerializerOptions CreateDefaultOptions()
        {
            return new JsonSerializerOptions
            {
                IncludeFields = true,
                PropertyNameCaseInsensitive = false,
                PropertyNamingPolicy = null,
                WriteIndented = false,
            };
        }

        /// <summary>
        /// 序列化消息对象为 UTF-8 JSON（null 返回空载荷）。
        /// <para>Serialize a message to UTF-8 JSON (null yields an empty payload).</para>
        /// </summary>
        /// <typeparam name="T">消息类型<para>Message type</para></typeparam>
        /// <param name="message">消息对象<para>Message object</para></param>
        /// <returns>JSON 字节<para>JSON bytes</para></returns>
        public byte[] Serialize<T>(T message)
        {
            return message == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(message, _options);
        }

        /// <summary>
        /// 从载荷反序列化消息对象（零拷贝读取，不额外分配中间字节数组）。
        /// <para>Deserialize a message from the payload (reads in place without an intermediate copy).</para>
        /// </summary>
        /// <typeparam name="T">消息类型<para>Message type</para></typeparam>
        /// <param name="payload">载荷字节<para>Payload bytes</para></param>
        /// <param name="offset">起始偏移<para>Start offset</para></param>
        /// <param name="count">字节数<para>Byte count</para></param>
        /// <returns>消息对象；载荷为空时返回 default<para>The message; default when the payload is empty</para></returns>
        public T Deserialize<T>(byte[] payload, int offset, int count)
        {
            if (payload == null || count <= 0) return default;

            return JsonSerializer.Deserialize<T>(new ReadOnlySpan<byte>(payload, offset, count), _options);
        }

        #endregion
    }
}
