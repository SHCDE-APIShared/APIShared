using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using APIShared.NativeSites;

namespace APIShared
{
    internal sealed partial class NativeOwnershipRegistry
    {
        // Site layer. Atomic for the whole group: either every span is recorded or none is.
        internal NativeSiteRegistrationResult RegisterSites(string ownerGuid, string name, IReadOnlyList<NativeSiteSpan> spans)
        {
            NativeSiteRegistrationResult result = RegisterCore(ownerGuid, name, spans, out string lateLog, out Action<string> logger);
            if (lateLog != null)
            {
                try { logger?.Invoke(lateLog); }
                catch { }
            }
            return result;
        }

        private NativeSiteRegistrationResult RegisterCore(string ownerGuid, string name, IReadOnlyList<NativeSiteSpan> spans,
            out string lateLog, out Action<string> logger)
        {
            lateLog = null;
            logger = null;
            if (string.IsNullOrWhiteSpace(ownerGuid))
                return Invalid(ownerGuid, name, -1, "A non-empty BepInEx owner GUID is required.");
            if (string.IsNullOrWhiteSpace(name))
                return Invalid(ownerGuid, name, -1, "A non-empty registration name is required.");
            if (spans == null || spans.Count == 0)
                return Invalid(ownerGuid, name, -1, "At least one span is required.");

            var requested = new NativeSiteSpan[spans.Count];
            var hashes = new string[spans.Count];
            for (int index = 0; index < requested.Length; index++)
            {
                NativeSiteSpan span = spans[index];
                string failure = ValidateShape(span, out hashes[index]);
                if (failure != null)
                    return Invalid(ownerGuid, name, index, failure);
                requested[index] = span;
            }

            lock (sync)
            {
                if (readByte != null)
                {
                    for (int index = 0; index < requested.Length; index++)
                        if ((long)requested[index].Rva + requested[index].Length > imageLength)
                            return Invalid(ownerGuid, name, index,
                                $"Span {requested[index]} lies outside the CrusaderDE.dll image (0x{imageLength:X} bytes).");
                }

                NativeSiteRegistration[] existingGroup = FindGroup(ownerGuid, name);
                if (existingGroup.Length > 0)
                {
                    if (SameGroup(existingGroup, requested, hashes))
                        return new NativeSiteRegistrationResult(NativeSiteRegistrationStatus.AlreadyRegistered, ownerGuid, name,
                            Array.AsReadOnly(existingGroup), null, -1, Array.Empty<NativeSiteRegistration>(),
                            "The same spans are already registered under this name.");
                    return Invalid(ownerGuid, name, -1,
                        "This name is already registered by the owner with different spans; unregister it first.");
                }

                var shared = new List<NativeSiteRegistration>();
                for (int index = 0; index < requested.Length; index++)
                {
                    NativeSiteSpan span = requested[index];
                    foreach (NativeSiteRegistration existing in sites)
                    {
                        if (string.Equals(existing.OwnerGuid, ownerGuid, StringComparison.Ordinal) ||
                            !existing.Overlaps(span.Rva, span.Length))
                            continue;
                        if (NativeSiteRules.Allows(existing.Mode, existing.Rva, span.Mode, span.Rva))
                        {
                            if (!shared.Contains(existing))
                                shared.Add(existing);
                            continue;
                        }
                        return Refuse(new NativeSiteRegistrationResult(NativeSiteRegistrationStatus.Conflict, ownerGuid, name,
                            null, existing, index, null,
                            $"Span {index} ({span}) conflicts with {existing} under rule " +
                            $"{NativeSiteRules.Rule(existing.Mode, span.Mode)}."), out lateLog, out logger);
                    }
                }

                var verified = new bool[requested.Length];
                for (int index = 0; index < requested.Length; index++)
                {
                    if (hashes[index].Length == 0 || readByte == null)
                        continue;
                    string actual;
                    try { actual = HashLive(requested[index]); }
                    catch (Exception ex)
                    {
                        return Invalid(ownerGuid, name, index, $"Live bytes of span {requested[index]} could not be read: {ex.Message}");
                    }
                    if (!string.Equals(actual, hashes[index], StringComparison.Ordinal))
                        return Refuse(new NativeSiteRegistrationResult(NativeSiteRegistrationStatus.UnexpectedBytes, ownerGuid, name,
                            null, null, index, null,
                            $"Live bytes of span {index} ({requested[index]}) hash to {actual}, expected {hashes[index]}."),
                            out lateLog, out logger);
                    verified[index] = true;
                }

                var created = new NativeSiteRegistration[requested.Length];
                for (int index = 0; index < requested.Length; index++)
                {
                    NativeSiteSpan span = requested[index];
                    created[index] = new NativeSiteRegistration(ownerGuid, name, span.Rva, span.Length, span.Mode,
                        hashes[index], verified[index], nextSequence++, string.Empty);
                    sites.Add(created[index]);
                }
                Publish();
                shared.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
                return new NativeSiteRegistrationResult(NativeSiteRegistrationStatus.Accepted, ownerGuid, name,
                    Array.AsReadOnly(created), null, -1, shared.AsReadOnly(), string.Empty);
            }
        }

