# Ceras robust format — layout model

This documents the layout contract that generated writers/readers, the schema encoder, and the
migration interpreter must share. It is little-endian only; fixed-width primitives are the default.

## Envelope (two-region format, version 4)

```
header | schema bytes | schema string table | data section | payload string table
```

The header is a fixed 56-byte little-endian record written by one explicit codec
(`RobustHeader`):

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `CERA` |
| 4 | 1 | format version (currently 4) |
| 5 | 1 | payload string-table compression flags |
| 6 | 2 | reserved |
| 8 | 4 | game/app version (`robustVersion`) |
| 12 | 8 | producer build timestamp (UTC ticks; 0 = unknown) |
| 20 | 8 | schema string table offset (end of schema bytes) |
| 28 | 8 | data section offset |
| 36 | 8 | payload string table offset |
| 44 | 8 | deterministic layout fingerprint |
| 52 | 4 | CRC32 (computed with these 4 bytes zeroed) |

- That checksum is **CRC-32/ISO-HDLC** (ITU-T V.42 / IEEE 802.3, reflected polynomial
  `0xEDB88320` — the zlib/PKZIP/gzip CRC-32), *not* CRC-32C (Castagnoli). The two **generally
  differ** and are not interchangeable: they happen to agree on the empty input (both `0x00000000`)
  and on a handful of short inputs, and like any 32-bit CRC each of them collides on some longer
  ones — but a file written under one polynomial is rejected by a reader using the other, so
  switching would fail essentially every existing save on its own integrity check. The polynomial
  is part of the on-disk contract; `Helpers/HashHelpers` pins it, and
  `HistoricalPolymorphicFixtureTests.Every_Fixture_Is_An_Old_File_Of_This_Format_With_An_Intact_Checksum`
  pins the computed value against the checked-in golden files.

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
- The fingerprint also covers the **format version**, so identical declared layouts under two
  incompatible format versions cannot match each other's fast path.
- **A user custom formatter is not part of this contract and is refused in a robust context**
  (`Ceras.Format.RobustCustomFormatterPolicy`). The schema section describes *values*, and the
  fingerprint is derived from it; neither records which formatter produced the bytes, so a custom
  formatter's opaque payload is invisible to both. A later build that removes, reorders or retypes a
  member of the overridden type then cannot skip the value, and a build that kept the member still sees
  a *matching* fingerprint and takes the generated reader — silently wrong data, not an error. The
  check runs at context setup and again before every `Save`/`Load` (a type config seals lazily, so a
  formatter can be installed after the serializer was constructed). Only formatters that write
  describable bytes pass: the internal `RobustStringFormatter`/`RobustTypeFormatter`, and the ones the
  source generator installed for the context's manifest — identified by the provenance of that
  installation, not by their namespace or type name. Raw/transient contexts are unaffected.
  This is temporary and is lifted when the format carries explicit opaque-schema and
  formatter-selection metadata; until then a robust save contains no opaque custom payload.
- **Version 4 rejects every version 3 file**, including well-formed ones. A v3 file could hold a custom
  formatter's opaque bytes under a schema that described generated fields, and it can be
  indistinguishable from a good file by fingerprint — so the experimental format advances rather than
  keeping a promise it cannot keep. The two-region envelope itself is unchanged by the bump; the
  checked-in historical fixtures were regenerated for it.

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
  are aliases used only by migration). An enum member is identified the same way: its entry is recorded
  under its wire name, so the same attribute renames a member without a rename alias.
- Members are written in the deterministic order; the schema records the order explicitly.

### The order a closed generic's schema records

The sort key uses each member's **declared** type, and every closure of a generic definition is written
by one emitted class - the definition's. So the payload order of `Wrap<Coord>` is the order
`Wrap<T>`'s members sort in, where `T Value` lands *after* `System.Int32 Tag`, and it is the same for
every closure. A per-closure schema therefore records **that** order, not the order the closure's
substituted members would sort in (`Legacy.Coord Value` before `System.Int32 Tag`). The two differ for
most type arguments, and only a schema that lists the fields as they are written describes the bytes
that follow.

