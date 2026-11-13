using System;
using APIShared.Events;

namespace APIShared.Presentation
{
    /// <summary>Ordered component replacement for complete Lord messages at their actual Unity presentation.
    /// Native queueing, gameplay decisions and Extender clip selection run before these callbacks.</summary>
    public static class LordMessageEvents
    {
        [ThreadStatic] private static int callbackDepth;
        internal static bool InCallback => callbackDepth != 0;
        internal static readonly InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs> Registry =
            new InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>(() => {
                if (!Available) throw new InvalidOperationException(Diagnostic);
            });
        internal static readonly Func<LordMessageMutation, object, LordMessagePostEventArgs> PostFactory =
            (args, state) => new LordMessagePostEventArgs(args, state);
        /// <summary>Whether Lord messages can be replaced now: the managed presentation hooks are installed, their
        /// IL edit is part of the current GameMap body, and the native message protocol of this game binary is audited.
        /// Vanilla reference availability is reported separately by VanillaLordMessages.</summary>
        public static bool Available => LordMessageRuntime.IsReady;
        /// <summary>"Ready", or the first missing part of <see cref="Available"/>. Unsupported groups continue unchanged.</summary>
        public static string Diagnostic => LordMessageRuntime.Status;
        /// <summary>Isolated callback failures. A throwing Pre rolls back only its own changes and State.</summary>
        public static long CallbackFailures => Registry.CallbackFailures;
        /// <summary>Registers on Unity after APIShared initialization. Callbacks run synchronously on Unity,
        /// ordered by order, ordinal owner GUID and registration ID, without replay. Keep callbacks short:
        /// do not load assets, decode or block. Registrations and hooks remain until process exit.
        /// Each message freezes activation and its three selections; last valid selection wins per part.
        /// Recursive render notifications bypass this broker. Post means media calls returned normally,
        /// not that a decoder, audio channel or hardware produced output. No native/gameplay veto is exposed.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, Action<LordMessagePreEventArgs> pre,
            Action<LordMessagePostEventArgs> post, out LordMessageRegistration registration, out string reason, int order = 0)
        {
            registration = null;
            if (!Internal.UnityMainThreadDispatch.IsMainThread) { reason = "Register Lord message rules on Unity."; return false; }
            return Register(ownerGuid, registrationId, pre, post, out registration, out reason, order);
        }
        internal static bool Register(string owner, string id, Action<LordMessagePreEventArgs> pre,
            Action<LordMessagePostEventArgs> post, out LordMessageRegistration registration, out string reason, int order = 0,
            InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs> registry = null)
        {
            registration = null;
            var handle = new LordMessageRegistration();
            if (!(registry ?? Registry).TryRegister(owner, id, pre == null ? (Action<LordMessageMutation>)null : args => CallPre(pre, args),
                post == null ? (Action<LordMessagePostEventArgs>)null : args => CallPost(post, args), out reason, order, isActive: () => handle.Enabled)) return false;
            registration = handle; return true;
        }
        private static void CallPre(Action<LordMessagePreEventArgs> callback, LordMessageMutation args)
        { callbackDepth++; try { callback(args.Public); } finally { callbackDepth--; } }
        private static void CallPost(Action<LordMessagePostEventArgs> callback, LordMessagePostEventArgs args)
        { callbackDepth++; try { callback(args); } finally { callbackDepth--; } }
    }
}
