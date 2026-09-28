# Ceras API cleanup

Reconciled 2026-09-28 against `agents/cleanup-reconcile` (a6ee80d). Of the ten items this list
used to carry: five are verifiably done, one cannot be done (see *Decisions*), and the other four
have narrowed to the three items below. Anything larger belongs in `TODO.md`, not here.

## Open work

### 1. The internal `CerasRobustSerializer(CerasRobustConfig)` constructor skips the robust formatters

`CerasRobustSerializer.cs:190` — the public path builds its config through `MakeConfig`, which
calls `InitializeRobustFormatters` (`CerasRobustSerializer.cs:207`, `:212`) and installs
`RobustStringFormatter` / `RobustTypeFormatter` on the `string` and `System.Type` type configs.
The internal constructor does not, so `CerasSerializer`'s constructor falls back to
`StringFormatter` (varint length prefix + UTF-8, `SerializerBinary.cs:770`) and `TypeFormatter`
(a bias-tagged kind plus a binder-produced name, `TypeFormatter.cs:82`). Generated formatters ask
for `IFormatter<string>` rather than naming one (`RobustCodeEmitter.cs:483`), so they take
whatever the constructor installed.

`Save` on such a serializer therefore succeeds and writes a v4 file whose payload does not
match the grammar the format declares for those positions — a 4-byte string-table index, not a
length prefix (`Format/LAYOUT.md:161`, `:176`) — with an empty payload string table. The read then
fails with
`RobustStringFormatter`'s "Invalid string table index" (`RobustStringFormatter.cs:42`) — loud, but
a message that names neither the string nor the misconfiguration, and the bad file already exists
by then. Nothing rejects the combination earlier: `RobustCustomFormatterPolicy` passes (no user
formatter was installed, so there is nothing to reject) and the layout fingerprint matches, so
`Load` takes the generated path.

**Not reachable from the public surface today.** `CerasRobustSerializer` is `sealed`, the raw
methods are `protected internal` on `CerasSerializer`, and the only extension methods that reach
them are constrained to `CerasFastSerializer` (`Extensions.cs:14`, `:40`). The reachable callers
are the friend test assemblies, five of which *depend* on the gap — they call the raw
`Serialize`/`Deserialize` on a robust serializer and pass only because the formatters are missing:
`SchemaRegistryTests.cs:181` and `DiscoveryTests.cs:119`, `:145`, `:188`, `:222` (constructors at
`SchemaRegistryTests.cs:178`, `DiscoveryTests.cs:110`, `:132`, `:165`, `:220`; the other internal
constructor call sites are `ReviewFindingsTests.cs:496`, `ConfigSealingTests.cs:198`,
`GodotStyleRepro.cs:158`, all of which use `Save`).

Either give the internal constructor the same initialization and convert those five tests to
`Save`/`Load` or to a `CerasFastSerializer`, or delete the constructor and route every caller
through the public one.

### 2. Configuration that is still accepted after `Seal()`

Covered today: `CerasConfig.ThrowIfSealed` on `TypeBinder`, `RespectNonSerializedAttribute`,
`ObjectCacheComparer`, `AllowReflectionFallback`, `WarnOnStructWithAutoProperties`;
`CerasRawConfig.UseReinterpretFormatter`; `CerasRobustConfig.BuildTimestamp`,
`StringTableCompression`, `SetUnknownBuildTimestamp`; `SizeLimits` and `AdvancedConfig` each seal
themselves; `TypeConfig.ThrowIfSealed` on `TypeConstruction`, `CustomResolver`, `CustomFormatter`
and, through it, `MemberConfig.PersistentName`, `WriteBackOrder` and `SerializationOverride`.

Not covered, and what actually happens:

- **`CerasConfig.OnResolveFormatter`** (`CerasConfig.cs:224`) hands out the live
  `List<FormatterResolverCallback>`. `CerasSerializer` snapshots it once, after sealing
  (`CerasSerializer.cs:152`), so an entry added afterwards is silently ignored forever.
- **`CerasConfig.ConfigType` / `ConfigStaticType` / `ConfigType<T>()`** (`CerasConfig.cs:182`,
  `:187`, `:192`) create a new, unsealed `TypeConfig` after the config is sealed. Configuring a
  type the serializer has not resolved yet still takes effect — a `TypeConfig` seals on first
  schema build, not at config-seal time (`CerasSerializer.cs:752`) — while configuring one it has
  already resolved is silently ignored (pinned for `string` by `ConfigSealingTests.cs:228`).
- **`TypeConfig<T>.MapParameters`** (`TypeConfig.cs:546`) writes `ParameterMap` with no seal check.
  The only reader is `TypeConstruction.VerifyParameterMapping`, reached from
  `TypeConfig.VerifyConstructionMethod` (`TypeConfig.cs:299`), which itself is called only from
  `ReflectionFallbackFormatter.cs:43` — so a late mapping is applied or dropped depending on when
  the reflection formatter happens to be built.
- **`TypeConfig.UnsafeGetAllMembersIncludingCompilerGenerated()`** (`TypeConfig.cs:250`) returns the
  live member list typed as `IEnumerable<MemberConfig>`. A caller can cast it to `List<>` and
  mutate member configuration after the seal, which defeats every check above. It is marked
  "Don't use this", but it is public.

Deliberate, do not "fix": `CerasRobustConfig.DeclareRoot*` (`SerializerConfig.cs:51`) does not
seal-check the config, because the manifest seals later — at the first `Save`/`Load`, not at
construction — and `SchemaRegistry.DeclareRoot` throws `CerasSchemaException` then
(`SchemaRegistry.cs:246`; the reasoning is at `SerializerConfig.cs:180`). `OnConfigNewType`
(`CerasConfig.cs:203`) is exempt on purpose; the seal message says so (`TypeConfig.cs:416`).

### 3. Two cosmetic leftovers in the robust serializer

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
