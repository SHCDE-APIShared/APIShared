using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Events;
using APIShared.Internal;
using SHCDESE.API;
using SHCDESE.Extensions;
using UnityEngine;

namespace APIShared.Audio
{
    /// <summary>Preparation state of one immutable voice-resource registration.</summary>
    public enum UnitVoiceResourceState
    {
        /// <summary>Preparation has been queued and has not completed.</summary>
        Pending,
        /// <summary>Immutable decoded samples are available.</summary>
        Ready,
        /// <summary>Preparation failed; Diagnostic describes the failure.</summary>
        Failed,
        /// <summary>The map generation owning this registration has ended.</summary>
        Invalidated
    }

    /// <summary>Owner-private, prepared voice samples. This is not a Unity clip or a Vanilla sound ID.</summary>
    /// <remarks>Mod handles last until process exit. Map handles belong to the generation in which they
    /// were prepared and never retarget to a later map. Readiness and diagnostics may be read on any thread.</remarks>
    public sealed class UnitVoiceResourceHandle
    {
        private readonly UnitVoiceResourceStore store;
        private UnitVoicePcmData samples;
        private string diagnostic = "Pending";
        private int state;
        internal readonly int Generation;
        internal UnitVoiceResourceHandle(UnitVoiceResourceStore store, string owner, string id, bool map, int generation, float gain)
        { this.store = store; OwnerGuid = owner; RegistrationId = id; MapScoped = map; Generation = generation; BaseVolume = gain; }
        /// <summary>The plugin GUID which owns the indexed resource.</summary>
        public string OwnerGuid { get; }
        /// <summary>The owner's stable resource registration ID.</summary>
        public string RegistrationId { get; }
        /// <summary>True for a resource read from one current map archive.</summary>
        public bool MapScoped { get; }
        /// <summary>Finite, nonnegative resource gain, multiplied with the request's volume factor.</summary>
        public float BaseVolume { get; }
        /// <summary>True after preparation succeeds, until this handle's map generation ends.</summary>
        public bool IsReady => State == UnitVoiceResourceState.Ready && ReadSamples() != null;
        /// <summary>The preparation state, including immediate map-generation invalidation.</summary>
        public UnitVoiceResourceState State => MapScoped && Generation != store.Generation ? UnitVoiceResourceState.Invalidated : (UnitVoiceResourceState)Volatile.Read(ref state);
        /// <summary>Preparation or invalidation diagnostic. Use IsReady for availability decisions.</summary>
        public string Diagnostic => Volatile.Read(ref diagnostic);
        /// <summary>Samples per channel, or zero before preparation or after invalidation.</summary>
        public int SamplesPerChannel => ReadSamples()?.Frames ?? 0;
        /// <summary>Interleaved channel count, or zero while unavailable.</summary>
        public int Channels => ReadSamples()?.Channels ?? 0;
        /// <summary>Sample rate in hertz, or zero while unavailable.</summary>
        public int Frequency => ReadSamples()?.Frequency ?? 0;
        /// <summary>Bytes occupied by decoded float PCM, excluding playback clips and object overhead; zero while unavailable.</summary>
        public long DecodedByteCount { get { var value = ReadSamples(); return value == null ? 0 : checked((long)value.Frames * value.Channels * sizeof(float)); } }
        /// <summary>Prepared duration in seconds, or zero while unavailable.</summary>
        public double DurationSeconds { get { var value = ReadSamples(); return value == null ? 0 : (double)value.Frames / value.Frequency; } }
        internal UnitVoicePcmData ReadSamples() => MapScoped && Generation != store.Generation ? null : Volatile.Read(ref samples);
        internal void Publish(UnitVoicePcmData value) { Volatile.Write(ref samples, value); Volatile.Write(ref diagnostic, "Ready"); Volatile.Write(ref state, (int)UnitVoiceResourceState.Ready); }
        internal void Invalidate(string reason, bool retired = false)
        { Volatile.Write(ref samples, null); Volatile.Write(ref diagnostic, reason); Volatile.Write(ref state, (int)(retired ? UnitVoiceResourceState.Invalidated : UnitVoiceResourceState.Failed)); }
    }

