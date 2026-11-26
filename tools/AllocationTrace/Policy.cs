namespace APIShared.AllocationTrace;

// Offline evidence policy, not a replacement allocation measurement. Event10
// samples correlate to wider marker brackets; their accumulated amounts must
// never be reconciled to the test's exact per-thread GC counter delta.
internal sealed record Marker(int ProcessId, int OsThreadId, int Index, double Milliseconds,
    int Window, bool Begin, int ManagedThreadId, long Before, long After);
internal sealed record CounterWindow(int Window, long Before, long After, int ManagedThreadId);
internal sealed record Frame(string Address, string Method, string Module, int IlOffset);
internal sealed record AllocationSample(int ProcessId, int OsThreadId, int Index, double Milliseconds,
    int Window, int ClrInstance, string TypeId, string Address, int Kind, int HeapIndex,
    long AllocationAmount64, long ObjectSize, string TypeName, Frame[] Frames, bool StackComplete);
internal sealed class Evidence
{
    internal int ProcessId;
    internal int PointerSize;
    internal int EventsLost;
    internal bool Truncated, TestModuleObserved, CoreLibObserved, RuntimeObserved, RundownComplete;
    internal int RundownIndex;
    internal string ControlType = "", TestPath = "";
    internal readonly List<CounterWindow> Counters = new();
    internal readonly List<Marker> Markers = new();
    internal readonly List<AllocationSample> Samples = new();
    internal readonly List<string> Problems = new();
    internal long OutsideWindows, OtherThreads;
}
internal sealed record WindowResult(int Window, int OsThreadId, int ManagedThreadId,
    long CounterBytes, int SampleRows, int MatchingResolvedSampleRows);
