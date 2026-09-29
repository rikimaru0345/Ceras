# Ceras API cleanup

Reconciled after the experimental v5, post-seal guard and internal-constructor fixes.
Only two cosmetic leftovers remain here. Anything larger belongs in `TODO.md`.

## Open work

### Two cosmetic leftovers in the robust serializer

No format impact, no behaviour change; both are in diagnostic/codec code.

- **`NormalizedTypeSchema`** (`CerasRobustSerializer.cs:841`) has two constructors repeating the
  same `?.Select(...).ToArray() ?? Array.Empty(...)` normalization, and all of it is dead:
  `SchemaTypeInfo` (`SchemaTypeInfo.cs:65-68`) and `OldTypeSchema` (`SchemaSection.cs:32-36`) both
  normalize to non-null. `DumpEntry` then re-checks the same three collections for null
  (`CerasRobustSerializer.cs:883`, `:898`, `:904`), which its own doc comment says cannot happen.
  Reached only from the two `DumpSchema` diagnostics.
- **`RobustHeader` anchoring.** `WritePlaceholder` takes `ref int offset` and can place a header
  anywhere, while `FinalizeHeader` (`:70`), `Read` (`:85`) and `Validate` (`:105`) are hard-anchored
  at 0 — `FinalizeHeader` has no offset parameter at all. `Save` always writes the header at 0, so
  nothing is broken today; the asymmetry is a trap for the next caller. The offsets themselves are
  already centralized in that one class and consumed by name everywhere else (including the
  benchmark tool, via `InternalsVisibleTo`), so the "replace scattered constants with a struct"
  form of the old item is already satisfied.

Not ours, but left behind by the old item 7: two `TargetMembers` strings survive in the source
generator — `Diagnostics.cs:10`, a user-visible message referring to a config option that does not
exist, and a comment at `TypeHelpers.cs:724`.

## Decisions, kept so they are not reversed by accident

- **The internal robust constructor now initializes the robust string and `Type`
  formatters.** Its five tests that depended on raw serialization through a robust
  serializer now use real `Save`/`Load`; six regressions pin the internal path and its
  file grammar. A caller-supplied custom formatter is not overwritten silently.
- **Post-seal configuration guards are done.** Retained resolver/member-list references
  refuse mutations, known type configs stay inspectable but not mutable, creating a
  configuration for a new type after seal refuses, and `MapParameters` seal-checks.
  `OnConfigNewType` opens only the discovered type during its callback. `DeclareRoot*`
  remains legal until the manifest is baked at the first `Save`/`Load`; the registry
  refuses declarations after that.
- **`CerasSerializer` stays public.** Generated formatters in a consumer's assembly declare a
  `global::Ceras.CerasSerializer` field and call `ConsumeCurrentObjectFresh()`,
  `SetCurrentObject(object)` and `EnqueueDeserializeCallback(...)` on it
  (`RobustCodeEmitter.cs:48`, `:143`, `:158`, `:250`; `GenericFormatterEmitter.cs:58`, `:120`,
  `:128`, `:194`). Internalizing the class is not possible without changing that generated
  contract, so old item 1's first half is void. `ICerasAdvanced` is gone.
- **The raw `Serialize` / `Deserialize` / `SerializeStatic` family is not re-exposed on the robust
  serializer.** It is `protected internal` on `CerasSerializer`; the only public re-exposure is
  `CerasFastSerializer`'s (`CerasFastSerializer.cs:28-55`), and a robust serializer's public
  surface is `Save`/`Load` only. Old item 9 is done on the public surface; item 1 above is about
  the one internal door, not the public one. (`SaveAsync`/`LoadAsync`, `CerasSerializer.cs:526`,
  `:538`, and `PeekType`, `:610`, are unused by anything, including the robust path.)
- `CerasConfig.EmbedChecksum` is gone. `CerasSerializer.ProtocolChecksum` is public
  (`CerasSerializer.cs:130`) but inert: its only mutator, `Add`, is `internal` and has no caller,
  so `Checksum` is a fixed digest of nothing. The robust format's own integrity is the header
  CRC32, which `RobustHeader` computes over the whole block.
