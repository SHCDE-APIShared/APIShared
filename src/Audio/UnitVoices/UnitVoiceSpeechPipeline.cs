using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using APIShared.Internal;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace APIShared.Audio
{
    // Vanilla may overwrite its current clip field when an older async load completes. Own clips
    // therefore also retain their actual playback source until that source stops or finishes.
    internal sealed class UnitVoiceClipLeases<TClip, TSource, TManager> where TClip : class where TSource : class where TManager : class
    {
        private sealed class Identity<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly Identity<T> Instance = new Identity<T>();
            public bool Equals(T left, T right) => ReferenceEquals(left, right);
            public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
        }
        private sealed class Playback
        {
            internal readonly TSource Source;
            internal readonly TManager Manager;
            internal bool StopFailed;
            internal Playback(TSource source, TManager manager) { Source = source; Manager = manager; }
        }
        private readonly HashSet<TClip> owned = new HashSet<TClip>(Identity<TClip>.Instance);
        private readonly Dictionary<TClip, Playback> played = new Dictionary<TClip, Playback>(Identity<TClip>.Instance);
        private readonly List<TClip> retiring = new List<TClip>();
        private readonly HashSet<TSource> stopping = new HashSet<TSource>(Identity<TSource>.Instance);
        private readonly Func<TSource, bool> finished;
        private readonly Func<TSource, bool> destroyed;
        private readonly Action<TClip> release;
        internal UnitVoiceClipLeases(Func<TSource, bool> finished, Action<TClip> release, Func<TSource, bool> destroyed = null)
        { this.finished = finished ?? throw new ArgumentNullException(nameof(finished)); this.release = release ?? throw new ArgumentNullException(nameof(release)); this.destroyed = destroyed; }
        internal int OwnedCount => owned.Count;
        internal void Created(TClip clip) { if (clip == null || !owned.Add(clip)) throw new InvalidOperationException("A new owned voice clip is required."); }
        internal void Played(TClip clip, TSource source, TManager manager)
        { if (clip != null && owned.Contains(clip)) played[clip] = new Playback(source, manager); }
        internal void Release(TClip clip)
        {
            if (clip == null || !owned.Remove(clip)) return;
            played.Remove(clip); release(clip);
        }
        internal void ReleaseUnplayed(TClip clip) { if (clip != null && !played.ContainsKey(clip)) Release(clip); }
        internal void Sweep(TManager manager, bool paused)
        {
            if (paused || played.Count == 0) return;
            foreach (var pair in played) if (ReferenceEquals(pair.Value.Manager, manager) && finished(pair.Value.Source)) retiring.Add(pair.Key);
            Drain();
        }
        internal void Stopped(TSource source)
        {
            foreach (var pair in played) if (ReferenceEquals(pair.Value.Source, source)) retiring.Add(pair.Key);
            Drain();
        }
        internal void Stop(TSource source, Action<TSource> stop)
        {
            try { stop(source); }
            catch
            {
                foreach (var value in played.Values) if (ReferenceEquals(value.Source, source)) value.StopFailed = true;
                throw;
            }
            Stopped(source);
        }
        internal void StopManager(TManager manager, Action<TSource> stop, Action<Exception> failed)
        {
            foreach (var value in played.Values)
                if (ReferenceEquals(value.Manager, manager) && (!value.StopFailed || destroyed != null && destroyed(value.Source))) stopping.Add(value.Source);
            foreach (var source in stopping)
            {
                try { Stop(source, stop); }
                catch (Exception error) { failed(error); }
            }
            stopping.Clear();
        }
        private void Drain()
        {
            // Remove first, then release. A later unload/stop cannot destroy the same clip twice.
            for (int index = 0; index < retiring.Count; index++) Release(retiring[index]);
            retiring.Clear();
        }
    }

    internal readonly struct UnitVoiceSpeechSubmission
    {
        internal readonly bool WasAccepted;
        internal readonly bool? UnitsSpeech;
        internal UnitVoiceSpeechSubmission(bool accepted, bool? units) { WasAccepted = accepted; UnitsSpeech = units; }
    }

    // Frames belong to requests, never clips or persistent channel state. Every accepted load,
    // including non-unit speech, establishes a child frame before its async method starts.
    internal static class UnitVoiceSpeechFlow
    {
        private static readonly AsyncLocal<Pending> pending = new AsyncLocal<Pending>();
        private static readonly AsyncLocal<Frame> active = new AsyncLocal<Frame>();
        internal static bool HasAmbientRequest => pending.Value != null || active.Value != null;
        internal sealed class Pending
        {
            internal readonly object Manager;
            internal readonly int Channel;
            internal readonly float Gain;
            internal readonly AudioClip Clip;
            private int claimed;
            internal bool? AcceptedUnits;
            internal int ClipConsumed;
            internal Pending(object manager, int channel, float gain, AudioClip clip)
            { Manager = manager; Channel = channel; Gain = gain; Clip = clip; }
            internal bool WasAccepted => Volatile.Read(ref claimed) != 0;
            internal bool TryClaim(object manager, int channel, bool units)
            {
                if (!ReferenceEquals(Manager, manager) || Channel != channel || Interlocked.CompareExchange(ref claimed, 1, 0) != 0) return false;
                AcceptedUnits = units; return true;
            }
        }
        internal sealed class Frame
        {
            internal readonly object Manager;
            internal readonly int Channel;
            internal readonly float Gain;
            internal readonly Pending Request;
            internal Frame(object manager, int channel, Pending request)
            { Manager = manager; Channel = channel; Request = request; Gain = request?.Gain ?? 1f; }
        }
        internal readonly struct PendingScope : IDisposable
        {
            private readonly Pending previous;
            internal readonly Pending Request;
            internal PendingScope(Pending next) { previous = pending.Value; Request = next; pending.Value = next; }
            public void Dispose() { pending.Value = previous; }
        }
        internal readonly struct FrameScope : IDisposable
        {
            private readonly Frame previous;
            internal readonly Frame Current;
            internal FrameScope(Frame next) { previous = active.Value; Current = next; active.Value = next; }
            public void Dispose() { active.Value = previous; }
        }
        internal static PendingScope BeginRequest(object manager, int channel, float gain, AudioClip clip = null)
        {
            if (manager == null || channel < 1 || channel > 3 || !Finite(gain) || gain < 0) throw new ArgumentException("A manager, speech channel and finite nonnegative gain are required.");
            return new PendingScope(new Pending(manager, channel, gain, clip));
        }
        internal static FrameScope BeginAccepted(object manager, int channel, bool units)
        {
            var request = pending.Value;
            if (request == null || !request.TryClaim(manager, channel, units)) request = null;
            return new FrameScope(new Frame(manager, channel, request));
        }
        internal static float Gain(object manager, int channel)
        {
            var frame = active.Value;
            return frame != null && ReferenceEquals(frame.Manager, manager) && frame.Channel == channel ? frame.Gain : 1f;
        }
        internal static AudioClip TakeClip(object manager)
        {
            var frame = active.Value;
            var request = frame?.Request;
            if (request == null || !ReferenceEquals(frame.Manager, manager) || ReferenceEquals(request.Clip, null) ||
                Interlocked.CompareExchange(ref request.ClipConsumed, 1, 0) != 0) return null;
            return request.Clip;
        }
        internal static bool VerifyAsyncFlow()
        {
            if (ExecutionContext.IsFlowSuppressed()) return false;
            using (var scope = BeginAccepted(new object(), 1, false))
                return ObserveAfterAwait(scope.Current).GetAwaiter().GetResult();
        }
        private static async Task<bool> ObserveAfterAwait(Frame expected)
        {
            // No Unity dependency or captured Unity context: startup waits only for this short probe.
            await Task.Delay(1).ConfigureAwait(false);
            return ReferenceEquals(active.Value, expected);
        }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal static class UnitVoiceSpeechPipeline
    {
        internal delegate void LoadStart(MyAudioManager manager, int channel, string folder, string name, bool units, bool ignorePause);
        internal delegate Task<AudioClip> LoadPath(MyAudioManager manager, string path);
        private static LoadStart originalStart;
        private static LoadPath originalPath;
        private static readonly List<ILHook> hooks = new List<ILHook>();
        private static readonly UnitVoiceClipLeases<AudioClip, AudioSource, MyAudioManager> owned =
            new UnitVoiceClipLeases<AudioClip, AudioSource, MyAudioManager>(source => source == null || !source.isPlaying,
                ReleaseOwnedClip, source => source == null);
        private static readonly Action<AudioSource> StopOriginal = source => source.Stop();
        private static readonly Action<AudioSource> StopOwnedSource = source => { if (source != null) source.Stop(); };
        private static long cleanupFailures;
        private static string status = "Pending";
        private static int attempted;
        private static ManualLogSource log;
        internal static string Status => Volatile.Read(ref status);
        internal static bool IsReady => Status == "Ready";

        internal static void Initialize(ManualLogSource logger)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            log = logger;
            try
            {
                UnitVoiceSpeechIlContract.Methods methods = UnitVoiceSpeechIlContract.Validate();
                originalStart = (LoadStart)Delegate.CreateDelegate(typeof(LoadStart), methods.Start);
                originalPath = (LoadPath)Delegate.CreateDelegate(typeof(LoadPath), methods.Path);
                if (!UnitVoiceSpeechFlow.VerifyAsyncFlow()) throw new InvalidOperationException("ExecutionContext does not carry voice request frames across await.");
                Publish(methods.Play, il => { UnitVoiceSpeechIlContract.ApplyStarts(il); UnitVoiceSpeechIlContract.ApplyStops(il, 3); UnitVoiceSpeechIlContract.ApplyReleases(il, 3); }, "PlaySpeech");
                Publish(methods.MoveNext, il => UnitVoiceSpeechIlContract.ApplyAsync(il, methods), "AsyncLoader");
                Publish(methods.Monitor, il => { UnitVoiceSpeechIlContract.ApplyMonitorSweep(il, methods); UnitVoiceSpeechIlContract.ApplyReleases(il, 3); }, "Monitor");
                Publish(methods.StopAll, il => { UnitVoiceSpeechIlContract.ApplyStops(il, 3); UnitVoiceSpeechIlContract.ApplyReleases(il, 3); UnitVoiceSpeechIlContract.ApplyStopAllCleanup(il); }, "StopAll");
                Publish(methods.StopSpeech, il => { UnitVoiceSpeechIlContract.ApplyStops(il, 1); UnitVoiceSpeechIlContract.ApplyReleases(il, 1); }, "StopSpeech");
                Volatile.Write(ref status, "Ready");
                NativeApiLog.Info(log, "UNIT_VOICE_SPEECH_READY: permanent accepted-load, async playback and owned-clip lifetime adapters.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref status, "Unavailable: " + error.Message);
                NativeApiLog.Error(log, "UNIT_VOICE_SPEECH_UNAVAILABLE: " + error);
            }
        }
        private static void Publish(MethodInfo method, MonoMod.Cil.ILContext.Manipulator apply, string id)
        {
            var hook = new ILHook(method, apply, new ILHookConfig { ManualApply = true, ID = "APIShared.UnitVoiceSpeech." + id });
            // Publish the permanent root before Apply. Even a failed partially applied hook is never disposed.
            hooks.Add(hook); hook.Apply();
            if (!hook.IsApplied) throw new InvalidOperationException("Voice IL publication incomplete: " + id);
        }
        internal static UnitVoiceSpeechSubmission Forward(SFXManager manager, int channel, string fullpath, float volume,
            bool ignoreSpeechMuting, bool ignorePauseState, float factor)
        {
            if (!IsReady || !UnityMainThreadDispatch.IsMainThread || MyAudioManager.Instance == null)
            {
                manager.playSpeech(channel, fullpath, volume, ignoreSpeechMuting, ignorePauseState);
                return new UnitVoiceSpeechSubmission(false, null);
            }
            RequireFlow(factor);
            using (var scope = UnitVoiceSpeechFlow.BeginRequest(MyAudioManager.Instance, channel, factor))
            {
                manager.playSpeech(channel, fullpath, volume, ignoreSpeechMuting, ignorePauseState);
                return new UnitVoiceSpeechSubmission(scope.Request.WasAccepted, scope.Request.AcceptedUnits);
            }
        }
        internal static bool TryPlayResource(int channel, string originalFolder, string originalFileName, bool unitsSpeech,
            bool ignoreSpeechMuting, bool ignorePauseState, float factor, UnitVoiceResourceHandle resource,
            out UnitVoiceSpeechSubmission submission, out string reason)
        {
            submission = new UnitVoiceSpeechSubmission(false, null);
            if (!IsReady || !UnityMainThreadDispatch.IsMainThread || channel < 1 || channel > 3 || MyAudioManager.Instance == null)
            { reason = IsReady ? "Unity speech channel unavailable." : Status; return false; }
            if (resource == null || !resource.IsReady || !UnitVoiceSpeechFlow.Finite(factor * resource.BaseVolume))
            { reason = "Voice resource or resulting gain unavailable."; return false; }
            RequireFlow(factor * resource.BaseVolume);
            AudioClip clip;
            try { clip = UnitVoiceResources.CreatePlaybackClip(resource); }
            catch (Exception error) { reason = error.Message; return false; }
            owned.Created(clip);
            UnitVoiceSpeechFlow.Pending request = null;
            try
            {
                using (var scope = UnitVoiceSpeechFlow.BeginRequest(MyAudioManager.Instance, channel, factor * resource.BaseVolume, clip))
                {
                    request = scope.Request;
                    MyAudioManager.Instance.PlaySpeech(channel, originalFolder, originalFileName, true, unitsSpeech, ignoreSpeechMuting, ignorePauseState);
                    submission = new UnitVoiceSpeechSubmission(request.WasAccepted, request.AcceptedUnits);
                }
            }
            finally
            {
                // Once returned by the async-loader adapter, only the existing Speech cleanup paths own it.
                if (request == null || Volatile.Read(ref request.ClipConsumed) == 0) owned.ReleaseUnplayed(clip);
            }
            reason = string.Empty; return true;
        }
        private static void RequireFlow(float factor)
        {
            if (!UnitVoiceSpeechFlow.Finite(factor) || factor < 0) throw new ArgumentOutOfRangeException(nameof(factor));
            if (ExecutionContext.IsFlowSuppressed()) throw new InvalidOperationException("Voice request ExecutionContext flow is suppressed.");
        }
        // Target of the three accepted-load callsites in PlaySpeech. Non-unit callers get neutral frames too.
        internal static void Start(MyAudioManager manager, int channel, string folder, string name, bool units, bool ignorePause)
        {
            if (!IsReady || !UnitVoiceSpeechFlow.HasAmbientRequest) { originalStart(manager, channel, folder, name, units, ignorePause); return; }
            using (UnitVoiceSpeechFlow.BeginAccepted(manager, channel, units)) originalStart(manager, channel, folder, name, units, ignorePause);
        }
        // This call occurs synchronously before the first await, so no ThreadStatic crosses an async boundary.
        internal static Task<AudioClip> Load(MyAudioManager manager, string path)
        {
            var clip = IsReady ? UnitVoiceSpeechFlow.TakeClip(manager) : null;
            return ReferenceEquals(clip, null) ? originalPath(manager, path) : Task.FromResult(clip);
        }
        internal static void PlayOneShot(AudioSource source, AudioClip clip, MyAudioManager manager, int channel)
        {
            float gain = IsReady ? UnitVoiceSpeechFlow.Gain(manager, channel) : 1f;
            try { source.PlayOneShot(clip, gain); }
            catch { owned.ReleaseUnplayed(clip); throw; }
            owned.Played(clip, source, manager);
        }
        internal static void Stop(AudioSource source)
        {
            owned.Stop(source, StopOriginal); // Original exceptions propagate; no second stop or unsafe release.
        }
        internal static void Sweep(MyAudioManager manager, bool paused)
        {
            owned.Sweep(manager, paused);
        }
        internal static void StopAllCompleted(MyAudioManager manager)
        {
            // Vanilla skips Stop for paused sources (!isPlaying). Its current clip field may
            // have been overwritten by an earlier async load, so stop only remaining own sources.
            owned.StopManager(manager, StopOwnedSource, ReportCleanupFailure);
        }
        internal static bool Unload(AudioClip clip)
        {
            try { return clip.UnloadAudioData(); }
            finally { owned.Release(clip); }
        }
        private static void ReleaseOwnedClip(AudioClip clip)
        {
            try { if (clip != null) UnityEngine.Object.Destroy(clip); }
            catch (Exception error)
            {
                ReportCleanupFailure(error);
            }
        }
        private static void ReportCleanupFailure(Exception error)
        { if (FailureThrottle.ShouldReport(ref cleanupFailures)) NativeApiLog.Error(log, "UNIT_VOICE_CLIP_RELEASE: " + error); }
    }
}
