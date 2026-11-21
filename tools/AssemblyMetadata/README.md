# Bounded managed assembly inspection

This developer CLI writes hash-bound metadata and complete selected IL from a
managed assembly. It uses Mono.Cecil 0.11.6 as a tool dependency, with a resolver
that rejects every dependency request. It never loads or executes the inspected
assembly, reads symbols, decodes custom-attribute arguments or follows callees.
It requires only the .NET 10 SDK; game and Script Extender installations are not
required. Never publish proprietary game assemblies as CI inputs or artifacts.

## Usage

```powershell
dotnet run --project tools/AssemblyMetadata/AssemblyMetadata.csproj -- `
  --input audit-input/SHCDESE.dll --sha256 <expected-64-hex-sha256> `
  --output audit-output/selected.json --types 'Exact.Namespace.Owner' `
  --include-nested --methods '.ctor;Prepare'
```

The output parent directory must already exist. Each option is accepted once.
`--types` requires one to 32 distinct exact Cecil full names separated by
semicolons. Nested names use `/`, for example `Namespace.Owner/Nested`; they are
excluded unless explicitly named or `--include-nested` selects descendants of a
requested type. There is no assembly-wide type or callee expansion.

`--methods` takes distinct exact method names and retains all matching overloads.
Every requested name must match within the selected type family. Alternatively,
`--called-member 'Exact.Callee.Type::MethodName'` selects methods whose IL directly
uses `call`, `callvirt` or `newobj` with that exact declaring type and member name.
The filter is not a substring match and does not follow the called implementation.
With neither filter, all methods declared by the selected types are reported,
including complete bodies; this is not an inventory-only mode. Fields of each
selected type are included regardless of the method filter. Missing types, method
names or a called-member selection with no matches cause failure, not empty success.

Success returns 0 and writes a new JSON file. Invalid input, selection, limits or
an existing output return 1. A report is completely serialized within its bound
before an owned temporary file is written and moved without overwrite to the
requested name. The input is opened read-only and its actual bytes must match the
mandatory expected SHA-256. Existing input and output files are never replaced.

## Report and bounds

Schema 1 uses camelCase properties. `inputSha256` and `inputBytes` identify the
actual bytes inspected. `assemblyName`, `moduleName` and `moduleMvid` identify the
assembly main module. `census` counts all of that module's type, method and field
definitions; only the explicitly selected types have declarations and bodies in
`types`. Multi-module assemblies requiring another metadata file are refused.

Each selected type includes its full name, attributes, base type, interfaces and
field signatures. Each selected method includes its full signature, MethodDef
`metadataToken` in hexadecimal, attributes and implementation attributes. Bodies
contain every instruction with its IL offset, opcode and typed operand, local
types, stack/init-locals metadata and exception regions. Member operands include
their metadata tokens. Abstract or external methods have `hasBody: false` and
`body: null`; no body is invented. This is metadata evidence, not decompilation or
proof that IL, native ABI, publication lifetime or gameplay behavior is correct.

Types also report `layoutKind` (`Auto`, `Sequential` or `Explicit`), nullable
`packingSize` and `classSize`; fields report a nullable `offset`. `layoutKind` comes from TypeDef layout flags; the numeric values are declared
ClassLayout/FieldLayout metadata values, not calculated runtime or marshalled
layout. Missing records produce null, while a declared zero remains zero. In
particular, sequential fields without explicit FieldLayout records have null
offsets; no offsets are inferred from field order, packing, pointer width or types.
Overlapping explicit offsets are reported unchanged. The reader does not resolve
field types or load the target to obtain these values. Cecil 0.11.6's
[type layout getters](https://github.com/jbevain/cecil/blob/0.11.6/Mono.Cecil/TypeDefinition.cs)
and [field layout getters](https://github.com/jbevain/cecil/blob/0.11.6/Mono.Cecil/FieldDefinition.cs)
define these metadata-only reads. Present values outside Cecil's signed public
representations are refused before inspection (packing above 32,767, class size
or field offset above 2,147,483,647), rather than reported as missing. Layout-table
row counts are bounded by the corresponding type/field definition counts. These
checks inspect the whole module's layout tables, including unselected types and
fields; an unrepresentable row there can refuse an input that the older tool
accepted for another selection.

The JSON additions are additive within schema 1, while the module-wide numeric preflight narrows accepted inputs as described above. Existing property names and
meanings remain unchanged, and the existing audit reader accepts extra properties.
Closed-shape consumers must accept the added properties. Layout consumers must
check that the properties exist: an older schema-1 report lacking `offset` is not
evidence that a field has no declared offset. The extra JSON still counts against
the existing complete-output limit and may make a previously near-limit report
fail; no report is truncated and no size limit is raised.

| Bound | Limit |
|---|---|
| Input bytes | 16 MiB |
| Main-module census | 50,000 types; 200,000 methods; 200,000 fields |
| Nested declaring-type depth | 32 |
| Selected types / methods / fields | 256 / 512 / 8,192 |
| Declared methods in the selected type family | 4,096 before member collection materialization |
| Type census names / selected declaration names | 2 MiB of characters each |
| Interfaces per selected type | 256 |
| IL bytes per inspected body | 256 KiB; lower with `--max-body-bytes` |
| Cumulative body bytes | 1 MiB for selected bodies or filter-scanned bodies |
| Instructions per body / cumulative | 65,536 / 262,144 |
| Called-member scanned methods | 4,096, including nonmatching bodies |
| Locals / exception regions per body | 4,096 / 4,096 |
| One metadata text value | 16,384 UTF-16 characters |
| Cumulative selected text | At most the requested output-byte budget in characters |
| Complete UTF-8 JSON | 512 KiB default; `--max-output-bytes` accepts 1 byte to 8 MiB |

The raw PE IL-length and cumulative byte checks precede Cecil instruction
materialization. Nonmatching bodies inspected for a called-member filter count
against scan budgets too, including repeated body RVAs. Exceeding any limit
refuses the entire report; the tool never truncates selected bodies to fit.
Limits bound the input and report shape, not an OS memory or CPU quota. Run
externally bounded jobs when inspecting unfamiliar or malformed inputs.

## Verification and compatibility

`tests/Core.Tests/AssemblyMetadataTests.cs` authors small real managed assemblies
with Cecil and exercises exact/nested selections, overloads, bodyless methods,
field and call operands, explicit/sequential/unset layout metadata, exception regions, unresolved external references,
filter scan budgets, checksum/PE/size refusals and fresh-output behavior. It runs
with the ordinary game-independent Core suite and never executes the fixture IL.

This developer tool does not change runtime-plugin APIs, hooks, initialization,
saves or dependency loading. The report and input compatibility limits above
still apply. Its Cecil dependency belongs to this tool and the Core test project
only. A metadata report cannot replace
the affected runtime/backend tests described in
[native compatibility](../../docs/NATIVE_COMPATIBILITY.md).

## Optional public-release CI audit

The existing **Tests** workflow has a `public_extender_metadata_audit` dispatch input,
off by default. Enable it on the published ref to build the inspector and audit the
explicit [Extender 2.14.4 fixture](../Validation/PublicExtenderMetadata.2.14.4.json).
Regular push/PR checks are unchanged.

```text
gh workflow run tests.yml --ref <published-ref> -f public_extender_metadata_audit=true
```

The eight-minute Windows job downloads the pinned public ZIP, verifies its hash,
and extracts only three hash-checked public DLLs. Seven exact probes have a combined
5 MiB report limit; JSON artifacts stay below 8 MiB and expire after seven days.
The constructor probe permits 2 MiB; other probes permit 512 KiB each. Missing
selectors or exceeded limits fail without truncated bodies. Downloads and target
DLLs are deleted and never uploaded. This is metadata evidence for that package,
not installed/native ABI, payment, hook activation or gameplay verification.

### Preparation profile

The package/probe counts above describe the default `baseline` profile. Set the
additional `public_extender_metadata_fixture` choice to `preparation` to inspect
the fixed [descriptor preparation and inline-hook lifetime selections](../Validation/PublicExtenderPreparation.2.14.4.json).
The audit still requires `public_extender_metadata_audit=true`.

```text
gh workflow run tests.yml --ref <published-ref> -f public_extender_metadata_audit=true -f public_extender_metadata_fixture=preparation
```

This profile reads only `RedBird.X64.dll` from the same pinned public package.
It exports complete selected descriptor/transaction bodies, including nested
helpers, and the selected inline-hook generation/lifetime methods. Existing
timeout, hash, output, cleanup, retention and metadata-only limitations still apply.
The choice selects fixed repository fixtures; arbitrary fixture paths are not
accepted by the workflow. Omit it to retain the baseline audit.

### Purchase callbacks profile

Set `public_extender_metadata_fixture=purchase-callbacks`, together with the audit
enable flag, for the fixed [purchase-tail callback and event
selections](../Validation/PublicPurchaseCallbacks.2.14.4.json).
Seven probes read only `SHCDESE.dll` from the same pinned public package, with a
combined 2.875 MiB report limit. They select resource-subtraction, movement and
tribe wrappers by the event constructors they call, the event payload/base
accessors and publisher, and the player/tribe hook registrations. The previously
inspected unit constructor is not repeated.

These bodies distinguish public argument mutability, cancellation, Pre/Post
payloads and wrapper return handling. They do not establish observer-library
exception/ordering policy, successful hook installation, native debit completion
or transactional recruitment. The JSON remains bounded and metadata-only.

### Reader bindings profile

Set `public_extender_metadata_fixture=reader-bindings`, together with the audit
enable flag, for the fixed [record, resource and lifecycle selections](../Validation/PublicReaderBindings.2.14.4.json).
This profile reads `SHCDESE.dll` and `RedBird.Core.dll` from the same public package.
Eight probes have a combined 7.25 MiB report limit: declared record layouts,
player resource readers, global address setup, process hook ownership, save/reset
wrappers, native demand storage, the physical-cost array reader and managed cost
tracking/reset methods. The last selection distinguishes first-write snapshots,
stack overrides and original-value resets; source-version identity alone is not
a substitute for those binary method contracts.

Layout values describe metadata declarations. Sequential field order and packing
do not establish computed native offsets. The profile does not prove native
predicate equivalence, payment, save/reset exclusion, deployment or gameplay.
The same timeout, checksum, cleanup, retention and metadata-only limits apply.
