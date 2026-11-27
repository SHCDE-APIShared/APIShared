using System.Security.Cryptography;
using APIShared;
using APIShared.NativeSites;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace APIShared.Core.Tests;

[TestClass]
public class NativeSiteRegistryTests
{
    private static readonly NativeSiteMode[] Modes =
        { NativeSiteMode.Exclusive, NativeSiteMode.ChainableEntry, NativeSiteMode.CallOnly, NativeSiteMode.Observe };

    private static NativeSiteRegistrationResult Register(NativeOwnershipRegistry registry, string owner, string name,
        int rva, int length, NativeSiteMode mode, string hash = null) =>
        registry.RegisterSites(owner, name, new[] { new NativeSiteSpan(rva, length, mode, hash) });

    [TestMethod] public void OverlapRuleTableMatchesTheDocumentedMatrixAndIsSymmetric()
    {
        const NativeSiteOverlapRule C = NativeSiteOverlapRule.Conflict, OK = NativeSiteOverlapRule.Compatible,
            Chain = NativeSiteOverlapRule.SameEntryChain;
        // Rows and columns: Exclusive, ChainableEntry, CallOnly, Observe (docs/NATIVE_SITES.md).
        var table = new[,]
        {
            { C, C, OK, C },
            { C, Chain, OK, C },
            { OK, OK, OK, OK },
            { C, C, OK, OK }
        };
        for (int row = 0; row < Modes.Length; row++)
            for (int column = 0; column < Modes.Length; column++)
            {
                Assert.AreEqual(table[row, column], NativeSiteRules.Rule(Modes[row], Modes[column]), $"{Modes[row]} x {Modes[column]}");
                Assert.AreEqual(NativeSiteRules.Rule(Modes[row], Modes[column]), NativeSiteRules.Rule(Modes[column], Modes[row]), "symmetric");
            }
    }

    [TestMethod] public void EveryModePairBehavesAsDocumentedForSameStartInteriorAndAdjacentSpans()
    {
        foreach (NativeSiteMode existing in Modes)
            foreach (NativeSiteMode requested in Modes)
            {
                NativeSiteOverlapRule rule = NativeSiteRules.Rule(existing, requested);
                string pair = $"{existing} then {requested}";

                var sameStart = new NativeOwnershipRegistry();
                NativeSiteRegistration first = Register(sameStart, "A", "a", 0x100, 16, existing).Registrations[0];
                NativeSiteRegistrationResult result = Register(sameStart, "B", "b", 0x100, 8, requested);
                bool accepted = rule != NativeSiteOverlapRule.Conflict;
                Assert.AreEqual(accepted, result.Succeeded, "same start: " + pair);
                if (accepted)
                {
                    CollectionAssert.AreEqual(new[] { first }, result.SharedWith.ToArray(), "shared overlap is reported: " + pair);
                    Assert.AreEqual(2, sameStart.Registrations.Count);
                }
                else
                {
                    Assert.AreEqual(NativeSiteRegistrationStatus.Conflict, result.Status, pair);
                    Assert.AreSame(first, result.Conflict, "the refusal names the existing owner, mode and span: " + pair);
                    Assert.AreEqual(0, result.FailedSpanIndex);
                    Assert.AreEqual(1, sameStart.Registrations.Count, "nothing recorded: " + pair);
                }

                var interior = new NativeOwnershipRegistry();
                Register(interior, "A", "a", 0x100, 16, existing);
                result = Register(interior, "B", "b", 0x108, 16, requested);
                Assert.AreEqual(rule == NativeSiteOverlapRule.Compatible, result.Succeeded,
                    "a chain needs one entry; any other overlap follows the rule: " + pair);

                var adjacent = new NativeOwnershipRegistry();
                Register(adjacent, "A", "a", 0x100, 16, existing);
                result = Register(adjacent, "B", "b", 0x110, 4, requested);
                Assert.AreEqual(NativeSiteRegistrationStatus.Accepted, result.Status, "half-open spans: " + pair);
                Assert.AreEqual(0, result.SharedWith.Count);
            }
    }

