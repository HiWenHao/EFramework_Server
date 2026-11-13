/*
 * ================================================
 * Describe:      Server-side session state machine.
 * CreationTime:  2026-11-13 13:05:00
 * ScriptVersion: 0.1
 * ===============================================
 */

namespace EasyFramework.Server.Sessions
{
    /// <summary>
    /// 服务端会话状态：握手态只放行握手包，就绪态才进入业务分发。
    /// <para>Server session state: the handshaking state only admits handshake packets; business
    /// dispatch starts once ready.</para>
    /// </summary>
    public enum NetSessionState
    {
        /// <summary>
        /// 等待握手（仅当 <c>ServerOptions.RequireHandshake</c> 为 true 时出现）
        /// <para>Awaiting handshake (only when <c>ServerOptions.RequireHandshake</c> is true)</para>
        /// </summary>
        Handshaking = 0,

        /// <summary>
        /// 就绪，可正常收发业务消息
        /// <para>Ready; business messages flow normally</para>
        /// </summary>
        Ready = 1,

        /// <summary>
        /// 已关闭（等待本轮扫除回收）
        /// <para>Closed (waiting for the next sweep to reclaim it)</para>
        /// </summary>
        Closed = 2,
    }
}
