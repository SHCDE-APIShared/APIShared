# Core allocation failure diagnostics

This opt-in hosted diagnostic investigates
[issue 124](https://github.com/SHCDE-APIShared/APIShared/issues/124). It records a
single full Core suite occurrence with allocation evidence. It is not a correction,
benchmark, automatic retry or replacement for ordinary CI.

## Run the selected diagnostic branch

The existing `Tests` workflow supports `workflow_dispatch`. Select the diagnostic
branch and set `value_modifier_allocation_diagnostic` to true. For example:

```powershell
gh workflow run tests.yml --repo SHCDE-APIShared/APIShared --ref codex/<diagnostic-branch> -f value_modifier_allocation_diagnostic=true
```

Only this diagnostic job runs for that opt-in; ordinary push, pull-request and
default dispatch paths retain their existing tests. The job uses SDK 10.0.404 and
requests runtime 10.0.15, then checks the actual testhost's runtime/cohort evidence.
It builds once and invokes the full Core suite once, with no test filter or retry.
The capture command deliberately requires a GitHub-hosted Windows job. It does
not install tools globally or trace a developer's desktop.

The workflow uploads its bounded evidence even when the test or diagnostic fails.
A failure remains a failure. A passing measured occurrence is `NOT_REPRODUCED`
and does not make this diagnostic green or resolve issue 124. Missing, dropped,
truncated, mismatched or unresolved evidence is `INCONCLUSIVE`.

## Measurement and compatibility

`CoreAllocationDiagnostics=true` defines a test-only compilation symbol. The
normal source branch keeps the original 100-call warm-up, both 10,000-call loops,
counter expressions and strict equality assertions. The helper is excluded from
ordinary builds. No production modifier algorithm, threshold or JIT/tiering/PGO
setting is changed as a correction.

The diagnostic branch emits typed EventSource markers before the starting counter
and after a captured ending counter. It asserts exact equality against that same
ending value. JSON, hashing, controls and reporting occur outside those counters.
A zero-work marker control records the edge cost; a distinct escaping allocation
control validates the allocation/type/stack pipeline after the last reached interval.

Full allocation tracing changes the runtime's allocation helper selection and can
change timing or whether the failure occurs. This is an instrumented attribution
cohort, not proof of equivalent runtime behavior. The compiled test, PDB, runtime,
source checkout and diagnostic configuration are retained with each occurrence.

## Trace ownership and interpretation

Generated runsettings add startup tracing only to the testhost. The dotnet build
and test consoles are not the measured process. A diagnostic-only module initializer
snapshots those settings, then removes them from that testhost's child environment
after runtime startup so subprocess fixtures do not acquire extra trace buffers.
The existing current session continues, and its controls/events must still qualify.
Runtime provider mask
`0x432b0098` includes both legacy allocation flags, types/names and managed-stack
metadata. Both stackwalk aliases are explicitly enabled. The session's normal
shutdown supplies rundown; a mask or requested configuration is not evidence that
stack resolution actually succeeded.

The report selects the helper's actual PID. Begin and End event headers establish
its OS thread; the managed thread ID is a separate payload and is never treated
as an OS ID. Type IDs are joined to runtime type metadata, and allocation stacks
remain associated with their individual events. The report requires the zero-work
control, known allocating control, complete markers and rundown, matching cohort,
and exact X64 aligned-byte reconciliation. It rejects dropped events and ambiguous
probe/Assert boundary allocations rather than attributing them to `Calculate`.
Unresolved native frames are not invented from managed stacks.

Default GC allocation ticks and modern probabilistic allocation sampling do not
guarantee an event for the original 98,352-byte interval. This profile therefore
enables both legacy allocation flags at testhost startup. The report preserves the
raw event sizes and uses the verified X64 allocation alignment; it never subtracts
a threshold or applies a tolerance to force agreement.

## Budgets and evidence limits

The workflow has a five-minute deadline. Its owned process group has memory limits,
KillOnClose protection, a GO-gated worker and accounting-based termination/drain.
The owner stops at a 96 MiB trace warning threshold; traces above 128 MiB are not
accepted. Output and log caps are checked while the worker runs. The parser has
separate event, time, derived-output, row and stack-depth limits. No game, native
dump, SDK/runtime corpus copy or global tool installation is part of this profile.

Useful evidence includes the original test result, actual child identity/runtime,
test DLL/PDB/deps/runtimeconfig pins, compiler binlog, standard vstest diagnostics,
marker/counter/cohort data, trace, parsed allocation rows and owner cleanup result.
Evidence capture cannot convert an original failed test into a successful run.
An empty trace or a diagnostic pass with no failing occurrence cannot establish
zero allocation in the original CI failure or establish its cause.

The separate `tools/AllocationTrace` report uses a pinned public TraceEvent package.
Its `--self-test` checks concrete false-attribution rejection paths; it does not
simulate Core's modifier behavior or replace the trace controls.

## Implementation references

The startup/allocation behavior is checked against runtime 10.0.15 source:
[provider startup snapshot and all-object branch](https://github.com/dotnet/runtime/blob/v10.0.15/src/coreclr/vm/eventtrace.cpp),
[object publication](https://github.com/dotnet/runtime/blob/v10.0.15/src/coreclr/vm/gchelpers.cpp),
[allocation alignment](https://github.com/dotnet/runtime/blob/v10.0.15/src/coreclr/gc/gc.cpp),
[EventPipe session/rundown](https://github.com/dotnet/runtime/blob/v10.0.15/src/native/eventpipe/ep.c).
See also [EventPipe environment settings](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe).
