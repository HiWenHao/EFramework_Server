/*
 * ================================================
 * Describe:      Minimal assertion helpers (no test framework dependency).
 * CreationTime:  2026-11-13 13:50:00
 * ScriptVersion: 0.1
 * ===============================================
 */

using System;
using System.Collections.Generic;

namespace EFramework.Tests.Harness
{
    /// <summary>
    /// 断言集合：失败即抛出 <see cref="InvalidOperationException"/>，由 <see cref="TestRunner"/> 捕获计分。
    /// <para>Assertion helpers: a failure throws <see cref="InvalidOperationException"/>, which the runner
    /// catches and scores.</para>
    /// </summary>
    internal static class Assert
    {
        #region Methods

        public static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"{message} (expected={expected}, actual={actual})");
        }

        public static void NotNull(object value, string message)
        {
            if (value == null) throw new InvalidOperationException($"{message} (value is null)");
        }

        public static void BytesEqual(byte[] expected, byte[] actual, int length, string message)
        {
            if (actual == null)
                throw new InvalidOperationException($"{message} (actual is null)");
            if (actual.Length < length)
                throw new InvalidOperationException($"{message} (actual length {actual.Length} < {length})");

            for (int i = 0; i < length; i++)
            {
                if (expected[i] != actual[i])
                    throw new InvalidOperationException($"{message} (byte {i}: expected={expected[i]}, actual={actual[i]})");
            }
        }

        public static TException Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException expected)
            {
                return expected;
            }
            catch (Exception other)
            {
                throw new InvalidOperationException(
                    $"{message} (expected {typeof(TException).Name}, got {other.GetType().Name}: {other.Message})");
            }

            throw new InvalidOperationException($"{message} (expected {typeof(TException).Name}, nothing was thrown)");
        }

        #endregion
    }
}
