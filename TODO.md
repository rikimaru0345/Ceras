# Ceras implementation roadmap

Remaining work, reconciled with the 2026-09-27 review. Completed implementation history
belongs in git, tests, and the format/API documentation, not this checklist.

**Start with Priority 0 below. Do not jump to optimization or later milestones while
these correctness gates are open.** Robust save/load is the first delivery priority.
Networking shares its generator foundations and receives its dedicated execution path later.
The settled contracts below are targets; they are not claims that the code already meets them.

## Settled goals and contracts

- **Platform:** .NET 10 / C# 14, with Godot editor and exported-game integration tested.
  Unity-specific support is deferred. No ASP.NET dependency is needed or planned.
- **Generated code:** ordinary, inspectable, debuggable C# that the compiler and JIT can
  optimize. No expression compilation or runtime IL generation. Reflection is acceptable
  where needed in the upgrade interpreter. NativeAOT compatibility is desirable, but
  native deployment is not the motivation or a requirement to support Apple platforms.
- **Byte representation:** little-endian systems only; big-endian support and byte-order
  conversion are out of scope. Fixed-width primitives by default. Compact encodings
  (e.g. zigzag varints) require explicit overrides reflected in the layout/fingerprint.
  Raw primitive encoding does not mean persisting arbitrary CLR struct padding/layout.
- **Discovery:** users mark roots; recursively discover reachable types and polymorphic
  alternatives. Allow explicit additional known types, including closed generics hidden
  behind object/interface members. No annotation on every type. Unknown runtime types
  are errors; incomplete declarations are user error unless discovery itself is faulty.
- **Contexts:** each has a finite generated manifest, deterministic type IDs, and one
  layout fingerprint. Layout-affecting choices are compile-time context choices.
- **Robust:** documented, self-describing persistent format. Matching layouts use generated
  readers without parsing the schema. Mismatches use an identity-preserving old-data graph
  and structural migration without historical CLR classes. Shared references and mutable
  cycles are supported. Upgrade time/memory can be expensive; never compromise the happy
  path to optimize the upgrade path.
- **Versioning:** keep Ceras format version, layout fingerprint, and producer-build ordering
  separate. Embed a build timestamp once per owning build/context; reject newer-build saves
  even when layouts match. Clock correctness is outside scope. The game stores its own
  semantic version and handles post-load fixes; no Ceras application-migration scheduler.
- **Compatibility promise:** starts with the next documented robust format. Current
  experimental saves need not remain readable. Retain decoders for supported releases
  after that starting point. Older binaries must reject newer saves clearly.
- **Custom formatters:** permitted; authors own their byte compatibility and migration.
  Saves containing arbitrary custom payloads have no automatic upgrade guarantee.
  Opaque framing/isolation is deferred.
- **Networking:** transient format, compatibility exchanged at connection setup. No
  per-message schema, layout hashes, Ceras checksums, or compatibility negotiation during
  gameplay. Ordinary buffer bounds/length checks remain. No reference tracking or runtime
  cycle detection: cyclic input is misuse and may stack overflow. Shared objects serialize
  by value unless replaced by an ID formatter. Complex graphs can use a separate context
  and be carried as a byte payload.
- **Network ownership:** ordinary structs and reusable objects, caller-owned/pooled buffers,
  reusable collection storage. No mandatory ref structs; borrowed views are deferred.
  Allocation-free steady state is a target for suitable messages, not a promise for owning
  strings or arbitrary graphs. Send strings directly without interning.
- **Game-data IDs:** context-specific formatters write recipe/item/etc. IDs and resolve them
  to existing local objects on receipt. The same type may serialize differently in saves.

## Priority 0 — correctness gates before release or optimization

Robust save/load is first. Matching-layout reads must remain generated and must not parse
schemas. Add regressions on both paths, including real historical-writer fixtures when
claiming migration coverage. The fixtures in `fixtures/Ceras.HistoricalWriter` are checked
in; reader tests must not reference the historical writer assembly. Do not infer
cross-version compatibility from a fingerprint flip or a same-version roundtrip.