    /// <summary>Prepares immutable PCM for replacement voices without retaining a shared Unity AudioClip.</summary>
    /// <remarks>Resources use the Extender's WAV/OGG decoder: WAV is PCM16, 44100 Hz, mono or stereo;
    /// OGG is mono/stereo Vorbis. Preparation and completion are deferred on Unity. Successful registration means
    /// queued, not decoded. Prepare after the owner's Extender resource index is available and outside
    /// voice callbacks. Playback creates its own clip and performs no resource I/O or decoding.</remarks>
    public static class UnitVoiceResources
    {
        private static readonly UnitVoiceResourceStore store = new UnitVoiceResourceStore();
        private static int initialized;
        private static int mapReady;
        private static string diagnostic = "Pending";
        /// <summary>Whether resource preparation and map-lifetime observation are initialized.</summary>
        public static bool IsReady => Volatile.Read(ref initialized) != 0;
        /// <summary>Preparation-service availability, independent of native voice attribution.</summary>
        public static string Diagnostic => Volatile.Read(ref diagnostic);
        /// <summary>Isolated exceptions thrown by preparation completion callbacks.</summary>
        public static long CallbackFailures => store.CallbackFailures;

        internal static void Initialize(IMissionLifecycleCapability lifecycle)
        {
            if (IsReady) return;
            if (lifecycle == null) throw new ArgumentNullException(nameof(lifecycle));
            if (!lifecycle.TryRegisterObserver("Audio.UnitVoiceResources", Started, Boundary, Initialization, out var reason))
                throw new InvalidOperationException(reason?.Reason ?? "Unit voice resource lifecycle unavailable.");
            Volatile.Write(ref diagnostic, "Ready");
            Volatile.Write(ref initialized, 1);
        }
        private static void Started(MissionLifecycleNotification value) => Volatile.Write(ref mapReady, 1);
        private static void Initialization(MissionLifecycleNotification value)
        { if (value.IsBeforeInitialization) RetireMap(); }
        private static void Boundary(MissionLifecycleNotification value) => RetireMap();
        private static void RetireMap() { Volatile.Write(ref mapReady, 0); store.InvalidateMapResources(); }

        /// <summary>Queues preparation of an indexed owner-private mod resource. A ready handle is process-scoped.</summary>
        /// <remarks>Any thread after APIShared initialization. Completion runs deferred on Unity, including
        /// failures, and exceptions are isolated. Duplicate pending/ready owner-ID registrations are rejected.
        /// Do not wait synchronously for completion or call from UnitVoiceEvents callbacks.</remarks>
        public static bool TryPrepareModResource(string ownerGuid, string registrationId, string relativePath,
            Action<UnitVoiceResourceHandle> completed, out UnitVoiceResourceHandle handle, out string reason, float baseVolume = 1f)
            => Prepare(ownerGuid, registrationId, relativePath, false, completed, out handle, out reason, baseVolume);

        /// <summary>Queues a resource from the current map archive, with no mod-resource fallback.</summary>
        /// <remarks>Prepare after the map has started. End and initialization boundaries immediately invalidate
        /// the handle and pending preparation; the same owner-ID may be registered for a later generation,
        /// producing a new handle. Already transferred playback clips retain their own lifetime.</remarks>
        public static bool TryPrepareMapResource(string ownerGuid, string registrationId, string archivePath,
            Action<UnitVoiceResourceHandle> completed, out UnitVoiceResourceHandle handle, out string reason, float baseVolume = 1f)
            => Prepare(ownerGuid, registrationId, archivePath, true, completed, out handle, out reason, baseVolume);

