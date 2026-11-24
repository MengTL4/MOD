using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityEngine
{
    /// <summary>
    /// Async support on the virtual clock. Game call sites of Task.Delay are rewritten (tools/ShimGen
    /// rewrite) to <see cref="Delay(int)"/>, and the main thread runs under <see cref="FrameContext"/>,
    /// so awaits resume inside the frame loop exactly like UnitySynchronizationContext does — never on
    /// a pool thread and never by wall-clock time.
    /// </summary>
    public static class HeadlessAsync
    {
        private static readonly List<(float at, TaskCompletionSource<bool> tcs)> delays = new List<(float, TaskCompletionSource<bool>)>();

        public static int PendingDelays => delays.Count;

        /// <summary>Unscaled seconds until the soonest pending delay ends (MaxValue when none).</summary>
        internal static double SecondsToNextDelay()
        {
            double soonest = double.MaxValue;
            foreach (var (at, _) in delays)
            {
                soonest = Math.Min(soonest, at - UnityRuntime.UnscaledTime);
            }
            return soonest;
        }

        public static Task Delay(int millisecondsDelay) => Delay(millisecondsDelay, CancellationToken.None);

        public static Task Delay(TimeSpan delay) => Delay((int)delay.TotalMilliseconds, CancellationToken.None);

        public static Task Delay(TimeSpan delay, CancellationToken token) => Delay((int)delay.TotalMilliseconds, token);

        public static Task Delay(int millisecondsDelay, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return Task.FromCanceled(token);
            }
            if (millisecondsDelay <= 0)
            {
                return Task.CompletedTask;
            }
            var tcs = new TaskCompletionSource<bool>();
            delays.Add((UnityRuntime.UnscaledTime + millisecondsDelay / 1000f, tcs));
            return tcs.Task;
        }

        internal static void Reset() => delays.Clear();

        /// <summary>Completes due delays (their continuations are posted to the frame context).</summary>
        internal static void Tick()
        {
            if (delays.Count == 0)
            {
                return;
            }
            for (int i = 0; i < delays.Count; i++)
            {
                var (at, tcs) = delays[i];
                if (UnityRuntime.UnscaledTime + 1e-6f < at)
                {
                    continue;
                }
                delays.RemoveAt(i--);
                tcs.TrySetResult(true);
            }
        }
    }

    /// <summary>Main-thread synchronization context: posted callbacks run at the next frame's task point.</summary>
    public sealed class FrameContext : SynchronizationContext
    {
        private static readonly List<(SendOrPostCallback cb, object state)> queue = new List<(SendOrPostCallback, object)>();

        public static void Install()
        {
            if (Current is not FrameContext)
            {
                SetSynchronizationContext(new FrameContext());
            }
        }

        public override void Post(SendOrPostCallback d, object state)
        {
            if (Current is not FrameContext)
            {
                throw new InvalidOperationException("game continuation posted from a background thread");
            }
            queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object state) => d(state);

        public override SynchronizationContext CreateCopy() => this;

        public static int Pending => queue.Count;

        internal static void Reset() => queue.Clear();

        internal static void Drain()
        {
            if (queue.Count == 0)
            {
                return;
            }
            var batch = queue.ToArray();
            queue.Clear();
            foreach (var (cb, state) in batch)
            {
                try
                {
                    cb(state);
                }
                catch (Exception ex)
                {
                    UnityRuntime.Error("async continuation", ex);
                }
            }
        }
    }
}
