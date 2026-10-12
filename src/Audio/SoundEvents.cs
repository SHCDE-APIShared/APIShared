using System;
using APIShared.Events;

namespace APIShared.Audio
{
    /// <summary>Single owner for native-record playback interception; does not intercept UI, speech, music or every direct SFXManager call.</summary>
    public static class SoundEvents
    {
        internal static readonly InterceptionEvent<SoundPreEventArgs, SoundPostEventArgs> Registry =
            new InterceptionEvent<SoundPreEventArgs, SoundPostEventArgs>(() => {
                if (!PlaybackAvailable) throw new InvalidOperationException(PlaybackDiagnostic);
            });
        /// <summary>Managed playback availability, independent of native context support.</summary>
        public static bool PlaybackAvailable => SoundRuntime.PlaybackStatus == "Ready";
        /// <summary>Native ArabianIdleSwordDraw context availability; Unknown remains usable when unavailable.</summary>
        public static bool ArabianIdleContextAvailable => SoundRuntime.ContextStatus == "Ready";
        /// <summary>Currently supported catalog situations, including inferred interpretations. Unknown is always represented; native failure removes named contexts.</summary>
        public static System.Collections.Generic.IReadOnlyList<SoundSituation> SupportedSituations =>
            ArabianIdleContextAvailable ? KnownSituations : UnknownSituations;
        private static readonly System.Collections.Generic.IReadOnlyList<SoundSituation> KnownSituations =
            Array.AsReadOnly(BuildSituations());
        private static SoundSituation[] BuildSituations()
        {
            var values = new System.Collections.Generic.SortedSet<SoundSituation> { SoundSituation.Unknown };
            foreach (var source in SoundSources.All) foreach (int effect in source.Effects) values.Add(source.Situation(effect));
            var result = new SoundSituation[values.Count]; values.CopyTo(result); return result;
        }
        private static readonly System.Collections.Generic.IReadOnlyList<SoundSituation> UnknownSituations =
            Array.AsReadOnly(new[] { SoundSituation.Unknown });
        /// <summary>Managed availability or initialization failure reason.</summary>
        public static string PlaybackDiagnostic => SoundRuntime.PlaybackStatus;
        /// <summary>Native context availability or initialization/runtime failure reason.</summary>
        public static string ContextDiagnostic => SoundRuntime.ContextStatus;
        /// <summary>Known native source calls rejected by unit/type/coordinate/state validation. They remain Unknown; a bounded first rejection is logged.</summary>
        public static long ContextRejections => SoundRuntime.ContextRejections;
        /// <summary>Isolated Pre/Post callback failure count.</summary>
        public static long CallbackFailures => Registry.CallbackFailures;
        /// <summary>Register during Unity startup after APIShared managed initialization. Callbacks run synchronously on the Unity playback thread, outside registry locks, without replay, ordered by order/ordinal owner/ID. A failed callback rolls back its changes; successful vetoes are sticky. Recursive playback in callbacks bypasses notifications. Registration and handles persist until exit. Changes affect presentation only; audio asset availability is local. Returns false with a reason for invalid/duplicate registrations or unavailable managed interception.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, Action<SoundPreEventArgs> pre,
            Action<SoundPostEventArgs> post, out SoundRegistration registration, out string reason, int order = 0)
        {
            var candidate = new SoundRegistration();
            if (!Registry.TryRegister(ownerGuid, registrationId, pre, post, out reason, order, isActive: () => candidate.Enabled))
            { registration = null; return false; }
            registration = candidate; return true;
        }
        internal static void Dispatch(SoundContext context, bool unstoppable, Func<int, bool> validEffect,
            Action<int, float, float> original, InterceptionEvent<SoundPreEventArgs, SoundPostEventArgs> registry = null, Func<SoundClipHandle, float, float, bool, bool> playClip = null)
        {
            registry = registry ?? Registry;
            if (!registry.CanNotify) { original(context.OriginalSoundId, context.OriginalVolume, context.OriginalPan); return; }
            var invocation = registry.Begin(new SoundPreEventArgs(context, unstoppable, validEffect));
            if (!invocation.Pre.SkipOriginalFunction) {
                var pre = invocation.Pre;
                var target = pre.Target;
                if (target.Kind != SoundTargetKind.CustomClip || !(playClip ?? SoundClips.TryPlay)(target.Clip, pre.Volume, pre.Pan, unstoppable)) {
                    pre.FallBackToEffect(); original(pre.SoundId, pre.Volume, pre.Pan);
                }
            }
            if (invocation.HasPostObservers) invocation.Complete(state => new SoundPostEventArgs(invocation.Pre, state));
        }
    }
}
