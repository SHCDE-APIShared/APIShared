using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using APIShared.Internal;
using APIShared.Presentation;
using BepInEx.Logging;

namespace APIShared.Automation
{
    /// <summary>Opt-in access to in-process test automation. Start only from a dedicated test build.</summary>
    public sealed class AutomationClient
    {
        private readonly ModApiClient owner;
        private static IUiFrameWorkRegistration driver;
        private static AutomationRun active;

        internal AutomationClient(ModApiClient owner) => this.owner = owner;

        /// <summary>Starts one explicit process-wide run on the validated Unity thread after APIShared initialization.</summary>
        /// <remarks>Throws for invalid input, missing capabilities or a concurrent run. Steps and completion run on
        /// Unity, with one step transition or iterator advance per rendered frame. Step iterators are disposed at
        /// completion, cancellation or failure. Callers must keep iterator cleanup safe if the mission has ended.
        /// An explicit quit request flushes the result and exits the process after completion.</remarks>
        public AutomationRun Start(Guid runId, IEnumerable<AutomationStep> steps, ManualLogSource logger,
            Action<AutomationResult> completed = null, bool quitWhenDone = false)
        {
            RequireUnity();
            if (runId == Guid.Empty) throw new ArgumentException("A run identity is required.", nameof(runId));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (active != null) throw new InvalidOperationException("An automation run is already active.");
            var sequence = (steps ?? throw new ArgumentNullException(nameof(steps))).ToArray();
            if (sequence.Length == 0 || sequence.Any(step => step == null) ||
                sequence.Select(step => step.Name).Distinct(StringComparer.Ordinal).Count() != sequence.Length)
                throw new ArgumentException("Provide nonempty steps with unique names.", nameof(steps));
            if (active != null) throw new InvalidOperationException("An automation run is already active.");
            if (!owner.TryGetMissionLifecycle(out var missions, out var diagnostic))
                throw new InvalidOperationException(diagnostic.Reason);
            if (!owner.TryGetMissionPlayStateReadiness(out var readiness, out diagnostic))
                throw new InvalidOperationException(diagnostic.Reason);
            if (driver == null && !ApiShared.ForMod(APISharedPlugin.PluginGuid).Ui.TryRegisterBeforeRender(
                "ingame-test-automation", Drive, out driver, out var reason, 1000))
                throw new InvalidOperationException(reason);

            var run = new AutomationRun(runId, sequence, missions, readiness, logger, completed, quitWhenDone);
            active = run;
            driver.SetContinuous(true);
            return run;
        }

        internal static void RequireUnity()
        {
            if (!UnityMainThreadDispatch.IsMainThread)
                throw new InvalidOperationException("Automation requires the validated Unity thread.");
        }

        private static void Drive()
        {
            if (active == null || !active.Drive()) return;
            active = null;
            driver.SetContinuous(false);
        }
    }

    /// <summary>A single active test sequence. Action helpers are used from its step iterators on Unity.</summary>
    public sealed partial class AutomationRun
    {
        private readonly Guid id;
        private readonly AutomationStep[] steps;
        private readonly IMissionLifecycleCapability missions;
        private readonly IMissionPlayStateReadinessCapability readiness;
        private readonly ManualLogSource log;
        private Action<AutomationResult> completed;
        private readonly bool quitWhenDone;
        private readonly Stopwatch clock = new Stopwatch();
        private int index = -1;
        private TimeSpan waitSince;
        private IEnumerator<AutomationWait> iterator;
        private AutomationWait wait;
        private long boundSession;
        private bool cancelled, finished;

        internal AutomationRun(Guid id, AutomationStep[] steps, IMissionLifecycleCapability missions,
            IMissionPlayStateReadinessCapability readiness, ManualLogSource log,
            Action<AutomationResult> completed, bool quitWhenDone)
        {
            this.id = id; this.steps = steps; this.missions = missions; this.readiness = readiness;
            this.log = log; this.completed = completed; this.quitWhenDone = quitWhenDone;
        }

        /// <summary>Requests cancellation at the next runner advance on Unity. Repeated calls are harmless.</summary>
        public void Cancel() { AutomationClient.RequireUnity(); cancelled = true; }

        /// <summary>The current managed mission, if any. Fresh PlayState and ticks are separate prerequisites.</summary>
        public MissionContext CurrentMission => missions.Current;

        internal bool Drive()
        {
            if (finished)
            {
                if (!quitWhenDone) return true;
                if (clock.Elapsed < TimeSpan.FromSeconds(2)) return false;
                try
                {
                    foreach (ILogListener listener in BepInEx.Logging.Logger.Listeners)
                        if (listener is DiskLogListener disk) disk.LogWriter?.Flush();
                }
                catch (Exception error) { NativeApiLog.Error(log, "Automation log flush failed: " + error); }
                UnityEngine.Application.Quit();
                return true;
            }

            try
            {
                if (cancelled) return Finish(AutomationStatus.Cancelled, "Cancellation requested.");
                if (iterator == null)
                {
                    if (++index == steps.Length) return Finish(AutomationStatus.Passed, "");
                    var step = steps[index];
                    clock.Restart();
                    if (step.MissionBound)
                        boundSession = CurrentMission?.SessionId ?? throw new InvalidOperationException("No current mission.");
                    CheckSession();
                    NativeApiLog.Info(log, "INGAME_TEST run=" + id.ToString("N") + " step=" + Line(step.Name));
                    iterator = step.Execute(this).GetEnumerator();
                }

                CheckSession();
                if (clock.Elapsed >= steps[index].Timeout)
                    return Finish(AutomationStatus.Failed, "Timed out waiting for " + (wait?.Description ?? "the step") + ".");
                if (wait != null && (clock.Elapsed - waitSince < wait.Duration || wait.Condition?.Invoke() == false))
                    return false;
                CheckSession();
                bool yielded = iterator.MoveNext();
                CheckSession();
                if (clock.Elapsed >= steps[index].Timeout)
                    return Finish(AutomationStatus.Failed, "Timed out executing the step.");
                if (yielded)
                {
                    wait = iterator.Current ?? throw new InvalidOperationException("Yield AutomationWait.NextFrame to wait one frame.");
                    waitSince = clock.Elapsed;
                }
                else
                    DisposeStep();
                return false;
            }
            catch (Exception error) { return Finish(AutomationStatus.Failed, error.ToString()); }
        }

        private void CheckSession()
        {
            if (boundSession != 0 && CurrentMission?.SessionId != boundSession)
                throw new InvalidOperationException("The test mission ended or was replaced.");
        }

        private void DisposeStep()
        {
            var previous = iterator;
            iterator = null;
            wait = null;
            boundSession = 0;
            previous?.Dispose();
        }

        private bool Finish(AutomationStatus status, string detail)
        {
            finished = true;
            try { DisposeStep(); }
            catch (Exception error) { status = AutomationStatus.Failed; detail += " Cleanup: " + error; }
            string step = steps[Math.Max(0, Math.Min(index, steps.Length - 1))].Name;
            var result = new AutomationResult(id, status, step, detail);
            string marker = status == AutomationStatus.Passed ? "PASS" :
                status == AutomationStatus.Cancelled ? "CANCELLED" : "FAIL";
            clock.Restart();
            NativeApiLog.Info(log, "INGAME_TEST run=" + id.ToString("N") + " result=" + marker +
                " step=" + Line(step) + " detail=" + Line(detail));
            var callback = completed;
            completed = null;
            try { callback?.Invoke(result); }
            catch (Exception error) { NativeApiLog.Error(log, "Automation completion callback failed: " + error); }
            return !quitWhenDone;
        }

        private static string Line(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
    }
}
