# Contributing

Open an issue with a reproducible problem or submit a development pull request against `develop`.
Describe the resulting behavior, affected contracts and tests you ran. Contributions
can add services or reorganize implementation; the current folder layout is a guide,
not a requirement. You can work directly in this standalone repository.
Use the [feature implementation guide](ARCHITECTURE.md#finding-and-extending-a-feature)
to locate contracts, services and hook owners.

## Branches and maintainer rights

Serpens66 and Ensrick are equal maintainers. Either may integrate a ready change or
publish a release without approval from the other. Pull requests provide visibility
and discussion; an approving review from the other maintainer is not required.
CODEOWNERS, if added, should route advisory reviews rather than assign exclusive rights.

- `dev/Serpens66` and `dev/Ensrick` are personal development branches. Experiments
  and unfinished work are allowed; passing tests are not a prerequisite for pushing.
- `develop` contains completed features and fixes ready for integration and gameplay
  testing. Automated source validation and the game-independent CLI tests must pass
  before integration. It is not automatically ready for release.
- `main` contains tested, release-ready changes. Automated CLI checks must pass;
  runtime changes also need the relevant local build, integration and gameplay checks.
  Passing CI alone does not establish gameplay compatibility.

Use personal or feature branches for development, integrate ready work into `develop`,
then promote release-ready work to `main`. Use merge commits between persistent
branches; do not squash or rebase those branch pairs. Disposable feature branches
may use squash merges. A PR includes all changes not yet integrated: separate
unfinished experiments before promoting a branch.

Either maintainer may commit straightforward documentation corrections directly to
`main` without a separate approval. Applicable automated checks must still pass;
documentation-only changes do not require gameplay tests. Merge such corrections
back into `develop`, then into personal branches. Coordinate changes to a personal
branch with its owner; do not move a branch currently used by another working checkout.
Target substantive development contributions at `develop`; use `main` for release
promotion and the documentation exception above.

The intended protection policy requires automated CI checks on `main`, without
mandatory approving reviews, while permitting maintainer documentation corrections.
This describes the policy, not confirmation that GitHub protection is configured.
Personal branches have no mandatory test or review gate.

## Setup

On Windows, install the .NET 10 SDK and Visual Studio or Build Tools with the
.NET Framework 4.8.1 targeting pack. Full integration tests also need a local SHCDE
installation with BepInEx and Script Extender. Never commit these proprietary assemblies.

Set `SHCDE_GAME_DIR` to your installation directory before opening `APIShared.sln` or
running commands. `GameDir` is the equivalent MSBuild property.
`SHCDESE_EXTENDER_DIR` / `ExtenderDir` override the usual extender directory.
The build driver detects MSBuild using Visual Studio Installer; `SHCDE_MSBUILD` is
an optional override.
After a successful build, the APIShared and publisher BAT drivers can reuse these
local machine paths from `.local/build-proof.json` when the environment is unset.
Explicit environment settings take precedence; missing or invalid references still
produce an error before compilation. Direct IDE/MSBuild commands require their own
environment or properties.

## Tests

### Running tests

Tests use MSTest and appear in Visual Studio Test Explorer. From the repository
root, run the core suite with only the .NET 10 SDK; no game installation is needed:

```powershell
dotnet test tests/Core.Tests/Core.Tests.csproj
dotnet test tests/Core.Tests/Core.Tests.csproj --filter FullyQualifiedName~JsonTests
dotnet test tests/Content.Tests/Content.Tests.csproj
```

Content tests exercise the public game-independent library and its separate API
baseline. The Framework-to-SDK build regression can be run on Windows with
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Validation/Test-ContentBuild.ps1 -FixtureDirectory <empty-directory> -MSBuild <MSBuild.exe>`.
It builds the actual Content target with isolated fixture sources and verifies
nested edits, additions and removals. Its logs and fixture remain for inspection.

With the game and Extender dependencies configured as described in [Setup](#setup):

```powershell
dotnet test tests/APISharedTests/APISharedTests.csproj
dotnet test tests/LobbyModSettingsPresetTests/LobbyModSettingsPresetTests.csproj
```

Test observable behavior rather than source wording, filenames or implementation
order. Retain byte/layout expectations where they express a native contract.
Process-wide fixtures must run serially and restore mutated state; tests using
temporary directories must clean them up.

### Coverage and limitations

GitHub Actions runs source, metadata and XAML validation and the game-independent
Core.Tests and Content.Tests suites on Windows. Additional Linux checks cover Workshop recovery,
maintenance and shell scripts. See the [CI workflow](.github/workflows/tests.yml)
for the commands. CI does not run installed-game integration or gameplay tests.
Core.Tests uses the public SharpZipLib package when the installed Extender library
is unavailable. Release-helper tests simulate GitHub publishing; they do not
publish real releases.

Core tests compile the actual dependency-free implementations. Test doubles model
external SDK boundaries, not native memory layout. Runtime tests use the real
installed assemblies with isolated memory/backend fixtures. Preset and controller
tests check settings behavior with UI/session doubles; they do not verify in-game
rendering. For changes affecting gameplay, report the relevant game checks and
any remaining gaps in the pull request.

The local build validates runtime contracts and installed interop, runs the test
suites, compiles public consumer examples and prepares the plugin and publisher
packages. It also builds and tests the independent
[dependency bootstrap](docs/DEPENDENCY_BOOTSTRAP.md); that guide describes its
package/removal fixtures and remaining game and lifecycle acceptance checks.
Automated build success does not replace those checks.

Compatibility tests compare the compiled public API and internals used by
InternalsVisibleTo consumers with the checked-in
[API compatibility baseline](docs/API_COMPATIBILITY.md). CI checks only the
game-independent subset; the guide explains the coverage split.

### Selecting build sources and installation

Run the full build without installing its output:

```powershell
& './build.bat' /workspace /nopause /noinstall
```

`/workspace` (also available as `/source:working`) builds the current working tree,
including uncommitted changes, without committing or switching branches. The
publisher sub-build uses the same working sources. Do not combine this selection
with `/branch`.

To build the last local commit on `develop` instead:

```powershell
& './build.bat' /branch:develop /nopause /noinstall
```

`/branch:main` selects the last local `main` commit. Branch builds use a detached
worktree and leave the current checkout unchanged. Builds selected with
`/branch:develop` use `_dev` installation directories.

Without arguments, the APIShared and publisher BAT entrypoints offer a
`main`/`develop` menu. Calls with `/nopause` and no source selection default to the
current working tree. Release and internal child builds retain their pinned inputs.

`/noinstall` skips installation independently of source selection. Omit it to
install the built package; close the game before installation. The parent APIShared
driver handles installation, while the publisher helper only prepares local artifacts.

### Selecting build checks

`build.bat` supports `/tests:full` (the default), `/tests:quick` and `/tests:none`,
independently of source selection and `/noinstall`. For example:

```powershell
& './build.bat' /source:working /tests:quick /nopause /noinstall
```

`full` runs all maintained build checks. `quick` checks current runtime/JSON/lifecycle,
dependency metadata, standalone source/project boundaries, XAML, permanent hooks and
installed interop contracts, including compiled popularity references. It skips the
regression suites, native test programs, process/Workshop fixtures and consumer-example
builds. `none` skips those checks as well as test compilation. All modes still compile
and prepare the runtime, Content library, bootstrap and publisher; toolchain,
package-integrity and installation boundaries remain enforced.

Conflicting or invalid modes are errors. An explicit mode cannot silently select an
older branch driver without mode support. Direct `tools/Build.ps1` calls accept the
equivalent `-TestMode full|quick|none` parameter. Standalone validation defaults to
full checks; `-CheckMode quick` selects its source/interop subset.

Step logs include millisecond timestamps, durations and explicit skips. The local
`.local/build-steps.json` records the mode and individual results. Build evidence also
records the mode; only `full` is accepted as a tested SDK for cache reuse or release.
Reduced builds still retain machine paths for the next build. After updating the
evidence format, an older proof needs one fresh full build.

### Results and build evidence

The full build driver writes TRX results to `.local/test-results` and records a
successful build in `.local/build-proof.json`, including source/reference
fingerprints, the source commit and package hashes. `tools/BuildProof.ps1`
validates this local evidence; it does not independently attest provenance.
Game-independent build-evidence checks run in CI and can be run locally:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/Validation/Test-BuildProof.ps1
```

The installer consumer preflight has retained metadata fixtures for absent, legacy,
compatible and unrelated plugins. Run its real Windows PowerShell subprocess with
an existing empty fixture directory and a game installation supplying Mono.Cecil:
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Validation/Test-WorkshopConsumerReferences.ps1 -GameDir <game-directory> -FixtureDirectory <empty-directory>`.
This checks rejection before installation; it does not install packages or prove
actual hook coexistence in the game.

Release checks accept a clean subsequent commit when every recorded build input
and package hash still matches. The recorded build commit is retained; changes to
compiled sources, packaging inputs or delivered files invalidate the evidence.

## Compatibility and releases

Keep required public contracts and serialized formats compatible unless a breaking
change is explicitly intended and documented. An API change updates the
[baseline](docs/API_COMPATIBILITY.md#updating-the-public-baseline) in the same pull
request. APIShared owns its sources independently;
consumer mods use the public DLL. References in consumer packages use `Private=false`.

Follow the initialization, ownership and lifetime contracts in
[ARCHITECTURE](ARCHITECTURE.md), the
[native compatibility guide](docs/NATIVE_COMPATIBILITY.md) and the
[API catalog](docs/API_CATALOG.md) when changing shared runtime behavior.

Consultation with the other maintainer before a breaking change is recommended,
not mandatory. Either maintainer may decide independently. Assess binary, source
and behavioral compatibility, document affected contracts and consumer impact, and
provide appropriate tests and migration guidance where needed. A second maintainer's
approval is not a prerequisite for implementation or merging.

Either maintainer may prepare releases from a tested, release-ready `main` revision
in a committed and pushed standalone checkout:
`release.bat` builds and creates a GitHub draft. Version bumps belong to release preparation,
not test iterations.
The release driver creates build evidence, checksums and provenance; it does not
automatically test consumer mods in the game. For changes affecting
shared runtime behavior or compatibility, test the relevant consumer sets against
the same pinned APIShared revision and record the checks performed and remaining gaps.

## Documentation with a change

Explain public contracts where they are declared: prerequisites, owner and ID rules,
thread, lifetime, ordering, replay, failure and cancellation behavior where applicable.
Explain the responsibility and persistent call path at complex implementation entries.
Useful comments describe decisions and constraints rather than repeating names.

Keep the catalog, integration guide and affected examples consistent with API changes.
README is a short entry point; ARCHITECTURE explains common structure, ownership
and lifetime. The catalog helps readers find APIs and the integration guide explains
common consumer workflows. Substantial topic details belong in focused guides such
as [Audio](docs/AUDIO.md) and [Workshop](docs/WORKSHOP.md), with usage, public limits
and internal implementation clearly separated. Small features need no separate file.
Give detailed explanations one documentation home and link rather than duplicate.

When moving content, preserve contracts and evidence, update relative links and
anchors, and remove stale copies. Keep concrete native-update contracts in
[UpdateToNewDLL](UpdateToNewDLL.md). Run `tools/Validation/Test-Documentation.ps1`
in fresh Windows PowerShell and use CRLF line endings for affected text files.
Document completed features in the catalog and appropriate guides, adding a brief
README link where useful.