        // Removes an owner's named group. Intended only to roll back an installation that was not
        // published; published hooks stay registered for the process lifetime.
        internal int UnregisterSites(string ownerGuid, string name)
        {
            if (ownerGuid == null || name == null)
                return 0;
            lock (sync)
            {
                int removed = sites.RemoveAll(site =>
                    string.Equals(site.OwnerGuid, ownerGuid, StringComparison.Ordinal) &&
                    string.Equals(site.Name, name, StringComparison.Ordinal));
                if (removed > 0)
                    Publish();
                return removed;
            }
        }

        // Zero-allocation read: counts every registration overlapping [rva, rva + length) and copies
        // up to destination.Length of them in registration order. Destination may be null or empty.
        internal int Query(int rva, int length, NativeSiteRegistration[] destination)
        {
            if (length < 1)
                throw new ArgumentOutOfRangeException(nameof(length));
            NativeSiteRegistration[] items = Volatile.Read(ref snapshot).Items;
            int capacity = destination == null ? 0 : destination.Length;
            int count = 0;
            long end = (long)rva + length;
            for (int index = 0; index < items.Length; index++)
            {
                NativeSiteRegistration item = items[index];
                if (item.Rva < end && rva < item.EndRva)
                {
                    if (count < capacity)
                        destination[count] = item;
                    count++;
                }
            }
            return count;
        }

        // Zero-allocation read: the earliest site registration that writes the byte at rva
        // (Exclusive or ChainableEntry). Capability reservations are not writers.
        internal bool TryGetWriter(int rva, out NativeSiteRegistration registration)
        {
            NativeSiteRegistration[] items = Volatile.Read(ref snapshot).Items;
            for (int index = 0; index < items.Length; index++)
            {
                NativeSiteRegistration item = items[index];
                if (item.CapabilityId.Length == 0 &&
                    (item.Mode == NativeSiteMode.Exclusive || item.Mode == NativeSiteMode.ChainableEntry) &&
                    item.Rva <= rva && rva < item.EndRva)
                {
                    registration = item;
                    return true;
                }
            }
            registration = null;
            return false;
        }

        private NativeSiteRegistrationResult Refuse(NativeSiteRegistrationResult result, out string lateLog, out Action<string> logger)
        {
            refusalCount++;
            if (refusals.Count < MaximumRecordedRefusals)
                refusals.Add(result);
            Publish();
            lateLog = reportEmitted ? "NATIVE_SITES refusal: " + result : null;
            logger = refusalLog;
            return result;
        }

        private static NativeSiteRegistrationResult Invalid(string ownerGuid, string name, int index, string reason) =>
            new NativeSiteRegistrationResult(NativeSiteRegistrationStatus.Invalid, ownerGuid, name, null, null, index, null, reason);

        private static string ValidateShape(NativeSiteSpan span, out string hash)
        {
            hash = string.Empty;
            if (span.Rva < 0)
                return $"Span {span} has a negative RVA.";
            if (span.Length < 1)
                return $"Span {span} must cover at least one byte.";
            if ((long)span.Rva + span.Length > int.MaxValue)
                return $"Span {span} exceeds the 32-bit RVA range.";
            if (span.Mode < NativeSiteMode.Exclusive || span.Mode > NativeSiteMode.Observe)
                return $"Span {span} has an unknown mode.";
            string text = span.ExpectedOriginalSha256.Trim();
            if (text.Length == 0)
                return null;
            if (text.Length != 64)
                return $"Span {span} expected SHA-256 must have 64 hexadecimal digits.";
            foreach (char character in text)
                if (!Uri.IsHexDigit(character))
                    return $"Span {span} expected SHA-256 must have 64 hexadecimal digits.";
            hash = text.ToUpperInvariant();
            return null;
        }

        private string HashLive(NativeSiteSpan span)
        {
            var bytes = new byte[span.Length];
            long start = moduleBase + span.Rva;
            for (int index = 0; index < bytes.Length; index++)
                bytes[index] = readByte(start + index);
            return Sha256Hex.Of(bytes);
        }

        private NativeSiteRegistration[] FindGroup(string ownerGuid, string name)
        {
            var group = new List<NativeSiteRegistration>();
            foreach (NativeSiteRegistration site in sites)
                if (string.Equals(site.OwnerGuid, ownerGuid, StringComparison.Ordinal) &&
                    string.Equals(site.Name, name, StringComparison.Ordinal))
                    group.Add(site);
            return group.ToArray();
        }

        private static bool SameGroup(NativeSiteRegistration[] existing, NativeSiteSpan[] requested, string[] hashes)
        {
            if (existing.Length != requested.Length)
                return false;
            for (int index = 0; index < existing.Length; index++)
                if (existing[index].Rva != requested[index].Rva || existing[index].Length != requested[index].Length ||
                    existing[index].Mode != requested[index].Mode ||
                    !string.Equals(existing[index].ExpectedOriginalSha256, hashes[index], StringComparison.Ordinal))
                    return false;
            return true;
        }
    }
}
