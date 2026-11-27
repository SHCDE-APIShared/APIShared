using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace APIShared.ModSettings
{
    /// <summary>
    /// Bundles the personal settings file writes of single edits. Each edit still updates the
    /// controller's working state at once; only its disk mirror waits for a quiet period or the
    /// next flush point (launch preparation, mission context, mission lifecycle, process exit).
    /// </summary>
    /// <remarks>Game independent. Without a bound scheduler nothing is deferred and controllers write
    /// immediately. Flushes run on the bound flush thread (Unity) and hold no lock while writing.
    /// Every controller flushes its own payload; a failed write stays pending for the next flush point.</remarks>
    internal static class PersonalSettingsWriteQueue
    {
        internal static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);
        // Caps the delay while edits never pause (a held slider), bounding what a crash can lose.
        internal static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(2);
        private static readonly object Sync = new object();
        private static readonly List<LobbyPresetController> Writers = new List<LobbyPresetController>();
        private static Binding binding;
        private static TimeSpan deadline, firstPending;
        private static bool timerRequested;

        internal sealed class Binding
        {
            internal readonly Func<TimeSpan> Now;
            internal readonly Action<TimeSpan> RequestElapsedAfter;
            internal readonly Func<bool> IsFlushThread;
            internal readonly Func<Action, bool> PostToFlushThread;

            /// <param name="now">Monotonic clock.</param>
            /// <param name="requestElapsedAfter">Thread-safe; calls OnQuietPeriodElapsed on the flush thread after at least the delay.</param>
            /// <param name="isFlushThread">Whether the caller is the flush thread.</param>
            /// <param name="postToFlushThread">Runs an action later on the flush thread; false when unavailable.</param>
            internal Binding(Func<TimeSpan> now, Action<TimeSpan> requestElapsedAfter,
                Func<bool> isFlushThread, Func<Action, bool> postToFlushThread)
            {
                Now = now ?? throw new ArgumentNullException(nameof(now));
                RequestElapsedAfter = requestElapsedAfter ?? throw new ArgumentNullException(nameof(requestElapsedAfter));
                IsFlushThread = isFlushThread ?? throw new ArgumentNullException(nameof(isFlushThread));
                PostToFlushThread = postToFlushThread ?? throw new ArgumentNullException(nameof(postToFlushThread));
            }
        }

        internal static bool IsBound => Volatile.Read(ref binding) != null;

        // Bound once by the runtime on the Unity thread; tests bind a manual scheduler.
        internal static void Bind(Binding value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (Interlocked.CompareExchange(ref binding, value, null) != null)
                throw new InvalidOperationException("Personal settings writes are already bound.");
        }

        /// <summary>Records a pending write of this controller. False without a scheduler: the caller writes now.</summary>
        internal static bool TryDefer(LobbyPresetController writer)
        {
            Binding current = Volatile.Read(ref binding);
            if (current == null) return false;
            bool request;
            lock (Sync)
            {
                if (!Writers.Contains(writer)) Writers.Add(writer);
                TimeSpan now = current.Now();
                if (!timerRequested) firstPending = now;
                TimeSpan afterQuiet = now + QuietPeriod, latest = firstPending + MaximumDelay;
                deadline = afterQuiet < latest ? afterQuiet : latest;
                request = !timerRequested;
                timerRequested = true;
            }
            if (request)
            {
                try { current.RequestElapsedAfter(QuietPeriod); }
                catch
                {
                    lock (Sync) timerRequested = false;
                    return false;
                }
            }
            return true;
        }

        /// <summary>Keeps a controller whose write failed reachable for the next flush point, without a timer.</summary>
        internal static void Track(LobbyPresetController writer)
        {
            lock (Sync)
                if (!Writers.Contains(writer)) Writers.Add(writer);
        }

        /// <summary>Scheduler callback. Each later edit moves the deadline, so the write follows the last edit.</summary>
        internal static void OnQuietPeriodElapsed()
        {
            Binding current = Volatile.Read(ref binding);
            if (current == null) return;
            TimeSpan remaining;
            lock (Sync)
            {
                remaining = deadline - current.Now();
                if (remaining <= TimeSpan.Zero) timerRequested = false;
            }
            if (remaining > TimeSpan.Zero)
            {
                current.RequestElapsedAfter(remaining);
                return;
            }
            FlushAll();
        }

        /// <summary>Writes every pending controller now. Off the flush thread the flush is posted there.
        /// Each controller is attempted; the first failure is rethrown afterwards to the calling host.</summary>
        internal static void FlushAll()
        {
            Binding current = Volatile.Read(ref binding);
            if (current != null && !current.IsFlushThread())
            {
                current.PostToFlushThread(FlushAll);
                return;
            }
            LobbyPresetController[] writers;
            lock (Sync) writers = Writers.ToArray();
            ExceptionDispatchInfo failure = null;
            foreach (LobbyPresetController writer in writers)
            {
                try { writer.FlushPersonalStorage(); }
                catch (Exception exception) { failure = failure ?? ExceptionDispatchInfo.Capture(exception); }
            }
            failure?.Throw();
        }

#if API_SHARED_PRESET_TESTS
        internal static void ResetForTests()
        {
            lock (Sync)
            {
                Writers.Clear();
                timerRequested = false;
                deadline = TimeSpan.Zero;
            }
            Volatile.Write(ref binding, null);
        }
#endif
    }
}
