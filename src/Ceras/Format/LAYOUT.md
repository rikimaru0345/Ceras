# Ceras robust format — layout model (Phase 3.1)

This documents the layout contract that generated writers/readers, the schema encoder, and the
migration interpreter must share. It is little-endian only; fixed-width primitives are the default.

## Envelope (two-region format, version 3)

```
header | schema bytes | schema string table | data section | payload string table
```

The header is a fixed 56-byte little-endian record written by one explicit codec
(`RobustHeader`):

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `CERA` |
| 4 | 1 | format version (currently 3) |
| 5 | 1 | payload string-table compression flags |
| 6 | 2 | reserved |
| 8 | 4 | game/app version (`robustVersion`) |
| 12 | 8 | producer build timestamp (UTC ticks; 0 = unknown) |
| 20 | 8 | schema string table offset (end of schema bytes) |
| 28 | 8 | data section offset |
| 36 | 8 | payload string table offset |
| 44 | 8 | deterministic layout fingerprint |
| 52 | 4 | CRC32 (computed with these 4 bytes zeroed) |

- The schema (bytes + its own string table) is **baked once** when the declared set is sealed, in
  deterministic order by type ID, and is reused by every `Save`. There is no save-time discovery
  walk, and **nothing** is ever added after the seal: a context has exactly one ID map and one layout
  fingerprint for its whole lifetime, so no save can stop matching the fast path because a second
  root shape was used.
- The shapes no manifest can describe — a one-dimensional array, a built-in closed collection
  (`List<>`/`Dictionary<>`/`HashSet<>`/`Queue<>`/`Stack<>`), a `KeyValuePair`, a tuple, an enum, and a
  collection subclass used as the *root* — are therefore declared up front, before the first
  `Save`/`Load`: `config.DeclareRoot<T>()` (or `DeclareRoots(params Type[])`) at runtime, or the
  equivalent compile-time declaration `[assembly: CerasKnownType(typeof(T), "context")]`, which the
  generator turns into the same registration. Declarations are order-independent: they are folded in
  at the first seal in canonical-name order, so two serializers that declare the same roots in
  different order produce the same IDs, the same fingerprint and the same bytes. A root that is not
  part of the manifest is rejected with a `CerasSchemaException` naming both ways to declare it.
  A shape that is only a *member* of a declared type needs no declaration: the bake derives its
  structural schema.
- Schema strings and payload strings live in **separate** regions and use independent indices, so
  the happy path can skip schema interpretation without decoding any metadata names.
- The layout fingerprint is deterministic over the declared layout and is independent of the
  producer build timestamp. Loading rejects a save whose build timestamp is newer than the current
  build (when the current build timestamp is known) before decoding the payload.

## Serialization contexts

The declared set is scoped to a **context**: a named, finite set of serializable types selected when
a serializer is configured (`config.UseGeneratedFormatters(name)`). Each context has its own
generated manifest, its own deterministic type-ID space, and its own layout fingerprint. The type
IDs and the fingerprint cover exactly the types reachable from that context's `[CerasContext]` roots;
types belonging only to another context are absent from the manifest and are rejected as undeclared
roots. Consequently the same CLR type may have different IDs and a different surrounding fingerprint
in two contexts — IDs are context-local, and a fingerprint identifies a context's contract, not an
individual type. The schema/isolation guarantees below are therefore per context, and a save written
under one context must be loaded only under that context (a mismatched context falls into the
interpreter, where a root absent from the current manifest is rejected with an undeclared-root error).
A compilation that shows serialization intent (a type-level `[CerasSerializable]`/`[CerasContext]`
or an assembly-level `[CerasSerializable<T>]`/`[CerasKnownType]`) but declares no context is rejected
at compile time with `CERAS025`; the generator still emits the (empty) registration, so that
diagnostic is the only failure. A compilation that merely references the generator without any
serialization intent is left alone.


## Type identity

Stable type names are canonical: `Namespace.Outer+Nested\`Arity` plus ordered generic arguments
(containing type arguments first), e.g. `System.Collections.Generic.List\`1<System.Int32>`.
Arrays are `T[]`/`T[,]` (rank preserved), pointers are `T*`, and generic type parameters are
`!ordinal`. Names never contain assembly versions, culture, or public key tokens, so the same
logical layout fingerprints identically across .NET runtimes.

