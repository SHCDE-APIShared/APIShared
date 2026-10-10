# APIShared contribution notes

This is a standalone community repository. See CONTRIBUTING.md for setup and tests,
ARCHITECTURE.md for responsibilities, and docs/API_CATALOG.md for existing features.

- Explain public contracts directly in code, including relevant prerequisites,
  ownership, IDs, thread, lifetime, ordering, replay, errors and cancellation.
- Explain complex implementation entries and persistent call paths; avoid comments
  that merely repeat symbol names. Keep mod-specific algorithms in consumer mods.
- Maintain the catalog, integration guide and affected examples with API changes.
  Prefer links and inline contracts over a separate document for every feature.
- Document every new feature in the appropriate existing guide or catalog section.
  Keep README.md a short entry point with links where useful; this documentation
  maintenance is authorized by the maintainer.
- Preserve user changes. Do not commit or publish unless requested. Changes to runtime
  behavior need appropriate tests; documentation changes must not conceal behavior fixes.

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
- If a planned change breaks an existing public contract or could make a foreign
  mod using it stop working as intended, ask the maintainer explicitly BEFORE
  implementing it and wait for the answer. Explain the affected API, old and new
  behavior, consumer impact and a compatible alternative where possible. Ask also
  when a material compatibility risk cannot be resolved; independent compatible
  work may continue. General permission to edit APIShared, a version bump or
  passing tests of the maintainer's own mods does not authorize a breaking change.
  Unknown external consumers are not evidence of compatibility. Without approval,
  retain the existing contract; do not silently break it or introduce an unsolicited
  parallel fallback. This rule applies independently of Steam publication.
