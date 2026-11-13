/*
 * ================================================
 * Describe:      Tiny test runner with per-case timing and a pass/fail summary.
 * CreationTime:  2026-11-13 13:50:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace EFramework.Tests.Harness
{
    /// <summary>
    /// 极简测试运行器：逐条执行、就地打印、汇总退出码（非 0 表示有失败）。
    /// <para>Minimal runner: executes cases one by one, prints inline and returns a non-zero exit code on
    /// failure.</para>
    /// </summary>
    internal sealed class TestRunner
    {
        #region Fields

        private readonly List<string> _failures = new List<string>();
        private int _passed;

        #endregion

        #region Methods

        public void Run(string name, Action body)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                body();
                _passed++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  PASS  ");
                Console.ResetColor();
                Console.WriteLine($"{name}  ({ElapsedMs(start)}ms)");
            }
            catch (Exception e)
            {
                _failures.Add($"{name}: {e.Message}");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  FAIL  ");
                Console.ResetColor();
                Console.WriteLine($"{name}  ({ElapsedMs(start)}ms)");
                Console.WriteLine($"        {e.GetType().Name}: {e.Message}");
            }
        }

        public static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine($"== {title} ==");
        }

        /// <summary>
        /// 打印汇总并返回进程退出码。
        /// <para>Print the summary and return the process exit code.</para>
        /// </summary>
        /// <returns>0 全部通过；1 存在失败<para>0 when everything passed; 1 otherwise</para></returns>
        public int Summarize()
        {
            Console.WriteLine();
            Console.WriteLine(new string('-', 70));

            if (_failures.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"ALL PASSED ({_passed} cases)");
                Console.ResetColor();
                return 0;
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {_failures.Count} of {_passed + _failures.Count} cases");
            Console.ResetColor();

            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine($"  {i + 1}. {_failures[i]}");
            }

            return 1;
        }

        private static long ElapsedMs(long startTicks)
        {
            return (Stopwatch.GetTimestamp() - startTicks) * 1000 / Stopwatch.Frequency;
        }

        #endregion
    }
}