internal sealed record PolicyResult(bool Passed, string Status, string[] Reasons,
    int MatchingResolvedSampleCount, WindowResult[] Windows, AllocationSample[] Samples)
{
    public string AttributionStatus => Passed && MatchingResolvedSampleCount > 0 ? "PARTIAL_CANDIDATE_HINT" : "INCONCLUSIVE";
}

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
        if (string.IsNullOrWhiteSpace(e.TestPath)) Reject("TEST_MODULE_PATH_MISSING");
        if (e.Markers.Count > 8 || e.Samples.Count > 512) Reject("EVIDENCE_ROW_LIMIT");
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
        var indexes = new HashSet<int>();
        foreach (AllocationSample row in e.Samples)
        {
            if (!spans.TryGetValue(row.Window, out var span) || row.ProcessId != e.ProcessId ||
                row.OsThreadId != span.Start.OsThreadId || row.Index <= span.Start.Index || row.Index >= span.End.Index)
                Reject("SAMPLE_OUTSIDE_ITS_EXACT_PID_THREAD_BRACKET");
            if (row.Kind is < 0 or > 2 || row.HeapIndex < 0 || row.AllocationAmount64 <= 0 || row.ObjectSize <= 0 ||
                row.TypeId == "0x0" || row.Address == "0x0") Reject("INVALID_ALLOCATION_TICK_PAYLOAD");
            if (!indexes.Add(row.Index)) Reject("DUPLICATE_SAMPLE_EVENT_INDEX");
            if (string.IsNullOrWhiteSpace(row.TypeName)) Reject("SAMPLE_TYPE_UNRESOLVED");
            if (!row.StackComplete || row.Frames.Length == 0 || row.Frames.Length > 64)
                Reject("SAMPLE_STACK_MISSING_UNRESOLVED_OR_TRUNCATED");
            if (row.Window is 1 or 2)
            {
                if (row.Frames.Any(f => f.Method.Contains("CoreAllocationDiagnostics", StringComparison.Ordinal) ||
                    f.Method.Contains("Assert.", StringComparison.Ordinal)))
                    Reject("PROBE_OR_ASSERT_SAMPLE_AT_BRACKET_EDGE");
                if (!HasMeasuredCaller(row, e.TestPath)) Reject("MEASURED_SAMPLE_CALLER_NOT_JOINED");
            }
        }
        if (e.Samples.Select(a => a.ClrInstance).Distinct().Take(2).Count() != 1)
            Reject("SAMPLE_CLR_INSTANCE_AMBIGUOUS");
        int matching = 0;
        foreach (var pair in spans.OrderBy(p => p.Value.Start.Index))
        {
            var rows = e.Samples.Where(a => a.Window == pair.Key).ToArray();
            long delta = pair.Value.End.After - pair.Value.End.Before;
            if (pair.Key == 4 && (delta != 0 || rows.Length != 0)) Reject("EMPTY_MARKER_CONTROL_NOT_ZERO");
            if ((pair.Key is 1 or 2) && delta == 0 && rows.Length != 0)
                Reject("SAMPLE_IN_ZERO_COUNTER_BRACKET");
            int hints = (pair.Key is 1 or 2) && delta > 0
                ? rows.Count(a => a.StackComplete && HasMeasuredCaller(a, e.TestPath)) : 0;
            matching += hints;
            windows.Add(new(pair.Key, pair.Value.Start.OsThreadId,
                pair.Value.Start.ManagedThreadId, delta, rows.Length, hints));
        }
        var controls = e.Samples.Where(a => a.Window == 3 && a.TypeName == e.ControlType).ToArray();
        if (!controls.Any(a => a.ObjectSize >= 4L * 1024 * 1024 && a.StackComplete &&
            a.Frames.Any(f => SamePath(f.Module, e.TestPath) && f.Method.Contains("CoreAllocationDiagnostics.Complete", StringComparison.Ordinal))))
            Reject("DISTINCT_FOUR_MIB_CONTROL_TYPE_AND_STACK_MISSING");
        if (!windows.Any(w => w.Window == 3 && w.CounterBytes >= 4L * 1024 * 1024))
            Reject("FOUR_MIB_CONTROL_COUNTER_MISSING");
        // Invalid evidence cannot retain a tempting candidate count. Even valid
        // rows remain partial marker-bracket hints, never byte-complete attribution.
        bool passed = reasons.Count == 0;
        if (!passed)
            for (int i = 0; i < windows.Count; i++) windows[i] = windows[i] with { MatchingResolvedSampleRows = 0 };
        return new(passed, passed ? "QUALIFIED_SAMPLED_PIPELINE" : "INCONCLUSIVE",
            reasons.Order(StringComparer.Ordinal).ToArray(), passed ? matching : 0, windows.ToArray(), e.Samples.ToArray());
    }

    private static bool HasMeasuredCaller(AllocationSample row, string testPath) => row.Frames.Any(f =>
        SamePath(f.Module, testPath) && (f.Method.Contains("ValueModifierTests.InvalidInputsAreRejectedAndCompiledCommonPathDoesNotAllocate", StringComparison.Ordinal) ||
        f.Method.Contains(row.Window == 1 ? "ValueModifierPlan.Calculate(" : "ValueModifierPlan.CalculateRatio(", StringComparison.Ordinal)));

    private static bool SamePath(string left, string right) =>
        string.Equals(left.Replace('/', '\\'), right.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

    internal static int SelfTest()
    {
        Evidence Good()
        {
            var e = new Evidence { ProcessId = 77, PointerSize = 8, ControlType = "CoreAllocationDiagnostics+AllocationControl[]", TestPath = "C:/Fixture/Core.Tests.dll",
                TestModuleObserved = true, CoreLibObserved = true, RuntimeObserved = true, RundownComplete = true, RundownIndex = 90 };
            foreach (var w in new[] { (4, 10, 20, 100L, 100L), (1, 30, 40, 200L, 98552L), (3, 50, 60, 300L, 4194628L) })
            {
                e.Counters.Add(new(w.Item1, w.Item4, w.Item5, 9));
                e.Markers.Add(new(77, 11, w.Item2, w.Item2, w.Item1, true, 9, 0, 0));
                e.Markers.Add(new(77, 11, w.Item3, w.Item3, w.Item1, false, 0, w.Item4, w.Item5));
            }
            Frame[] frames = { new("0x1", "ValueModifierPlan.Calculate(System.Int32)", "C:/Fixture/Core.Tests.dll", 0) };
            Frame[] control = { new("0x2", "CoreAllocationDiagnostics.Complete()", "C:/Fixture/Core.Tests.dll", 0) };
            e.Samples.Add(new(77, 11, 35, 35, 1, 1, "0x10", "0x100", 0, 0, 106496, 24, "FixtureType", frames, true));
            e.Samples.Add(new(77, 11, 55, 55, 3, 1, "0x11", "0x200", 1, 0, 4198424, 4194328, e.ControlType, control, true));
            return e;
        }
        int checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("Policy self-test: " + label); checks++; }
        void Reject(Action<Evidence> mutation, string code)
        { Evidence e = Good(); mutation(e); PolicyResult result = Evaluate(e); Check(!result.Passed && result.AttributionStatus == "INCONCLUSIVE" && result.MatchingResolvedSampleCount == 0 && result.Windows.All(w => w.MatchingResolvedSampleRows == 0) && result.Reasons.Contains(code), code); }
        PolicyResult good = Evaluate(Good());
        Check(good.Passed && good.AttributionStatus == "PARTIAL_CANDIDATE_HINT" && good.MatchingResolvedSampleCount == 1, "sample is only a partial hint despite unequal tick amount, object size and98352-byte counter");
        Evidence miss = Good(); miss.Samples.RemoveAt(0);
        Check(Evaluate(miss).Passed && Evaluate(miss).AttributionStatus == "INCONCLUSIVE" && Evaluate(miss).MatchingResolvedSampleCount == 0, "qualified control cannot turn a missing measured sample into attribution");
        Evidence noFailure = Good(); noFailure.Samples.RemoveAt(0);
        noFailure.Counters[1] = noFailure.Counters[1] with { After = 200 };
        noFailure.Markers[3] = noFailure.Markers[3] with { After = 200 };
        noFailure.Counters.Add(new(2, 250, 250, 9));
        noFailure.Markers.Add(new(77, 11, 43, 43, 2, true, 9, 0, 0));
        noFailure.Markers.Add(new(77, 11, 47, 47, 2, false, 0, 250, 250));
        Check(Evaluate(noFailure).Passed && Evaluate(noFailure).MatchingResolvedSampleCount == 0,
            "zero-counter Calculate and ratio qualify only the pipeline, without measured samples");
        Reject(e => e.Markers[2] = e.Markers[2] with { ProcessId = 88 }, "WRONG_MARKER_PID");
        Reject(e => e.Samples[0] = e.Samples[0] with { OsThreadId = 12 }, "SAMPLE_OUTSIDE_ITS_EXACT_PID_THREAD_BRACKET");
        Reject(e => e.Markers.RemoveAt(3), "MISSING_DUPLICATE_OR_UNKNOWN_MARKER");
        Reject(e => e.EventsLost = 1, "EVENTS_LOST");
        Reject(e => e.Samples.RemoveAt(1), "DISTINCT_FOUR_MIB_CONTROL_TYPE_AND_STACK_MISSING");
        Reject(e => e.Samples[1] = e.Samples[1] with { TypeName = "System.Byte[]" }, "DISTINCT_FOUR_MIB_CONTROL_TYPE_AND_STACK_MISSING");
        Reject(e => e.Samples[1] = e.Samples[1] with { StackComplete = false }, "SAMPLE_STACK_MISSING_UNRESOLVED_OR_TRUNCATED");
        Reject(e => e.Truncated = true, "TRACE_TRUNCATED_OR_BOUNDED_READER_CUTOFF");
        Reject(e => e.Samples[0] = e.Samples[0] with { Frames = new[] { new Frame("0x1", "CoreAllocationDiagnostics.End", "C:/Fixture/Core.Tests.dll", 0) } }, "PROBE_OR_ASSERT_SAMPLE_AT_BRACKET_EDGE");
        Reject(e => e.Samples[0] = e.Samples[0] with { Frames = new[] { new Frame("0x1", "Unrelated.Work", "C:/Fixture/Core.Tests.dll", 0) } }, "MEASURED_SAMPLE_CALLER_NOT_JOINED");
        Reject(e => e.Samples[0] = e.Samples[0] with { Frames = new[] { new Frame("0x1", "ValueModifierPlan.Calculate(System.Int32)", "C:/Unrelated/Core.Tests.dll", 0) } }, "MEASURED_SAMPLE_CALLER_NOT_JOINED");
        Reject(e => { e.Markers[0] = e.Markers[0] with { Index = 31 }; e.Markers[1] = e.Markers[1] with { Index = 39 }; }, "REACHED_WINDOW_ORDER_MUST_BE_EMPTY_CALCULATE_OPTIONAL_RATIO_CONTROL");
        Reject(e => e.Samples[0] = e.Samples[0] with { Window = 4, Index = 15 }, "EMPTY_MARKER_CONTROL_NOT_ZERO");
        Console.WriteLine($"PASS {checks} pure sampled-evidence policy checks; no Core workload, measurement, EventPipe capture or TraceEvent decoder exercised.");
        return 0;
    }
}
