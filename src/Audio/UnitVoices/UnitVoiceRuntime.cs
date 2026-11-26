using System;
using System.Reflection;
using System.Threading;
using APIShared.Internal;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;

namespace APIShared.Audio
{
    // Process root. No Unity component callback owns this service or its executable hooks.
    internal static class UnitVoiceRuntime
    {
        private static Patching.SafeIlHook copyHook;
        private static ManualLogSource log;
        private static int attempted, playbackReady, copyReady, lifecycleReady, confirmed;
        private static long nextInvocation, failureLogs;
        private static string failure = "Pending";
        private static MissionContext mission;
        private const string PlaybackFailurePrefix = "Unit voice record integration unavailable: ";
        internal static bool ManagedReady => Volatile.Read(ref playbackReady) != 0 && Volatile.Read(ref copyReady) != 0 &&
            Volatile.Read(ref lifecycleReady) != 0 && UnitVoiceSpeechPipeline.IsReady;
        internal static string Status => ManagedReady && UnitVoiceNativeRuntime.Available ? "Ready" :
            Volatile.Read(ref failure) != "Pending" ? Volatile.Read(ref failure) :
            !ManagedReady ? "Pending managed unit voice integration." : UnitVoiceNativeRuntime.Status;

        internal static bool ValidatePlayback(MethodInfo method, ManualLogSource logger)
        {
            log = logger;
            try { UnitVoicePlaybackIlContract.Validate(method); return true; }
            catch (Exception error) { Fail("Unit voice record integration unavailable: " + error.Message); return false; }
        }
        internal static void SetPlaybackReady(bool value)
        {
            // A later rebuild of processTestMap can restore the speech edit after a failed one.
            if (value && (Volatile.Read(ref failure) ?? string.Empty).StartsWith(PlaybackFailurePrefix, StringComparison.Ordinal))
                Volatile.Write(ref failure, "Pending");
            Volatile.Write(ref playbackReady, value ? 1 : 0);
            UnitVoiceNativeRuntime.RefreshDemand();
        }
        internal static void SetPlaybackFailure(string reason)
        {
            Volatile.Write(ref playbackReady, 0);
            Fail(PlaybackFailurePrefix + reason);
            UnitVoiceNativeRuntime.RefreshDemand();
        }
        internal static void InitializeManaged(ManualLogSource logger)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            log = logger;
            try
            {
                var copy = typeof(EngineInterface).GetMethod("CopyPlayStateStruct", BindingFlags.Public | BindingFlags.Static);
                if (copy == null) throw new MissingMethodException("Public PlayState copy method missing.");
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.Audio.UnitVoiceCopy", copy,
                    UnitVoiceCopyIlContract.Apply, logger, out copyHook, out string reason))
                    throw new InvalidOperationException("Unit voice copy hook publication failed: " + reason);
                // The Script Extender also edits this method; a later rebuild can drop our edit.
                copyHook.Rebuilt += hook => { Volatile.Write(ref copyReady, hook.IsApplied ? 1 : 0); UnitVoiceNativeRuntime.RefreshDemand(); };
                Volatile.Write(ref copyReady, copyHook.IsApplied ? 1 : 0);
                UnitVoiceSpeechPipeline.Initialize(logger);
                if (!UnitVoiceSpeechPipeline.IsReady) throw new InvalidOperationException(UnitVoiceSpeechPipeline.Status);
                if (!ApiShared.Current.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out var lifecycle, out var diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Mission lifecycle unavailable.");
                UnitVoiceResources.Initialize(lifecycle);
                if (!lifecycle.TryRegisterObserver("Audio.UnitVoices", Started, Ended, Initializing, out diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Unit voice lifecycle registration failed.");
                Volatile.Write(ref lifecycleReady, 1);
                // A late lifecycle replay can precede the ready flag. Republish only data demand.
                UnitVoiceNativeRuntime.RefreshDemand();
                NativeApiLog.Info(log, "UNIT_VOICE_MANAGED_READY: permanent copy/record/request hooks; no component lifetime or polling.");
            }
            catch (Exception error)
            {
                Fail("Unit voice managed integration unavailable: " + error.Message);
                UnitVoiceResources.Fail(error.Message);
            }
        }
        private static void Started(MissionLifecycleNotification notification)
        {
            Volatile.Write(ref mission, notification.Context);
            UnitVoiceNativeRuntime.SetSession(notification.Context.SessionId, true);
        }
        private static void Ended(MissionLifecycleNotification notification)
        {
            var current = Volatile.Read(ref mission);
            if (current != null && current.SessionId != notification.Context.SessionId) return;
            Volatile.Write(ref mission, null);
            UnitVoiceNativeRuntime.SetSession(notification.Context.SessionId, false);
        }
        private static void Initializing(MissionLifecycleNotification notification)
        {
            Volatile.Write(ref mission, null);
            UnitVoiceNativeRuntime.SetSession(notification.Context.SessionId, false);
        }
        internal static void Play(SFXManager self, int channel, string path, float volume,
            bool ignoreMuting, bool ignorePause, EngineInterface.PlayState state)
        {
            // A failed copy-hook rebuild can leave an already accepted token unbound.
            // Restore its file even while dormant, without attributing it to an older state binding.
            if (UnitVoiceTransport.TryRecoverOriginalPath(path, out string recovered))
            { self.playSpeech(channel, recovered, volume, ignoreMuting, ignorePause); return; }
            // Do not allocate a closure or touch live game state on dormant or recursive paths.
            if (!ManagedReady || !UnitVoiceEvents.Demand || UnitVoiceEvents.InCallback ||
                !UnityMainThreadDispatch.IsMainThread || ExecutionContext.IsFlowSuppressed() ||
                !UnitVoiceTransport.TryGetBinding(state, out var binding))
            { self.playSpeech(channel, path, volume, ignoreMuting, ignorePause); return; }
            var current = Volatile.Read(ref mission);
            if (current == null || binding.Source.SessionId != current.SessionId || path != binding.OriginalPath)
            { self.playSpeech(channel, path, volume, ignoreMuting, ignorePause); return; }
            UnitVoiceContext context;
            try
            {
                string branch = ConfigSettings.Settings_EnglishSpeech || FatControler.UsesEnglishSpeechFolder()
                    ? "EnglishSpeech" : "LocalizedSpeech";
                context = new UnitVoiceContext(binding.Source, path, Interlocked.Increment(ref nextInvocation), channel, volume,
                    ignoreMuting, ignorePause, Localization.GameLocale.Current(), branch, current);
            }
            catch (Exception error)
            {
                // Optional metadata failures precede any original call; do not catch submission.
                Fail("Unit voice playback metadata unavailable: " + error.Message);
                self.playSpeech(channel, path, volume, ignoreMuting, ignorePause); return;
            }
            Dispatch(self, context);
        }
        private static void Dispatch(SFXManager self, UnitVoiceContext context)
        {
            UnitVoiceEvents.Dispatch(context, pre => Submit(self, pre));
            if (Volatile.Read(ref confirmed) == 0 && Interlocked.Exchange(ref confirmed, 1) == 0)
                NativeApiLog.Info(log, "UNIT_VOICE_PLAYBACK_CONFIRMED: Unity record callback after startup cleanup.");
        }
        private static void Submit(SFXManager self, UnitVoicePreEventArgs pre)
        {
            var context = pre.Context;
            var target = pre.Target;
            if (target.Kind == UnitVoiceTargetKind.Resource)
            {
                string basename = Basename(context.OriginalPath);
                string reason = "Original speech category is unavailable.";
                UnitVoiceCatalog.TryGetCue(context.Source.OriginalCueKey, out var originalCue);
                // An intent that Vanilla never routes has no folder of its own; a replacement is meant to be
                // heard, so it plays through its bank's folder. MyAudioManager does not check the file name.
                string folder = UnitVoiceCatalog.SpeechFolder(originalCue) ?? UnitVoiceCatalog.BankSpeechFolder(originalCue);
                if (folder != null &&
                    UnitVoiceSpeechPipeline.TryPlayResource(context.Channel, folder, basename, folder == "troops",
                        context.IgnoreSpeechMuting, context.IgnorePauseState, pre.VolumeFactor, target.Resource,
                        out var submission, out reason))
                { pre.SetPlaybackOutcome(submission.WasAccepted); return; }
                pre.FallBackToOriginal(reason);
            }
            string path = pre.Target.Kind == UnitVoiceTargetKind.Vanilla ? "fx\\speech\\" + pre.Target.Cue.FileName : context.OriginalPath;
            var result = UnitVoiceSpeechPipeline.Forward(self, context.Channel, path, context.OriginalVolume,
                context.IgnoreSpeechMuting, context.IgnorePauseState, pre.VolumeFactor);
            pre.SetPlaybackOutcome(result.WasAccepted);
        }
        private static string Basename(string path)
        {
            int at = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return path.Substring(at + 1).ToLowerInvariant();
        }
        private static void Fail(string message)
        {
            Volatile.Write(ref failure, message);
            if (FailureThrottle.ShouldReport(ref failureLogs))
                try { NativeApiLog.Error(log, message); } catch { }
        }
    }
}
