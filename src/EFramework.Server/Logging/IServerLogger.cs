/*
 * ================================================
 * Describe:      Server logging abstraction (swappable for Serilog / NLog / Unity Debug).
 * CreationTime:  2026-11-13 12:55:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;

namespace EasyFramework.Server
{
    /// <summary>
    /// 日志抽象：框架只依赖该接口，接入 Serilog / NLog / 云日志时替换实现即可。
    /// <para>Logging abstraction: the framework depends only on this interface; swap the implementation
    /// to plug in Serilog / NLog / cloud logging.</para>
    /// </summary>
    public interface IServerLogger
    {
        #region Properties

        /// <summary>
        /// 当前输出阈值（低于该级别的调用直接返回，不产生字符串）
        /// <para>Current output threshold (calls below it return immediately, without formatting strings)</para>
        /// </summary>
        LogLevel Level { get; set; }

        #endregion

        #region Methods

        /// <summary>
        /// 输出诊断细节
        /// <para>Write diagnostic detail</para>
        /// </summary>
        /// <param name="message">日志内容<para>Log message</para></param>
        void Debug(string message);

        /// <summary>
        /// 输出常规信息
        /// <para>Write informational output</para>
        /// </summary>
        /// <param name="message">日志内容<para>Log message</para></param>
        void Info(string message);

        /// <summary>
        /// 输出告警
        /// <para>Write a warning</para>
        /// </summary>
        /// <param name="message">日志内容<para>Log message</para></param>
        void Warn(string message);

        /// <summary>
        /// 输出错误（可携带异常堆栈）
        /// <para>Write an error (optionally with an exception stack trace)</para>
        /// </summary>
        /// <param name="message">日志内容<para>Log message</para></param>
        /// <param name="exception">关联异常，可为 null<para>Related exception; may be null</para></param>
        void Error(string message, Exception exception = null);

        #endregion
    }
}