- **Serialize callbacks in robust generated format.** `[OnBeforeSerialize]` and
  `[OnAfterSerialize]` are currently ignored there, although raw mode honors them and
  the public attribute promises invocation. Emit them around member writes with the
  existing callback/access conventions; accept parameterless or `CerasSerializer`
  signatures, but diagnose `MigrationContext` on Save (there is no migration).
  Cover order, private/generic access, serializer argument, nested operations, and
  generated versus raw behavior. Never silently ignore an unsupported signature.
- **Banned members must not disappear silently.** A serializable type with an
  `Action`/delegate or other banned member currently saves it as an implicit omission;
  even configuring a custom formatter does not rescue that member. **Decision:** an
  included banned member is a hard, actionable generator/runtime error. Require an
  explicit `[Exclude]` or a supported replacement representation; pin generated and
  reflection paths and avoid turning an existing save into a silent null on load.
  The LiveTesting audit also found banned *runtime values* through `object` and
  `Type` slots silently become null (`ReferenceFormatter`/`TypeFormatter`); apply the
  same fail-loud policy there. A banned generic `Type` argument currently reaches
  `MakeGenericType` as null and leaks `ArgumentNullException`; diagnose at the
  original rejected value instead, with a typed serializer error.
- **Wrap-only user collection construction.** The migration reader can construct a
  `List<T>`/`Dictionary<K,V>` subclass through a wrapping constructor, but the generated
  matching-layout collection formatter still calls `Activator.CreateInstance` without
  arguments. Either support the admitted shape on both paths or reject it at bake time
  with a useful diagnostic; pin the same type through both reader paths. A separate
  `ReadOnlyCollection<T>` subclass is also admitted structurally but the dedicated
  formatter constructs the base wrapper, and `ReferenceFormatter` fails with a bare
  `ArgumentNullException("method")` on Save; reject or support this variant too.
- **Polymorphic built-in generic values disappear on migration.** A closed built-in
  collection held in an `object` slot is written as a `Type` value (definition id +
  generic arguments), but `ReadPolymorphicValue` treats the first tag as a schema
  type id and can return null without consuming the collection. Reproduced for
  `List<int>` and `Queue<int>`; test with a trailing field and matching-layout
  control, then align the interpreter with the writer's real type-value grammar.
- **Enum stable-name migration.** Confirmed silent wrong value on rename/renumber:
  generated and declared enum schemas record the C# name rather than the wire
  `[CerasStableName]`, while migration matches that attribute. Emit the stable wire
  name in both schemas and also recognize the current C# name when reading old files.
  An older file containing a *previous* C# name still needs an explicit
  `[PreviousName]` alias; test both generations of files.
- **System-namespace discovery.** Confirmed: user-defined `System.*` types are
  wrongly skipped by the generator; framework types such as Uri, Version and
  BigInteger are admitted at compile time but rejected at the first robust Save
  with suggestions that cannot work. Distinguish source-defined types and diagnose
  unsupported BCL members early; do not broadly treat arbitrary BCL types as
  structural or promise support just because raw serialization handles them.
- **Bool width and malformed collection schema.** The alleged removed `List<bool>`
  corruption is a false positive for files written by this version: its normal skip
  path advances one byte. A hand-registered collection schema lacking element type
  arguments can still fail or desync in skip/read paths. Validate or reject that
  malformed shape consistently; setting bool's fixed width to one is safe but not
  a substitute for schema validation.
- **Custom formatter/schema mismatch on migration.** Investigate whether overriding a
  generated formatter can leave a structural schema in the file while writing opaque
  bytes. **Reproduced with separate historical writer/reader builds and a valid CRC:**
  removing such a field can silently corrupt following fields, and the fingerprint
  does not include formatter selection. **Decision:** temporarily reject user custom
  formatters in robust contexts with an actionable error; keep them in raw contexts.
  Do not keep accepting old experimental files whose fingerprint falsely matches:
  advance the experimental format version and deliberately update golden fixtures if
  needed. Restore robust custom formatters after Milestone 1 adds truthful opaque
  schema/formatter-selection metadata; migration must still refuse unframed opaque
  values it cannot consume. Opaque framing remains deferred.
- **Detect trailing migration payload bytes.** After interpreting a root, assert that
  the declared data section was consumed exactly. A mismatched schema can otherwise
  leave bytes silently unread; reject it with a typed, actionable error. This is a
  detector, not a substitute for truthful custom-payload metadata: a desync that
  lands exactly at the end can still corrupt values.
