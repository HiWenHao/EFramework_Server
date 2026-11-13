/*
 * ================================================
 * Describe:      Monotonic clock shared by the runtime (immune to wall-clock changes).
 * CreationTime:  2026-11-13 12:58:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System.Diagnostics;

namespace EasyFramework.Server
{
    /// <summary>
    /// 单调时钟（秒）：统一时间源，避免系统时间被校准时出现负时长或超时误判。
    /// <para>Monotonic clock in seconds: the single time source, immune to wall-clock corrections
    /// that would otherwise produce negative durations or false timeouts.</para>
    /// </summary>
    internal static class ServerClock
    {
        #region Properties

        /// <summary>
        /// 当前单调时刻（秒）
        /// <para>Current monotonic timestamp in seconds</para>
        /// </summary>
        public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        #endregion
    }
}