        private static bool Prepare(string owner, string id, string path, bool map, Action<UnitVoiceResourceHandle> completed,
            out UnitVoiceResourceHandle handle, out string reason, float gain)
        {
            handle = null;
            if (!IsReady) { reason = Diagnostic; return false; }
            if (UnitVoiceEvents.InCallback) { reason = "Prepare voice resources outside UnitVoiceEvents callbacks."; return false; }
            int generation = store.Generation;
            if (map && Volatile.Read(ref mapReady) == 0) { reason = "Prepare map voice resources after a successful mission start."; return false; }
            return store.TryPrepare(owner, id, path, map, gain, () => Decode(Read(owner, path, map)),
                action => {
                    if (!UnityMainThreadDispatch.TryEnqueue(action)) throw new InvalidOperationException("Unity dispatch unavailable.");
                }, completed, out handle, out reason, map ? (int?)generation : null);
        }
        private static byte[] Read(string owner, string path, bool map)
        {
            if (map) return GameMapArchiveManagerAPI.Instance.TryReadBinaryFile(path) ?? throw new InvalidOperationException("Map voice resource not found: " + path);
            if (!GameAssetManagerAPI.Instance.GetModFileBinaryContent(owner, path, out var bytes))
                throw new InvalidOperationException("Mod voice resource not found: " + path);
            return bytes;
        }
        private static UnitVoicePcmData Decode(byte[] bytes)
        {
            if (!UnityMainThreadDispatch.IsMainThread) throw new InvalidOperationException("Voice decoding requires Unity.");
            UnitVoiceEncodedAudio.Validate(bytes);
            AudioClip temporary = null;
            try
            {
                temporary = AudioClipExtensions.LoadAudioClipFromBytes(bytes);
                if (temporary == null) throw new InvalidOperationException("Voice resource could not be decoded.");
                int frames = temporary.samples, channels = temporary.channels, frequency = temporary.frequency;
                var data = new float[checked(frames * channels)];
                if (!temporary.GetData(data, 0)) throw new InvalidOperationException("Decoded voice samples are unavailable.");
                return new UnitVoicePcmData(frames, channels, frequency, data);
            }
            finally { if (temporary != null) UnityEngine.Object.Destroy(temporary); }
        }
        internal static AudioClip CreatePlaybackClip(UnitVoiceResourceHandle handle)
        {
            if (!UnityMainThreadDispatch.IsMainThread) throw new InvalidOperationException("Voice clip creation requires Unity.");
            var data = handle?.ReadSamples() ?? throw new InvalidOperationException("Voice resource is not ready.");
            AudioClip clip = null;
            try
            {
                clip = AudioClip.Create("APIShared.UnitVoice/" + handle.OwnerGuid + "/" + handle.RegistrationId,
                    data.Frames, data.Channels, data.Frequency, false);
                if (clip == null || !data.WriteTo(clip)) throw new InvalidOperationException("Voice playback clip could not be created.");
                if (!ReferenceEquals(handle.ReadSamples(), data)) throw new InvalidOperationException("Map changed during voice clip creation.");
                return clip;
            }
            catch { if (clip != null) UnityEngine.Object.Destroy(clip); throw; }
        }
    }

