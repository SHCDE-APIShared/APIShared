using System;
using System.Threading;

namespace APIShared.PlayerControl
{
    // Optional active-human view-lease protection. Original spectators do not need
    // it and must not depend on its availability. The runtime adapter owns the
    // exclusive view lease, engine monitor and validated native view cell; neither
    // consumers nor packets provide these delegates. This class installs no hook.
    internal sealed class ControlSaveIdentity
    {
        private sealed class Binding
        {
            internal readonly long Epoch, Token;
            internal readonly int Human;
            internal Binding(long epoch, long token, int human) { Epoch = epoch; Token = token; Human = human; }
        }

        private readonly Func<bool> inEngineContext;
        private readonly Func<long, bool> isCurrentLocalWorld;
        private readonly Func<int> readView;
        private readonly Action<int> writeView;
        private Binding binding;
        private long nextToken;
        private int saveDepth;
        private bool changingIdentity;
        private string failure;

        internal ControlSaveIdentity(Func<bool> inEngineContext, Func<long, bool> isCurrentLocalWorld,
            Func<int> readView, Action<int> writeView)
        {
            this.inEngineContext = inEngineContext ?? throw new ArgumentNullException(nameof(inEngineContext));
            this.isCurrentLocalWorld = isCurrentLocalWorld ?? throw new ArgumentNullException(nameof(isCurrentLocalWorld));
            this.readView = readView ?? throw new ArgumentNullException(nameof(readView));
            this.writeView = writeView ?? throw new ArgumentNullException(nameof(writeView));
        }

        // Safe for the existing GetLocalPlayerId hook: no lock, native lookup,
        // allocation, consumer predicate or Unity access in this hot observation.
        internal int ProtectedHumanId => Volatile.Read(ref binding)?.Human ?? 0;
        internal bool IsScopeActive => saveDepth != 0 || changingIdentity;
        internal string Failure => Volatile.Read(ref failure);

        internal bool TryBind(long epoch, int actualHuman, out long token, out string reason)
        {
            token = 0; reason = null;
            if (changingIdentity || saveDepth != 0 || binding != null || nextToken == long.MaxValue)
            { reason = "The view identity is already protected or a save is in progress."; return false; }
            changingIdentity = true;
            try
            {
                if (!inEngineContext() || epoch <= 0 || actualHuman < 1 || actualHuman > 8 || !isCurrentLocalWorld(epoch))
                { reason = "A serialized active-human singleplayer view context is required."; return false; }
                // Bind before changing the native view. Do not adopt another API's
                // already changed viewpoint as the original human identity. Reads
                // may cross managed observers, so recheck the world afterwards.
                if (!TryRead(out int current, out reason) || current != actualHuman || !isCurrentLocalWorld(epoch))
                { reason = reason ?? "The current native view is not the original human player in the requested world."; return false; }
                var next = new Binding(epoch, ++nextToken, actualHuman);
                Volatile.Write(ref failure, null);
                Volatile.Write(ref binding, next); token = next.Token;
                return true;
            }
            finally { changingIdentity = false; }
        }

        internal bool TryRelease(long token, out string reason)
        {
            reason = null;
            var current = Volatile.Read(ref binding);
            if (changingIdentity || !inEngineContext() || current == null || current.Token != token)
            { reason = "No matching protected view identity."; return false; }
            changingIdentity = true;
            try
            {
                if (!Current(current))
                { reason = "The protected world has ended; retire its identity without writing into the new world."; return false; }
                if (!TryRead(out int nativeView, out reason) || nativeView != current.Human || !Current(current))
                { reason = reason ?? "Restore the original human view before releasing its identity."; return false; }
                Volatile.Write(ref binding, null); Volatile.Write(ref failure, null);
                return true;
            }
            finally { changingIdentity = false; }
        }

        // Lifecycle replacement only. It never writes a view from an old world
        // into the next one; an ordinary lease release must first restore its view.
        internal bool RetireEndedWorld(long expectedEpoch)
        {
            var current = Volatile.Read(ref binding);
            if (!inEngineContext() || current == null || current.Epoch != expectedEpoch || isCurrentLocalWorld(expectedEpoch)) return false;
            if (!ReferenceEquals(Interlocked.CompareExchange(ref binding, null, current), current)) return false;
            Volatile.Write(ref failure, null);
            return true;
        }

        // The native writer adapter passes its arguments as a struct and a cached
        // static delegate. Inactive saves take the complete original path without
        // invoking the mode/read/write adapters. Native callers must catch managed
        // exceptions; an original-writer exception still runs this finally block.
        internal bool TryRunWriter<TState, TResult>(TState state, Func<TState, TResult> original,
            out TResult result, out string reason)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            reason = null; result = default;
            var captured = Volatile.Read(ref binding);
            if (captured == null) { result = original(state); return true; }
            if (changingIdentity || !inEngineContext() || !Current(captured))
            { reason = "The protected save has no current serialized local world."; return false; }
            // Block a replacement lease before the first native adapter, not just
            // while the original writer runs. Nested original saves remain valid.
            saveDepth++;
            int previous = 0;
            bool restoreNeeded = false, restoreSucceeded = true;
            try
            {
                if (!TryRead(out previous, out reason) || previous < 1 || previous > 8 || !Current(captured))
                { reason = Fail(reason ?? "The previous native view or world cannot be restored."); return false; }
                restoreNeeded = true; // A throwing setter may already have written.
                if (!TryWrite(captured, captured.Human, out reason))
                { reason = Fail(reason); return false; }
                result = original(state);
            }
            finally
            {
                try
                {
                    // Never roll back a setter/read that replaced the world or
                    // released this lease. This applies before the writer too.
                    if (restoreNeeded && Current(captured))
                    {
                        restoreSucceeded = TryWrite(captured, previous, out string restore);
                        if (!restoreSucceeded) reason = Fail((reason == null ? string.Empty : reason + " ") +
                            "Could not restore the leased view after saving: " + restore);
                    }
                }
                finally { saveDepth--; }
            }
            return restoreSucceeded;
        }

        private bool TryRead(out int view, out string reason)
        {
            view = 0; reason = null;
            try { view = readView(); return true; }
            catch (Exception error) { reason = "Native view read failed: " + error.GetType().Name + ": " + error.Message; return false; }
        }

        private bool Current(Binding expected) => ReferenceEquals(Volatile.Read(ref binding), expected) &&
            isCurrentLocalWorld(expected.Epoch) && ReferenceEquals(Volatile.Read(ref binding), expected);

        private bool TryWrite(Binding expected, int view, out string reason)
        {
            reason = null;
            try
            {
                if (!Current(expected)) { reason = "The protected view or world changed."; return false; }
                writeView(view);
                if (!Current(expected)) { reason = "The protected view or world changed during its native write."; return false; }
                if (readView() == view && Current(expected)) return true;
                reason = "Native view write was not confirmed.";
            }
            catch (Exception error) { reason = "Native view write failed: " + error.GetType().Name + ": " + error.Message; }
            return false;
        }

        private string Fail(string reason) { Volatile.Write(ref failure, reason); return reason; }
    }
}