There is exactly one runtime identity helper, `Ceras.Format.TypeIdentity.GetCanonicalName`, and it
must match the generator's `Ceras.SourceGenerator.TypeHelpers.GetRobustName` exactly; the generator
emits these canonical names for field types and enum underlying types (never `Type.FullName`).
Structural/declared runtime schemas (`SchemaRegistry.TryBuildStructuralSchema`,
`BuildDeclaredSchema`) use the same helper. `SchemaRegistry.TryResolveType` parses canonical names
back to CLR types on cold paths; the remaining assembly scan is a last resort for genuinely
external/legacy unqualified `Type.FullName` names.

## Member layout

- Field order is deterministic: `MemberType + MemberName + DeclaringType + "f"/"p"` (ordinal).
- Field identity is the stable wire name (`[CerasStableName]` overrides the CLR name; previous names
  are aliases used only by migration).
- Members are written in the deterministic order; the schema records the order explicitly.

## Primitive encodings (fixed-width default)

| Type | Bytes | Encoding |
|---|---|---|
| bool | 1 | 0/1 |
| byte/sbyte | 1 | raw |
| char/int16/uint16 | 2 | little-endian |
| int32/uint32 | 4 | little-endian |
| int64/uint64 | 8 | little-endian |
| float/double | 4/8 | IEEE-754 little-endian |
| decimal | 16 | the four `decimal.GetBits` words, in order **lo, mid, hi, flags**, each little-endian |
| DateTime | 8 | `DateTime.ToBinary()` bits (ticks plus `Kind`); decode with `FromBinary` |
| DateTimeOffset | 10 | 8-byte `Ticks` (wall-clock) + 2-byte offset in minutes |
| TimeSpan | 8 | ticks |
| Guid | 16 | raw |
| string | 4 + table | 4-byte string-table index |

**No primitive uses a compact encoding.** `int32`/`uint32` are fixed 4-byte little-endian both in
the generated writers (`WriteInt32Fixed`) and in the formatters (`Int32Formatter`), which is what
`fixedByteSize` and `WellKnownTypes.FixedSizes` claim for them; `string` is a fixed 4-byte table
index in both the payload and the schema string table. The two remaining compact encodings are not
primitives and are documented at their use sites: collection counts (`bias +1`) and
reference/polymorphism markers (bias `+5`), both written with `WriteUInt32Bias`. Recording those
in the layout fingerprint is still open (TODO, explicit layout model). Raw primitive encoding
never persists CLR struct padding.

`decimal` is the one primitive whose documented order is **not** its CLR memory order: the runtime
lays the struct out as `_flags, _hi, _lo, _mid`, so blitting a decimal would write the flags word
first and the mantissa words in a different order (and would depend on endianness). It is therefore
excluded from the reinterpret fast path (`ReflectionHelper.IsBlittableType`), which means the raw
serializer writes the **same** `GetBits` layout for a standalone `decimal`, a `decimal[]` element, a
`KeyValuePair<decimal, _>` entry and any struct that merely contains one. The raw (transient) path
has exactly one decimal encoding, and it is the row above.

## Collections and arrays

Built-in collections (`List<>`, `Dictionary<,>`, `HashSet<>`, `Queue<>`, `Stack<>`, arrays) are
encoded **structurally**: a well-known definition ID plus ordered argument type IDs, not by
reflecting CLR internals. Counts use bias `+1`; `-1` marks null. This keeps implementation details
such as `Dictionary\`2+Entry` out of the schema (`WellKnownTypes.IsWellKnownCollectionDefinition`).

Any other concrete collection shape — a subclass, or a shape with no public parameterless constructor
and no `Add` such as `ReadOnlyCollection<T>` / `ReadOnlyDictionary<K,V>` — is described as the
built-in payload it is written as (`List<>` / `Dictionary<,>` plus the element/key/value identities)
and is written as exactly those bytes. On the migration path the interpreter rebuilds such a shape by
constructing the built-in collection and wrapping it (`ReadOnlyCollection<T>(IList<T>)`,
`ReadOnlyDictionary<K,V>(IDictionary<K,V>)`); a shape it can neither construct nor wrap is rejected
with an actionable `CerasSchemaException` instead of a raw reflection exception.

