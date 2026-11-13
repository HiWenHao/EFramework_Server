/*
 * ================================================
 * Describe:      Raise the OS timer resolution so a sub-16ms tick can actually be honoured.
 * CreationTime:  2026-11-13 15:32:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EasyFramework.Server
{
    /// <summary>
    /// 临时提高系统计时器精度，让低于 16ms 的节拍真的能按配置执行。
    /// <para>Temporarily raise the OS timer resolution so ticks shorter than ~16ms are really honoured.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要</b>：Windows 默认计时器周期约 15.6ms，<c>WaitHandle.WaitOne(10)</c> 实际会睡
    /// 15~16ms —— 实测「配置 10ms、实际 15.78ms」，于是所有回包白白多等几毫秒，而且
    /// <c>TickIntervalMs</c> 这个参数就不再可信。Linux 的 nanosleep + hrtimer 没有这个问题，
    /// 所以这里只在 Windows 上生效。</para>
    /// <para><b>Why</b>: Windows' default timer period is ~15.6ms, so WaitOne(10) really sleeps 15-16ms —
    /// measured "configured 10ms, actual 15.78ms". Every reply pays a few extra milliseconds and
    /// TickIntervalMs stops being trustworthy. Linux nanosleep + hrtimer does not have this problem, so the
    /// raise only happens on Windows.</para>
    /// <para><b>代价</b>：进程存活期间系统计时器精度被整体提高（所有进程受益，也意味着更少的省电机会）。
    /// 因此做成可关闭：<see cref="ServerOptions.HighResolutionTimer"/>。</para>
    /// <para><b>Cost</b>: the system-wide timer resolution stays raised for the process lifetime (other
    /// processes benefit too, at the cost of fewer power-saving opportunities). Hence the opt-out via
    /// ServerOptions.HighResolutionTimer.</para>
    /// </remarks>
    internal sealed class TimerResolutionScope : IDisposable
    {
        #region Fields

        private readonly uint _periodMs;
        private readonly bool _raised;

        #endregion

        #region Methods

        private TimerResolutionScope(uint periodMs, bool raised)
        {
            _periodMs = periodMs;
            _raised = raised;
        }

        /// <summary>
        /// 尝试把计时器精度提到指定毫秒（非 Windows 或失败时返回一个"什么都不做"的实例）。
        /// <para>Try to raise the timer resolution (returns a no-op scope on non-Windows or on failure).</para>
        /// </summary>
        /// <param name="periodMs">目标周期（毫秒）<para>Target period in ms</para></param>
        /// <returns>作用域；释放时恢复原精度<para>A scope that restores the previous resolution when disposed</para></returns>
        public static TimerResolutionScope TryRaise(uint periodMs)
        {
            if (periodMs == 0) return new TimerResolutionScope(0, false);
            if (!OperatingSystem.IsWindows()) return new TimerResolutionScope(periodMs, false);

            return new TimerResolutionScope(periodMs, TimeBeginPeriod(periodMs) == 0);
        }

        /// <summary>
        /// 是否真的提高了精度（供日志说明实际生效情况）。
        /// <para>Whether the resolution was really raised (for logging).</para>
        /// </summary>
        public bool Raised => _raised;

        public void Dispose()
        {
            // 显式判断平台：分析器要求调用受 [SupportedOSPlatform] 限制的 API 前做守卫
            if (!_raised || _periodMs == 0 || !OperatingSystem.IsWindows()) return;

            _ = TimeEndPeriod(_periodMs);
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
        [SupportedOSPlatform("windows")]
        private static extern uint TimeBeginPeriod(uint periodMs);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
        [SupportedOSPlatform("windows")]
        private static extern uint TimeEndPeriod(uint periodMs);

        #endregion
    }
}
