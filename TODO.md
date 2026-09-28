# Ceras implementation roadmap

Remaining work, reconciled with the 2026-09-27 review. Completed implementation history
belongs in git, tests, and the format/API documentation, not this checklist.

**Priority 0 (the save/load correctness gates) is closed.** Milestone 1 (authoritative
layouts and finite contexts) is the next correctness milestone; Milestone 3
(optimization) is already proceeding on the now-merged save/load fast path. Networking
shares its generator foundations and receives its dedicated execution path later. The
settled contracts below are targets; they are not claims that the code already meets them.

## Settled goals and contracts

- **Platform:** .NET 10 / C# 14, with Godot editor and exported-game integration tested.
  No first-class Unity integration or Unity-specific patching is planned; the legacy
  addon is not a supported target. No ASP.NET dependency is needed or planned.
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
  strings or arbitrary graphs. Send strings directly without interning. Prefer sharing
  the generated fast-path/layout foundation with robust saves where it preserves both
  paths' correctness and performance; do not mandate one serializer wrap the other.
- **Game-data IDs:** context-specific formatters write recipe/item/etc. IDs and resolve them
  to existing local objects on receipt. The same type may serialize differently in saves.

## Milestone 1 — authoritative layouts and finite contexts

Build on existing manifests, canonical identities, fixed-width primitives, baked
schemas, header codec, and separate schema/payload string tables. Never grow a
context after seal or parse the schema on a matching-layout load.

- Derive writer, reader, schema encoding, fingerprint and migration decoder from one
  explicit layout model. Record reference representation and payload grammar in the
  file schema during Milestone 1, before the old-data graph, so an old file can be
  decoded without consulting a missing historical CLR type. This deliberately changes
  the experimental format and requires reviewed fixture/fingerprint updates; first
  extract byte-preserving shared rules and pin existing payload bytes. The v4 temporary
  guard refuses user custom formatters in robust contexts (raw contexts still permit
  them), but `SchemaRegistry.Register` can still deliberately register a lying schema,
  and reinterpret/reflection/resolver formatters may write bytes that no schema
  describes. Verify writer selection against the authoritative layout; the
  manifest-coupled guard is not an unforgeable proof of schema truth. Record array
  rank if robust polymorphic multidimensional arrays are ever supported; v4 rejects
  them before Save because the current Type value only records an element type.
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
- Extend the generator's `CERAS019` duplicate-name check to aliases, per context,
  and to types it currently skips: the runtime already refuses a name two manifest
  entries claim (`SchemaRegistry.ClaimNames`), but that is a save/load-time error
  where the compiler could have said it. Run the check over `[PreviousTypeName]`
  too, over one dictionary per context rather than the union of all contexts, and
  stop treating `SpecialType` types as exempt (a `[CerasSerializable("X")]` on two
  differently-kinded types currently produces no diagnostic). Include
  `contextMinimalTypes` and declared structural roots.
- Expose an immutable, mode-specific context compatibility descriptor for diagnostics
  and network handshakes. It must not report a layout fingerprint before the manifest is
  sealed and baked: `ComputeFingerprint` is provisional until then and excludes pending
  `DeclareRoot` entries, so report `IsFinal`/`LayoutFingerprint` together or not at all.
  The runtime never learns the generated context name, so either add an explicit
  `SetContextName` or expose an immutable copy of the manifest's stable names.
- `RobustTypeFormatter` resolves a stored `FullNameMarker` type value through a
  *private* `SimpleTypeBinder` rather than the context's `Config.TypeBinder`, so a
  context that abbreviates or remaps type names cannot read back its own name
  spelling. Pass `Config.TypeBinder` in from `CerasRobustSerializer` (it is built
  there already); the other `Type`-value doors honour the configured binder, so the
  disagreement is confined to this one path.
- Exercise `samples/GodotIntegration` and `/games/godot-factory-test` in editor play,
  assembly reload and exported execution with real private/generic save graphs.

## Milestone 2 — old-data graph and migration

Replace the streaming interpreter's discard-and-assign limitation; retain the existing
reader regressions if it supersedes interim reader fixes. Historical writer fixtures are
already present but need expansion for the cases below. Matching-layout reads must stay
generated and must not parse the schema, and reader tests must not reference the
historical writer assembly: pin migration coverage with real historical-writer fixtures on
both readers, and never infer cross-version compatibility from a fingerprint flip or a
same-version roundtrip.

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

- Robust Save writes its payload straight into the output buffer; the intermediate
  scratch writer and its copy are gone. Still open: offer caller-owned buffers so the
  final trim copy can be avoided too, and measure integrity and string-table costs
  separately.
- `List<T>`/`T[]` of fixed-width primitives are now read in place instead of
  enumerated, and bulk-written in blocks rather than element by element; `decimal`
  and arbitrary structs are deliberately excluded. Still open: typed
  common-collection formatters that avoid reflected construction, for collections
  beyond `List<T>`; test segmented input, reuse, limits and truncation.
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
- Update README/tutorials/samples/format docs with verified contracts and remove
  obsolete runtime-generation, universal-type, Unity/AOT and diagnostic claims.
- Review concrete formatter selection and unsupported-type diagnostics against
  generated availability, context-only roots and attributed collections.
- Replace colliding sanitized generated identifiers with deterministic collision-
  resistant names; improve incremental discovery without losing cross-assembly types.
- Preserve explicit inaccessible-signature diagnostics and generic accessor semantics;
  revisit ref-return accessors only for a demonstrated need.
- Clean build warnings and smoke-test realistic trimming/NativeAOT; optionally omit
  unsupported legacy Unity addon from the default build.

## Deferred

Borrowed ref-struct packet views; opaque framing for custom payloads; lazy old-data
graphs; broad speculative specializations, compression
or interning redesign, and JIT tuning without representative evidence. Support for
`ReadOnlyCollection<T>`/`ReadOnlyDictionary<K,V>` (including subclasses) and
`List<T>`/`Dictionary<K,V>` subclasses is deferred in both raw and robust modes;
the base `List<T>`/`Dictionary<K,V>` remain supported. The refused families need a
reviewed construction, cycle and comparer contract before support is restored.
