using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.Extensions;
using UnityEngine;

namespace APIShared.Audio
{
    /// <summary>Stable owner identity. Availability changes, identity does not. Borrowed clips are never destroyed here.</summary>
    public sealed class SoundClipHandle
    {
        internal AudioClip Clip;
        internal int Generation;
        private int ready;
        internal SoundClipHandle(string owner, string id, float gain, bool map) { OwnerGuid = owner; RegistrationId = id; BaseVolume = gain; MapScoped = map; }
        /// <summary>Owning mod GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local identity.</summary>
        public string RegistrationId { get; }
        /// <summary>Finite nonnegative multiplier for this clip.</summary>
        public float BaseVolume { get; }
        /// <summary>Logical availability. Actual Unity validity is checked again on playback.</summary>
        public bool IsReady => Volatile.Read(ref ready) != 0 && (!MapScoped || (SoundClips.MapAvailable && Generation == SoundClips.MapGeneration));
        /// <summary>Invalidated at map unload, load, save-load and start boundaries; register after OnPostLoad Post.</summary>
        public bool MapScoped { get; }
        /// <summary>Loading or invalidation diagnostic.</summary>
        public string Diagnostic { get; internal set; } = "Pending";
        internal void Publish(AudioClip value) { Clip = value; Diagnostic = "Ready"; Volatile.Write(ref ready, 1); }
        internal void Invalidate(string reason) { Volatile.Write(ref ready, 0); Diagnostic = reason; Clip = null; }
    }

    /// <summary>Preloaded clips for native-record presentation replacement. No loading happens inside sound callbacks.</summary>

    public static class SoundClips
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, SoundClipHandle> handles = new Dictionary<string, SoundClipHandle>(StringComparer.Ordinal);
        private static readonly HashSet<SoundClipHandle> owned = new HashSet<SoundClipHandle>();
        private static IDisposable[] mapSubscriptions;
        private static int mapFaulted;
        private static string mapDiagnostic = "Ready";
        /// <summary>Map-lifetime publisher availability or isolated failure reason.</summary>
        public static string MapDiagnostic => Volatile.Read(ref mapDiagnostic);
        internal static bool MapAvailable => Volatile.Read(ref mapFaulted) == 0;
        private static int epoch;
        internal static int MapGeneration => Volatile.Read(ref epoch);
        private static long callbackFailures;
        /// <summary>Isolated load-completion callback failures.</summary>
        public static long CallbackFailures => Interlocked.Read(ref callbackFailures);
        private static FieldInfo playList, clipField;
        internal static void Initialize()
        {
            ValidateMembers();
            // The static R3 publisher and static subscription root survive plugin destruction.
            mapSubscriptions = new[] {
                MapLoaderR3EventHooks.OnUnloadMap.Observable.Subscribe(MapBoundary),
                MapLoaderR3EventHooks.OnLoadMap.Observable.Subscribe(MapBoundary),
                MapLoaderR3EventHooks.OnLoadSave.Observable.Subscribe(MapBoundary),
                MapLoaderR3EventHooks.OnStartMap.Observable.Subscribe(MapBoundary)
            };
        }
        private static void MapBoundary(EventHookBase e)
        {
            if (e.Phase != EventHookPhase.Pre) return;
            // SE can suppress unload inside campaign/skirmish/non-skirmish startup.
            // Conservative generation boundaries also cover loading, saves and starts.
            // These publisher callbacks must not escape: SE catch paths may repeat Vanilla.
            try {
                var destroy = DetachMapClips();
                UnityMainThreadDispatcher.EnqueueStatic(() => {
                    foreach (var clip in destroy) if (clip != null) UnityEngine.Object.Destroy(clip);
                });
            } catch (Exception ex) {
                Volatile.Write(ref mapDiagnostic, "Unavailable: " + ex.Message);
                if (Interlocked.Exchange(ref mapFaulted, 1) == 0) SoundRuntime.ReportClipBoundaryFailure(ex);
            }
        }

