using System;
using System.Threading;
using APIShared.Internal;

namespace APIShared.Presentation
{
    /// <summary>Process-local identity of one native notification presentation.</summary>
    public readonly struct NotificationPresentationId : IEquatable<NotificationPresentationId>
    {
        internal NotificationPresentationId(ulong value) => Value = value;
        internal ulong Value { get; }

        /// <summary>Whether this value identifies a presentation. It may already have ended.</summary>
        public bool IsValid => Value != 0;

        /// <inheritdoc/>
        public bool Equals(NotificationPresentationId other) => Value == other.Value;
        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is NotificationPresentationId other && Equals(other);
        /// <inheritdoc/>
        public override int GetHashCode() => Value.GetHashCode();
    }

    /// <summary>Observes notification identities without changing queue or playback state.</summary>
    public static class NotificationPresentations
    {
        private sealed class Rendered
        {
            internal Rendered(NotificationPresentationRecord record, long epoch, Uri request = null)
            {
                Id = new NotificationPresentationId(record.Ticket);
                HasVideo = record.HasVideo;
                Epoch = epoch;
                NativeEpoch = record.Epoch;
                Request = request;
            }

            internal readonly NotificationPresentationId Id;
            internal readonly bool HasVideo;
            internal readonly long Epoch;
            internal readonly int NativeEpoch;
            internal readonly Uri Request;
        }

        private static Rendered rendered;
        private static long textTicket;
        private static long epoch;
        [ThreadStatic] private static NotificationPresentationId dispatching;
        [ThreadStatic] private static bool dispatchingSpeech;

        internal readonly struct DispatchScope
        {
            internal DispatchScope(NotificationPresentationId id, bool speech) { Id = id; Speech = speech; }
            internal readonly NotificationPresentationId Id;
            internal readonly bool Speech;
        }

        /// <summary>Whether the loaded game has validated observation at both native and Unity boundaries.</summary>
        public static bool IsAvailable => NotificationNativeRuntime.IsReady && LordMessageRuntime.IsReady;

        /// <summary>Gets the most recently observed presentation on the Unity thread.</summary>
        /// <remarks>The simulation may already have prepared its successor.</remarks>
        public static bool TryGetRendered(out NotificationPresentationId id) => TryGetRendered(out id, out _);

        /// <summary>Gets the rendered identity and whether it submitted a native video request.</summary>
        public static bool TryGetRendered(out NotificationPresentationId id, out bool hasVideo)
        {
            var current = Current();
            id = current?.Id ?? default;
            hasVideo = current?.HasVideo ?? false;
            return id.IsValid;
        }

        /// <summary>Gets the accepted URI of the rendered video on the Unity thread.</summary>
        /// <remarks>The caller must check that the same request and HUD still own the displayed media.</remarks>
        public static bool TryGetRenderedVideo(out NotificationPresentationId id, out Uri request)
        {
            var current = Current();
            request = current?.Request;
            id = request == null ? default : current.Id;
            return id.IsValid;
        }

        /// <summary>Whether the current message-bar assignment belongs to this identity on Unity.</summary>
        public static bool IsRenderedText(NotificationPresentationId id) =>
            id.IsValid && Current()?.Id.Equals(id) == true && unchecked((ulong)Interlocked.Read(ref textTicket)) == id.Value;

        /// <summary>Gets the presentation identity within its Unity speech or video submission.</summary>
        public static bool TryGetDispatching(out NotificationPresentationId id)
        {
            id = IsAvailable && UnityMainThreadDispatch.IsMainThread ? dispatching : default;
            return id.IsValid;
        }

        /// <summary>Gets the presentation identity within its Unity speech submission only.</summary>
        public static bool TryGetDispatchingSpeech(out NotificationPresentationId id)
        {
            id = default;
            return dispatchingSpeech && TryGetDispatching(out id);
        }

        /// <summary>Checks the current native identity from within the serialized engine call.</summary>
        public static bool IsNativeCurrent(NotificationPresentationId id) =>
            id.IsValid && IsAvailable && NotificationNativeRuntime.IsCurrent(id.Value);

        internal static long Epoch => Interlocked.Read(ref epoch);

        private static Rendered Current()
        {
            if (!IsAvailable || !NotificationNativeRuntime.HasActivePresentation || !UnityMainThreadDispatch.IsMainThread) return null;
            var current = Volatile.Read(ref rendered);
            return current != null && current.Epoch == Epoch && current.NativeEpoch == NotificationNativeRuntime.Epoch ? current : null;
        }

        internal static void Reset()
        {
            Interlocked.Increment(ref epoch);
            Volatile.Write(ref rendered, null);
            RetireText();
        }

        internal static void RetireText() => Interlocked.Exchange(ref textTicket, 0);
        internal static void ForgetRendered() => Volatile.Write(ref rendered, null);

        internal static void Observe(NotificationPresentationRecord record, long expectedEpoch, bool text = false, Uri request = null)
        {
            if (expectedEpoch != Epoch || record.Epoch != NotificationNativeRuntime.Epoch) return;
            var current = Volatile.Read(ref rendered);
            if (current == null || current.Id.Value != record.Ticket || current.Epoch != expectedEpoch || request != null)
                Volatile.Write(ref rendered, new Rendered(record, expectedEpoch, request));
            if (text) Interlocked.Exchange(ref textTicket, unchecked((long)record.Ticket));
        }

        internal static DispatchScope EnterDispatch(NotificationPresentationRecord record, bool speech = false)
        {
            var previous = new DispatchScope(dispatching, dispatchingSpeech);
            dispatching = new NotificationPresentationId(record.Ticket);
            dispatchingSpeech = speech;
            return previous;
        }

        internal static DispatchScope SuspendDispatch()
        {
            var previous = new DispatchScope(dispatching, dispatchingSpeech);
            dispatching = default;
            dispatchingSpeech = false;
            return previous;
        }

        internal static void ExitDispatch(DispatchScope previous)
        {
            dispatching = previous.Id;
            dispatchingSpeech = previous.Speech;
        }
    }
}
