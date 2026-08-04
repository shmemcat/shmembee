# Cross-repository compatibility fixtures

`contract-fixtures/` is a byte-for-byte copy of the complete versioned Phase 2
fixture set maintained by Shmemplaylist. Tests resolve these files from their
own output directory; they never read a sibling repository or depend on a
developer machine's checkout layout.

The shared parser-success, malformed-encoding, normalization, semantic
checksum, and deterministic-writer vectors exercise existing Shmembee
production APIs directly. Android-only canonical mutation-profile and
playlist-operation manifests are checked as fixture schema/data invariants
because Shmembee has no equivalent production API; the tests deliberately do
not invent one.

Intentional platform divergences:

- Android rejects a production record whose normalized path is empty as a
  safety refinement. The current desktop v1 parser can represent that record
  with an empty `NormalizedPhonePath`, so the desktop test documents the
  behavior instead of expecting Android's typed rejection.
- Kotlin models invalid normalization with nullable/rejection results. The C#
  `TrackPathNormalizer` reports null or blank input with `ArgumentException`.
- Android's canonical writable gate has no desktop equivalent. Shmembee can
  prove deterministic writer bytes but does not claim mutation eligibility
  from the GoneMAD canonical-profile vectors.

When updating the fixture contract, replace the entire `contract-fixtures/`
tree from its source and preserve exact bytes, including BOMs, line endings,
malformed encodings, and zero-byte files.
