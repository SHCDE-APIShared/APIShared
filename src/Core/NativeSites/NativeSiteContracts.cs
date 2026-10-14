using System;
using System.Collections.Generic;

namespace APIShared.NativeSites
{
    /// <summary>
    /// How a registrant uses a span of the loaded CrusaderDE.dll image. Spans are RVAs relative to
    /// that module and half-open: [Rva, Rva + Length). The overlap rules between modes are documented
    /// on <see cref="NativeSiteRegistry.GetOverlapRule"/> and in docs/NATIVE_SITES.md.
    /// </summary>
    public enum NativeSiteMode
    {
        /// <summary>
        /// Writes the span and cannot share it: a function-entry detour that refuses an occupied
        /// entry, an inline or context hook, or a byte or code-immediate patch. Register the complete
        /// displaced or written range. Conflicts with every overlapping registration of another
        /// owner except <see cref="CallOnly"/>.
        /// </summary>
        Exclusive = 0,
        /// <summary>
        /// A function-entry detour installed through a backend that layers over another detour at
        /// the same entry and always calls through to the next one. Register the entry patch span,
        /// starting at the function entry. Shares the span only with another ChainableEntry at the
        /// same start RVA and with <see cref="CallOnly"/> callers.
        /// </summary>
        ChainableEntry = 1,
        /// <summary>
        /// Calls the function at Rva through its live entry, accepting whatever detours run there,
        /// and writes nothing. Compatible with every mode. Register the entry (a length of 1 is enough).
        /// </summary>
        CallOnly = 2,
        /// <summary>
        /// Reads the span and relies on its bytes staying as validated: pristine prologue or
        /// function hashes, decoded CALL sites, trampoline continuations, code immediates read as
        /// data. Writes nothing. Conflicts with overlapping writers of another owner.
        /// </summary>
        Observe = 3
    }

    /// <summary>Outcome of two overlapping registrations of different owners.</summary>
    public enum NativeSiteOverlapRule
    {
        /// <summary>Both registrations may coexist.</summary>
        Compatible = 0,
        /// <summary>Both are chainable entry detours: they coexist only when both spans start at the same RVA.</summary>
        SameEntryChain = 1,
        /// <summary>The later registration is refused.</summary>
        Conflict = 2
    }

    /// <summary>Result status of <see cref="NativeSiteRegistry.Register(string, string, IReadOnlyList{NativeSiteSpan})"/>.</summary>
    public enum NativeSiteRegistrationStatus
    {
        /// <summary>Every span was recorded. Compatible overlaps are listed in <see cref="NativeSiteRegistrationResult.SharedWith"/>.</summary>
        Accepted = 0,
        /// <summary>The same owner already registered the same name with identical spans; nothing changed.</summary>
        AlreadyRegistered = 1,
        /// <summary>A span overlaps another owner's registration under a conflicting rule. Nothing was recorded.</summary>
        Conflict = 2,
        /// <summary>The live bytes of a span do not match its expected original SHA-256. Nothing was recorded.</summary>
        UnexpectedBytes = 3,
        /// <summary>The arguments are invalid (empty owner or name, empty or out-of-image span, unknown mode,
        /// malformed hash, or the name is already registered with different spans). Nothing was recorded.</summary>
        Invalid = 4
    }

    /// <summary>One span of a registration request.</summary>
    public readonly struct NativeSiteSpan
    {
        /// <summary>Creates a span request.</summary>
        /// <param name="rva">Start RVA inside CrusaderDE.dll.</param>
        /// <param name="length">Byte length; at least 1.</param>
        /// <param name="mode">How the registrant uses the span.</param>
        /// <param name="expectedOriginalSha256">Optional SHA-256 (64 hex digits) of the span's bytes before
        /// the registrant changes them. Checked against live memory at registration once APIShared has bound
        /// the module (see <see cref="NativeSiteRegistry.IsImageBound"/>).</param>
        public NativeSiteSpan(int rva, int length, NativeSiteMode mode, string expectedOriginalSha256 = null)
        {
            Rva = rva;
            Length = length;
            Mode = mode;
            this.expectedOriginalSha256 = expectedOriginalSha256;
        }

        private readonly string expectedOriginalSha256;

        /// <summary>Start RVA inside CrusaderDE.dll.</summary>
        public int Rva { get; }
        /// <summary>Byte length.</summary>
        public int Length { get; }
        /// <summary>How the registrant uses the span.</summary>
        public NativeSiteMode Mode { get; }
        /// <summary>Expected SHA-256 of the original bytes, or empty (also for a default instance).</summary>
        public string ExpectedOriginalSha256 => expectedOriginalSha256 ?? string.Empty;

        /// <inheritdoc />
        public override string ToString() => $"0x{Rva:X}+{Length} {Mode}";
    }

