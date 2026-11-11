using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;

namespace APIShared.NativeSites
{
    /// <summary>
    /// Prefer a shared APIShared service; use this registry for hooks APIShared doesn't cover yet.
    /// Process-wide, cooperative registry of native sites in CrusaderDE.dll: which mod writes, chains,
    /// calls or observes which bytes. Register a span before installing a hook there; a refusal names
    /// the existing owner, mode and span instead of a silent double detour. Query to learn who owns a
    /// site. APIShared registers its own sites when it installs them.
    /// </summary>
    /// <remarks>
    /// <para>Role: a supplementary safeguard and diagnostic, not an alternative to shared services. A
    /// refusal identifies a conflict and does not resolve it: the refused feature stays off, and which
    /// side is refused can depend on registration order. A recurring conflict is fixed by a shared
    /// service with ordering, veto and replacement rules. An accepted overlap only means the spans do
    /// not overlap in a way that corrupts bytes; it is not a compatibility guarantee.</para>
    /// <para>Address space: RVAs relative to the loaded CrusaderDE.dll, half-open spans. Rules between
    /// owners: <see cref="GetOverlapRule"/>. An owner's own registrations never conflict with each other.</para>
    /// <para>Thread and timing: every member may be called from any thread. Register at startup or
    /// install time, ideally from a LibraryLoaded handler of a plugin that declares a (soft)
    /// BepInDependency on APIShared_Serp, so APIShared has bound the image and registered its eager sites
    /// first. Queries read an immutable snapshot without locks and allocate nothing; they are still meant
    /// for install-time decisions and diagnostics, never for hooked paths.</para>
    /// <para>Lifetime: registrations are process-lived. <see cref="Unregister"/> exists only to roll back an
    /// installation that was never published.</para>
    /// <para>Limits: the registry is cooperative. It does not see hooks that were never registered
    /// (Script Extender's own spans, other backends, plugins that do not use it), does not verify that a
    /// registrant actually installed anything, and does not protect memory. Expected-bytes checks run only
    /// once the image is bound. See docs/NATIVE_SITES.md.</para>
    /// </remarks>
    public static class NativeSiteRegistry
    {
        private static NativeOwnershipRegistry Store => ApiSharedRuntime.ProcessInstance.NativeOwnership;
        private static int reportScheduled;

        /// <summary>
        /// Registers one span. Same contract as the group overload.
        /// </summary>
        public static NativeSiteRegistrationResult Register(string ownerGuid, string name, int rva, int length,
            NativeSiteMode mode, string expectedOriginalSha256 = null) =>
            Store.RegisterSites(ownerGuid, name, new[] { new NativeSiteSpan(rva, length, mode, expectedOriginalSha256) });

        /// <summary>
        /// Registers a group of spans atomically under one owner-chosen name: either every span is recorded
        /// or none is. Re-registering the identical group returns <see cref="NativeSiteRegistrationStatus.AlreadyRegistered"/>.
        /// </summary>
        /// <remarks>
        /// Checks run in this order: arguments and image bounds (Invalid), the same name already registered,
        /// overlap with other owners' sites (Conflict, naming the first conflicting registration in
        /// registration order), expected original bytes against live memory (UnexpectedBytes). Conflicts and
        /// unexpected bytes are also recorded in <see cref="Refusals"/>; APIShared logs them once in its
        /// startup report and individually afterwards. Never throws for invalid input. On a refusal, keep
        /// Vanilla behavior: the result identifies the conflict but does not resolve it. Acceptance with
        /// <see cref="NativeSiteRegistrationResult.SharedWith"/> entries only means no corrupting byte
        /// overlap, not that the hooks are compatible.
        /// </remarks>
        public static NativeSiteRegistrationResult Register(string ownerGuid, string name, IReadOnlyList<NativeSiteSpan> spans) =>
            Store.RegisterSites(ownerGuid, name, spans);

        /// <summary>
        /// Removes every span the owner registered under <paramref name="name"/> and returns how many were removed.
        /// Use only to roll back an installation that failed before publication.
        /// </summary>
        public static int Unregister(string ownerGuid, string name) => Store.UnregisterSites(ownerGuid, name);

        /// <summary>
        /// Counts the registrations overlapping [rva, rva + length) and copies up to
        /// <c>destination.Length</c> of them in registration order. Allocates nothing.
        /// </summary>
        /// <param name="rva">Start RVA.</param>
        /// <param name="length">Byte length; at least 1.</param>
        /// <param name="destination">Caller-owned buffer; may be null or empty to count only.</param>
        /// <returns>The total number of overlapping registrations, which may exceed the buffer length.</returns>
        public static int Query(int rva, int length, NativeSiteRegistration[] destination) =>
            Store.Query(rva, length, destination);

        /// <summary>
        /// Finds the earliest registration that writes the byte at <paramref name="rva"/>
        /// (<see cref="NativeSiteMode.Exclusive"/> or <see cref="NativeSiteMode.ChainableEntry"/>).
        /// Allocates nothing.
        /// </summary>
        public static bool TryGetWriter(int rva, out NativeSiteRegistration registration) =>
            Store.TryGetWriter(rva, out registration);

        /// <summary>Immutable snapshot of every registration in registration order, for diagnostics.</summary>
        public static IReadOnlyList<NativeSiteRegistration> Registrations => Store.Registrations;

        /// <summary>Immutable snapshot of refused registrations (Conflict, UnexpectedBytes), first 256 kept.</summary>
        public static IReadOnlyList<NativeSiteRegistrationResult> Refusals => Store.Refusals;

        /// <summary>Whether APIShared has bound the CrusaderDE.dll image (image bounds and expected-bytes checks active).</summary>
        public static bool IsImageBound => Store.IsBound;

        /// <summary>
        /// Rule for two overlapping registrations of different owners. Symmetric.
        /// CallOnly is compatible with everything; Observe is compatible with Observe and CallOnly;
        /// ChainableEntry pairs share only one entry (<see cref="NativeSiteOverlapRule.SameEntryChain"/>);
        /// every other pair (anything with Exclusive except CallOnly, ChainableEntry with Observe) conflicts.
        /// The rules describe byte overlap only; a compatible or chainable result is not a guarantee
        /// that the hooks behave correctly together.
        /// </summary>
        public static NativeSiteOverlapRule GetOverlapRule(NativeSiteMode first, NativeSiteMode second) =>
            NativeSiteRules.Rule(first, second);

        // Logs one summary line on the next Unity main-thread turn, after the startup handlers that run in
        // the same frame; later refusals are logged individually. Called from LibraryLoaded; once per process.
        internal static void ScheduleStartupReport(ManualLogSource log)
        {
            if (Interlocked.Exchange(ref reportScheduled, 1) != 0)
                return;
            NativeOwnershipRegistry store = Store;
            store.SetRefusalLog(message => NativeApiLog.Warning(log, message));
            Action report = () =>
            {
                try
                {
                    // API_SHARED_DIAGNOSTICS: no success-summary construction without opt-in.
                    // Refusals remain warnings even when routine diagnostics are disabled.
                    string summary = store.EmitReport(APIShared.Internal.DebugLogHelper.DiagnosticsEnabled);
                    if (summary == null) return;
                    if (store.RefusalCount == 0) NativeApiLog.Info(log, summary);
                    else NativeApiLog.Warning(log, summary);
                }
                catch { }
            };
            if (!APIShared.Internal.UnityMainThreadDispatch.TryEnqueue(report))
                report();
        }
    }
}
