using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIShared.Economy;

namespace APIShared.Economy
{
    // The native initialization/publication boundary only. The registry and public API
    // are production sources; machine generation is exercised separately on real code.
    internal static class WorkerProductionBonusNativeRuntime
    {
        internal static bool Ready = true;
        internal static int Published = 50;
        internal static bool IsAvailable => Ready;
        internal static string Diagnostic => Ready ? string.Empty : "fixture unavailable";
        internal static void PublishPercent(int value) => Published = value;
    }
}

internal static partial class Program
{
    private static void TestRegistry()
    {
        int published = 50;
        bool available = true, reject = false;
        var publications = new List<int>();
        var registry = new WorkerProductionBonusRegistry(() => available, () => "fixture occupied", value => {
            if (reject) throw new InvalidOperationException("native publication rejected");
            published = value;
            publications.Add(value);
        });
        Check(registry.TryRegister("z.owner", "a", out var z, out _), "First independent owner registered.");
        Check(registry.TryRegister("a.owner", "z", out var a, out _), "Second independent owner registered.");
        Check(publications.Count == 0 && published == 50, "Registration is inactive and changes no native value.");
        Check(!registry.TryRegister("z.owner", "a", out var duplicate, out _) && duplicate == null, "Duplicate owner-local identity rejected.");
        Check(!registry.TryRegister(" ", "id", out _, out _) && !registry.TryRegister("owner", "", out _, out _), "Empty identity rejected.");
        a.SetPercent(-100);
        z.SetPercent(0);
        Check(published == 0, "Equal-order owner GUID is ordinal; later GUID wins.");
        a.SetPercent(999);
        Check(published == 0, "Earlier owner edit cannot mask a later active owner.");
        Check(registry.TryRegister("z.owner", "b", out var laterId, out _), "Same owner can register a different identity.");
        laterId.SetPercent(25);
        Check(published == 25, "Ordinal registration ID breaks equal-owner/order ties.");
        Check(registry.TryRegister("0.owner", "first", out var priority, out _, 1), "Explicit priority owner registered.");
        priority.SetPercent(1000);
        Check(published == 1000, "Explicit order precedes GUID and ID ordering.");
        priority.SetPercent(null);
        Check(published == 25, "Null reveals the previous owner.");
        laterId.SetPercent(null);
        Check(published == 0, "Null reveals an explicit zero contribution.");
        z.SetPercent(null);
        Check(published == 999, "Disabling one owner preserves a different active owner.");
        foreach (int bad in new[] { int.MinValue, -101, 1001, int.MaxValue })
        {
            int before = publications.Count;
            bool refused = false;
            try { a.SetPercent(bad); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            Check(refused && published == 999 && publications.Count == before, "Out-of-range input preserves native and registry state.");
        }
        a.SetPercent(50);
        Check(published == 50, "An explicit Vanilla value is valid.");
        reject = true;
        bool failed = false;
        try { a.SetPercent(-50); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && published == 50, "Failed native publication is visible to the caller.");
        reject = false;
        z.SetPercent(20);
        z.SetPercent(null);
        Check(published == 50, "Failed publication did not commit an unobservable contribution.");
        available = false;
        Check(!registry.TryRegister("unavailable", "id", out var absent, out string reason) && absent == null && reason.Contains("fixture occupied"), "Unavailable native owner rejects new registration with diagnostic.");
        failed = false;
        try { a.SetPercent(-10); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && published == 50, "An unavailable publication preserves the old contribution.");
        available = true;
        z.SetPercent(75);
        z.SetPercent(null);
        Check(published == 50, "Availability failure did not commit state.");
        a.SetPercent(null);
        Check(published == 50, "All inactive owners restore Vanilla.");

        var concurrent = new WorkerProductionBonusRegistry(() => true, () => "", value => published = value);
        var handles = new WorkerProductionBonusOverrideRegistration[8];
        for (int index = 0; index < handles.Length; index++)
            Check(concurrent.TryRegister("parallel", "slot" + index, out handles[index], out _, index), "Concurrent fixture owner registered.");
        Parallel.For(0, handles.Length, index => {
            for (int value = 0; value < 50; value++) handles[index].SetPercent(value);
            handles[index].SetPercent(index * 100);
        });
        Check(published == 700, "Concurrent configuration has deterministic ordered ownership after completion.");
        handles[7].SetPercent(null);
        Check(published == 600, "Concurrent writers retained independent final contributions.");

        WorkerProductionBonusNativeRuntime.Ready = false;
        Check(!WorkerProductionBonusOverrides.IsAvailable && WorkerProductionBonusOverrides.CompatibilityKey == string.Empty,
            "Unavailable service exposes no peer compatibility key.");
        WorkerProductionBonusNativeRuntime.Ready = true;
        Check(WorkerProductionBonusOverrides.IsAvailable && WorkerProductionBonusOverrides.CompatibilityKey.Contains(NativeHash),
            "Available public peer key includes the audited native identity.");
        Check(WorkerProductionBonusOverrides.TryRegister("external.test.mod", "fixed-yield", out var publicRegistration, out _),
            "Public API works with an arbitrary foreign mod identity.");
        publicRegistration.SetPercent(-50);
        Check(WorkerProductionBonusNativeRuntime.Published == -50, "Public registration publishes signed whole percentages.");
        publicRegistration.SetPercent(null);
        Check(WorkerProductionBonusNativeRuntime.Published == 50, "Public null disables logically.");
    }
}