This is a property of the schema, not of the payload: correcting it changed no payload byte. What it
changes is the schema section's field order and therefore the layout fingerprint, for the closures whose
two orders differ. A file written before the correction recorded the substituted order for such a
closure, so its own schema misdescribes its own bytes; no reader can recover that, because the order the
file was written in is not recorded anywhere else in it.

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
index in both the payload and the schema string table. The one remaining compact encoding is not a
primitive and is documented at its use site: reference/polymorphism markers, written with
`WriteUInt32Bias` at bias `+5`. Collection counts are *not* uniformly compact — see
[Collections and arrays](#collections-and-arrays) for the per-shape rule. Those biases and constants
are **not** hashed into the fingerprint individually — only `FormatVersion` is. They are covered only
insofar as a change to them is accompanied by a deliberate `FormatVersion` bump; see
[Not yet implemented](#not-yet-implemented). Raw primitive encoding never persists CLR struct padding.

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
reflecting CLR internals. This keeps implementation details such as `Dictionary\`2+Entry` out of the
schema (`WellKnownTypes.IsWellKnownCollectionDefinition`).

**The element count is encoded per shape, not per "collection".** `ArrayFormatter` and
`CollectionFormatter` (for `List<>`/`HashSet<>`/`Dictionary<,>` and every collection subclass) write
`WriteUInt32Bias(count, 1)`, so the count is a compact biased varint in which `-1` marks a null
collection. `QueueFormatter` and `StackFormatter` do **not**: they write the count through the injected
`IFormatter<int>`, which is the fixed 4-byte little-endian `int` encoding (`Int32Formatter`), unbiased
and with no null marker — a null queue or stack cannot be written at all, because `Serialize` reads
`value.Count` before it looks at anything else. So:

| shape | count | null marker |
|---|---|---|
| array, `List<>`, `HashSet<>`, `Dictionary<,>`, collection subclass | `WriteUInt32Bias(count, 1)` | `-1` |
| `Queue<>`, `Stack<>` | `WriteInt32Fixed(count)` | none — a negative count is corruption |

The two encodings are indistinguishable for the small counts a test save usually holds, which is what
makes this a silent-corruption hazard rather than a loud one: a reader that applies the biased rule to
a queue or a stack decodes a *plausible* count, consumes the wrong number of bytes and desynchronises
every field after it. The migration interpreter therefore picks the encoding from the **file's**
definition ID (`SchemaTraversalEngine.ReadCollectionCount`), never from the current CLR type, for the
same reason it picks the rest of the payload grammar: a rename, a re-assigned ID or a changed shape
must still decode by the layout the writer used. The bound (`SizeLimits.MaxCollectionSize`) is checked
in the same place, so it cannot drift between the shapes either.

Any other concrete collection shape the built-in collection formatter serves — a `HashSet<>` /
`LinkedList<>` / `SortedList<,>` subclass, or one that implements `ICollection<T>` /
`IDictionary<K,V>` directly — is described as the built-in payload it is written as (`List<>` /
`Dictionary<,>` plus the element/key/value identities) and is written as exactly those bytes. On the
migration path the interpreter rebuilds such a shape by constructing the shape itself; when it has no
public parameterless constructor but a wrapping constructor, it constructs the built-in collection the
shape describes, hands that to the wrapping constructor, and then adds the elements to the **backing**
collection rather than to the wrapper (which may have no `Add` at all). The value and the fill target are
deliberately different objects here (`Format/CurrentObjectFactory.CollectionFill`), and whether that
works depends entirely on the wrapping constructor: one that *retains* the collection it is handed ends
up holding every element, while one that *copies* it holds only what it copied — an empty collection,
because the copy was taken before anything was added. A shape it can neither construct nor wrap is
rejected with an actionable `CerasSchemaException` instead of a raw reflection exception. The four
families below are removed from this set on purpose, and the copying case is one of the reasons.

**The robust format describes only rank-1 arrays.** `MultiDimensionalArrayFormatter` (rank +
per-dimension lengths + flattened elements) exists in the raw serializer, but the structural schema
and the interpreter describe a single element type, so a multidimensional array is rejected at bake
time (`SchemaRegistry.TryBuildStructuralSchema`), by the generator for a member (`CERAS027`), and by
the interpreter when reading. Use a jagged array or flatten it.

A **polymorphic slot** is the one place none of those applies, so it refuses the value itself
(`Format/MultidimensionalArrayGuard`). A member declared `object` or `System.Array` — or an `object`
element of a declared collection — says nothing about the concrete type, so the only record of it is
the [`Type` value](#the-type-value-a-polymorphic-values-type-tag) behind the reference marker, and the
`Array` form of that grammar is a single element-type tail with no rank in it. Writing a rank > 1 array
there produced a save that loaded as a one-dimensional array over the same flattened elements, with no
error in either direction. It is now a `CerasSchemaException` at write time, raised ahead of the
reference marker and the object-cache registration (`ReferenceFormatter.Serialize`) and, for a
`Type`-valued slot, ahead of the type value itself (`RobustTypeFormatter.Serialize`). Carrying the rank
in the type value — and teaching the structural schema, the generated code and the interpreter about it
— needs a format change and is Milestone 1 work. The raw serializer is unaffected: its `TypeFormatter`
names the full CLR type, rank included. A file that already holds such a value (written before this, or
hand-written) cannot be recognised on read, because the rank is not in the bytes; the exact-consumption
check usually notices the resulting desync, but that is a detector, not a recovery.

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

## The temporary collection policy

`Format/UnsupportedCollectionPolicy` refuses four collection families outright:

| refused | kept |
|---|---|
| `ReadOnlyCollection<T>` — the built-in type and every subclass | the exact `List<T>` and the exact `Dictionary<K,V>` |
| `ReadOnlyDictionary<K,V>` — the built-in type and every subclass | `HashSet<>` and its subclasses; the exact `Queue<>` and `Stack<>` |
| every `List<T>` subclass | `LinkedList<>`, `SortedList<,>`, `SortedSet<>` and every other collection the built-in formatter serves |

Note the `Queue<>`/`Stack<>` row against the paragraph above it: a `Queue<>`/`Stack<>` **subclass** is
refused, but by a different and older rule (`CollectionShapeRejection.UnsupportedQueueOrStack` — no
formatter can round-trip it at all), not by this policy. The exact built-in `Queue<>`/`Stack<>` are fine.

This is an **owner decision, not a format property**: the format can still describe these shapes, and
no byte described below changes. What the policy removes is the promise that they round-trip, and the
three shapes do not fail alike — which is the whole argument for refusing the families as a group
rather than fixing them one at a time. Measured against the code as it stood before this policy:

- A **`List<T>` / `Dictionary<TKey,TValue>` subclass with no parameterless constructor loses its elements
  silently on migration.** The wrap-and-copy fallback constructs the wrapper from a *freshly empty*
  backing collection and only then fills that backing collection, which a copying constructor has already
  copied. The result is a real instance holding zero elements, the following members still aligned, and
  no error. The matching-layout reader does not get that far — `Activator.CreateInstance` without
  arguments, then a `MissingMethodException`.
- A **`ReadOnlyCollection<T>` subclass fails on the way out.** The interpreter reconstructs it correctly
  (the wrapper *retains* the list it is handed, so filling the backing list afterwards does reach it),
  but the dedicated formatter was typed for the *base* `ReadOnlyCollection<T>`, so the reference wrapper
  could not bind a serializer for the subclass: a bare `ArgumentNullException("method")` for a typed
  slot, an `InvalidCastException` for a polymorphic `object` slot.
- The **base `ReadOnlyCollection<T>` worked** — it round-tripped through the raw serializer, including a
  wrapper holding a reference to itself. It is refused because it cannot be told apart from the subclass,
  which resolves to it.

**The exact-type distinction is the whole rule.** The predicate walks the base chain and matches generic
*definitions*: `ReadOnlyCollection<>` / `ReadOnlyDictionary<,>` in any position, `List<>` /
`Dictionary<,>` only when the walked type is *not* the type itself. An inheritance ban on `List<>` would
take `List<int>` down with its subclasses, which is exactly the shape that has to keep working.

**Where it is enforced** — one predicate, asked at every layer that would have to produce a value,
because a refusal that exists only on the write side is a shape a legacy file can still walk into:

| layer | site | error |
|---|---|---|
| source generator | an annotated member | `CERAS033` |
| source generator | an assembly-level declaration or a `[CerasContext]` root | `CERAS034` |
| robust bake | `SchemaRegistry.TryBuildStructuralSchema` (and so `DeclareRoot` and a Save's root check) | `CerasSchemaException` |
| robust bake | `SchemaSection.ResolveId` — a member, a generic argument or an array element that reaches one | `CerasSchemaException` naming the declaring type and the position |
| raw / transient | `BannedTypes` — a member, a value in a reference slot, a `System.Type` value | `BannedTypeException` |
| raw / transient | `CollectionFormatterResolver` — a root, and a payload that names the type while it is read | `BannedTypeException` |
| raw / transient read | `TypeFormatter.Deserialize` | `BannedTypeException` |
| migration read | `SchemaTraversalEngine` — a collection payload, a class-shaped entry, and a file entry whose name still resolves to a refused type | `CerasSchemaException` |
| migration read | `SchemaTraversalEngine.ReadTypeValue` — a `Type` value naming one | `BannedTypeException` |

**Nested reachability is the same question.** The bake expands a described collection into its
element/key/value type identities, so `List<ReadOnlyCollection<int>>` and
`ReadOnlyCollection<int>[]` are refused at the *argument* that is refused; the generator walks the same
shapes (`TypeHelpers.FindUnsupportedCollectionType`) and reports the offending argument, long before
the bake.

**An `IReadOnly*` signature is not one of these shapes.** `IReadOnlyList<T>`,
`IReadOnlyDictionary<K,V>`, `ICollection<T>` and `IList<T>` are ordinary API declarations. The predicate
matches base-type identity, never a name or a namespace, and the generator's mirror refuses interfaces
outright — a name-shaped check would reject code that has nothing to do with serialization.

**One position is deliberately *not* refused: the migration skip path.** A refused shape sitting in a
member the user has since *removed* has no value to produce, and its payload still has to be accounted
for byte-exactly or every field after it decodes from the wrong offset. Accounting for it is right;
refusing it would turn an intentional deletion into an unloadable save. Only the read path — the one
that would have to invent a value — refuses. This is the same asymmetry the `Queue<>`/`Stack<>` subclass
rejection already has.

`SchemaRegistry.Register(type, info)` with an **explicit** descriptor stays available: it is the
documented public forge, and it is how a historical file's shape is described by hand. Only the derived
paths — `DeclareRoot`, and `Register(type, null)`, which derive the descriptor from the CLR type — are
refused.

## References, null, and polymorphism

Reference/polymorphism markers are defined once in `Format/Protocol.cs`
(`RefNull=-1`, `RefNewValue=-2`, `RefNewValueSameType=-3`, `RefInlineType=-5`, bias `+5`).
A marker is written with the bias so non-negative values are object-cache IDs.

**An object-cache ID names one instance, so the cache is keyed on reference identity.** The id is
the object's *position* in the reader's deserialization cache, and the reader fills those positions
in stream order, so an id can only ever be shared by the same instance: a non-negative field is a
back-reference to the very object that was written at that id, and everything else is a full new
value. The writer's dictionary (`Helpers/ObjectCache.cs`) therefore uses
`ReferenceEqualityComparer` unless `CerasConfig.ObjectCacheComparer` says otherwise, and a type's
own `Equals`/`GetHashCode` is never consulted — for two instances that are `Equals` but not
`ReferenceEquals`, the second is written out in full and comes back as a second object. Keying on
the default object comparer instead collapsed those into one back-reference, so the second slot came
back as the first object: a save that loaded cleanly and described a different graph than the one
written. Sharing is unaffected — the same instance in two slots is still one byte and one object.

`ObjectCacheComparer` is a deliberate opt-in and an identity-losing one: a value comparer makes a
merely-equal instance collapse into a back-reference to the first, which is only right for a type
whose instances are interchangeable by definition. Such a save is still an ordinary save of the shape
described here and is read back faithfully, collapse included — the change is entirely on the writer
side, since the reader allocates a proxy only when it reads a new-value marker, in stream order.

**Strings are the deliberate exception to "one slot, one occurrence"** in the robust format only: the
payload string table interns them, so two equal strings come back as one instance regardless of the
object cache. The raw format writes a string by value every time. Neither is a reference-cache
decision, and neither makes two distinct *objects* share.

## The `Type` value: a polymorphic value's type tag

A `RefNewValue` marker is followed by the **concrete runtime type of the value**, written by
`RobustTypeFormatter` — and that is a *`Type` value*, not a schema type ID, unless the type happens to
have a schema entry of its own. The two id spaces are disjoint: ids `0..127` are permanently reserved
for well-known types and manifest ids start at `WellKnownTypes.UserTypeBase` (128), so a single tag
decides which of the three cases it is.

```
TypeValue := LEB(tag) [tail]

tag = 0                       (Null)        terminal: the value is null
tag = 127                     (FullName)    tail = a 4-byte string-table index, read-only path
tag in 1..42, a scalar                     terminal
tag = 16                      (Array)       tail = one TypeValue: the element type
tag in {17..22, 23..36, 41}   (a definition) tail = LEB(argCount), then argCount × TypeValue
tag >= 128                    (a manifest id) terminal: the file's schema section describes it
```

Ids `43..126` are reserved and unassigned; no writer emits them and a reader refuses them rather than
guessing. The grammar is **recursive**: `List<List<int>>` is `List, 1, List, 1, Int32` and `int[][]` is
`Array, Array, Int32`, and one walk of the rules above produces the whole tree. That is unambiguous
rather than a best-effort parse, because the tag alone selects the case and every case says how many
bytes its tail occupies.

**The payload follows the type value, and the definition decides which payload.** It is the same
decision the schema section encodes for a member: a `GenericInstantiation` (definition + ordered
argument ids), whose `ClassifyPayload` answer is a count plus elements/entries, a presence byte, the
items of a tuple, a key and a value, or — for a definition the well-known set does not name — the
members of a user generic class. The migration interpreter therefore interns a nested described type
value as exactly that shape (`TypeValueShapes`), so `List<List<int>>` in a slot takes the very same path
a declared `List<List<int>>` member takes: its argument is a second synthesized entry, each level's
reference marker is the writer's own, and the per-shape element-count encoding and the
`SizeLimits.MaxCollectionSize` bound apply unchanged at every level. A terminal tag — a scalar, or a
manifest id — keeps the ordinary per-id walk.

A **nested** argument or element that is itself a reference type is written behind its own reference
marker (an inner `List<>`, array, dictionary, queue or class), while a value type is written inline (a
`Nullable<>`, a `Tuple`/`ValueTuple`, a `KeyValuePair`, a struct). That is the same decision the
interpreter already makes for a described member, from the type the *current* model resolves the
argument to.

**A declared `System.Object` is a marker and nothing else.** `new object()`'s runtime type *is*
`object`, so the writer takes `RefNewValueSameType` and writes no type tag and no payload at all: the
value is one byte, and an `object` root is two (the root type id and the marker). `object` has no
serializable members, so there is nothing between them. The object cache still gets a slot per
occurrence, so two `new object()` values stay distinct and the same one twice stays shared. The
generated reader and the interpreter agree on this, and both are pinned by a real cross-version fixture.
A *type tag* of `object` is unreachable — a tag is written only when the declared and runtime types
differ, and nothing derives from `object` except itself — so a doctored one is refused as corruption.

**A declared `System.Array` is that same slot, under a different declared-type id.** A member (or root,
or collection element) declared as the abstract `System.Array` names no element type, so the writer
reaches it through `ReferenceFormatter<Array>`: a reference marker, the concrete array type as a `Type`
value, and then that array's own payload — the identical grammar the paragraph above describes for
`object`, which is why the same code reads both. The difference is only in *identity*, and the number
`16` is the whole of it. It means three different things depending on where it appears:

| where `16` appears | what it is | payload |
|---|---|---|
| a member's, root's or element's declared type id in the schema section | the declared type `System.Array` | reference marker, then the `Type` value, then the payload |
| the definition id of a `GenericInstantiation` entry (`System.Int32[]`, `System.Int32[][]`, …) | a *declared concrete* array, written by `ArrayFormatter` | a biased element count, then the elements — **no marker, no type value** |
| a `Type` value's tag | the concrete runtime type of a polymorphic value that is an array | the element type, then that same count-plus-elements payload |

So the three must never be conflated, in either direction: a declared-`System.Array` member skipped as
though it had no marker leaves a marker, a type value and a count in the stream, and a declared-`T[]`
member read as one that has a marker reads its element count as a reference marker. The interpreter keeps
them apart by the id itself — `SchemaTraversalEngine.WalkValue` answers the declared-type id as a
polymorphic slot, `ReadGenericInstantiation` answers the `GenericInstantiation` entry — and the
`ArraySlot*.bin` cross-version fixtures pin it on both directions (read and skip) with a trailing
sentinel as the witness. Note also that the array of a *polymorphic* slot is registered in the object
cache (the reference wrapper registers it) while the array of a member declared as that concrete array
type is not (`ArrayFormatter` writes it by value): which is why the same array in two `System.Array`
slots is a back-reference to the first, and the same array in two `int[]` members is written twice.

Bounded on purpose: a type-id LEB128 that does not fit in a non-negative `int` is refused before it can
be read as a small well-known id; an argument count is checked against the definition's own arity (at
most 7, the tuple family) *and* against the bytes that remain before anything is allocated from it; one
type value may contain at most 256 nodes and nest at most 64 levels deep. A tag or argument that names a
manifest id the file's own schema section does not describe is refused on both the read and the skip
path, because nothing then says how its payload is written.

**The synthesized entries are interned by value, not by occurrence.** Two type values that decode to the
same structure — at every level of nesting, and with argument order significant — resolve to one entry,
so the retained cost of a polymorphic slot's type value is the number of *distinct* nested shapes in the
save, not the number of values that mention them. All of it is per-load state: the engine that owns the
decoder is created by one `Load` and becomes garbage with it, so nothing is retained across operations.
That is what makes the per-load bound (65536 distinct identities) unreachable for a real save while
still bounding a crafted one.

**Not expressible: a multidimensional array's rank.** The `Array` tag names one element type, so
`T[,]` and `T[]` produce the same type value while their payloads differ. This is a pre-existing gap —
`int[,]` in an `object` slot does not round-trip even in the same version — and it is reported here
rather than guessed at; the rank-1 restriction above already rejects the shape everywhere the schema is
consulted.

## Schema types

`SchemaTypeInfo` kinds: `Enum`, `Struct`, `Class`, `GenericInstantiation`. `GenericInstantiation`
records the definition type (`UnderlyingTypeName`) and ordered argument types (`ArgTypeNames`), which
lets the interpreter describe value/reference representation without requiring the old CLR type to
exist. Custom formatters own their bytes and are never described as an ordinary generated member
schema.

An `Enum` entry records its underlying type id and one name→value entry per declared member. The name
is the member's stable wire name — `[CerasStableName]`, or the C# member name when the attribute is
absent — and it has to be unique, because it is the member's identity in a migration: two members under
one name are a compile-time error (CERAS009) in a generated enum and a `CerasSchemaException` at the
bake in a runtime-derived one. Both schema paths use the same rule
(`RobustCodeEmitter.BuildSchemaExpression`, `SchemaRegistry.BuildDeclaredSchema`,
`Ceras.Format.EnumWireName`), which is what keeps a writer's record and the migration map in agreement.
Each value is written as **eight raw bits** (little-endian), never as a signed magnitude, so a `ulong`
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
   - a retyped field with a *defined* conversion is coerced; the widening set is exactly
     `Int16 -> Int32`, `Int32 -> Int64`, `Int32 -> Single`, `Int32 -> Double`, `Single -> Double`
     (`MigrationPlanBuilder.DetectCoercion`). An unmatched old field is skipped and surfaces in
     `MigrationContext.SkippedFields`; an unmatched new field keeps its declared default and surfaces
     in `AddedFields`. Note that an enum member whose *name* is gone is **not** skipped: the raw value
     is reinterpreted in the current enum, so a deleted member arrives as an unnamed value rather
     than as `0`. An enum member is matched, in this order, by its wire name, by the *current* C# member
     name, and then by its `[PreviousName]` aliases (`MigrationPlanBuilder.BuildEnumMap`). The second
     step is what keeps a file written before the schema recorded wire names — which recorded the C#
     name — migratable, and it only fills a name no member claims, so it cannot change how a resolvable
     name resolves. The consequence, which is a limit rather than a fallback: a name the current build
     cannot derive at all (a member renamed in C# *and* repinned to a new wire name, read from a file
     that recorded the old C# name) matches nothing and arrives as its raw number. There is no way to
     infer it — it has to be declared as a `[PreviousName]`;
   - an impossible collection/array count, an un-representable `decimal`/`DateTimeOffset`/enum/`Type`
     payload, or a value no CLR type can hold is reported as `CerasDataCorruptionException`;
   - a polymorphic value whose concrete type is not in the manifest is refused at **write** time,
     not silently written by value: `CerasException` wrapping "Type '…' appears as a polymorphic
     value during serialization but is not registered in the schema registry" (thrown from
     `RobustTypeFormatter`). This is a declaration gap, not a corrupt file, so it is not part of the
     migration path;
   - a rank > 1 array reached through a polymorphic slot or a `Type` value is likewise refused at
     **write** time, with a `CerasSchemaException` naming the array, its rank and the slot (see
     [Collections and arrays](#collections-and-arrays)). Nothing of that value reaches the buffer, and
     because the reference caches are cleared in a `finally` (`CerasSerializer.Serialize`) the refused
     save also leaves the serializer reusable — a refusal that abandoned its registrations would make
     the *next* save write a back-reference to an id the reader never sees;
   - a file written **before** the format could describe collection and tuple shapes structurally
     records such a value as a class with no members while its payload is a count plus elements.
     Reading it would desynchronize the rest of the save silently, so it is rejected with an
     actionable `CerasSchemaException`. **Such saves are not migratable**; load them with the build
     that wrote them, or start the save again. The structural collection/tuple descriptions landed
     in the current 4.1.x development line (this repository has never published a `0.5`; the
     released versions are 1.x, 2.x, 3.0.x and 4.0.x), so there is no release number to name and the
     exception itself is the only marker.
5. **The declared data section is consumed exactly**, on both readers. The data section holds nothing
   but the declared root type id followed by the root's payload — `Save` starts the payload string
   table at the very next byte and never pads the region — so a reader that stops before the header's
   `stringTableOffset` has left part of the save unread, and the graph it returns is only as good as
   the part it read. That used to happen silently: the interpreter skips a field it cannot map and a
   payload its grammar does not describe, and then returned. A leftover is now a typed error naming the
   region, where the reader stopped and how many bytes it never looked at: `CerasSchemaException`
   after the interpreter (a schema/layout disagreement, which is what a caller can act on) and
   `CerasDataCorruptionException` after the generated reader (a matching fingerprint means the file
   claims this build's own layout, so a leftover is not a layout disagreement but bytes that are not a
   Ceras save of it). The check is O(1) and needs no schema parse, so the matching-fingerprint path
   still parses nothing; on the interpreter path it runs *before* the deferred `[OnAfterDeserialize]`
   callbacks, because a load that did not read the whole data section must not hand a half-described
   graph to user code.
   **This is a detector, not proof of layout or schema truth.** A desync that happens to land exactly
   on the end of the region passes it and still decodes wrong values, and a deliberately lying
   `SchemaRegistry.Register`-ed schema matches its own fingerprint either way. It is not a substitute
   for truthful custom-payload metadata; it only turns "your save loaded, but part of it was never
   read" from a silent success into a loud failure.

Whether a given save is *meant* to stay loadable is a separate question from whether this decoder
can read it: the compatibility starting format has not been declared yet, so nothing here promises
that any particular save survives. See `TODO.md` (Milestone 2, "declare the compatibility starting
format only after fixtures pass").

## Not yet implemented

Layout-level gaps. The envelope, the primitive encodings, the reference markers and both load paths
are implemented and covered by tests in `tests/Ceras.Test`; this list is not a claim that the rest
of the roadmap is finished (see `TODO.md` for that), only that these specific items are missing from
the layout.

- A single explicit layout IR that the generated writers/readers, the schema encoder, the
  fingerprint and the migration decoder are all derived from. Today those are separate
  implementations kept in agreement by tests.
- Explicit compact-encoding overrides (for example zigzag varints), and recording the *choices* in
  the fingerprint rather than relying on a manual `FormatVersion` bump. Today the collection-count
  and reference-marker biases are fixed constants that the fingerprint does not cover directly.
- Custom-formatter context isolation guarantees. A custom formatter's bytes are opaque here, so a
  save containing one has no automatic upgrade guarantee.
- An inspectable old-data graph. The interpreter still streams: it reads an old value and assigns it
  to the current member, so application migration code cannot examine the old graph as data.
  Reparenting a type, inlining/removed types and lossy conversions are consequently refused rather
  than transformed.
- A member whose declared type changes *between* a concrete array and a polymorphic slot — `int[] Slot`
  becoming `Array Slot` (or `object Slot`) or the reverse. That change moves the reference marker, so
  the two layouts have no common reading: the file's member type id says "a count, no marker" while
  the current member says "a marker", and the interpreter follows the current one
  (`SchemaTraversalEngine.ReadAndApplyFields` decides the array case from the *current* field type).
  Such a file is rejected today, by the exact-consumption check, which is the honest outcome for a
  shape neither reader can decode. Making it migratable needs a decision about which side wins — the
  file's grammar, as everywhere else, or a refusal at bake time — rather than a reader tweak. (The
  generated fast path is never reached for such a file anyway: a changed member type changes the
  fingerprint, so the interpreter is always the reader.)
