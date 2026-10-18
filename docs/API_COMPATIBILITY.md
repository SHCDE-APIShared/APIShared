# API compatibility baseline

The checked-in baseline catches accidental binary and source breaks of the managed API,
including the internals that `InternalsVisibleTo` consumers compile against. Intentional
breaks remain possible under the [maintainer policy](../CONTRIBUTING.md#compatibility-and-releases);
this guide explains how to record them. Native contracts are covered by
[native compatibility](NATIVE_COMPATIBILITY.md).

## What is recorded

| File in `tests/ApiBaseline` | Contents | A difference means |
|---|---|---|
| `PublicApi.txt` | Every exported type with its public and protected members: kind, modifiers, base types, interfaces, signatures, parameter names, `ref`/`out`/`in`/`params`, defaults, generic constraints, constant and enum values | A removed or changed line breaks source or binary consumers. An added line is new API that must be recorded |
| `FriendInternals.<Assembly>.txt` | The internal types and members that the named consumer binds in its compiled IL, as binary identities without access or parameter names, plus the values of pinned internal enums | A missing line makes builds of that consumer fail at JIT time next to the new APIShared |

Each `InternalsVisibleTo` grant except `APISharedTests` needs exactly one pin file, and each
pin file needs its grant. Pinned internals are compatibility-relevant until a public
replacement exists and the consumer builds that use them are no longer supported. Each pin
file header names the consumer builds it was scanned from.

`PluginVersion` is recorded without its value, because the value changes with every release.

## Local build and public CI

| Check | `build.bat /nopause /noinstall` with the installed game | Public CI `core`, no game files |
|---|---|---|
| Public baseline of the whole compiled `APIShared.dll` | Yes (APISharedTests) | No: compiling `APIShared.dll` needs the game and Script Extender assemblies |
| Public baseline of the production types that Core.Tests compiles from `src/` | Yes | Yes. A type with partial parts that Core.Tests does not compile is checked for additions only |
| Friend pins still resolve and stay reachable | All pins | Only pins on types Core.Tests compiles |
| Grants and pin files name the same consumers | Yes | Yes, read from `Properties/AssemblyInfo.cs` |
| Baseline files are sorted, unique and CRLF | Yes | Yes |
| Formatter regression fixtures (`ApiSignatureTests`) | Yes | Yes |
| Rescan of compiled consumer assemblies | Opt-in, see below | No |
| Compiling the public consumer examples | Yes, existing build step | No |

The CI test results (the `test-results` TRX artifact) record how many baseline types and
pins were compared. A change outside that subset passes CI and fails only the local build, so
a pull request that changes compiled API should report the local build result.

## Updating the public baseline

With the installed dependencies configured as in [CONTRIBUTING](../CONTRIBUTING.md#setup),
regenerate the file and review its diff:

```powershell
$env:APISHARED_UPDATE_API_BASELINE = '1'
dotnet test tests/APISharedTests/APISharedTests.csproj --configuration Release --filter "FullyQualifiedName~PublicSurfaceMatchesTheBaseline"
Remove-Item Env:APISHARED_UPDATE_API_BASELINE
git diff tests/ApiBaseline
```

- Added lines only: an additive change. Commit the baseline with it. Consumers that use the
  addition need an APIShared release that contains it as their minimum version.
- Removed or changed lines: a breaking change. In the pull request, state for each affected
  member the old and new contract, the consumers it breaks (check the pin files and known
  consumers), and the migration path or compatible alternative. Update the catalog, guides and
  examples. Keeping the old member as a forwarding overload avoids most breaks.

## Friend consumer pins

Pin files are never regenerated from APIShared itself, because that would accept every break.
They change only through consumer scans and deliberate removals:

1. Keep the pinned member and add its replacement, public where consumers need it.
2. When no supported consumer build references the member any more, rescan those builds and
   delete the pin lines none of them needs.
3. Then change or remove the member.

To add a grant, add the `InternalsVisibleTo` line, create `FriendInternals.<AssemblyName>.txt`
with a header and scan the consumer.

### Rescanning a consumer

Scan compiled consumer assemblies, such as release assets or a local consumer build, against
the current build:

```powershell
$env:APISHARED_CONSUMER_SCAN = 'path\to\BugfixesAndQoL.dll'
dotnet test tests/APISharedTests/APISharedTests.csproj --configuration Release --filter "FullyQualifiedName~ScannedFriendConsumersBindOnlyPinnedInternals"
Remove-Item Env:APISHARED_CONSUMER_SCAN
```

The test fails for references that no longer resolve and for internal references missing from
the pin file. With `APISHARED_UPDATE_API_BASELINE=1` it adds the missing references; it never
removes pins, because older builds of the consumer may still need them. Then update the file
header with the scanned builds. Without `APISHARED_CONSUMER_SCAN` the test is skipped.

## Limits

The checks compare compiled shape, not behavior: thread, ordering, timing, payload meaning and
the other contracts of the maintainer policy still need review and tests. They do not record
attributes such as `Obsolete` or `Flags`, struct layout, nullable annotations, internal
constant values that consumers compile into their IL, or access by name through reflection or
Harmony. The scan matches internal overrides by name and parameter count, and does not detect
members added to an internal interface that a consumer implements.

## Internal implementation

`tests/ApiBaseline/ApiSignatures.cs` formats both files through reflection; the full local
run and CI link the same source. APISharedTests reads the compiled `APIShared.dll` and scans
consumers with Mono.Cecil. Core.Tests reads its own portable PDB: a type counts as production
when all its source documents are under `src/`, so test doubles of production types are
excluded. A type whose name is also declared `partial` in a `src/` file that Core.Tests does
not compile is checked for additions only.
