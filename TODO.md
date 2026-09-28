# Ceras implementation roadmap

Remaining work, reconciled with the 2026-09-27 review. Completed implementation history
belongs in git, tests, and the format/API documentation, not this checklist.

**Priority 0's original gates are closed, not a claim of full save compatibility.**
Finish the v5 opaque-formatter change and verify it against the game, then make the
layout model authoritative and connect the old-data graph to the migration reader.
Do not prioritize NativeAOT, speculative tuning, or networking ahead of game saves.
The settled contracts below are targets, not claims that the code already meets them.

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
- **Custom formatters:** permitted only when the robust schema describes their framed
  opaque bytes and the fingerprint records formatter selection; authors own byte
  compatibility and migration. There is no automatic upgrade guarantee for arbitrary
  custom payloads. This is being implemented as experimental v5, not yet merged.
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
  **This is the highest-value item on the whole roadmap, and the reason is a real
  consumer, not a hypothetical.** The game's earlier `wave-robustsave` branch had four
  custom formatters in a robust context and could not save
  *or* load at all: `RobustCustomFormatterPolicy` refuses the type at construction, and
  `Save()` and `Load()` both build the serializer, so the save feature did not work in
  that build. Its tracker records the policy as a one-day-old commit against a path that
  had worked for months, and confirms nothing has shipped and no savegame exists to
  load — so the compatibility promise has no real cost to keep here. Two of the four
  were load-bearing and removing them was a *semantic* change, not a free win:
  - a recipe reference became a fresh copy rather than a registry-identical reference,
    so `ReferenceEquals` breaks and the file carries the whole recipe table per factory.
    This is precisely the "context-specific recipe/item ID lookup" this project settled
    on, and it is **unreachable in a robust context today**;
  - a relative-time value (`value - now`, so "expires in 5 s" survives a load) became an
    absolute double, so an old save's `RevealedUntil = 40.5` is stale the instant it
    loads. Note the general lesson, which outlives this consumer: **rebasing a value
    against load time is not expressible as an `IFormatter<T>` in a robust context, and
    never was** — it needs a post-load pass over the loaded graph, not a custom writer.
  The game works around this today by storing recipe IDs and game time as generated
  state instead of using custom formatters. **Current
  game checkout is `/games/godot-factory-test` on `main`, not the removed
  `/games/wt-robustsave` worktree.** It now stores recipe IDs and restores registry
  references in `Game.Rebuild`, and serializes its game clock as generated state; its
  logic tests assert recipe identity, paused time, and loaded-world simulation. It
  checks in two older experimental robust saves (`populated-world-v1.bin` and
  `-v2.bin`) as migration fixtures. These are not shipped/player data and the v5
  format break is permitted, but the game's fixture tests must be updated explicitly
  after v5 lands — never overwrite these files as a side effect of a scratch test.
- Extend the generator's `CERAS019` duplicate-name check to aliases, per context,
  and to types it currently skips: the runtime already refuses a name two manifest
  entries claim (`SchemaRegistry.ClaimNames`), but that is a save/load-time error
  where the compiler could have said it. Run the check over `[PreviousTypeName]`
  too, over one dictionary per context rather than the union of all contexts, and
  stop treating `SpecialType` types as exempt (a `[CerasSerializable("X")]` on two
  differently-kinded types currently produces no diagnostic). Include
  `contextMinimalTypes` and declared structural roots.
- Re-run `samples/GodotIntegration` under headless Godot after the v5 merge (it has
  a self-checking success sentinel). Exercise the active `/games/godot-factory-test`
  logic save suite after every format change, including recipe registry identity,
  paused-time semantics and older fixtures. Editor assembly reload and exported-game
  execution are **not automated**; neither project has an export preset. Build an
  explicit safe harness before claiming those paths verified. The immutable
  compatibility descriptor and configured `TypeBinder` path already exist; verify
  them in integration rather than reimplementing them. **Pre-v5 baseline:** game logic
  suite 536/536, and the Ceras sample on Godot 4.7.2 headless imports and prints
  `CERAS_GODOT_INTEGRATION_OK` (exit 0). Re-run both after the format change.

## Milestone 2 — old-data graph and migration

Replace the streaming interpreter's discard-and-assign limitation; retain the existing
reader regressions if it supersedes interim reader fixes. Historical writer fixtures are
already present but need expansion for the cases below. Matching-layout reads must stay
generated and must not parse the schema, and reader tests must not reference the
historical writer assembly: pin migration coverage with real historical-writer fixtures on
both readers, and never infer cross-version compatibility from a fingerprint flip or a
same-version roundtrip.

