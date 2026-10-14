using System;
using APIShared.Events;

namespace APIShared.Audio
{
    /// <summary>Single owner for native-record playback interception; does not intercept UI, speech, music or every direct SFXManager call.</summary>
    public static class SoundEvents
    {
        internal static readonly SoundDemand Demand = SoundNativeDemand.Playback;
        private static readonly Func<int, bool> ValidEffect = SoundRuntime.IsVanillaEffect;
        private static readonly Func<SoundClipHandle, float, float, bool, bool> PlayClip = SoundClips.TryPlay;
        private static readonly Func<SoundPreEventArgs, object, SoundPostEventArgs> PostFactory = (pre, state) => new SoundPostEventArgs(pre, state);
        internal static readonly InterceptionEvent<SoundPreEventArgs, SoundPostEventArgs> Registry =
            new InterceptionEvent<SoundPreEventArgs, SoundPostEventArgs>(() => {
                if (!PlaybackAvailable) throw new InvalidOperationException(PlaybackDiagnostic);
            }, hasDemand: () => Demand.HasObservers);
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
            Demand.Attach(candidate);
            registration = candidate; return true;
        }
        // No capture or context construction may precede this production demand guard.
        internal static void Play(SFXManager manager, int sound, float volume, float pan, bool unstoppable, short[] buffer, int index)
        {
            if (!Registry.CanNotify) { manager.playSound(sound, volume, pan, unstoppable); return; }
            DispatchCore(SoundContextPolicy.Read(buffer, index, sound, volume, pan), unstoppable,
                ValidEffect, new Playback(manager, unstoppable), Registry, PlayClip);
        }
        // The callback-backed sink keeps isolated tests on the same core as real playback.
        internal static void Dispatch(SoundContext context, bool unstoppable, Func<int, bool> validEffect,
            Action<int, float, float> original, InterceptionEvent<SoundPreEventArgs, SoundPostEventArgs> registry = null, Func<SoundClipHandle, float, float, bool, bool> playClip = null)
        {
            registry = registry ?? Registry;
            if (!registry.CanNotify) { original(context.OriginalSoundId, context.OriginalVolume, context.OriginalPan); return; }
            DispatchCore(context, unstoppable, validEffect, new Playback(original), registry, playClip ?? PlayClip);
        }
        private static void DispatchCore(SoundContext context, bool unstoppable, Func<int, bool> validEffect, Playback playback,
            InterceptionEvent<SoundPreEventArgs, SoundPostEventArgs> registry, Func<SoundClipHandle, float, float, bool, bool> playClip)
        {
            var invocation = registry.Begin(new SoundPreEventArgs(context, unstoppable, validEffect));
            var pre = invocation.Pre;
            if (!pre.SkipOriginalFunction) {
                var clip = pre.SelectedClip;
                if (clip == null || !playClip(clip, pre.Volume, pre.Pan, unstoppable)) {
                    pre.FallBackToEffect(); playback.Invoke(pre.SoundId, pre.Volume, pre.Pan);
                }
            }
            if (invocation.HasPostObservers) invocation.Complete(pre, PostFactory);
        }
        private readonly struct Playback
        {
            private readonly SFXManager manager;
            private readonly bool unstoppable;
            private readonly Action<int, float, float> callback;
            internal Playback(SFXManager manager, bool unstoppable) { this.manager = manager; this.unstoppable = unstoppable; callback = null; }
            internal Playback(Action<int, float, float> callback) { manager = null; unstoppable = false; this.callback = callback; }
            internal void Invoke(int id, float gain, float pan)
            {
                if (callback != null) callback(id, gain, pan);
                else manager.playSound(id, gain, pan, unstoppable);
            }
        }
    }
}
