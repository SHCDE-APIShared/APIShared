using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public class SavegameRecordComparisonTests
{
    private static readonly KeyValuePair<string, string>[] Declared =
    {
        new("Author.Garrison", "Author.Garrison.Data"),
        new("Author.Navy", "Author.Navy.Ships"),
    };

    private static List<SavegameRecordDigest> Digests(Dictionary<string, byte[]> stored,
        IEnumerable<KeyValuePair<string, string>> declared = null) =>
        SavegameRecordDigest.Capture(declared ?? Declared, id => stored.TryGetValue(id, out byte[] bytes) ? bytes : null);

    [TestMethod]
    public void IdenticalStoredRecordsMatch()
    {
        var stored = new Dictionary<string, byte[]> { ["Author.Garrison.Data"] = new byte[] { 1, 2, 3 } };
        List<SavegameRecordDigest> host = Digests(stored), peer = Digests(new(stored));
        Assert.IsNull(SavegameRecordComparison.Describe(host, peer, "Bob"));
        Assert.AreEqual(SavegameRecordState.Present, host[0].State);
        Assert.AreEqual(32, host[0].Hash.Length);
        Assert.AreEqual(SavegameRecordState.Absent, host[1].State, "A save without the record has it absent on every player.");
        Assert.AreEqual(0, host[1].Hash.Length);
        Assert.IsNull(SavegameRecordComparison.Describe(new List<SavegameRecordDigest>(), new List<SavegameRecordDigest>(), "Bob"));
    }

    [TestMethod]
    public void EachDifferenceNamesThePlayerModAndRecord()
    {
        var full = new Dictionary<string, byte[]> { ["Author.Garrison.Data"] = new byte[] { 1 }, ["Author.Navy.Ships"] = new byte[] { 2 } };
        var empty = new Dictionary<string, byte[]>();
        var changed = new Dictionary<string, byte[]> { ["Author.Garrison.Data"] = new byte[] { 9 }, ["Author.Navy.Ships"] = new byte[] { 2 } };

        string missingOnPeer = SavegameRecordComparison.Describe(Digests(full), Digests(empty), "Bob");
        StringAssert.Contains(missingOnPeer, "record 'Author.Garrison.Data' of mod 'Author.Garrison' is in the host's save but missing from Bob's");
        StringAssert.Contains(missingOnPeer, "record 'Author.Navy.Ships' of mod 'Author.Navy'");
        StringAssert.Contains(SavegameRecordComparison.Describe(Digests(empty), Digests(full), "Bob"),
            "is missing from the host's save but present in Bob's");
        string differs = SavegameRecordComparison.Describe(Digests(full), Digests(changed), "Bob");
        StringAssert.Contains(differs, "record 'Author.Garrison.Data' of mod 'Author.Garrison' differs between the host's save and Bob's");
        Assert.IsFalse(differs.Contains("Author.Navy.Ships"), "Matching records are not reported.");
        StringAssert.Contains(differs, "desynchronize");

        var oneDeclared = new[] { Declared[0] };
        StringAssert.Contains(SavegameRecordComparison.Describe(Digests(full), Digests(full, oneDeclared), "Bob"),
            "record 'Author.Navy.Ships' of mod 'Author.Navy' is declared by the host but not by Bob");
        StringAssert.Contains(SavegameRecordComparison.Describe(Digests(full, oneDeclared), Digests(full), "Bob"),
            "is declared by Bob but not by the host");
    }

    [TestMethod]
    public void UnreadableRecordsNeverMatchAndOnlyTheFailingRecordIsAffected()
    {
        var stored = new Dictionary<string, byte[]> { ["Author.Navy.Ships"] = new byte[] { 2 } };
        List<SavegameRecordDigest> broken = SavegameRecordDigest.Capture(Declared,
            id => id == "Author.Garrison.Data" ? throw new InvalidDataException("damaged") : stored.TryGetValue(id, out byte[] b) ? b : null);
        Assert.AreEqual(SavegameRecordState.Unreadable, broken[0].State);
        Assert.AreEqual(SavegameRecordState.Present, broken[1].State);
        StringAssert.Contains(SavegameRecordComparison.Describe(broken, broken, "Bob"),
            "record 'Author.Garrison.Data' of mod 'Author.Garrison' cannot be read from the host's save");
        StringAssert.Contains(SavegameRecordComparison.Describe(Digests(stored), broken, "Bob"), "cannot be read from Bob's save");
    }

    [TestMethod]
    public void TheExplanationListsAtMostThreeDifferences()
    {
        var declared = Enumerable.Range(0, 6).Select(i => new KeyValuePair<string, string>("Author.Mod", "Record" + i)).ToArray();
        var host = Digests(declared.ToDictionary(x => x.Value, x => new byte[] { 1 }), declared);
        var peer = Digests(new Dictionary<string, byte[]>(), declared);
        string text = SavegameRecordComparison.Describe(host, peer, "Bob");
        StringAssert.Contains(text, "Record0");
        StringAssert.Contains(text, "Record2");
        Assert.IsFalse(text.Contains("Record3"));
        StringAssert.Contains(text, "and 3 more");
    }

    [TestMethod]
    public void PeerSuppliedListsAreValidated()
    {
        Assert.IsTrue(SavegameRecordComparison.IsValid(Digests(new())));
        Assert.IsFalse(SavegameRecordComparison.IsValid(null));
        var tooMany = Enumerable.Range(0, 65).Select(i => new SavegameRecordDigest("o", "r" + i, SavegameRecordState.Absent, null)).ToList();
        Assert.IsFalse(SavegameRecordComparison.IsValid(tooMany));
        Assert.IsFalse(SavegameRecordComparison.IsValid(new[]
        {
            new SavegameRecordDigest("o", "Same", SavegameRecordState.Absent, null),
            new SavegameRecordDigest("o", "SAME", SavegameRecordState.Absent, null),
        }), "Identifiers are unique ignoring case, as archive names are.");
        Assert.IsFalse(SavegameRecordComparison.IsValid(new[] { new SavegameRecordDigest("o", "r", SavegameRecordState.Present, new byte[31]) }));
        Assert.IsFalse(SavegameRecordComparison.IsValid(new[] { new SavegameRecordDigest("o", "r", SavegameRecordState.Absent, new byte[32]) }));
        Assert.IsFalse(SavegameRecordComparison.IsValid(new[] { new SavegameRecordDigest("o", "r", (SavegameRecordState)7, null) }));
        Assert.IsFalse(SavegameRecordComparison.IsValid(new[] { new SavegameRecordDigest(" ", "r", SavegameRecordState.Absent, null) }));
        Assert.IsFalse(SavegameRecordComparison.IsValid(new[] { new SavegameRecordDigest("o", new string('r', 201), SavegameRecordState.Absent, null) }));
    }

    [TestMethod]
    public void TheHostAcceptsOnlyCurrentPeersWhoseRecordsMatch()
    {
        var stored = new Dictionary<string, byte[]> { ["Author.Garrison.Data"] = new byte[] { 1 } };
        var agreement = new SavegameLoadAgreement("load-3", "values", new ulong[] { 101, 102 }, Digests(stored));
        var other = Digests(new() { ["Author.Garrison.Data"] = new byte[] { 2 } });

        Assert.IsFalse(agreement.Accept(101, "old-load", "values", other, "Bob", out string stale));
        Assert.IsNull(stale, "A stale confirmation must not end the current preparation.");
        Assert.IsFalse(agreement.Accept(999, "load-3", "values", other, "Eve", out string foreign));
        Assert.IsNull(foreign);

        Assert.IsFalse(agreement.Accept(102, "load-3", "values", other, "Carol", out string mismatch));
        StringAssert.Contains(mismatch, "Carol");
        StringAssert.Contains(mismatch, "Author.Garrison.Data");
        Assert.IsFalse(agreement.Accept(102, "load-3", "values", null, "Carol", out string invalid));
        StringAssert.Contains(invalid, "invalid");

        Assert.IsTrue(agreement.Accept(101, "load-3", "values", Digests(new(stored)), "Bob", out string none));
        Assert.IsNull(none);
        Assert.IsFalse(agreement.Ready);
        CollectionAssert.AreEqual(new ulong[] { 102 }, agreement.Unconfirmed.ToArray());
        Assert.IsTrue(agreement.Accept(102, "load-3", "values", Digests(new(stored)), "Carol", out _));
        Assert.IsTrue(agreement.Ready);
        Assert.AreEqual(0, agreement.Unconfirmed.Count());

        var settingsOnly = new SavegameLoadAgreement("load-4", "values", new ulong[] { 101 });
        Assert.IsTrue(settingsOnly.Accept(101, "load-4", "values", new List<SavegameRecordDigest>(), "Bob", out _),
            "Without declared records only the settings fingerprint matters.");
    }
}