    [TestMethod] public void SameOwnerNeverConflictsWithItself()
    {
        var registry = new NativeOwnershipRegistry();
        Assert.IsTrue(Register(registry, "A", "detour", 0x100, 14, NativeSiteMode.Exclusive).Succeeded);
        NativeSiteRegistrationResult own = registry.RegisterSites("A", "checks", new[]
        {
            new NativeSiteSpan(0x100, 31, NativeSiteMode.Observe),
            new NativeSiteSpan(0x104, 4, NativeSiteMode.Exclusive)
        });
        Assert.AreEqual(NativeSiteRegistrationStatus.Accepted, own.Status);
        Assert.AreEqual(0, own.SharedWith.Count, "own registrations are not reported as shared");
    }

    [TestMethod] public void GroupRegistrationIsAtomic()
    {
        var registry = new NativeOwnershipRegistry();
        Register(registry, "A", "a", 0x200, 8, NativeSiteMode.Exclusive);
        NativeSiteRegistrationResult result = registry.RegisterSites("B", "group", new[]
        {
            new NativeSiteSpan(0x100, 8, NativeSiteMode.Exclusive),
            new NativeSiteSpan(0x300, 8, NativeSiteMode.Observe),
            new NativeSiteSpan(0x204, 2, NativeSiteMode.Observe)
        });
        Assert.AreEqual(NativeSiteRegistrationStatus.Conflict, result.Status);
        Assert.AreEqual(2, result.FailedSpanIndex);
        Assert.AreEqual(0, registry.Query(0x100, 8, null), "earlier spans of a refused group are not recorded");
        Assert.AreEqual(0, registry.Query(0x300, 8, null));
        Assert.AreEqual(1, registry.Registrations.Count);
    }

