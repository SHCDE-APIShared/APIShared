namespace APIShared.AllocationTrace;

// Pure evidence policy only. These rows never emulate Calculate or create allocation
// measurements: the reader supplies actual TraceLog events and helper counter values.
internal sealed record Marker(int ProcessId, int OsThreadId, int Index, double Milliseconds,
    int Window, bool Begin, int ManagedThreadId, long Before, long After);
internal sealed record CounterWindow(int Window, long Before, long After, int ManagedThreadId);
internal sealed record Frame(string Address, string Method, string Module, int IlOffset);
internal sealed record Allocation(int ProcessId, int OsThreadId, int Index, double Milliseconds,
    int Window, int ClrInstance, string TypeId, string Address, int ObjectCount,
    long ObjectBytes, string TypeName, Frame[] Frames, bool StackComplete)
{
    // The legacy event records Object.GetSize. CoreCLR x64's GC allocates pointer-
    // aligned objects; retain both raw and aligned sizes and require exact equality.
    // No minimum-size adjustment or allowance is invented here. Other architectures
    // refuse in the reader. See runtime/v10.0.15 gc/gcpriv.h Align and vm/eventtrace.cpp.
    internal long AlignedBytes => checked((ObjectBytes + 7) & ~7L);
    public long? X64AlignedBytes => ObjectBytes <= 0 || ObjectBytes > long.MaxValue - 7 ? null : (ObjectBytes + 7) & ~7L;
}
internal sealed class Evidence
{
    internal int ProcessId;
    internal int PointerSize;
    internal int EventsLost;
    internal bool Truncated, TestModuleObserved, CoreLibObserved, RuntimeObserved, RundownComplete;
    internal int RundownIndex;
    internal string ControlType = "";
    internal readonly List<CounterWindow> Counters = new();
    internal readonly List<Marker> Markers = new();
    internal readonly List<Allocation> Allocations = new();
    internal readonly List<string> Problems = new();
    internal long OutsideWindows, OtherThreads;
}
internal sealed record WindowResult(int Window, int OsThreadId, int ManagedThreadId,
    long CounterBytes, long AlignedEventBytes, int AllocationRows, bool Reconciled);
internal sealed record PolicyResult(bool Passed, string Status, string[] Reasons,
    WindowResult[] Windows, Allocation[] Allocations);