- **Dead branches (former 0.8.13).** Remove the always-true include guard in
  `MemberExplorer`, the unreachable reflective `CollectionFormatter` branch, and
  stale `WellKnownTypes` width comments where still present; validate behavior before
  deleting. Do not conflate this with a change to default member selection.
- **Warnings.** Nullable is enabled project-wide. The five assignment/argument
  warning families (`CS8601`, `CS8604`, `CS8618`, `CS8600`, `CS8603`) are now clear
  under `src/Ceras/Formatters/`, but remain elsewhere. Make per-site
  contract/check/default decisions; no broad suppression. Maintain zero dereference
  warnings. The generator and all project props now pin C# 14; the generated-output
  syntax/binding test enforces that consumer version.
- **Roadmap and public claim drift.** Remove completed narratives from this file and
  stale `TODO Phase` comments, outdated expected-failure tests and package claims
  (`zero-allocations`, universal types, Unity/IL2CPP). Keep `Format/LAYOUT.md`, README,
  and experimental compatibility wording aligned with verified bytes and behavior.

## Milestone 1 — authoritative layouts and finite contexts

Build on existing manifests, canonical identities, fixed-width primitives, baked
schemas, header codec, and separate schema/payload string tables. Never grow a
context after seal or parse the schema on a matching-layout load.

- Derive writer, reader, schema encoding, fingerprint and migration decoder from one
  explicit layout model. Record reference representation and payload grammar in the
  file schema during Milestone 1, before the old-data graph, so an old file can be
  decoded without consulting a missing historical CLR type. This deliberately changes
  the experimental format and requires reviewed fixture/fingerprint updates; first
  extract byte-preserving shared rules and pin existing payload bytes.
- Split generator context registration/discovery from source emission as the layout
  model settles. `CerasFormatterGenerator.cs` and `RobustCodeEmitter.cs` still mirror
  some type/encoding decisions; converge on one canonical helper rather than
  refactoring them together with the already-separated runtime decoder.
- Add explicit compact-encoding overrides reflected in layout/fingerprint. Complete
  byte-level `Format/LAYOUT.md` coverage of counts, IDs, markers, tables and shapes,
  backed by differential bytes tests.
- Make formatter resolution context-specific, including primitive/member fast paths
  and concrete formatter injection. Test distinct save/network encodings and ID
  formatters for the same CLR type. Restore robust custom formatters behind explicit
  opaque schema and formatter-selection fingerprint metadata after the P0 temporary
  rejection. Custom payloads have no automatic upgrade promise; an unframed value
  that cannot be consumed during migration must fail loudly.
- Reject duplicate stable names and ambiguous aliases across the entire finite
  manifest, including structural and external declarations.
- Replace bounded assembly scans in `SchemaRegistry.TryResolveType` and definition
  resolution with manifest identities or explicit binder resolution.
- Expose an immutable, mode-specific context compatibility descriptor for diagnostics
  and network handshakes.
- Exercise `samples/GodotIntegration` and `/games/godot-factory-test` in editor play,
  assembly reload and exported execution with real private/generic save graphs.

## Milestone 2 — old-data graph and migration

Replace the streaming interpreter's discard-and-assign limitation; retain Priority 0
regressions if it supersedes interim reader fixes. Historical writer fixtures are
already present but need expansion for the cases below.

- Decode bytes into an identity-preserving graph of old type identities, stable fields,
  scalar values, collections and references; expose it to application migration code,
  then materialize current objects without historical CLR types.
- Preserve references whose first occurrence is in a removed field and later use is
  retained; test wrappers, aliases, sharing, cycles and unknown polymorphic types.
  Pin two shared collection references followed by a distinct object so a shifted
  identity-cache slot cannot masquerade as a passing roundtrip; cover a removed
  `System.Type` value and the following field on the historical decoder.
- Support reparenting and removed/inlined types via explicit transformations over old
  data; leave semantic game fixes to the game, with no migration scheduler.
- Apply defined lossless conversions and aliases automatically. Reject ambiguous or
  lossy transformations unless explicitly permitted, and preserve or explicitly
  authorize loss of unknown polymorphic values.
- Evolve nested arrays/lists/dictionaries/sets/queues/stacks and their elements;
  preserve order/duplicates or require opt-in loss and detect dictionary key collisions.