    /// <summary>
    /// An immutable recorded span. Instances are shared by every reader; they never change after publication.
    /// </summary>
    public sealed class NativeSiteRegistration
    {
        internal NativeSiteRegistration(string ownerGuid, string name, int rva, int length, NativeSiteMode mode,
            string expectedOriginalSha256, bool originalBytesVerified, long sequence, string capabilityId)
        {
            OwnerGuid = ownerGuid;
            Name = name;
            Rva = rva;
            Length = length;
            EndRva = rva + length;
            Mode = mode;
            ExpectedOriginalSha256 = expectedOriginalSha256 ?? string.Empty;
            OriginalBytesVerified = originalBytesVerified;
            Sequence = sequence;
            CapabilityId = capabilityId ?? string.Empty;
        }

        /// <summary>BepInEx GUID of the registrant.</summary>
        public string OwnerGuid { get; }
        /// <summary>Owner-chosen name of the registration group (for example a hook or feature name).</summary>
        public string Name { get; }
        /// <summary>Start RVA inside CrusaderDE.dll.</summary>
        public int Rva { get; }
        /// <summary>Byte length.</summary>
        public int Length { get; }
        /// <summary>Exclusive end RVA.</summary>
        public int EndRva { get; }
        /// <summary>How the owner uses the span.</summary>
        public NativeSiteMode Mode { get; }
        /// <summary>Expected SHA-256 of the original bytes supplied by the owner, or empty.</summary>
        public string ExpectedOriginalSha256 { get; }
        /// <summary>True when the expected hash was supplied and matched live memory at registration.</summary>
        public bool OriginalBytesVerified { get; }
        /// <summary>Process-wide registration order. A later ChainableEntry at the same entry has a higher value.</summary>
        public long Sequence { get; }
        /// <summary>
        /// Empty for site registrations. Non-empty for an APIShared capability reservation: a consumer's
        /// logical ownership of memory that APIShared itself hooks (for example gatehouse timing values).
        /// Capability reservations never conflict with site registrations; the hooked span is registered
        /// separately by APIShared.
        /// </summary>
        public string CapabilityId { get; }

        /// <summary>Whether this registration overlaps [rva, rva + length).</summary>
        public bool Overlaps(int rva, int length) => Rva < (long)rva + length && rva < EndRva;

        /// <inheritdoc />
        public override string ToString() =>
            $"{OwnerGuid}/{Name} 0x{Rva:X}+{Length} {Mode}" + (CapabilityId.Length == 0 ? string.Empty : " capability=" + CapabilityId);
    }

    /// <summary>Immutable result of a registration request.</summary>
    public sealed class NativeSiteRegistrationResult
    {
        internal NativeSiteRegistrationResult(NativeSiteRegistrationStatus status, string ownerGuid, string name,
            IReadOnlyList<NativeSiteRegistration> registrations, NativeSiteRegistration conflict, int failedSpanIndex,
            IReadOnlyList<NativeSiteRegistration> sharedWith, string reason)
        {
            Status = status;
            OwnerGuid = ownerGuid ?? string.Empty;
            Name = name ?? string.Empty;
            Registrations = registrations ?? Array.Empty<NativeSiteRegistration>();
            Conflict = conflict;
            FailedSpanIndex = failedSpanIndex;
            SharedWith = sharedWith ?? Array.Empty<NativeSiteRegistration>();
            Reason = reason ?? string.Empty;
        }

        /// <summary>Outcome.</summary>
        public NativeSiteRegistrationStatus Status { get; }
        /// <summary>True for <see cref="NativeSiteRegistrationStatus.Accepted"/> and <see cref="NativeSiteRegistrationStatus.AlreadyRegistered"/>.</summary>
        public bool Succeeded => Status == NativeSiteRegistrationStatus.Accepted || Status == NativeSiteRegistrationStatus.AlreadyRegistered;
        /// <summary>Requesting owner GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Requested registration name.</summary>
        public string Name { get; }
        /// <summary>The recorded spans on success, in request order; empty otherwise.</summary>
        public IReadOnlyList<NativeSiteRegistration> Registrations { get; }
        /// <summary>The existing registration that refused the request (<see cref="NativeSiteRegistrationStatus.Conflict"/>), otherwise null.</summary>
        public NativeSiteRegistration Conflict { get; }
        /// <summary>Index of the refused or invalid span in the request, or -1.</summary>
        public int FailedSpanIndex { get; }
        /// <summary>Other owners' registrations that overlap the accepted spans under a compatible rule, in registration order.</summary>
        public IReadOnlyList<NativeSiteRegistration> SharedWith { get; }
        /// <summary>Human-readable explanation; empty for a plain acceptance.</summary>
        public string Reason { get; }

        /// <inheritdoc />
        public override string ToString() =>
            $"{Status}: {OwnerGuid}/{Name}" + (Reason.Length == 0 ? string.Empty : " - " + Reason);
    }
}