internal static class EvidencePolicy
{
    internal static PolicyResult Evaluate(Evidence e)
    {
        var reasons = new HashSet<string>(e.Problems, StringComparer.Ordinal);
        void Reject(string reason) { if (reasons.Count < 64) reasons.Add(reason); }
        if (e.ProcessId <= 0) Reject("INVALID_HELPER_PID");
        if (e.PointerSize != 8) Reject("X64_TRACE_REQUIRED");
        if (e.EventsLost != 0) Reject("EVENTS_LOST");
        if (e.Truncated) Reject("TRACE_TRUNCATED_OR_BOUNDED_READER_CUTOFF");
        if (!e.TestModuleObserved || !e.CoreLibObserved || !e.RuntimeObserved)
            Reject("TESTHOST_RUNTIME_MODULE_JOIN_MISSING");
        if (!e.RundownComplete) Reject("RUNDOWN_COMPLETION_MISSING");
        if (string.IsNullOrWhiteSpace(e.ControlType)) Reject("CONTROL_TYPE_MISSING");
        if (e.Markers.Count > 8 || e.Allocations.Count > 512) Reject("EVIDENCE_ROW_LIMIT");
        var windows = new List<WindowResult>();
        var spans = new Dictionary<int, (Marker Start, Marker End)>();
        var counters = new Dictionary<int, CounterWindow>();
        foreach (CounterWindow counter in e.Counters)
            if (counter.Window is < 1 or > 4 || !counters.TryAdd(counter.Window, counter))
                Reject("DUPLICATE_OR_UNKNOWN_HELPER_WINDOW");
        foreach (var group in e.Markers.GroupBy(m => m.Window))
        {
            Marker[] begins = group.Where(m => m.Begin).ToArray();
            Marker[] ends = group.Where(m => !m.Begin).ToArray();
            if (group.Key is < 1 or > 4 || begins.Length != 1 || ends.Length != 1)
            { Reject("MISSING_DUPLICATE_OR_UNKNOWN_MARKER"); continue; }
            Marker start = begins[0], end = ends[0];
            if (start.ProcessId != e.ProcessId || end.ProcessId != e.ProcessId) Reject("WRONG_MARKER_PID");
            if (start.OsThreadId <= 0 || end.OsThreadId != start.OsThreadId) Reject("WINDOW_OS_THREAD_CHANGED");
            if (start.ManagedThreadId <= 0 || start.Index >= end.Index) Reject("INVALID_MARKER_ORDER_OR_THREAD");
            if (!counters.TryGetValue(group.Key, out CounterWindow? counter) ||
                counter.Before != end.Before || counter.After != end.After ||
                counter.ManagedThreadId != start.ManagedThreadId) Reject("HELPER_COUNTER_MARKER_MISMATCH");
            if (end.Before < 0 || end.After < end.Before) Reject("INVALID_COUNTER_RANGE");
            spans[group.Key] = (start, end);
        }
        if (!spans.Keys.Order().SequenceEqual(counters.Keys.Order())) Reject("WINDOW_SET_MISMATCH");
        foreach (int required in new[] { 1, 3, 4 })
            if (!spans.ContainsKey(required)) Reject("REQUIRED_WINDOW_MISSING_" + required);
        if (spans.TryGetValue(1, out var first))
        {
            bool firstFailed = first.End.After != first.End.Before;
            if (spans.ContainsKey(2) == firstFailed) Reject("REACHED_INTERVAL_SET_INCONSISTENT_WITH_FIRST_ASSERTION");
        }
        var ordered = spans.Values.OrderBy(s => s.Start.Index).ToArray();
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i - 1].End.Index >= ordered[i].Start.Index ||
                ordered[i - 1].Start.OsThreadId != ordered[i].Start.OsThreadId)
                Reject("OVERLAPPING_WINDOWS_OR_CONTROL_THREAD_CHANGED");
        int[] expectedOrder = spans.ContainsKey(2) ? new[] { 4, 1, 2, 3 } : new[] { 4, 1, 3 };
        if (!ordered.Select(s => s.Start.Window).SequenceEqual(expectedOrder))
            Reject("REACHED_WINDOW_ORDER_MUST_BE_EMPTY_CALCULATE_OPTIONAL_RATIO_CONTROL");
        if (ordered.Length != 0 && e.RundownIndex <= ordered[^1].End.Index)
            Reject("RUNDOWN_DOES_NOT_FOLLOW_REACHED_WINDOWS");
        var addresses = new HashSet<(int Window, int Clr, string Address)>();
        foreach (Allocation row in e.Allocations)
        {
            if (!spans.TryGetValue(row.Window, out var span) || row.ProcessId != e.ProcessId ||
                row.OsThreadId != span.Start.OsThreadId || row.Index <= span.Start.Index || row.Index >= span.End.Index)
                Reject("ALLOCATION_OUTSIDE_ITS_EXACT_PID_THREAD_BRACKET");
            if (row.ObjectCount != 1 || row.ObjectBytes <= 0) Reject("NON_SINGLE_OBJECT_ALLOCATION_EVENT");
            if (!addresses.Add((row.Window, row.ClrInstance, row.Address))) Reject("DUPLICATE_ALLOCATION_ADDRESS_IN_ONE_WINDOW");
            if (string.IsNullOrWhiteSpace(row.TypeName)) Reject("ALLOCATION_TYPE_UNRESOLVED");
            if (!row.StackComplete || row.Frames.Length == 0 || row.Frames.Length > 64)
                Reject("ALLOCATION_STACK_MISSING_UNRESOLVED_OR_TRUNCATED");
            if ((row.Window is 1 or 2) && row.Frames.Any(f =>
                f.Method.Contains("CoreAllocationDiagnostics", StringComparison.Ordinal) ||
                f.Method.Contains("Assert.", StringComparison.Ordinal)))
                Reject("PROBE_OR_ASSERT_ALLOCATION_AT_BRACKET_EDGE");
        }
        if (e.Allocations.Select(a => a.ClrInstance).Distinct().Take(2).Count() != 1)
            Reject("ALLOCATION_CLR_INSTANCE_AMBIGUOUS");
        foreach (var pair in spans.OrderBy(p => p.Value.Start.Index))
        {
            var rows = e.Allocations.Where(a => a.Window == pair.Key).ToArray();
            long sum = 0, delta = 0;
            try
            {
                foreach (Allocation row in rows) sum = checked(sum + row.AlignedBytes);
                delta = checked(pair.Value.End.After - pair.Value.End.Before);
            }
            catch (OverflowException) { Reject("ALLOCATION_ACCOUNTING_OVERFLOW"); }
            bool reconciled = sum == delta;
            if (!reconciled) Reject("COUNTER_EVENT_RECONCILIATION_FAILED_" + pair.Key);
            if (pair.Key == 4 && (delta != 0 || sum != 0 || rows.Length != 0)) Reject("EMPTY_MARKER_CONTROL_NOT_ZERO");
            windows.Add(new WindowResult(pair.Key, pair.Value.Start.OsThreadId,
                pair.Value.Start.ManagedThreadId, delta, sum, rows.Length, reconciled));
        }
        var controls = e.Allocations.Where(a => a.Window == 3 && a.TypeName == e.ControlType).ToArray();
        if (controls.Length != 1 || controls[0].ObjectCount != 1 || !controls[0].StackComplete ||
            !controls[0].Frames.Any(f => f.Method.Contains("CoreAllocationDiagnostics", StringComparison.Ordinal)))
            Reject("EXACT_ALLOCATING_CONTROL_TYPE_COUNT_OR_STACK_MISSING");
        if (!windows.Any(w => w.Window == 3 && w.CounterBytes > 0)) Reject("ALLOCATING_CONTROL_COUNTER_NOT_POSITIVE");
        return new PolicyResult(reasons.Count == 0, reasons.Count == 0 ? "TRACE_WINDOWS_VALIDATED" : "INCONCLUSIVE",
            reasons.Order(StringComparer.Ordinal).ToArray(), windows.ToArray(), e.Allocations.ToArray());
    }

    internal static int SelfTest()
    {
        Evidence Good()
        {
            var e = new Evidence { ProcessId = 77, PointerSize = 8, ControlType = "CoreAllocationDiagnostics+AllocationControl",
                TestModuleObserved = true, CoreLibObserved = true, RuntimeObserved = true, RundownComplete = true, RundownIndex = 90 };
            foreach (var w in new[] { (4, 10, 20, 100L, 100L), (1, 30, 40, 200L, 240L), (3, 50, 60, 300L, 476L) })
            {
                e.Counters.Add(new(w.Item1, w.Item4, w.Item5, 9));
                e.Markers.Add(new(77, 11, w.Item2, w.Item2, w.Item1, true, 9, 0, 0));
                e.Markers.Add(new(77, 11, w.Item3, w.Item3, w.Item1, false, 0, w.Item4, w.Item5));
            }
            Frame[] frames = { new("0x1", "ValueModifierPlan.Calculate", "Core.Tests.dll", 0) };
            Frame[] control = { new("0x2", "CoreAllocationDiagnostics.Control", "Core.Tests.dll", 0) };
            e.Allocations.Add(new(77, 11, 35, 35, 1, 1, "0x10", "0x100", 1, 33, "FixtureType", frames, true));
            e.Allocations.Add(new(77, 11, 55, 55, 3, 1, "0x11", "0x200", 1, 24, e.ControlType, control, true));
            e.Allocations.Add(new(77, 11, 56, 56, 3, 1, "0x12", "0x300", 1, 152, "System.Byte[]", control, true));
            return e;
        }
        int checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("Policy self-test: " + label); checks++; }
        void Reject(Action<Evidence> mutation, string code)
        { Evidence e = Good(); mutation(e); Check(!Evaluate(e).Passed && Evaluate(e).Reasons.Contains(code), code); }
        Check(Evaluate(Good()).Passed, "complete rows with explicit x64 padding reconcile");
        Reject(e => e.Markers[2] = e.Markers[2] with { ProcessId = 88 }, "WRONG_MARKER_PID");
        Reject(e => e.Markers[3] = e.Markers[3] with { OsThreadId = 12 }, "WINDOW_OS_THREAD_CHANGED");
        Reject(e => e.Markers.RemoveAt(3), "MISSING_DUPLICATE_OR_UNKNOWN_MARKER");
        Reject(e => e.EventsLost = 1, "EVENTS_LOST");
        Reject(e => e.Allocations.RemoveAt(1), "EXACT_ALLOCATING_CONTROL_TYPE_COUNT_OR_STACK_MISSING");
        Reject(e => e.Allocations[1] = e.Allocations[1] with { StackComplete = false }, "ALLOCATION_STACK_MISSING_UNRESOLVED_OR_TRUNCATED");
        Reject(e => e.Allocations[0] = e.Allocations[0] with { ObjectBytes = 41 }, "COUNTER_EVENT_RECONCILIATION_FAILED_1");
        Reject(e => e.Truncated = true, "TRACE_TRUNCATED_OR_BOUNDED_READER_CUTOFF");
        Reject(e => e.Allocations[0] = e.Allocations[0] with { Frames = new[] { new Frame("0x1", "CoreAllocationDiagnostics.End", "Core.Tests.dll", 0) } }, "PROBE_OR_ASSERT_ALLOCATION_AT_BRACKET_EDGE");
        Reject(e => { e.Markers[0] = e.Markers[0] with { Index = 31 }; e.Markers[1] = e.Markers[1] with { Index = 39 }; }, "REACHED_WINDOW_ORDER_MUST_BE_EMPTY_CALCULATE_OPTIONAL_RATIO_CONTROL");
        Reject(e => e.Allocations[0] = e.Allocations[0] with { Window = 4, Index = 15 }, "EMPTY_MARKER_CONTROL_NOT_ZERO");
        Console.WriteLine($"PASS {checks} pure trace-evidence policy checks; no Core workload, measurement, EventPipe capture or TraceEvent decoder exercised.");
        return 0;
    }
}