    [TestMethod] public void IdenticalRegistrationIsIdempotentAndChangedSpansAreInvalid()
    {
        var registry = new NativeOwnershipRegistry();
        NativeSiteRegistrationResult first = Register(registry, "A", "a", 0x100, 8, NativeSiteMode.Exclusive);
        NativeSiteRegistrationResult again = Register(registry, "A", "a", 0x100, 8, NativeSiteMode.Exclusive);
        Assert.AreEqual(NativeSiteRegistrationStatus.AlreadyRegistered, again.Status);
        Assert.IsTrue(again.Succeeded);
        Assert.AreSame(first.Registrations[0], again.Registrations[0]);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, Register(registry, "A", "a", 0x100, 9, NativeSiteMode.Exclusive).Status);
        Assert.AreEqual(1, registry.Registrations.Count);
    }

    [TestMethod] public void InvalidRequestsAreRejectedWithoutRecording()
    {
        var registry = new NativeOwnershipRegistry();
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, Register(registry, " ", "a", 0x100, 1, NativeSiteMode.Exclusive).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, Register(registry, "A", "", 0x100, 1, NativeSiteMode.Exclusive).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, registry.RegisterSites("A", "a", Array.Empty<NativeSiteSpan>()).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, registry.RegisterSites("A", "a", null).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, Register(registry, "A", "a", -1, 1, NativeSiteMode.Exclusive).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, Register(registry, "A", "a", 0x100, 0, NativeSiteMode.Exclusive).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, Register(registry, "A", "a", int.MaxValue, 2, NativeSiteMode.Exclusive).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, Register(registry, "A", "a", 0x100, 1, (NativeSiteMode)9).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, Register(registry, "A", "a", 0x100, 1, NativeSiteMode.Exclusive, "ABC").Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, Register(registry, "A", "a", 0x100, 1, NativeSiteMode.Exclusive, new string('G', 64)).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, registry.RegisterSites("A", "a", new[] { default(NativeSiteSpan) }).Status,
            "a default span covers no byte");
        registry.BindModule(0x10000, 0x1000, address => 0);
        NativeSiteRegistrationResult outside = Register(registry, "A", "a", 0xFFC, 8, NativeSiteMode.Exclusive);
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, outside.Status, "bound image bounds are enforced");
        Assert.AreEqual(0, registry.Registrations.Count);
        Assert.AreEqual(0, registry.Refusals.Count, "invalid input is not a recorded refusal");
    }

    [TestMethod] public void ExpectedOriginalBytesAreCheckedAgainstLiveMemoryOnceBound()
    {
        var image = new byte[0x1000];
        new Random(0x0F09).NextBytes(image);
        string expectedHash = Convert.ToHexString(SHA256.HashData(image.AsSpan(0x100, 16)));

        var unbound = new NativeOwnershipRegistry();
        NativeSiteRegistrationResult unchecked_ = Register(unbound, "A", "a", 0x100, 16, NativeSiteMode.Exclusive, expectedHash);
        Assert.AreEqual(NativeSiteRegistrationStatus.Accepted, unchecked_.Status);
        Assert.IsFalse(unchecked_.Registrations[0].OriginalBytesVerified, "no image, no check");

        var registry = new NativeOwnershipRegistry();
        Assert.IsTrue(registry.BindModule(0x40000, image.Length, address => image[address - 0x40000]));
        Assert.IsTrue(registry.BindModule(0x40000, image.Length, address => 0), "rebinding the same base is accepted and ignored");
        Assert.IsFalse(registry.BindModule(0x50000, image.Length, address => 0), "a different base is refused");
        NativeSiteRegistrationResult good = Register(registry, "A", "a", 0x100, 16, NativeSiteMode.Exclusive, expectedHash.ToLowerInvariant());
        Assert.AreEqual(NativeSiteRegistrationStatus.Accepted, good.Status);
        Assert.IsTrue(good.Registrations[0].OriginalBytesVerified);
        Assert.AreEqual(expectedHash, good.Registrations[0].ExpectedOriginalSha256, "normalized to upper case");

        image[0x200] ^= 0xFF;
        string stale = Convert.ToHexString(SHA256.HashData(new byte[16]));
        NativeSiteRegistrationResult changed = Register(registry, "B", "b", 0x200, 16, NativeSiteMode.Observe, stale);
        Assert.AreEqual(NativeSiteRegistrationStatus.UnexpectedBytes, changed.Status);
        Assert.AreEqual(0, registry.Query(0x200, 16, null), "unexpected bytes are not recorded");
        Assert.AreEqual(1, registry.Refusals.Count);

        var faulting = new NativeOwnershipRegistry();
        faulting.BindModule(0x40000, image.Length, address => throw new AccessViolationException("unmapped"));
        Assert.AreEqual(NativeSiteRegistrationStatus.Invalid, Register(faulting, "A", "a", 0x100, 16, NativeSiteMode.Exclusive, expectedHash).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Accepted, Register(faulting, "A", "b", 0x100, 16, NativeSiteMode.Exclusive).Status,
            "without an expected expectedHash no byte is read");
    }

    [TestMethod] public void UnregisterRollsBackAnUnpublishedClaim()
    {
        var registry = new NativeOwnershipRegistry();
        Register(registry, "A", "hook", 0x100, 14, NativeSiteMode.Exclusive);
        Assert.AreEqual(NativeSiteRegistrationStatus.Conflict, Register(registry, "B", "hook", 0x100, 6, NativeSiteMode.Exclusive).Status);
        Assert.AreEqual(0, registry.UnregisterSites("B", "hook"), "another owner's group is untouched");
        Assert.AreEqual(1, registry.UnregisterSites("A", "hook"));
        Assert.AreEqual(NativeSiteRegistrationStatus.Accepted, Register(registry, "B", "hook", 0x100, 6, NativeSiteMode.Exclusive).Status);
        Assert.AreEqual(0, registry.UnregisterSites(null, null));
    }

    [TestMethod] public void ChainableEntriesShareOneEntryInRegistrationOrder()
    {
        var registry = new NativeOwnershipRegistry();
        NativeSiteRegistration first = Register(registry, "A", "chain", 0x500, 14, NativeSiteMode.ChainableEntry).Registrations[0];
        NativeSiteRegistrationResult second = Register(registry, "B", "chain", 0x500, 6, NativeSiteMode.ChainableEntry);
        NativeSiteRegistrationResult third = Register(registry, "C", "chain", 0x500, 14, NativeSiteMode.ChainableEntry);
        Assert.IsTrue(second.Succeeded && third.Succeeded);
        CollectionAssert.AreEqual(new[] { first, second.Registrations[0] }, third.SharedWith.ToArray());
        Assert.IsTrue(first.Sequence < second.Registrations[0].Sequence && second.Registrations[0].Sequence < third.Registrations[0].Sequence);
        Assert.IsTrue(registry.TryGetWriter(0x500, out NativeSiteRegistration writer));
        Assert.AreSame(first, writer, "the earliest chain member is reported as the writer");
        Assert.AreEqual(NativeSiteRegistrationStatus.Conflict, Register(registry, "D", "x", 0x500, 1, NativeSiteMode.Exclusive).Status);
        Assert.AreEqual(NativeSiteRegistrationStatus.Conflict, Register(registry, "D", "y", 0x502, 4, NativeSiteMode.ChainableEntry).Status,
            "a chain member must start at the same entry");
        Assert.AreEqual(NativeSiteRegistrationStatus.Accepted, Register(registry, "D", "z", 0x500, 1, NativeSiteMode.CallOnly).Status);
    }

    [TestMethod] public void CapabilityReservationsKeepTheirSeparateExclusiveSemantics()
    {
        var legacy = new NativeOwnershipRegistry();
        var first = new[] { new NativeInterval(100, 110) };
        Assert.IsTrue(legacy.TryReserve("A", "cap", NativeReservationMode.Exclusive, first, out _), "first reservation");
        Assert.IsTrue(legacy.TryReserve("A", "cap", NativeReservationMode.Exclusive, first, out _), "same reservation is idempotent");
        Assert.IsTrue(legacy.TryReserve("B", "other", NativeReservationMode.Exclusive, new[] { new NativeInterval(110, 120) }, out _));
        Assert.IsFalse(legacy.TryReserve("C", "third", NativeReservationMode.Exclusive, new[] { new NativeInterval(109, 111) }, out string conflict));
        Assert.AreEqual("A", conflict, "exclusive overlap identifies the first owner");
        Assert.AreEqual(2, legacy.Registrations.Count, "unbound views keep values that fit the RVA range");
        Assert.AreEqual(100, legacy.Registrations[0].Rva);

        const long moduleBase = 0x180000000;
        var registry = new NativeOwnershipRegistry();
        registry.BindModule(moduleBase, 0x200000, address => 0);
        Assert.IsTrue(registry.RegisterSites("APIShared_Serp", "Gatehouse.PermanentHooks", new[]
        {
            new NativeSiteSpan(0xB7BBB, 37, NativeSiteMode.Exclusive)
        }).Succeeded);
        Assert.IsTrue(registry.TryReserve("Consumer", "gatehouse-timing", NativeReservationMode.Exclusive,
            new[] { new NativeInterval(moduleBase + 0xB7BC3, moduleBase + 0xB7BC7) }, out _),
            "a consumer's capability reservation inside APIShared's own hook does not conflict with the site");
        NativeSiteRegistration capability = registry.Registrations.Single(r => r.CapabilityId.Length > 0);
        Assert.AreEqual(("Consumer", 0xB7BC3, 4, "gatehouse-timing", NativeSiteMode.Exclusive),
            (capability.OwnerGuid, capability.Rva, capability.Length, capability.CapabilityId, capability.Mode));
        Assert.IsTrue(registry.TryGetWriter(0xB7BC3, out NativeSiteRegistration writer));
        Assert.AreEqual("APIShared_Serp", writer.OwnerGuid, "a capability reservation is not the writer");
        Assert.AreEqual(2, registry.Query(0xB7BC3, 1, null));
        NativeSiteRegistrationResult foreign = Register(registry, "Foreign", "patch", 0xB7BC0, 8, NativeSiteMode.Exclusive);
        Assert.AreEqual("APIShared_Serp", foreign.Conflict.OwnerGuid, "foreign writers meet the hooked site, not the reservation");
        Assert.IsFalse(registry.TryReserve("Other", "gatehouse-timing", NativeReservationMode.Exclusive,
            new[] { new NativeInterval(moduleBase + 0xB7BC3, moduleBase + 0xB7BC7) }, out conflict));
        Assert.AreEqual("Consumer", conflict);

        var lateBound = new NativeOwnershipRegistry();
        Assert.IsTrue(lateBound.TryReserve("Consumer", "cap", NativeReservationMode.Exclusive,
            new[] { new NativeInterval(moduleBase + 0x10, moduleBase + 0x14) }, out _));
        Assert.AreEqual(0, lateBound.Registrations.Count, "an absolute address outside the RVA range waits for the image");
        lateBound.BindModule(moduleBase, 0x1000, address => 0);
        Assert.AreEqual(0x10, lateBound.Registrations.Single().Rva, "binding converts existing reservations");
    }

    [TestMethod] public void RefusalsAreRecordedReportedOnceAndLoggedIndividuallyAfterTheReport()
    {
        var registry = new NativeOwnershipRegistry();
        var logs = new List<string>();
        registry.SetRefusalLog(logs.Add);
        Assert.IsTrue(registry.EmitReport().EndsWith("refusals: none."));

        var quiet = new NativeOwnershipRegistry();
        quiet.SetRefusalLog(logs.Add);
        Register(quiet, "A", "a", 0x100, 8, NativeSiteMode.Exclusive);
        Register(quiet, "B", "b", 0x100, 8, NativeSiteMode.Exclusive);
        Assert.AreEqual(0, logs.Count, "refusals before the startup report wait for it");
        string report = quiet.EmitReport();
        StringAssert.Contains(report, "1 site registrations by 1 owners (A=1)");
        StringAssert.Contains(report, "refusals: 1: Conflict: B/b");
        Register(quiet, "C", "c", 0x104, 2, NativeSiteMode.Observe);
        Assert.AreEqual(1, logs.Count);
        StringAssert.StartsWith(logs[0], "NATIVE_SITES refusal: Conflict: C/c");

        var flood = new NativeOwnershipRegistry();
        Register(flood, "A", "a", 0, 0x1000, NativeSiteMode.Exclusive);
        for (int index = 0; index < 300; index++)
            Register(flood, "B", "b" + index, index, 1, NativeSiteMode.Exclusive);
        Assert.AreEqual(NativeOwnershipRegistry.MaximumRecordedRefusals, flood.Refusals.Count);
        Assert.AreEqual(300, flood.RefusalCount);
        StringAssert.Contains(flood.EmitReport(), "| +290 more");
    }

    [TestMethod] public void QueryAndWriterLookupReturnOverlapsInRegistrationOrder()
    {
        var registry = new NativeOwnershipRegistry();
        NativeSiteRegistration call = Register(registry, "Caller", "c", 0xE2610, 1, NativeSiteMode.CallOnly).Registrations[0];
        NativeSiteRegistration detour = Register(registry, "Detour", "d", 0xE2610, 14, NativeSiteMode.Exclusive).Registrations[0];
        NativeSiteRegistration inner = Register(registry, "Inline", "i", 0xE2705, 20, NativeSiteMode.Exclusive).Registrations[0];
        var buffer = new NativeSiteRegistration[1];
        Assert.AreEqual(2, registry.Query(0xE2610, 2, buffer), "total count exceeds the buffer");
        Assert.AreSame(call, buffer[0]);
        var all = new NativeSiteRegistration[4];
        Assert.AreEqual(3, registry.Query(0xE2600, 0x200, all));
        CollectionAssert.AreEqual(new[] { call, detour, inner, null }, all);
        Assert.IsTrue(registry.TryGetWriter(0xE261D, out NativeSiteRegistration writer) && writer == detour);
        Assert.IsFalse(registry.TryGetWriter(0xE261E, out writer), "end is exclusive");
        Assert.IsNull(writer);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => registry.Query(0, 0, null));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<NativeSiteRegistration>)registry.Registrations).Add(call),
            "the snapshot is read-only");
    }

    [TestMethod] public void ConcurrentRegistrationsNeverAdmitTwoConflictingOwners()
    {
        const int Threads = 8, Contested = 64;
        var registry = new NativeOwnershipRegistry();
        var accepted = new int[Contested];
        int readerViolations = 0, writerFailures = 0;
        using var start = new ManualResetEventSlim();
        bool writing = true;
        var readers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            var buffer = new NativeSiteRegistration[Threads + 1];
            start.Wait();
            while (Volatile.Read(ref writing))
                for (int index = 0; index < Contested; index++)
                {
                    int count = registry.Query(0x1000 + index * 0x20, 16, buffer);
                    if (count > 1) Interlocked.Increment(ref readerViolations);
                    _ = registry.Query(0x9000, 1, buffer);
                }
        })).ToList();
        var writers = Enumerable.Range(0, Threads).Select(thread => new Thread(() =>
        {
            var order = Enumerable.Range(0, Contested).OrderBy(index => (index * 37 + thread * 11) % Contested).ToArray();
            start.Wait();
            foreach (int index in order)
            {
                NativeSiteRegistrationResult result = Register(registry, "T" + thread, "s" + index,
                    0x1000 + index * 0x20 + (thread % 4), 12, NativeSiteMode.Exclusive);
                if (result.Status == NativeSiteRegistrationStatus.Accepted) Interlocked.Increment(ref accepted[index]);
                else if (result.Status != NativeSiteRegistrationStatus.Conflict) Interlocked.Increment(ref writerFailures);
                if (!Register(registry, "T" + thread, "chain" + index, 0x9000, 6, NativeSiteMode.ChainableEntry).Succeeded)
                    Interlocked.Increment(ref writerFailures);
            }
        })).ToList();
        readers.ForEach(t => t.Start());
        writers.ForEach(t => t.Start());
        start.Set();
        writers.ForEach(t => t.Join());
        Volatile.Write(ref writing, false);
        readers.ForEach(t => t.Join());

        CollectionAssert.AreEqual(Enumerable.Repeat(1, Contested).ToArray(), accepted, "exactly one owner per contested span");
        Assert.AreEqual(0, readerViolations, "no reader observed two exclusive owners of one span");
        Assert.AreEqual(0, writerFailures, "every losing writer got Conflict and every chain member was admitted");
        Assert.AreEqual(Threads * Contested - Contested, registry.RefusalCount);
        long[] chain = registry.Registrations.Where(r => r.Rva == 0x9000).Select(r => r.Sequence).ToArray();
        Assert.AreEqual(Threads * Contested, chain.Length, "every chainable entry was admitted");
        CollectionAssert.AreEqual(chain.OrderBy(s => s).ToArray(), chain, "the snapshot is in registration order");
        Assert.AreEqual(chain.Length, chain.Distinct().Count());
    }

    [TestMethod] public void QueriesAllocateNothingAfterWarmup()
    {
        var registry = new NativeOwnershipRegistry();
        for (int index = 0; index < 200; index++)
            Register(registry, "Owner" + (index % 7), "site" + index, 0x1000 + index * 0x40, 14,
                (NativeSiteMode)(index % 4));
        registry.TryReserve("Consumer", "cap", NativeReservationMode.Exclusive, new[] { new NativeInterval(0x100, 0x104) }, out _);
        var buffer = new NativeSiteRegistration[4];
        long sink = 0;
        void Run()
        {
            for (int index = 0; index < 200; index++)
            {
                int rva = 0x1000 + index * 0x40 + 3;
                sink += registry.Query(rva, 4, buffer);
                sink += registry.Query(rva, 1, null);
                if (registry.TryGetWriter(rva, out NativeSiteRegistration writer)) sink += writer.Length;
                sink += registry.Registrations.Count + registry.Registrations[index].Rva;
            }
        }
        for (int warm = 0; warm < 50; warm++) Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 200; pass++) Run();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0, allocated, $"query fast path allocated {allocated} bytes (sink {sink})");
    }
}
