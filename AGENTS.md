# APIShared contribution notes

This is a standalone community repository. See CONTRIBUTING.md for setup and tests,
ARCHITECTURE.md for responsibilities, and docs/API_CATALOG.md for existing features.

- Explain public contracts directly in code, including relevant prerequisites,
  ownership, IDs, thread, lifetime, ordering, replay, errors and cancellation.
- Explain complex implementation entries and persistent call paths; avoid comments
  that merely repeat symbol names. Keep mod-specific algorithms in consumer mods.
- Maintain the catalog, integration guide and affected examples with API changes.
  Prefer links and inline contracts over a separate document for every feature.
- Keep README.md a short entry point. After completing a feature, ask the maintainer
  whether it should also be mentioned there before making an unsolicited addition.
- Preserve user changes. Do not commit or publish unless requested. Changes to runtime
  behavior need appropriate tests; documentation changes must not conceal behavior fixes.

These notes add no prescribed folder layout or AI-specific workflow.