- Align construction, reuse and callbacks with the generated reader; extend distinct-
  build fixtures for defaults, rename, reparenting, collection changes, sharing/cycles
  and newer-build rejection.
  Assert which reader path executes.
- Declare the compatibility starting format only after fixtures pass. Add format-
  version dispatch and retain released decoders; experimental saves need not load.

## Milestone 3 — measured happy-path optimization

Only after correctness and runnable baselines; no changed bytes or reuse semantics.

- Eliminate robust Save's intermediate payload/copies where possible and offer
  caller-owned buffers; measure integrity and string-table costs separately.
- Add typed common-collection formatters, avoiding reflected construction and
  interface enumeration. Bulk-copy `byte[]` and suitable fixed-width primitive
  collections only where differential tests prove CLR layout equals wire encoding
  (not `decimal` or arbitrary structs); test segmented input, reuse, limits, truncation.
- Move `SerializerBinary.ReadDecimal`'s unnecessary fast-path `stackalloc` to the
  segmented branch, subject to measurement; check `ReinterpretFormatter` similarly.
- Batch fixed-size generated writes and reduce dispatch/locals; cache context-local
  formatter instances and use typed factories only where measurements warrant it.
- Generate closed specializations only for measured hot instantiations and
  cross-read them with generic writers/readers and the migration decoder.

## Milestone 4 — dedicated networking execution

`CerasFastSerializer` still shares reference machinery and is not this mode yet.

- Generate a finite-manifest network path without reference tables/proxies, identity
  preservation, cycle detection, per-message schema/hash/checksum or negotiation.
  Shared objects serialize by value except through explicit ID formatters.
- Provide connection-time compatibility information for the transport.
- Support direct strings, struct messages, reusable class/collection reads and
  caller-owned/pooled writes without mandatory per-message arrays.
- Exercise context-specific recipe/item ID lookup and a separately serialized graph
  byte-payload escape hatch without charging ordinary messages for graph machinery.
- Benchmark sustained traffic and steady-state allocations for structs, reused
  classes, ID references, arrays and strings; document cycles as misuse.

## Milestone 5 — cleanup and documentation

Perform local cleanup alongside related fixes; no runtime IL/expression generation.

- Retire obsolete reflection `Schema`/`SchemaMember`/`PrimarySchema`, fallbacks,
  factories, ordering helpers and dead robust `FullNameMarker` write assumptions once
  consumers are replaced. Keep any required released-format decoder. Audit the
  internal `CerasRobustSerializer(CerasRobustConfig)` constructor: unlike the public
  path it skips robust formatter initialization, so internal callers can write
  length-prefixed strings against a schema expecting table indices.
- Reconcile `CLEANUP_TODO.md` against real APIs, move justified work here and delete
  the stale list. Do not internalize `CerasSerializer` while generated code needs it;
  review raw method exposure on robust serializers and remaining config sealing.
- Retire the broken `samples/LiveTesting` sandbox, its comparison dependencies,
  merge-blitting microbenchmark, unadopted ref-proxy experiment, and friend-assembly
  declaration. The working benchmark has been relocated to the standalone,
  self-checking `samples/Ceras.Benchmarks` project with its own generated context;
  seven previously missing formatter-injection, comparer, direct-formatter and
  readonly-reuse checks are now proper tests. The README no longer claims legacy
  comparison numbers. Banned runtime values in `object`/`Type` remain a separate
  fail-loud P0 above; do not mistake their removal from the sample for a fix.
- Update README/tutorials/samples/format docs with verified contracts and remove
  obsolete runtime-generation, universal-type, Unity/AOT and diagnostic claims.
- Review concrete formatter selection and unsupported-type diagnostics against
  generated availability, context-only roots and attributed collections.
- Replace colliding sanitized generated identifiers with deterministic collision-
  resistant names; improve incremental discovery without losing cross-assembly types.
- Preserve explicit inaccessible-signature diagnostics and generic accessor semantics;
  revisit ref-return accessors only for a demonstrated need.
- Clean build warnings and smoke-test realistic trimming/NativeAOT; optionally omit
  deferred Unity addon from the default build.

## Deferred

Unity-specific integration; borrowed ref-struct packet views; opaque framing for
custom payloads; lazy old-data graphs; broad speculative specializations, compression
or interning redesign, and JIT tuning without representative evidence.