- Connect the implemented old-data graph node model (`OldDataGraph.cs`, plus generic
  `IdentityCache<T>`) to a file-faithful decoder, expose the populated graph to application
  migration code, then materialize current objects without historical CLR types. The
  model exists; no runtime reader constructs it yet. Pin matching-layout allocations
  and ensure this work cannot put graph construction on the generated-reader path.
  **Fail-loud gate before migration becomes the default:** in today's streaming reader,
  `SkipPolymorphicValue` reserves a null object-cache slot for a first occurrence in a
  removed member; a retained field's later back-reference resolves that in-range slot
  to null, and load succeeds with the stream aligned. Distinguish an explicitly skipped
  slot from a legitimate temporarily-null slot in a half-built cycle, and refuse the
  former with a typed error until the graph can preserve/materialize it. Pin a real
  historical fixture where a removed member contains the first occurrence and a
  retained member back-references it, followed by a distinct object witness.
  **Builder shape:** a separate file-faithful decoder (no current model in scope), with
  shared payload-grammar decisions extracted from `SchemaTraversalEngine`; an arena
  index for every value and a separate writer-cache-slot → arena-index mapping so
  reserve-before-fill handles cycles and removed members. Land the shared grammar
  extraction and builder tests before redirecting `Load`'s slow path. Never copy the
  current reader's `FullNameMarker` lookup that uses a current registry id against
  the file's id table — those id spaces are independent; resolve by file identity.
  Keep the v5 opaque-frame integration behind the v5 merge, since the current graph
  model has no opaque node kind yet. Pin zero graph builds and zero schema parses on
  the fingerprint-matched generated path with exact counters, plus a representative
  allocation check; do not let migration cost leak into matching-layout loads.
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
- Generate closed specializations only for measured hot instantiations and
  cross-read them with generic writers/readers and the migration decoder.
- Typed factories remain open, and should wait for evidence: devirtualizing the
  writer was measured and is *not* the win it looks like. A by-ref struct writer
  costs 2.05 → 3.45 ns/field on the path that already exists, because a
  reference-typed callee must reload around every call; batching a generated
  formatter's fixed-width members into one `GetSpan` per run reaches the same
  place with no signature change and no contract movement, and that is what
  landed. Only measure a remaining idea end-to-end — a microbenchmark whose call
  site sees exactly one writer type gets guarded devirtualization the real
  shared call site never sees, which is how a 33% figure became a 2% one.

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
- `CLEANUP_TODO.md` is reconciled and is now the detail for three concrete items;
  do not re-litigate the list itself. `CerasSerializer` stays public while generated
  formatters call its public members. Give the internal
  `CerasRobustSerializer(CerasRobustConfig)` constructor the same robust formatter
  setup the public path has — it can otherwise write length-prefixed strings against a
  schema expecting table indices — and convert the tests that depend on the gap. Then
  close the configuration surfaces that still accept a write after `Seal()`:
  `OnResolveFormatter`, `ConfigType*`, `TypeConfig<T>.MapParameters`, and
  `UnsafeGetAllMembersIncludingCompilerGenerated`.
- Guard the public pooled `Serialize` overload against caller-owned buffers: it currently
  treats a replacement buffer as pooled, so a mistaken ownership flag can offer a caller's
  array to another renter. Keep the ownership contract explicit and pin the failure path.
- Update README/tutorials/samples/format docs with verified contracts and remove
  obsolete runtime-generation, universal-type, Unity/AOT and diagnostic claims.
- Review concrete formatter selection and unsupported-type diagnostics against
  generated availability, context-only roots and attributed collections.
- Replace colliding sanitized generated identifiers with deterministic collision-
  resistant names; improve incremental discovery without losing cross-assembly types.
  Also drop the two dead `TargetMembers` strings in the generator: a user-facing
  diagnostic message naming a config option that does not exist, and a comment.
- Preserve explicit inaccessible-signature diagnostics and generic accessor semantics;
  revisit ref-return accessors only for a demonstrated need.
