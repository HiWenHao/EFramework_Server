/*
 * ================================================
 * Describe:      Log severity used by the server runtime.
 * CreationTime:  2026-11-13 12:55:00
 * ScriptVersion: 0.1
 * ===============================================
 */

namespace EasyFramework.Server
{
    /// <summary>
    /// 日志级别：数值越大越严重，低于阈值的日志被丢弃。
    /// <para>Log severity: higher is more severe; entries below the configured threshold are dropped.</para>
    /// </summary>
    public enum LogLevel
    {
        /// <summary>
        /// 诊断细节（每包每会话级别，生产环境关闭）
        /// <para>Diagnostic detail (per-packet/per-session; off in production)</para>
        /// </summary>
        Debug = 0,

        /// <summary>
        /// 生命周期与关键业务事件
        /// <para>Lifecycle and key business events</para>
        /// </summary>
        Info = 1,

        /// <summary>
        /// 可恢复异常（丢包、非法帧、未注册 Opcode）
        /// <para>Recoverable anomalies (drops, malformed frames, unregistered opcodes)</para>
        /// </summary>
        Warn = 2,

        /// <summary>
        /// 需要人工介入的错误（处理器抛出、绑定失败）
        /// <para>Errors that need attention (handler threw, bind failed)</para>
        /// </summary>
        Error = 3,

        /// <summary>
        /// 完全关闭日志
        /// <para>Logging fully disabled</para>
        /// </summary>
        None = 4,
    }
}
