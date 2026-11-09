using System;
using System.Collections.Generic;

namespace APIShared.Automation
{
    /// <summary>A named Unity-thread test step with a monotonic deadline.</summary>
    public sealed class AutomationStep
    {
        /// <summary>Creates a step. Mission-bound steps require the same current mission through all waits and actions.</summary>
        public AutomationStep(string name, TimeSpan timeout,
            Func<AutomationRun, IEnumerable<AutomationWait>> execute, bool missionBound = false)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A step name is required.", nameof(name));
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            Name = name;
            Timeout = timeout;
            Execute = execute ?? throw new ArgumentNullException(nameof(execute));
            MissionBound = missionBound;
        }

        internal string Name { get; }
        internal TimeSpan Timeout { get; }
        internal Func<AutomationRun, IEnumerable<AutomationWait>> Execute { get; }
        internal bool MissionBound { get; }
    }

    /// <summary>A Unity-frame, monotonic delay or condition yielded by a test step.</summary>
    public sealed class AutomationWait
    {
        private AutomationWait(string description, TimeSpan delay, Func<bool> condition)
        { Description = description; Duration = delay; Condition = condition; }

        /// <summary>Resumes on a later rendered frame, including while the simulation is paused.</summary>
        public static AutomationWait NextFrame { get; } = new AutomationWait("next frame", TimeSpan.Zero, null);

        /// <summary>Resumes on a later frame at or after the nonnegative monotonic delay.</summary>
        public static AutomationWait Delay(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
            return new AutomationWait("delay", duration, null);
        }

        /// <summary>Checks the named condition on later Unity frames, within the step deadline.</summary>
        public static AutomationWait Until(string description, Func<bool> condition)
        {
            if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Describe the condition.", nameof(description));
            return new AutomationWait(description, TimeSpan.Zero, condition ?? throw new ArgumentNullException(nameof(condition)));
        }

        internal string Description { get; }
        internal TimeSpan Duration { get; }
        internal Func<bool> Condition { get; }
    }

    /// <summary>Terminal status of an explicitly started test run.</summary>
    public enum AutomationStatus
    {
        /// <summary>All steps completed.</summary>
        Passed,
        /// <summary>A step or its cleanup failed, including timeouts.</summary>
        Failed,
        /// <summary>Cancellation was requested by the caller.</summary>
        Cancelled
    }

    /// <summary>The single terminal report of a run.</summary>
    public sealed class AutomationResult
    {
        internal AutomationResult(Guid runId, AutomationStatus status, string step, string detail)
        { RunId = runId; Status = status; Step = step; Detail = detail; }

        /// <summary>Caller-provided run identity.</summary>
        public Guid RunId { get; }
        /// <summary>Terminal status.</summary>
        public AutomationStatus Status { get; }
        /// <summary>Last step reached.</summary>
        public string Step { get; }
        /// <summary>Failure or cancellation details, or an empty string.</summary>
        public string Detail { get; }
    }
}