        internal static void ValidateMembers()
        {
            playList = typeof(SFXManager).GetField("play_list", BindingFlags.Instance | BindingFlags.NonPublic);
            if (playList == null || !playList.FieldType.IsGenericType || playList.FieldType.GetGenericTypeDefinition() != typeof(List<>) || playList.FieldType.GetGenericArguments()[0].FullName != "SFXManager+sh1_sound")
                throw new InvalidOperationException("Private clip table changed.");
            clipField = playList.FieldType.GetGenericArguments()[0].GetField("clip");
            if (clipField == null || !clipField.IsPublic || clipField.FieldType != typeof(AudioClip)) throw new InvalidOperationException("Clip field changed.");
            var play = typeof(MyAudioManager).GetMethod("playSFX", new[] { typeof(AudioClip), typeof(float), typeof(float), typeof(bool), typeof(bool) });
            if (play == null || !play.IsPublic || play.ReturnType != typeof(void)) throw new InvalidOperationException("Custom clip playback signature changed.");
        }
        /// <summary>Queue owner-private mod resource loading on Unity. Resource indexes must already be registered by the Extender. Completion runs deferred on Unity; never block waiting for it. Handle is process-scoped.</summary>
        public static bool TryLoadModClip(string ownerGuid, string registrationId, string relativePath, Action<SoundClipHandle> completed,
            out SoundClipHandle handle, out string reason, float baseVolume = 1f)
            => Register(ownerGuid, registrationId, baseVolume, false, () => {
                if (!GameAssetManagerAPI.Instance.GetModFileBinaryContent(ownerGuid, relativePath, out var bytes)) throw new InvalidOperationException("Mod audio resource not found: " + relativePath);
                return AudioClipExtensions.LoadAudioClipFromBytes(bytes);
            }, true, completed, out handle, out reason);
        /// <summary>Queue a current-map archive resource. Register after OnPostLoad Post; load/start boundaries also invalidate pending handles. Handle becomes invalid at map unload; the same owner/ID may be registered again on a later map with the same base volume. No asset-mod fallback.</summary>
        public static bool TryLoadMapClip(string ownerGuid, string registrationId, string archivePath, Action<SoundClipHandle> completed,
            out SoundClipHandle handle, out string reason, float baseVolume = 1f)
            => Register(ownerGuid, registrationId, baseVolume, true, () => {
                var bytes = GameMapArchiveManagerAPI.Instance.TryReadBinaryFile(archivePath);
                if (bytes == null) throw new InvalidOperationException("Map audio resource not found: " + archivePath);
                return AudioClipExtensions.LoadAudioClipFromBytes(bytes);
            }, true, completed, out handle, out reason);
        /// <summary>Queue capture of an existing Extender play-list ID into a borrowed map-scoped handle. IDs are resolved once on Unity, never reused after list shifts. Caller must import while the ID still denotes the intended clip. BaseVolume is explicit; Extender playback also ignores list-entry volume.</summary>
        public static bool TryImportExtenderClip(string ownerGuid, string registrationId, int clipId, Action<SoundClipHandle> completed,
            out SoundClipHandle handle, out string reason, float baseVolume = 1f)
            => Register(ownerGuid, registrationId, baseVolume, true, () => {
                if (SFXManager.instance == null) throw new InvalidOperationException("SFXManager unavailable.");
                var list = playList.GetValue(SFXManager.instance) as IList;
                if (list == null || clipId < 0 || clipId >= list.Count) throw new ArgumentOutOfRangeException(nameof(clipId));
                return (AudioClip)clipField.GetValue(list[clipId]);
            }, false, completed, out handle, out reason);
        internal static bool Register(string owner, string id, float gain, bool map, Func<AudioClip> load, bool owns, Action<SoundClipHandle> callback,
            out SoundClipHandle handle, out string reason, Action<Action> queue = null, Func<AudioClip, bool> alive = null,
            Action<AudioClip> release = null, Action<AudioClip, string> name = null)
        {
            queue = queue ?? UnityMainThreadDispatcher.EnqueueStatic;
            alive = alive ?? (clip => clip != null);
            release = release ?? (clip => UnityEngine.Object.Destroy(clip));
            name = name ?? ((clip, text) => clip.name = text);
            handle = null; reason = null;
            if (!SoundEvents.PlaybackAvailable) { reason = SoundEvents.PlaybackDiagnostic; return false; }
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || owner.IndexOf('\0') >= 0 || id.IndexOf('\0') >= 0 || !SoundPreEventArgs.Finite(gain) || gain < 0) { reason = "Owner, ID and finite nonnegative base volume required."; return false; }
            if (map && !MapAvailable) { reason = MapDiagnostic; return false; }
            int generation = Volatile.Read(ref epoch);
            SoundClipHandle value;
            lock (gate) {
                string key = owner + "\0" + id;
                if (handles.TryGetValue(key, out value)) {
                    if (!map || !value.MapScoped || value.Generation >= generation || value.BaseVolume != gain) { reason = "Duplicate clip registration."; return false; }
                    value.Generation = generation;
                    value.Invalidate("Pending");
                } else {
                    value = new SoundClipHandle(owner, id, gain, map) { Generation = generation };
                    handles.Add(key, value);
                }
            }
            handle = value;
            queue(() => {
                AudioClip clip = null;
                try {
                    if (map && generation != Volatile.Read(ref epoch)) throw new InvalidOperationException("Map changed before clip loading.");
                    clip = load();
                    if (!alive(clip)) throw new InvalidOperationException("Clip could not be decoded or no longer exists.");
                    lock (gate) {
                        if (map && generation != epoch) throw new InvalidOperationException("Map changed during clip loading.");
                        if (owns) { name(clip, "APIShared_" + owner.Length + "_" + owner + "_" + id); owned.Add(value); }
                        value.Publish(clip);
                    }
                } catch (Exception ex) {
                    try { if (owns && !ReferenceEquals(clip, null)) release(clip); }
                    finally { if (!map || value.Generation == generation) value.Invalidate(ex.Message); }
                }
                try { callback?.Invoke(value); } catch { Interlocked.Increment(ref callbackFailures); }
            });
            return true;
        }
        internal static List<AudioClip> DetachMapClips()
        {
            var destroy = new List<AudioClip>();
            lock (gate) {
                int cutoff = Interlocked.Increment(ref epoch);
                foreach (var value in handles.Values) if (value.MapScoped && value.Generation < cutoff) {
                    var clip = value.Clip;
                    if (owned.Remove(value) && !ReferenceEquals(clip, null)) destroy.Add(clip);
                    value.Invalidate("Map unloaded.");
                }
            }
            return destroy;
        }
        internal static bool TryPlay(SoundClipHandle handle, float gain, float pan, bool unstoppable)
        {
            if (handle == null || !handle.IsReady) return false;
            var clip = handle.Clip;
            if (clip == null) { handle.Invalidate("Clip destroyed by its owner."); return false; }
            float volume = gain * handle.BaseVolume;
            if (!SoundPreEventArgs.Finite(volume) || MyAudioManager.Instance == null) return false;
            // Preserve the Extender's downstream playSFX override hook and Vanilla channel/global policy.
            MyAudioManager.Instance.playSFX(clip, volume, pan, unstoppable, false);
            return true;
        }
    }
}
