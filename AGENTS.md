# APIShared contribution notes

This is a standalone community repository. See CONTRIBUTING.md for setup and tests,
ARCHITECTURE.md for responsibilities, and docs/API_CATALOG.md for existing features.

- Explain public contracts directly in code, including relevant prerequisites,
  ownership, IDs, thread, lifetime, ordering, replay, errors and cancellation.
- Explain complex implementation entries and persistent call paths; avoid comments
  that merely repeat symbol names. Keep mod-specific algorithms in consumer mods.
- Maintain the catalog, integration guide and affected examples with API changes.
  Prefer links and inline contracts; use a topic guide when substantial detail warrants it.
- Make every new feature discoverable in the catalog and document it in the appropriate guide.
  Keep README.md a short entry point with links where useful; this documentation
  maintenance is authorized by the maintainers.
- Preserve user changes. Do not commit or publish unless requested. Changes to runtime
  behavior need appropriate tests; documentation changes must not conceal behavior fixes.
- Do not create extra backup copies of Git-tracked files; Git history is sufficient.
  Uncommitted user changes are not covered by that history and must still be preserved.

Serpens66 and Ensrick are equal maintainers. Either may integrate changes and publish
releases without approval from the other. Pull-request reviews are advisory. Follow
the branch readiness and test requirements in [CONTRIBUTING.md](CONTRIBUTING.md#branches-and-maintainer-rights).
Straightforward documentation corrections may go directly to main; applicable
automated checks still need to pass. This authority does not instruct an automated
agent to commit or publish without a request from the user.

These notes add no prescribed folder layout or AI-specific workflow.

## Shared services and consumer compatibility

- Check the API catalog, integration guide and architecture before extending a
  service. Reuse suitable Script Extender events and APIShared capabilities or
  registration brokers. A shared interception site has one hook owner; consumers
  must not install competing hooks or compile APIShared implementation sources.
- Keep reusable services and cross-mod hook coordination here. Consumer settings,
  activation rules, localized text and command engines stay with consumers. The
  public assembly must work with arbitrary mod GUIDs and without the maintainer's
  mod pack, workspace source links or neighboring repositories.
- Prefer additive extensions and retain process-lifetime ownership, independent
  failures and the documented original behavior. Specify prerequisites, ordering,
  caller thread, lifetime, payloads, private State, cancellation and exception
  handling; update affected public documentation, examples and behavior tests.
- Before every code change, assess binary, source and behavioral compatibility.
  Breaking changes include removing or changing public types, signatures,
  namespaces, identities or interface requirements, as well as changing defaults,
  payload/ID meanings, ordering, thread, timing, availability, lifetime,
  cancellation, exceptions, side effects or persisted data contracts.
- For a planned breaking change or material compatibility risk, consultation with
  the other maintainer before implementation is recommended, not mandatory. Either
  maintainer may decide independently. Document the affected API, old and new
  behavior, consumer impact and a compatible alternative where possible; provide
  appropriate tests and migration guidance where needed. A version bump or passing
  tests of one consumer set alone is not evidence of compatibility. Unknown external
  consumers are not evidence of compatibility either. Do not hide breaking behavior
  in a documentation change or introduce an unsolicited parallel fallback. These
  assessment and documentation duties apply independently of Steam publication.

## Hot paths and event design

- Optimize code invoked continuously by permanent hooks and frequent publishers.
  Check actual demand, logical activation and recursion before doing optional work.
  Dormant services must follow the complete original path with minimal overhead.
- Keep early-return paths free of closures, LINQ results, formatted strings,
  context objects, reflection queries, file/asset access and game lookups. Inspect
  generated IL: a compiler can hoist a closure allocation ahead of a C# guard.
- Use existing durable events/publishers instead of Update-style polling. Retain
  process-lifetime callbacks and change logical state rather than executable hooks.
- Publish ordered immutable registration snapshots; execute callbacks outside
  registry locks. Preserve Pre/Post pairing, owner-private State, failure isolation,
  recursion rules and documented thread semantics through every optimization.
- Cache demand only when every activation transition is owned and accounted for.
  Dynamic predicates without change notification must remain dynamically evaluated.
  Serialize rare writes; keep frequent reads cheap without introducing false zero.
- Preload resources and prepare reusable metadata outside hot paths. Cache live
  game objects only under an audited lifetime contract. Bound diagnostics and avoid
  repeated atomic writes after a one-time marker has already been recorded.
- Measure allocations and execution time separately after warm-up, using equivalent
  scenarios and compiled production paths. Add IL/allocation regressions for fast
  returns. Report runtime and fixture limitations; do not infer an FPS gain.
- Prefer clear internal changes with measured benefit over blanket inlining,
  removing synchronization or pooling. Public event args may be retained by callers;
  do not recycle them or weaken immutable results and callback rollback contracts.
- Apply these rules to new or changed paths. They do not authorize broad unrelated
  service rewrites or public contract changes; existing compatibility rules apply.

## Markdown documentation responsibilities

- Give each document a clear purpose and audience. README is a short installation
  and navigation entry; ARCHITECTURE explains shared structure, ownership,
  initialization and lifetime. Neither is a feature manual or benchmark report.
- Keep the API catalog useful for discovery and links to contracts/source. The
  integration guide explains common consumer workflows. CONTRIBUTING covers
  contribution setup and checks; AGENTS contains working rules, not product manuals.
- Use focused topic guides for substantial contracts, workflows or implementation
  explanations, as with [Audio](docs/AUDIO.md) and [Workshop](docs/WORKSHOP.md).
  Small features can remain in existing guides; do not create a file per API.
- Give each detailed explanation one authoritative documentation home. Keep brief
  summaries elsewhere and link to it instead of copying long sections. Public
  contracts still belong at their code declarations; keep prose consistent with them.
- Separate usage, public limitations and internal implementation with named sections.
  Put feature measurements beside that implementation, with fixture and runtime
  limits. Keep concrete native update contracts in UpdateToNewDLL.md and general
  compatibility policy in docs/NATIVE_COMPATIBILITY.md.
- When moving content, preserve requirements, evidence and limitations; update
  relative links and anchors, retain useful old entry headings, and remove obsolete
  duplicates. Do not silently turn a documentation move into a behavior change.
- Review related documents and examples after a change. Run the documentation-link
  check in fresh Windows PowerShell and verify CRLF for affected text files. Keep
  repository links portable and do not depend on the maintainer's workspace paths.

## Useful tests and stable contracts

- Add a test only for a concrete relevant regression or binding contract that is
  not adequately covered already. Identify its inputs, observable result and
  plausible failure; weigh runtime, complexity and maintenance against that risk.
  Extend a suitable existing test before adding another suite or checker.
- Test behavior and required safety invariants. Do not prescribe internal folder
  layouts, private names, source wording, feature counts or current version
  literals unless their exact form is necessary to the contract being checked.
  Document that necessity and the limits of the check beside the test.
- Equivalent refactoring must not fail tests without a contractual reason.
  Source heuristics are limited guardrails, not proof of runtime behavior. Preserve
  meaningful backend, error-path and process-context coverage when removing duplicates.