    internal static class UnitVoiceEncodedAudio
    {
        // Validate the RIFF traversal before entering the Extender decoder, whose WAV reader
        // assumes fixed PCM fmt offsets and advances by the signed chunk size without padding.
        internal static void Validate(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) throw new ArgumentException("Voice audio is empty or truncated.");
            if (Tag(bytes, 0, "OggS")) return;
            if (bytes.Length < 44 || !Tag(bytes, 0, "RIFF") || !Tag(bytes, 8, "WAVE") || !Tag(bytes, 12, "fmt "))
                throw new ArgumentException("Voice audio must be OGG Vorbis or a PCM WAV with a leading fmt chunk.");
            if ((ulong)Read32(bytes, 4) + 8 != (ulong)bytes.Length) throw new ArgumentException("WAV RIFF size does not match its complete payload.");
            uint fmtSize = Read32(bytes, 16);
            int channels = Read16(bytes, 22), alignment = Read16(bytes, 32);
            if (fmtSize < 16 || Read16(bytes, 20) != 1 || (channels != 1 && channels != 2) || Read32(bytes, 24) != 44100 || Read16(bytes, 34) != 16 ||
                alignment != channels * 2 || Read32(bytes, 28) != (uint)(44100 * alignment))
                throw new ArgumentException("WAV voices require complete PCM16, 44100 Hz, mono/stereo headers.");
            int position = 12;
            bool data = false;
            while (position < bytes.Length)
            {
                if (bytes.Length - position < 8) throw new ArgumentException("WAV chunk header is truncated.");
                uint length = Read32(bytes, position + 4);
                if (length > int.MaxValue || (ulong)position + 8 + length > (ulong)bytes.Length)
                    throw new ArgumentException("WAV chunk exceeds its payload.");
                if ((length & 1) != 0) throw new ArgumentException("Odd-sized WAV chunks are not supported by the selected decoder.");
                if (Tag(bytes, position, "data"))
                {
                    if (length == 0 || length % alignment != 0) throw new ArgumentException("WAV PCM data has incomplete sample frames.");
                    data = true;
                }
                position = checked(position + 8 + (int)length);
            }
            if (!data) throw new ArgumentException("WAV audio contains no sample data.");
        }
        private static bool Tag(byte[] bytes, int position, string value) => position + 4 <= bytes.Length &&
            bytes[position] == value[0] && bytes[position + 1] == value[1] && bytes[position + 2] == value[2] && bytes[position + 3] == value[3];
        private static uint Read32(byte[] bytes, int at) => (uint)(bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24);
        private static int Read16(byte[] bytes, int at) => bytes[at] | bytes[at + 1] << 8;
    }

    // This immutable data boundary is shared by production preparation and game-independent fixtures.
    internal sealed class UnitVoicePcmData
    {
        internal readonly int Frames, Channels, Frequency;
        private readonly float[] samples;
        internal UnitVoicePcmData(int frames, int channels, int frequency, float[] values)
        {
            if (frames <= 0 || (channels != 1 && channels != 2) || frequency <= 0 || values == null || values.Length != checked(frames * channels))
                throw new ArgumentException("Complete interleaved voice samples are required.");
            foreach (float sample in values) if (float.IsNaN(sample) || float.IsInfinity(sample)) throw new ArgumentException("Voice samples must be finite.");
            Frames = frames; Channels = channels; Frequency = frequency; samples = (float[])values.Clone();
        }
        internal float[] CopySamples() => (float[])samples.Clone();
        internal bool WriteTo(AudioClip clip) => clip.SetData(samples, 0);
    }

    internal sealed class UnitVoiceResourceStore
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, UnitVoiceResourceHandle> handles = new Dictionary<string, UnitVoiceResourceHandle>(StringComparer.Ordinal);
        private int generation;
        private long callbackFailures;
        internal int Generation => Volatile.Read(ref generation);
        internal long CallbackFailures => Interlocked.Read(ref callbackFailures);
        internal bool TryPrepare(string owner, string id, string path, bool map, float gain, Func<UnitVoicePcmData> decode,
            Action<Action> queue, Action<UnitVoiceResourceHandle> completed, out UnitVoiceResourceHandle handle, out string reason, int? expectedGeneration = null)
        {
            handle = null;
            if (string.IsNullOrWhiteSpace(owner) || owner.IndexOf('\0') >= 0 || string.IsNullOrWhiteSpace(id) || id.IndexOf('\0') >= 0 ||
                !SafePath(path) || float.IsNaN(gain) || float.IsInfinity(gain) || gain < 0 || decode == null || queue == null)
            { reason = "Owner, resource ID, safe relative path and finite nonnegative gain are required."; return false; }
            UnitVoiceResourceHandle value;
            lock (sync)
            {
                if (map && expectedGeneration.HasValue && expectedGeneration.Value != Generation)
                { reason = "Map changed before voice preparation was registered."; return false; }
                string key = owner + "\0" + id;
                if (handles.TryGetValue(key, out var old) && (!map || !old.MapScoped || old.Generation == Generation))
                { reason = "This owner already registered that voice resource ID."; return false; }
                value = new UnitVoiceResourceHandle(this, owner, id, map, Generation, gain);
                handles[key] = value;
            }
            try { queue(() => Complete(value, decode, completed)); }
            catch (Exception error)
            {
                value.Invalidate("Preparation could not be queued: " + error.Message);
                lock (sync)
                {
                    string key = owner + "\0" + id;
                    if (handles.TryGetValue(key, out var current) && ReferenceEquals(current, value)) handles.Remove(key);
                }
                reason = value.Diagnostic; return false;
            }
            handle = value; reason = string.Empty; return true;
        }
        private void Complete(UnitVoiceResourceHandle handle, Func<UnitVoicePcmData> decode, Action<UnitVoiceResourceHandle> completed)
        {
            try
            {
                if (handle.MapScoped && handle.Generation != Generation) throw new InvalidOperationException("Map changed before voice preparation.");
                var data = decode() ?? throw new InvalidOperationException("Voice decoder produced no samples.");
                lock (sync)
                {
                    if (handle.MapScoped && handle.Generation != Generation) throw new InvalidOperationException("Map changed during voice preparation.");
                    handle.Publish(data);
                }
            }
            catch (Exception error) { handle.Invalidate(error.Message, handle.MapScoped && handle.Generation != Generation); }
            try { completed?.Invoke(handle); }
            catch (Exception error)
            {
                long count = Interlocked.Increment(ref callbackFailures);
                if (count == 1 || (count & (count - 1)) == 0)
                    InterceptionDiagnostics.Report(handle.OwnerGuid + "/" + handle.RegistrationId + " UnitVoiceResource completion", error);
            }
        }
        internal void InvalidateMapResources()
        {
            lock (sync)
            {
                Interlocked.Increment(ref generation);
                foreach (var handle in handles.Values) if (handle.MapScoped) handle.Invalidate("Map generation ended.", true);
            }
        }
        internal static bool SafePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || path.IndexOf(':') >= 0) return false;
            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)) return false;
            foreach (string part in normalized.Split('/')) if (part.Length == 0 || part == "." || part == "..") return false;
            return true;
        }
    }
}
