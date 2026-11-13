/*
 * ================================================
 * Describe:      Console logger with level filtering and thread-safe output.
 * CreationTime:  2026-11-13 12:55:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;

namespace EasyFramework.Server
{
    /// <summary>
    /// 控制台日志实现：带时间戳与级别标签，多线程写入串行化（收包线程与消息泵都会写）。
    /// <para>Console logger: timestamped level tags, serialized writes (both the receive thread
    /// and the pump write to it).</para>
    /// </summary>
    public sealed class ConsoleServerLogger : IServerLogger
    {
        #region Fields

        // Console 在多线程下按行并不原子，统一加锁避免日志交错
        private readonly object _writeGate = new object();

        #endregion

        #region Properties

        /// <summary>
        /// 当前输出阈值
        /// <para>Current output threshold</para>
        /// </summary>
        public LogLevel Level { get; set; } = LogLevel.Info;

        #endregion

        #region Methods

        /// <summary>
        /// 输出诊断细节
        /// <para>Write diagnostic detail</para>
        /// </summary>
        /// <param name="message">日志内容<para>Log message</para></param>
        public void Debug(string message)
        {
            Write(LogLevel.Debug, "DBG", message, null);
        }

        /// <summary>
        /// 输出常规信息
        /// <para>Write informational output</para>
        /// </summary>
        /// <param name="message">日志内容<para>Log message</para></param>
        public void Info(string message)
        {
            Write(LogLevel.Info, "INF", message, null);
        }

        /// <summary>
        /// 输出告警
        /// <para>Write a warning</para>
        /// </summary>
        /// <param name="message">日志内容<para>Log message</para></param>
        public void Warn(string message)
        {
            Write(LogLevel.Warn, "WRN", message, null);
        }

        /// <summary>
        /// 输出错误（异常堆栈另起一行）
        /// <para>Write an error (the stack trace goes on its own line)</para>
        /// </summary>
        /// <param name="message">日志内容<para>Log message</para></param>
        /// <param name="exception">关联异常，可为 null<para>Related exception; may be null</para></param>
        public void Error(string message, Exception exception = null)
        {
            Write(LogLevel.Error, "ERR", message, exception);
        }

        // 统一出口：级别过滤 + 时间戳 + 串行写入
        private void Write(LogLevel level, string tag, string message, Exception exception)
        {
            if (level < Level || Level == LogLevel.None) return;

            string line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}";
            lock (_writeGate)
            {
                Console.WriteLine(line);
                if (exception != null) Console.WriteLine(exception);
            }
        }

        #endregion
    }
}
