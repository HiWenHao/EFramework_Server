/*
 * ================================================
 * Describe:      Demo message contracts (echo request / response).
 * CreationTime:  2026-11-13 13:40:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using EasyFramework.Systems.Net;

namespace EFramework.App.Messages
{
    /// <summary>
    /// 回声请求：客户端把任意文本发上来，用于验证链路与 RPC 通路。
    /// <para>Echo request: the client sends arbitrary text to verify the link and the RPC path.</para>
    /// </summary>
    [NetOpcode(1001)]
    public sealed class EchoReq
    {
        #region Properties

        /// <summary>
        /// 待回显的文本
        /// <para>Text to echo back</para>
        /// </summary>
        public string Content { get; set; }

        #endregion
    }

    /// <summary>
    /// 回声响应：带回服务端时间与会话 Id，便于客户端确认自己落在哪条会话上。
    /// <para>Echo response: carries the server time and session id so the client can tell which
    /// session it landed on.</para>
    /// </summary>
    [NetOpcode(1002)]
    public sealed class EchoRes
    {
        #region Properties

        /// <summary>
        /// 回显文本（原样返回请求内容）
        /// <para>Echoed text (the request content, returned as-is)</para>
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// 服务端处理时刻（Unix 毫秒）
        /// <para>Server processing time (Unix milliseconds)</para>
        /// </summary>
        public long ServerUnixMs { get; set; }

        /// <summary>
        /// 服务端会话 Id（客户端可用它对照服务端日志）
        /// <para>Server session id (matches the server log)</para>
        /// </summary>
        public int SessionId { get; set; }

        #endregion
    }

    /// <summary>
    /// 服务端主动通知：演示单向推送（客户端按 Opcode 注册处理器即可接收，无需回包）。
    /// <para>Server-initiated notice: demonstrates a one-way push (the client registers a handler by
    /// opcode and no reply is needed).</para>
    /// </summary>
    [NetOpcode(1003)]
    public sealed class ServerNotice
    {
        #region Properties

        /// <summary>
        /// 通知文本
        /// <para>Notice text</para>
        /// </summary>
        public string Text { get; set; }

        #endregion
    }
}
