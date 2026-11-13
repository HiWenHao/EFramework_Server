/*
 * ================================================
 * Describe:      Handshake verdict returned by a validator (1-byte code + UTF-8 reason).
 * CreationTime:  2026-11-13 13:20:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;

namespace EasyFramework.Server.Handshake
{
    /// <summary>
    /// 握手结论：结果码 + 失败原因，直接映射到客户端 <c>HandshakeResult</c> 包的载荷格式。
    /// <para>Handshake verdict: result code plus failure reason, mapped directly onto the client's
    /// <c>HandshakeResult</c> payload format.</para>
    /// </summary>
    /// <remarks>
    /// 载荷格式与客户端 <c>NetConnection.OnHandshakePacket</c> 对齐：<c>[code:1 byte][reason:UTF-8]</c>。
    /// 因此结果码必须落在 0~255。
    /// <para>The payload matches the client's <c>NetConnection.OnHandshakePacket</c>:
    /// <c>[code:1 byte][reason:UTF-8]</c>, so the code must fit in 0~255.</para>
    /// </remarks>
    public readonly struct HandshakeResult
    {
        #region Properties

        /// <summary>
        /// 结果码（0 = 通过，非 0 为业务定义的原因码）
        /// <para>Result code (0 = accepted; non-zero is a business-defined reason)</para>
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// 失败原因（通过时为空）
        /// <para>Failure reason (empty when accepted)</para>
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// 是否通过
        /// <para>Whether the handshake was accepted</para>
        /// </summary>
        public bool IsSuccess => Code == 0;

        /// <summary>
        /// 通过结论（结果码 0、无原因）
        /// <para>An acceptance verdict (code 0, no reason)</para>
        /// </summary>
        public static HandshakeResult Success => new HandshakeResult(0, string.Empty);

        #endregion

        #region Methods

        /// <summary>
        /// 构造握手结论。
        /// <para>Create a handshake verdict.</para>
        /// </summary>
        /// <param name="code">结果码（0~255）<para>Result code (0~255)</para></param>
        /// <param name="message">失败原因，可为空<para>Failure reason; may be empty</para></param>
        public HandshakeResult(int code, string message)
        {
            if (code is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(code), code, "Handshake code must fit in one byte (0~255).");

            Code = code;
            Message = message ?? string.Empty;
        }

        /// <summary>
        /// 构造拒绝结论（结果码自动收敛到 1~255，避免误传 0 变成「通过」）。
        /// <para>Create a rejection verdict (the code is clamped to 1~255 so a stray 0 cannot turn into
        /// an acceptance).</para>
        /// </summary>
        /// <param name="code">业务原因码（小于 1 时按 1 处理）<para>Business reason code (values below 1 become 1)</para></param>
        /// <param name="message">失败原因<para>Failure reason</para></param>
        /// <returns>拒绝结论<para>A rejection verdict</para></returns>
        public static HandshakeResult Reject(int code, string message)
        {
            int normalized = code is < 1 or > 255 ? 1 : code;
            return new HandshakeResult(normalized, message);
        }

        /// <summary>
        /// 返回可读描述（日志用）。
        /// <para>Returns a readable description (for logging).</para>
        /// </summary>
        /// <returns>描述文本<para>Description text</para></returns>
        public override string ToString()
        {
            return IsSuccess ? "accepted" : $"rejected(code={Code}, reason={Message})";
        }

        #endregion
    }
}
