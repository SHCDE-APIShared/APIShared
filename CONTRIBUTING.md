# Contributing

Open an issue with a reproducible problem or submit a development pull request against `develop`.
Describe the resulting behavior, affected contracts and tests you ran. Contributions
can add services or reorganize implementation; the current folder layout is a guide,
not a requirement. No AI tools or separate mod workspace are needed.
Use the [feature implementation guide](ARCHITECTURE.md#finding-and-extending-a-feature)
to locate contracts, services and hook owners. The HUD service shows
how larger features keep registrations, behavior and hooks in named parts.

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
an optional override. Paths are not tied to the maintainer's machine.
After a successful build, the APIShared and publisher BAT drivers can reuse these
local machine paths from `.local/build-proof.json` when the environment is unset.
Explicit environment settings take precedence; missing or invalid references still
produce an error before compilation. Direct IDE/MSBuild commands require their own
environment or properties.

## Tests

GitHub Actions runs source validation and the game-independent Core.Tests suite
without a game installation or the maintainer's surrounding workspace. Core.Tests
uses the public SharpZipLib 1.4.2 package in that environment; local runs with a
configured installation keep using the installed Extender library. Release-helper
tests always cover APIShared's own publishing decisions with simulated GitHub;
additional surrounding workspace-driver tests run when those drivers are present.
Real game/Extender integration remains part of the local build driver.

Tests use MSTest and appear in Visual Studio Test Explorer. Run the same commands
from PowerShell. The core suite needs no game installation:

```powershell
dotnet test tests/Core.Tests/Core.Tests.csproj
dotnet test tests/Core.Tests/Core.Tests.csproj --filter FullyQualifiedName~JsonTests
```

With the installed dependencies configured:

```powershell
dotnet test tests/APISharedTests/APISharedTests.csproj
dotnet test tests/LobbyModSettingsPresetTests/LobbyModSettingsPresetTests.csproj
& './build.bat' /nopause /noinstall
```

The full driver validates runtime contracts and installed interop, runs all suites,
compiles public consumer examples and prepares the plugin package. Remove `/noinstall`
to install it after closing the game; elevation is only needed for protected directories.
The suites compare the compiled public API and the internals that InternalsVisibleTo
consumers bind with the checked-in [API compatibility baseline](docs/API_COMPATIBILITY.md);
CI compares only the game-independent subset. That guide lists the coverage split.
TRX results from the driver are stored in `.local/test-results`.
Successful builds also write `.local/build-proof.json`: source/reference fingerprints,
the source commit and package hashes. `tools/BuildProof.ps1` validates this local evidence;
it does not attest provenance independently. Game-free build-evidence tests run with
`./tools/Validation/Test-BuildProof.ps1` and in CI.
Release checks accept a clean subsequent commit when every recorded build input
and package hash still matches. The recorded build commit is retained; changing
compiled sources, packaging inputs or delivered files still invalidates evidence.

Core tests compile the actual dependency-free implementations. UnitAccess doubles model
the external SDK boundary, not native layout. Runtime tests use the real installed
assemblies and isolated memory/backend fixtures. Preset tests compile the actual
settings implementation with UI/session doubles; they verify persistence and convergence,
not rendering in the game. Controller tests use a plain settings host to verify role
filtering, mission isolation, reentrant setters and snapshot ownership without a UI.
Process-wide fixtures run serially and restore mutated state.
Temporary preset and atomic-file directories are cleaned up after each test.

Savegame participation tests cover
the generic plugin exclusion contract independently of any consumer mod.

Pull-request CI runs core tests and source/metadata/XAML checks on a GitHub-hosted
Windows runner. It does not claim to run installed-game integration or gameplay tests.
For changes affecting gameplay, report the relevant game checks and any remaining gaps.

## Compatibility and releases

Keep required public contracts and serialized formats compatible unless a breaking
change is explicitly intended and documented. An API change updates the
[baseline](docs/API_COMPATIBILITY.md#updating-the-public-baseline) in the same pull
request. APIShared owns its sources independently;
consumer mods use the public DLL. References in consumer packages use `Private=false`.
Avoid tests that prescribe source wording, filenames or implementation order. Test
observable behavior; retain byte/layout expectations where they express a native contract.

SHCDE destroys startup plugin components. Persistent services need a static root or
long-lived publisher. Published hooks remain installed until process exit; settings
use logical activation. The runtime's dependency-free JSON parser avoids loading extra
serializer assemblies. See [native compatibility](docs/NATIVE_COMPATIBILITY.md) before
changing native behavior and the [API catalog](docs/API_CATALOG.md) for threading rules.

Consultation with the other maintainer before a breaking change is recommended,
not mandatory. Either maintainer may decide independently. Assess binary, source
and behavioral compatibility, document affected contracts and consumer impact, and
provide appropriate tests and migration guidance where needed. A second maintainer's
approval is not a prerequisite for implementation or merging.

Either maintainer may prepare releases from a tested, release-ready `main` revision
in a committed and pushed standalone checkout:
`release.bat` builds and creates a GitHub draft. Version bumps belong to release preparation,
not test iterations. The Serpens66 mod workspace uses an independent local APIShared clone and records exact SDK inputs in release provenance;
contributors work in this repository with ordinary branches and pull requests.
The release driver creates build evidence, checksums and provenance; it does not
automatically test both maintainers' consumer mods in the game. For changes affecting
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
in fresh Windows PowerShell and check affected text files for CRLF. Preserve the
README as a short entry; propose an addition after a new feature is complete.
These expectations apply equally to human and AI-assisted contributions.
