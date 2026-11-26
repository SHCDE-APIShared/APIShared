# Core allocation failure diagnostics

This opt-in hosted diagnostic investigates
[issue 124](https://github.com/SHCDE-APIShared/APIShared/issues/124). It records one
full Core suite occurrence and standard sampled allocation stacks. It is a
bounded search for partial hints, not a correction, benchmark, automatic retry or
replacement for ordinary CI.

## Run the selected diagnostic branch

The existing `Tests` workflow supports `workflow_dispatch`. Select the diagnostic
branch and set `value_modifier_allocation_diagnostic` to true. For example:

```powershell
gh workflow run tests.yml --repo SHCDE-APIShared/APIShared --ref codex/<diagnostic-branch> -f value_modifier_allocation_diagnostic=true
```

Only this diagnostic job runs for that opt-in; ordinary push, pull-request and
default dispatch paths retain their existing tests. The job uses SDK 10.0.404 and
runtime 10.0.15, then checks the actual testhost's runtime/cohort evidence. It builds
once and invokes the full Core suite once, without a filter, reordering or retry.
The capture command requires a GitHub-hosted Windows job. It does not trace a
developer's desktop or install tools globally. Evidence is uploaded on failure.

The separate `value_modifier_allocation_guard_probe` input runs only a tiny
synthetic open-writer fixture. It skips SDK setup, Core and EventPipe. Its expected
negative worker exit remains visible; a separate result qualifies early stopping
and owned cleanup. If both inputs are selected, this probe takes precedence.

## Measurement and compatibility

`CoreAllocationDiagnostics=true` defines a test-only compilation symbol. The
normal source branch retains the original 100-call warm-up, both 10,000-call loops,
counter expressions and strict equality assertions. The helper is excluded from
ordinary builds. No production modifier algorithm, assertion, threshold or default
JIT/tiering/PGO setting is changed as a correction.

Typed EventSource markers precede the starting counter and follow the captured
ending counter. The diagnostic asserts exact equality against that same ending
value. Hashing, JSON and the controls run outside the measured counters. A zero-work
marker control records the edge cost. After the last reached measured interval,
the helper allocates one escaping array of a distinct 128-byte blittable struct:
32,768 elements, or 4 MiB of payload. Its sampled type is the array itself, not a
small wrapper around a generic byte array. The existing End-to-Complete lifecycle
also runs this control after a failed first counter, before its strict assertion.

Tracing and diagnostic controls can change runtime timing and reproduction. A
passing instrumented occurrence does not prove equivalent uninstrumented behavior.
The four actual test DLL/PDB/deps/runtimeconfig files are copied and verified before
the test; source, compiler, runtime and helper metadata identify the cohort.

## Sampled trace contract and interpretation

Generated runsettings add startup tracing only to the testhost. The build and test
consoles are not the measured process. A diagnostic-only module initializer snapshots
these settings, then removes them from the testhost's child environment so subprocess
fixtures do not start additional trace sessions. Its existing native session continues.

Runtime mask `0x410b0099`, level 5, requests ordinary `GCAllocationTick` event 10,
managed stacks and the existing method/type resolution metadata. It excludes both
legacy all-object allocation flags and modern probabilistic allocation sampling.
Both stackwalk aliases are explicitly enabled. Requested flags do not prove actual
stack resolution: the control event, exact type and resolved stack must be present.

The .NET 10 implementation accumulates approximately 100 KiB per heap and heap kind;
the small-object path can account for newly granted allocation-context space. Thus
98,352 bytes can produce no tick. `AllocationAmount64` is an accumulated amount,
not the exact bytes allocated by the named object, type, thread or marker interval.
The parser preserves it and `ObjectSize` as separate raw fields. It never sums tick
amounts, subtracts a sampling threshold or reconciles them to the strict counter.

The report selects the helper PID and runtime instance. Begin/End event headers
establish the OS thread; their managed thread ID remains a separate payload. Only
event-10 version-4 samples within that PID/thread bracket are retained. Their types
must resolve consistently, and their stacks must resolve without truncation. Measured
samples must include the compiled test caller or relevant modifier method in the
actual test-module path. The positive control requires the exact distinct array type,
at least 4 MiB object size and its helper stack in that same module and control bracket.
Complete markers, zero-work control, module/runtime joins and rundown are required.

Markers bracket a wider interval than the counters. A helper or Assert stack,
unrelated caller, sample inside a zero-counter bracket, missing stack/type, event
loss, truncation or other ambiguity makes attribution `INCONCLUSIVE`. Unresolved
native frames are not invented from managed stacks. Even a valid control proves
only that the sampler/type/stack pipeline observed the control region; it cannot
prove coverage of earlier windows or exclude a missed allocation.

The report's `pipelineStatus=QUALIFIED_SAMPLED_PIPELINE` describes those controls
and joins. Parser exit 0 admits only this pipeline; its attribution status can
still be `INCONCLUSIVE`. The owner separately interprets the strict TRX/counters
and always preserves the original test exit. Test and attribution outcomes are:

- A passing target with zero measured counter deltas is `NOT_REPRODUCED`.
- A failing target with a positive counter delta and a matching resolved sample
  yields only `PARTIAL_ALLOCATION_SAMPLE_HINT`. The stack is a candidate for
  investigation, not proof that it accounts for the counter delta or caused issue 124.
- A failed interval without such a sample remains `INCONCLUSIVE`, even when the
  positive control qualifies. Absence of a sample is never a zero-allocation claim.

The original test exit is preserved. No result makes this diagnostic a green fix
or closes issue 124. The former all-object trace and its rejected oversized result
remain historical evidence; this profile does not silently fall back to it.

## Budgets and evidence limits

The workflow has a five-minute deadline. Its GO-gated owned process group retains
memory limits, KillOnClose and accounting-based termination/drain. The owner checks
live trace/log/derived sizes through fresh shared file handles because directory
metadata can lag open writes. Unknown critical sizes fail closed. It stops at
96 MiB of trace and accepts at most 128 MiB. Existing total-output, log, parser
event/time/derived-output, row and stack-depth limits remain in place. Sampling is
expected to reduce event volume, but its actual size and usefulness are unmeasured
until a bounded run completes; a cap refusal is not permission to retry or raise it.

Useful evidence includes the original TRX, actual child runtime, retained compiled
cohort, compiler binlog, console output, marker/counter/cohort data,
trace, sampled rows and owner cleanup. No game, native dump, SDK/runtime corpus
copy or global tool installation is part of this profile.

Verbose VSTest diagnostics are omitted: the earlier run generated over 5.5 MB of
those logs, exceeding the unchanged 4 MiB log budget. The helper and trace module
joins still verify the actual testhost PID, runtime and compiled cohort.

The separate `tools/AllocationTrace` reader uses a pinned public TraceEvent package.
Its pure policy checks reject concrete false correlations: wrong PID/thread/module,
helper edges, missing/incorrect control types, unresolved stacks and lost/truncated
evidence. They also check that a qualified control cannot turn a missing measured
sample into attribution. They do not execute the Core workload or replace actual
EventPipe decoding and controls.

## Implementation references

The sampling contract is checked against .NET 10.0.15
[threshold and accumulated accounting](https://github.com/dotnet/runtime/blob/v10.0.15/src/coreclr/gc/gc.cpp#L17789-L17801),
[allocation-context accounting](https://github.com/dotnet/runtime/blob/v10.0.15/src/coreclr/gc/gc.cpp#L15847-L15852),
[event-10 manifest](https://github.com/dotnet/runtime/blob/v10.0.15/src/coreclr/vm/ClrEtwAll.man#L3091-L3110),
[type retrieval](https://github.com/dotnet/runtime/blob/v10.0.15/src/coreclr/vm/gchelpers.cpp),
and [EventPipe session/rundown](https://github.com/dotnet/runtime/blob/v10.0.15/src/native/eventpipe/ep.c).
The pinned [TraceEvent decoder](https://github.com/microsoft/perfview/blob/v4.0.2/src/TraceEvent/Parsers/ClrTraceEventParser.cs#L3777-L3787)
supplies typed version-4 fields. See also
[EventPipe settings](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe).