- Build warnings are clean; omit the unsupported legacy Unity addon from the default build.
- **Trimming works; NativeAOT is parked by owner decision (2026-09-28).** Not a target
  until it becomes one, and the owner will say when. Keep the repeatable check either way —
  `samples/TrimAotSmoke/run-smoke.sh` builds the library's consumer app four ways (jit,
  self-contained, trimmed, AOT) and runs each, so a failure is attributable, and it is outside
  the solution so it costs the normal build nothing. Measured state:
  - `jit`, `sc` and `trim` all pass, all three sections (raw round trip, robust round trip,
    robust schema migration). Trimming is therefore *not* a live risk, and the earlier
    "trimmed/AOT fail" claim is stale.
  - On merged master, `aot` still fails. Four of the five originally ordered defects are fixed: reference
    formatter constructors are rooted on their open generic definitions; the owning assembly is
    an explicit input instead of an `Assembly.GetCallingAssembly()` stack walk, gated on
    `RuntimeFeature.IsDynamicCodeSupported`; the resolver-constructed formatter families and the
    generated formatters and their manifest members are rooted; and the library's own
    `TypeConfig<T>`/`MemberConfig<T>` are constructed directly. What remains is a *different*
    class of problem: `ArrayFormatter<int>` reports "missing native code or metadata", because
    rooting members keeps a `MakeGenericType`-closed generic loadable but NativeAOT has no
    compiled instantiation for a closed generic it only learns about at runtime. No amount of
    rooting fixes that; the compiler has to be told, and only the source generator knows which
    closed types exist. The obvious route is extending the generator's existing
    `TrimmingRoots` emission, but note the honest limit that any such fix has: the transient
    path can be asked to serialize a type no generated context knows about, which is what the
    reflection fallback is for.
  - The unfixed read-path defect stands and is the one that would matter if AOT ever became a
    target: the read path resolves saved type *names* back to `Type`s, so a trimmed app that
    kept the writer but dropped a type cannot load its own saves. Fix it by resolving against
    the generated manifest — which is the same work as Milestone 1's manifest identities.
  - **Work that finished after AOT was parked, kept on branch
    `agents/aot-closed-instantiation` (`428ced3`), deliberately not merged.** All four smoke
    modes pass on it, `aot` included. It is unmerged because AOT is not a target and because it
    changes what the generator emits for every consumer. Do not discard it: the measurements
    behind it are the expensive part, and they are recorded here.
  - What the remaining AOT blocker actually was, measured over 36 family/argument combinations
    (all reached only through `MakeGenericType`): **no amount of `[DynamicDependency]` creates
    native code.** A dependency is a *metadata* root; a marked constructor is not a request for
    code, and `[DynamicDependency]` on the *closed* instantiation is byte-for-byte as ineffective
    as on the open one. Only a *call* the compiler cannot discard produces it. Separately, the
    open-definition roots already let the compiler emit the **canonical** instantiation, which
    serves exactly the instantiations whose type arguments are all reference types — so the
    only cases needing a named instantiation are the ones with a value type in the argument
    list. Two traps worth not rediscovering: a discarded `typeof(closed);` statement is
    dead-code-eliminated and achieves nothing, and a constraint *between two type parameters*
    (`ReflectionSerializationHelper<TBase, TSpecific> where TSpecific : TBase`) is not satisfied
    by the canonical instantiation, so it needs naming even when both arguments are references.
  - The honest boundary, which that branch cannot remove and which matches the library's own
    `[RequiresUnreferencedCode]`: a type reached **purely** through the reflection fallback (no
    generated context names it) is unsupported under AOT, as is a member of a referenced-but-
    not-generated type, and a collection reachable only behind a collection-typed member whose
    concrete type appears nowhere else in the context. The generator enumerating the closed
    instantiations is the only mechanism that scales; no library-side change can answer "which
    instantiations exist" for an unbounded runtime-closed set.
  - A **pre-existing** trimming-only defect found on the way, independent of all the above and
    not fixed anywhere: `CollectionFormatter`'s capacity-constructor read path
    (`CurrentObjectFactory.cs`) drops `List<T>`/`Dictionary<,>`'s public constructor metadata, so
    an *unset* `List<int>` member fails with `MissingMethodException: No parameterless
    constructor defined for type 'List<int>'`. Note that `samples/TrimAotSmoke` passes **only
    because every collection member there is pre-initialised in a field initializer**, so the read
    path reuses rather than constructs — the smoke does not cover this and its green is misleading
    on that point.

## Deferred

Borrowed ref-struct packet views; opaque framing for custom payloads; lazy old-data
graphs; broad speculative specializations, compression
or interning redesign, and JIT tuning without representative evidence. Support for
`ReadOnlyCollection<T>`/`ReadOnlyDictionary<K,V>` (including subclasses) and
`List<T>`/`Dictionary<K,V>` subclasses is deferred in both raw and robust modes;
the base `List<T>`/`Dictionary<K,V>` remain supported. The refused families need a
reviewed construction, cycle and comparer contract before support is restored.
