using Miniscript;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

/// <summary>Provides interpreter-scoped intrinsic call pacing.</summary>
internal static class MiniScriptIntrinsicRateLimiter
{
    private static readonly string[] WrappedIntrinsicNamePrefixes =
    {
        "uplink_ssh_",
        "uplink_fs_",
        "uplink_net_",
        "uplink_ftp_",
    };

    private static readonly object wrapSync = new();
    private static readonly ConcurrentDictionary<int, IntrinsicCode> originalCodesById = new();
    private static readonly ConditionalWeakTable<Interpreter, LimiterState> limiterStates = new();

    /// <summary>Configures per-interpreter intrinsic call limit and ensures wrappers are installed.</summary>
    internal static void ConfigureInterpreter(Interpreter interpreter, double maxCallsPerSecond)
    {
        if (interpreter is null)
        {
            throw new ArgumentNullException(nameof(interpreter));
        }

        EnsureWrapped();
        limiterStates.Remove(interpreter);
        if (maxCallsPerSecond > 0)
        {
            limiterStates.Add(interpreter, new LimiterState(maxCallsPerSecond));
        }
    }

    private static void EnsureWrapped()
    {
        lock (wrapSync)
        {
            _ = Intrinsic.GetByName("print");
            for (var intrinsicId = 1; intrinsicId < Intrinsic.all.Count; intrinsicId++)
            {
                var intrinsic = Intrinsic.all[intrinsicId];
                if (intrinsic is null || intrinsic.code is null)
                {
                    continue;
                }

                if (!ShouldWrapIntrinsic(intrinsic.name))
                {
                    continue;
                }

                if (originalCodesById.ContainsKey(intrinsicId))
                {
                    continue;
                }

                var originalCode = intrinsic.code;
                originalCodesById[intrinsicId] = originalCode;
                intrinsic.code = (context, partialResult) =>
                {
                    if (context?.interpreter is Interpreter interpreter &&
                        limiterStates.TryGetValue(interpreter, out var limiterState))
                    {
                        limiterState.WaitForPermit();
                    }

                    return originalCode(context, partialResult);
                };
            }
        }
    }

    private static bool ShouldWrapIntrinsic(string? intrinsicName)
    {
        if (string.IsNullOrWhiteSpace(intrinsicName))
        {
            return false;
        }

        for (var index = 0; index < WrappedIntrinsicNamePrefixes.Length; index++)
        {
            if (intrinsicName.StartsWith(WrappedIntrinsicNamePrefixes[index], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class LimiterState
    {
        private readonly object sync = new();
        private readonly long ticksPerCall;
        private long nextTick;

        internal LimiterState(double maxCallsPerSecond)
        {
            if (maxCallsPerSecond <= 0)
            {
                ticksPerCall = 0;
                return;
            }

            var ticks = Stopwatch.Frequency / maxCallsPerSecond;
            ticksPerCall = Math.Max(1L, (long)Math.Ceiling(ticks));
        }

        internal void WaitForPermit()
        {
            if (ticksPerCall <= 0)
            {
                return;
            }

            long waitTicks;
            lock (sync)
            {
                var now = Stopwatch.GetTimestamp();
                if (nextTick == 0)
                {
                    nextTick = now;
                }

                waitTicks = nextTick - now;
                if (waitTicks < 0)
                {
                    waitTicks = 0;
                }

                var baseTick = waitTicks > 0 ? nextTick : now;
                nextTick = baseTick + ticksPerCall;
            }

            if (waitTicks <= 0)
            {
                return;
            }

            WaitForTicks(waitTicks);
        }

        private static void WaitForTicks(long waitTicks)
        {
            var deadline = Stopwatch.GetTimestamp() + waitTicks;
            while (true)
            {
                var remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                {
                    return;
                }

                var sleepMilliseconds = (int)((remaining * 1000L) / Stopwatch.Frequency);
                if (sleepMilliseconds > 0)
                {
                    Thread.Sleep(Math.Max(1, sleepMilliseconds));
                    continue;
                }

                Thread.SpinWait(64);
            }
        }
    }
}