**The robust format describes only rank-1 arrays.** `MultiDimensionalArrayFormatter` (rank +
per-dimension lengths + flattened elements) exists in the raw serializer, but the structural schema
and the interpreter describe a single element type, so a multidimensional array is rejected at bake
time (`SchemaRegistry.TryBuildStructuralSchema`), by the generator for a member (`CERAS027`), and by
the interpreter when reading. Use a jagged array or flatten it.

**A `Queue<>`/`Stack<>` *subclass* is not describable at all.** `QueueFormatter`/`StackFormatter`
construct the exact built-in type, so they lose the subclass, and `CollectionFormatter` needs an `Add`
method that `Queue`/`Stack` do not have — so no formatter can round-trip the shape. The shape
classifier (`WellKnownTypes.TryResolveCollectionShape`) therefore *reports* it as
`CollectionShapeRejection.UnsupportedQueueOrStack` rather than raising from a `Try*` predicate, and the
three sites that must not admit it act on that report: the bake refuses it (naming the declaring type
and the member that reached it), `DeclareRoot` refuses it, and the interpreter refuses it mid-decode
(naming the name the file used, and as a `CerasSchemaException` — the bytes are irrelevant, it is the
current model that cannot describe the shape). All three carry the same reason text
(`WellKnownTypes.DescribeCollectionShapeRejection`).

## References, null, and polymorphism

Reference/polymorphism markers are defined once in `Format/Protocol.cs`
(`RefNull=-1`, `RefNewValue=-2`, `RefNewValueSameType=-3`, `RefInlineType=-5`, bias `+5`).
A marker is written with the bias so non-negative values are object-cache IDs.

## Schema types

`SchemaTypeInfo` kinds: `Enum`, `Struct`, `Class`, `GenericInstantiation`. `GenericInstantiation`
records the definition type (`UnderlyingTypeName`) and ordered argument types (`ArgTypeNames`), which
lets the interpreter describe value/reference representation without requiring the old CLR type to
exist. Custom formatters own their bytes and are never described as an ordinary generated member
schema.

An `Enum` entry records its underlying type id and one name→value entry per declared member. Each
value is written as **eight raw bits** (little-endian), never as a signed magnitude, so a `ulong`
member above `long.MaxValue` is representable; the recorded underlying type id says how those bits
read back, which is why the entry needs no separate signedness flag. The raw bits are also what the
migration map is keyed by, so a renamed member keeps its value across an unsigned range. A dump shows
the magnitude for an unsigned underlying type and a two's-complement negative value for a signed one.

## Load order and what is rejected

1. Magic and format version, then the section-offset envelope.
2. **CRC32 over the whole block.** Integrity is checked before any field it protects is interpreted,
   so a corrupted build-timestamp byte is reported as corruption rather than as "produced by a newer
   build", and a corrupted flags byte as corruption rather than as an unknown compression flag.
3. Semantic header fields: compression flag, producer build timestamp, layout fingerprint.
4. Matching fingerprint → generated readers (no schema parsing). Mismatched → the migration
   interpreter, which refuses rather than guessing:
   - a retyped field with no defined conversion (e.g. `double` → `decimal`) throws
     `CerasSchemaException` naming the field and both types, instead of silently dropping the value
     and leaving the declared default;
   - an impossible collection/array count, an un-representable `decimal`/`DateTimeOffset`/enum/`Type`
     payload, or a value no CLR type can hold is reported as `CerasDataCorruptionException`;
   - a file written **before** the format could describe collection and tuple shapes structurally
     records such a value as a class with no members while its payload is a count plus elements.
     Reading it would desynchronize the rest of the save silently, so it is rejected with an
     actionable `CerasSchemaException`. **Such pre-0.5 experimental saves are not migratable**; load
     them with the build that wrote them, or start the save again.

## Remaining (Phase 3.1)

- Explicit compact-encoding overrides and an explicit protocol-header spec.
- Deriving generated writers/readers and the schema encoder from a single layout IR.
- Custom-formatter context isolation guarantees.
