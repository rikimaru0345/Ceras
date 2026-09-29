# Ceras robust format — layout model

This documents the layout contract that generated writers/readers, the schema encoder, and the
migration interpreter must share. It is little-endian only; fixed-width primitives are the default.

## Envelope (two-region format, version 5)

```
header | schema bytes | schema string table | data section | payload string table
```

The header is a fixed 56-byte little-endian record written by one explicit codec
(`RobustHeader`):

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `CERA` |
| 4 | 1 | format version (currently 5) |
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
- The fingerprint also covers **formatter selection** for every value a user custom formatter wrote:
  the entry's kind, the formatter's name and whether a reference marker stands in front of the frame.
  See [A user custom formatter's payload is an opaque block](#a-user-custom-formatters-payload-is-an-opaque-block).
  That section states what the fingerprint does and does not buy.
- **Version 5 rejects every version 3 and version 4 file**, including well-formed ones. Either could
  hold a custom formatter's opaque bytes under a schema that described generated fields, and such a file
  can be indistinguishable from a good one by fingerprint — so the experimental format advances rather
  than keeping a promise it cannot keep. The two-region envelope itself is unchanged by the bump, and so is
  the encoding of every existing schema kind: a v4 file has no `Opaque` entry, no frame and no formatter
  name in its section, so nothing it contains is described differently. The checked-in historical fixtures
  were regenerated for it, and every one of their diffs is exactly the nine bytes this paragraph predicts:
  offset 4 (the version), 48..55 (the layout fingerprint, which hashes the version, and the CRC32 over the
  block). No schema byte and no payload byte moved.

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
back to CLR types on cold paths.

Resolution never walks the loaded assemblies. A name is answered by exactly three identities, in this
order, and a name none of them answers stands for no type at all:

1. the manifest's own names — each registered entry's stable name and its `[PreviousTypeName]`
   aliases, which the manifest's one-name-one-entry rule already makes unambiguous;
2. `Type.GetType`, for the framework and assembly-qualified spellings;
3. the context's configured `ITypeBinder` (`Config.TypeBinder`), reached only from the
   canonical-name parser's definition lookup and memoized per name. This is the one door outside the
   manifest, it is explicit, and `CerasRobustConfig.Seal` installs it before any save can exist, so
   the same bytes resolve the same way in any process that configured the same context.

Requiring a binder rather than scanning is what makes resolution a property of the context: which
types a name stands for is a fact about this context's manifest and configuration, not about whatever
happens to be loaded when the name is read. A binder name that would claim a *different* manifest
entry's name is refused at the seal, since the same string in a save file would otherwise bind to one
type through the manifest and another through the binder.

Note that `SimpleTypeBinder`, the default binder, still searches assemblies itself — its declared
`CustomSearchAssemblies`, then the entry assembly, then everything loaded. That search is now the only
remaining broad lookup in the robust read path, and it is reached only through the configured binder,
on cold paths, and memoized per name.

## Member layout

- Field order is deterministic: `MemberType + MemberName + DeclaringType + "f"/"p"` (ordinal), spelled
  with the [canonical type identity](#type-identity) and derived once for both writers — see
  [the order below](#the-order-a-types-members-are-written-in).
- Field identity is the stable wire name (`[CerasStableName]` overrides the CLR name; previous names
  are aliases used only by migration). An enum member is identified the same way: its entry is recorded
  under its wire name, so the same attribute renames a member without a rename alias.
- Members are written in the deterministic order; the schema records the order explicitly.
- **Which formatter writes a member is resolved at runtime, never baked in at compile time.** A reference
  member's field is a `ReferenceFormatter<T>`, which resolves its inner formatter when it is injected; a
  value-type member's field is an `IFormatter<T>`. A *primitive* member is the one exception and is bound to
  its concrete built-in formatter (`Int32Formatter` and friends), which is sound because
  `TypeConfig.ThrowIfTypeIsNotConfigurable` refuses a custom formatter for a primitive outright — the
  built-in encoding is chosen before any user configuration is consulted, so nothing can displace it.
  The value-type case used to name the *generated* formatter class, which decided at compile time which
  formatter wrote the member: a formatter the application installed afterwards could not be used at all
  (injection cannot put a different formatter into a field typed as the generated class, and it refuses
  rather than silently keeping the generated one). That is the same class of defect as the raw path's
  reinterpret selection below — a payload that depends on something the schema does not record — and
  fixing it is what makes [a user custom formatter's
  payload](#a-user-custom-formatters-payload-is-an-opaque-block) possible for a value type, which is the
  shape a `struct` value (a time, a coordinate, a duration) always takes. Measured cost: none that this
  tree's benchmark can see (a 10-member `struct` graph saves and loads within run-to-run noise of the
  concrete binding), and no byte of any existing save changed.

### The order a type's members are written in

A payload of a declared type is its members in one fixed order, and the order is not the declaration
order. It is the ordinal order of a sort key:

```
<canonical name of the member's type> <member's CLR name> <canonical name of its declaring type> f|p
```

"Canonical name" is [the type identity above](#type-identity), so the same string the schema section
records a type under: `Namespace.Type\`Arity<args>`, `Namespace.Outer+Nested`, `T[]`, `T[,]`, `T*`, and
a declared type parameter as `!` plus its position. `f`/`p` is a field or a property. Each member's
**own** declaring type is used, so an inherited member sorts the same way whichever type reaches it.
A member's wire name (`[CerasStableName]`, `[PreviousName]`) is **not** in the key: the order follows
the CLR name, so renaming a member on the wire moves nothing.

Both writers use this one key, and that is load-bearing rather than tidy. The generated writer emits its
members in this order; the reflection fallback (`ReflectionFallbackFormatter`) sorts the member list it
derives from reflection with `SchemaMemberComparer`, which computes the same string; and a per-closure
schema records the order its writer emits, because a save carries no member order of its own - the
schema section names the fields, and the matching-layout reader never looks at it. A key spelled
differently on the two sides is a second order for one type, so the same value would be written as two
different byte layouts depending on which writer reached it, each self-consistent, and the reader for
the other one would take a member's neighbour's value. The key used to be spelled as a Roslyn display
string on the generated side and `Type.FullName` on the runtime side, and the two disagree for every
shape whose spellings differ - a tuple (`(System.Int32, System.String)` against
`System.ValueTuple`2[[System.Int32, …]]`), a nullable (`System.Int32?` against `System.Nullable`1[…]`),
a nested type (`Outer.Inner` against `Outer+Inner`), any closed generic. `MemberOrderUnificationTests`
pins the agreement by writing the same value through both writers and comparing the bytes.

### The order a closed generic's schema records

Every closure of a generic definition is written by one emitted class - the definition's. So the
payload order of `Wrap<Coord>` is the order `Wrap<T>`'s members sort in, from their **declared**
types, and it is the same for every closure. A declared type parameter is spelled `!0`, which sorts
below every real type name, so `T Value` lands *before* `System.Int32 Tag` whatever the argument is;
for an argument that sorts after `System.Int32` - a `string`, say - the substituted order is the other
way round, and one class cannot emit both. A per-closure schema therefore records **that** order, not
the order the closure's substituted members would sort in (`Legacy.Coord Value` before
`System.Int32 Tag`).

The two orders coincide for many arguments and differ for the rest, so what this costs is: the schema
section's field order and therefore the layout fingerprint, for the closures where they differ, and
nothing in the payload beyond those closures. A file written before a key change records the order
*its* writer emitted, and the order it was written in is not recorded anywhere else in it, so a file
whose own schema lies about its own bytes cannot be recovered by any reader.

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
[Not yet implemented](#not-yet-implemented). Raw primitive encoding never persists CLR struct padding:
a padded value is refused by the reinterpret fast path rather than blitted, so its padding is never
written — see [Which types the raw path may reinterpret](#which-types-the-raw-path-may-reinterpret).

`decimal` is the one primitive whose documented order is **not** its CLR memory order: the runtime
lays the struct out as `_flags, _hi, _lo, _mid`, so blitting a decimal would write the flags word
first and the mantissa words in a different order (and would depend on endianness). It is therefore
excluded from the reinterpret fast path (`ReflectionHelper.IsBlittableType`), which means the raw
serializer writes the **same** `GetBits` layout for a standalone `decimal`, a `decimal[]` element, a
`KeyValuePair<decimal, _>` entry and any struct that merely contains one. The raw (transient) path
has exactly one decimal encoding, and it is the row above.

### Which types the raw path may reinterpret

A **raw/transient** context (`CerasRawConfig`, and only there) offers a fast path that copies a
value's memory image instead of writing it member by member: `ReinterpretFormatter<T>` writes
`Unsafe.SizeOf<T>()` bytes of CLR memory, and `ReinterpretArrayFormatter<T>` writes `count` of them
back to back. That is a *second* encoding for the same value, so a type is only offered the fast path
when the two are provably the same bytes. `ReflectionHelper.IsBlittableType` is the whole rule, and
it asks three questions about a composite struct:

1. **The same members.** A field the member list leaves out — one carrying `[Exclude]` or
   `[NonSerialized]`, or a compiler-generated field that is not an auto-property's backing field — has
   bytes in the image and nothing in the payload, so it cannot be reinterpreted.
2. **The same order.** The CLR lays fields out in declaration order, and Ceras writes a type's members
   in the [order above](#the-order-a-types-members-are-written-in) — by member type name first, so
   same-typed members fall through to the member name alone. `struct S { int One; int Two; int Three; }`
   therefore sorts to **One, Three, Two** while its memory image is One, Two, Three, and a blit would
   have written the declaration order. The comparison is made with the same comparer the runtime sorts a
   real member list with (`SchemaMemberComparer`), not with a restatement of its key.
3. **The same width, with no padding.** A padded struct differs in width as well as in order:
   `struct { byte B; int I; }` is 8 bytes of memory for 5 bytes of payload, so the blit would write
   three bytes no reader accounts for. The check is `SizeOf(T) == sum of the members' widths`, which
   also refuses a struct with no members at all, whose image is a single padding byte. A
   `[StructLayout(Pack = 1)]` struct that genuinely has no padding is unaffected and stays on the fast
   path — the rule refuses padding, not narrow fields.

**Why the question is asked at all:** the raw path's bytes used to depend on whether a type happened to
have a generated formatter, which is not a property of the value or of the build — a type is
reinterpreted until it is marked `[CerasSerializable]`, and silently member-written afterwards. Both
encodings round-trip, so nothing failed; a transient buffer simply stopped being readable at the moment
a formatter was generated for the type. Robust contexts were never affected (the resolver requires
`CerasRawConfig`), and neither are the checked-in fixtures: a type that qualified before and qualifies
now keeps its bytes, and `FormatVersion` is unchanged. The rule narrows the selection only — the member
order is the one both writers compute (see above), and the closed-generic case where a generated
per-closed schema and a reflective writer can still order one type's members differently is a property of
"one shared writer per definition", pinned rather than resolved. The one shape whose selection changed
*and* whose behaviour changed is a struct declaring a `fixed` buffer, which is now refused outright
rather than served by the blit; see below and
[what is rejected](#load-order-and-what-is-rejected).

One exemption remains. It is the same question `decimal` asks, answered the other way round:

- **`Guid`** is blittable even though its eleven private fields (`_a, _b, _c, _d.._k`) do **not** sort
  into layout order, because `SerializerBinary.WriteGuid` is an unaligned copy of those 16 bytes and a
  member of type `Guid` is written by exactly that. The image and the member encoding are the same 16
  bytes whatever order the fields sort in, so a `Guid` — and any struct containing one, if its own
  members qualify — keeps the fast path with unchanged bytes.

**A struct declaring a `fixed` buffer is refused, not blitted.** It used to be a second exemption, on the
claim that it "has no member encoding to disagree with at all". The claim was wrong: it has one, and it is
lossy. A `fixed` buffer is not an array to reflection — the compiler replaces the declaration with a
generated one-element struct (`<Buffer>e__FixedBuffer`) and keeps the declared length in a
`System.Runtime.CompilerServices.FixedBufferAttribute` **on the field**, so the length is nowhere in the
member's type and no encoding derived from that type can see it. The reflection fallback resolves the
member's type, finds the single element it really has, and writes it. For

```csharp
unsafe struct Fb { public fixed byte Buffer[25]; public int Value; }
```

that is measured, not assumed: the value is **32 bytes** of memory image (25 buffer + 3 of padding + the
4-byte `int`) and the reflection path wrote **5** — one byte for the buffer, four for the `int`. The save
succeeded, the stream stayed aligned, and `Buffer[1..24]` loaded back as zeros. `BigStruct` in the test
suite is the same: a 64-byte image, 29 bytes written.

The exemption was therefore covering up a **present** disagreement rather than preventing a hypothetical
one, and the blit's safety rested on the *other* path being broken. What that safety was is worth
recording, because the generator's diagnostic for this shape has since changed. It refused the
declaration at compile time, but under `CERAS029` — the framework-type diagnostic, whose story is a
`System.*` type the user could have declared differently or written a formatter for, and which reaches a
fixed buffer only because the member looks like a `byte*` there. That is not this shape's reason, and one
of `CERAS029`'s remedies is not advice here at all: keep the type out of every `[CerasContext]` and use
the reflection-based `CerasSerializer` is true of a `System.Uri` and false of this shape, whose
reflection path is the one that wrote 5 bytes for a 32-byte value. The shape therefore has a diagnostic
of its own (`CERAS035`, see [what is rejected](#load-order-and-what-is-rejected)), which refuses it for
the reason this section has been giving all along: the declared length is nowhere in the member's type,
so nothing derived from that type can see how many elements there are. That is also what takes the
blit's old safety away. A generator that could *write* the shape rather than refuse it would be one
build blitting it and another member-writing it — the exact failure the rule above exists to prevent,
and one it cannot see coming, because all three of its questions compare a memory image against a member
encoding, and a shape with no member encoding at all is not a disagreement it can measure. So the
blit is declined, the value is routed to the reflection fallback, and the reflection fallback **refuses**
it with a `CerasSchemaException` naming the member, its declared element count and its byte length
(`Formatters/FixedBufferGuard`). The three paths now agree in the only way available to this change: all
three refuse.

Removing the exemption *alone* would not have been enough, and the reason is worth recording: the width
check would have caught the padded case by accident (payload 25 + 4 = 29 against a 32-byte image) but
**not** a `[StructLayout(Pack = 1)]` one, whose image is exactly 29 bytes and would still have qualified
for the blit — leaving the packed struct lossless under the blit and truncated to 5 bytes under
reflection. Padding is an accident of one declaration; the shape is the thing that is undescribable.

A `fixed` buffer on a member the payload does not contain (`[Exclude]`, `[NonSerialized]`) is not
refused, exactly as such a member is not refused anywhere else — the check is made against the members
Ceras actually resolved, not against every field the CLR has. A custom `IFormatter<T>` for the declaring
type is also still a way out, because Ceras hands it the type whole and never looks at the buffer.

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
definition ID (`PayloadGrammar.ReadCollectionCount`), never from the current CLR type, for the
same reason it picks the rest of the payload grammar: a rename, a re-assigned ID or a changed shape
must still decode by the layout the writer used. The bound (`SizeLimits.MaxCollectionSize`) is checked
in the same place, so it cannot drift between the shapes either.

These rules live in one place, `Format/PayloadGrammar`, and it is worth saying which decisions are
stated there and which are deliberately not:

| decision | stated in | from |
|---|---|---|
| which payload an entry describes (`ClassifyPayload`) | `PayloadGrammar` | the file's definition ID / entry kind |
| the element-count encoding and its bound (`ReadCollectionCount`, `UsesFixedElementCount`) | `PayloadGrammar` | the file's definition ID, limit passed in |
| the argument id at a position (`ArgId`), advancing by a fixed width (`Advance`) | `PayloadGrammar` | the file's entry |
| whether a collection definition is one of the built-ins (`IsCollectionPayload`) | `PayloadGrammar` | the file's definition ID |
| whether *this* value carries a reference marker | `PayloadGrammar.FileCarriesReferenceMarker` | the file's own record |
| whether *this build's declaration* carries a reference marker | `SchemaTraversalEngine.CarriesReferenceMarker` | the current CLR type |

`ClassifyPayload` answers `Unknown` for two different payloads that are written the same way — a user
generic's member list and a plain class's member list — and that is one grammar reached by two kinds of
entry rather than two grammars, so the enum has eight values. `PayloadGrammarTests` pins all eight, and
pins each *counted* shape against its own element-count encoding, so a shape and the rule that reads its
first bytes cannot disagree.

The last two rows are two different questions and both exist. The interpreter reads into the current
model, so it has to be right about what *this build* declares — a `List<T>` member is written through
the reference wrapper, and re-deciding that from the file alone gets it backwards. A file-faithful
decoder for the old-data graph holds no current model at all, so it asks the file: the entry's kind, the
opaque entry's own inline flag, and for a user generic the kind the file's section records for the
**definition** entry. The two disagree exactly where only the file knows the answer — an identity this
build cannot resolve, and a user generic whose open type changed between a struct and a class — and
`tests/Ceras.Test/Tests/PayloadGrammarTests.cs` pins both answers.

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

## The structural invariant: a collection description has to carry its arguments

A `GenericInstantiation` entry is the format's description of a **closed generic's payload**, and for the
structural definitions that description *is* the whole grammar. The definition id says the grammar (a
biased element count and then the elements, a 4-byte count and then the elements for a queue or a stack, a
presence byte, a key and a value, the items of a tuple) and the ordered argument ids say what each element,
key, value or item **is**. An entry that does not carry exactly them describes *no* payload, so a reader
that goes on anyway has to guess — and every guess is wrong in a way the payload cannot recover from:

- it consumes the count and then **nothing** per element (there is no width to advance by), so the first
  element's bytes are read as the next element and everything after the collection decodes from the wrong
  offset;
- or it consumes a **fixed** width per element taken from the *current* model, which is right often enough
  to keep the stream aligned while silently replacing every element with a default. A `List<int>` whose
  entry records no argument loaded its three stored `int`s back as three zeros, with no error anywhere;
- or — for a `Dictionary<K,V>` recorded with only its key, or a nested list whose inner entry records none —
  it hands the BCL a value of the wrong shape, and the failure arrives as a bare `ArgumentNullException` or
  `ArgumentException` from `IDictionary.Add` / `IList.Add` rather than as anything Ceras said.

So the invariant is stated once, in `Format/StructuralShape`, and asked at **every** door through which such
an entry can arrive — always *before* a payload byte is interpreted:

| door | site | asked | error |
|---|---|---|---|
| the authoring forge | `SchemaRegistry.Register(type, info)` with an explicit descriptor | the argument **count** against the definition it names | `CerasSchemaException` |
| the section parser | `SchemaSection.Read` | the argument count, and the definition against the ids the section records | `CerasDataCorruptionException` |
| the migration plans | `MigrationPlanBuilder.BuildAll` | the count and the definition (before the per-type plans), then the **argument identities** (after them) | `CerasSchemaException` |
| the `Type` value grammar | `TypeValueShapes.ReadBoundedArgCount` | the argument count | `CerasDataCorruptionException` |

`MigrationPlanBuilder.BuildAll` is listed separately from the parser because it is the boundary **every**
route into the interpreter passes through: `CerasRobustSerializer.Load` builds the plans there, and
`SchemaTraversalEngine`'s own constructor builds them when a caller hands it none. That is what makes the
interpreter's **read** and **skip** directions unable to disagree about a malformed shape — neither can
reach one, so there is nothing for them to disagree *about*. A per-element guard would be reachable from
one direction only, which is why the rule is a section-level one.

Two clauses are asked in a different order at the plan boundary, and deliberately:

- the **count** and the **definition** first, because an entry that describes no payload at all cannot be
  reasoned about per member, and the verdict a member-level comparison would print for one ("no defined
  conversion between these two types") is a guess about a type the file never described;
- the **argument identities** last, because that is a fact about one position of an otherwise readable
  entry, and the existing member walk (`MigrationPlanBuilder.RejectUndescribablePosition`, reached from
  `RejectMovedArrayPayload`) already reports it with the member and the position inside its shape named — a
  better message than an entry-scoped one. What the section-level clause adds is the coverage the member
  walk deliberately does not have: a member the application has since **removed** is skipped, and the
  skipped path has no current declaration to compare against, so a malformed entry behind one used to
  consume its count and then nothing.

**Deliberately not part of the invariant:** whether the file's *names* resolve in this build. A save written
by a build that had a type this one does not is a supported input, and its entries describe their payloads by
identity alone. A `GenericInstantiation` whose definition is a *user* generic — a manifest entry the file
itself describes — is equally legal, and its payload is its member list: that is `PayloadShape.Unknown`
(`Format/PayloadGrammar`), and
the argument-count rule does not apply to it, because a user generic's arity is not a fact about the format.
A hand-written descriptor naming a definition the format does not lay out is therefore left alone; only the
count of a *known* definition is checked.

**The two classifications, and where they overlap.** The same defect — a description that cannot be read —
is reported as one of two exception types, and the choice is made by *which layer* found it, not by what it
thinks of the file:

- `CerasDataCorruptionException` when the **file's own bytes** are self-inconsistent (the section parser,
  the `Type` value grammar). Nothing in this build's model is involved, and the header checksum has already
  verified the very bytes being complained about, so the only available evidence *is* the inconsistency. The
  message says so, and rules out bit rot, because that is the wrong thing to go looking for.
- `CerasSchemaException` when a **caller** opened the door — a hand-registered descriptor
  (`SchemaRegistry.Register`) is application code, with an author who can still fix it — or when the
  interpreter is asked what the file's own description means, which is a migration question
  (`MigrationPlanBuilder`, and its argument-identity clause).

The overlap is deliberate, and it is the point of asking one implementation everywhere: the same entry is
refused the same way on the read and the skip path, so a caller can catch "this file's description is
unreadable" without caring which door found it, while the exception *type* still says whether to look at the
bytes or at its own model. Both are reported while the old-to-current schema is validated, before the stored
root type id is read.

**`List<bool>` specifically, and why no width was changed.** The reported problem paired two claims, and only
the second is real. The first — that a *removed* `List<bool>` member corrupts a save written by this
version — is a false positive: `SchemaTraversalEngine.WalkValue` answers the `Bool` id with a one-byte
advance, which is exactly what `BoolFormatter` wrote, so the skip is byte-exact and every field after it
holds. `MalformedCollectionSchemaTests.A_Valid_Collection_Entry_Behind_A_Removed_Member_Is_Skipped_Byte_Exactly`
pins that for every shape in the table, `List<bool>` included. The behaviour is a property of the *skip*,
which answers the `Bool` id from its own switch, so it holds whether or not `WellKnownTypes.FixedSizes`
records a width for `bool`: setting one would change no byte and no skipped value, and it would not have
touched the real defect — a `List<bool>` whose *entry* records no argument. That is what the invariant
refuses, on both directions, naming the definition and both counts.

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
refused. An explicit descriptor is held to the structural invariant above (a definition of the format's own
has to record exactly that definition's argument count), which is the one thing the forge cannot opt out
of: a descriptor that lies about a shape is refused where the author can still see it, rather than reaching
a reader that could not honour it.

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
argument ids), whose `PayloadGrammar.ClassifyPayload` answer is a count plus elements/entries, a presence
byte, the items of a tuple, a key and a value, or — for a definition the well-known set does not name — the
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

The arity bound above is a *resource* guard; the semantic rule beside it is exact. A count that is not the
definition's own is refused, because a `Type` value names a concrete runtime type and a wrong argument
count names no type at all — the same fact a schema section records as ordered argument ids, and the same
structural invariant (see
[the structural invariant](#the-structural-invariant-a-collection-description-has-to-carry-its-arguments)).
This is the only door of that invariant reachable after a file's schema section has been parsed, because
these entries are synthesized mid-decode, and it is asked *before* the argument array is sized. Left to
`MakeGenericType` it would report the defect as a bare reflection `ArgumentException` on the read path,
while the skip path — which never closes a CLR type at all — would have consumed nothing per element and
desynchronized the rest of the save.

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

`SchemaTypeInfo` kinds: `Enum`, `Struct`, `Class`, `GenericInstantiation`, `Opaque`.
`GenericInstantiation` records the definition type (`UnderlyingTypeName`) and ordered argument types
(`ArgTypeNames`), which lets the interpreter describe value/reference representation without requiring
the old CLR type to exist. A descriptor whose definition is one the format lays out has to record
**exactly** that definition's argument count — a `GenericInstantiation` of `List` with no arguments
describes no payload at all, and is refused at the registration; see
[the structural invariant](#the-structural-invariant-a-collection-description-has-to-carry-its-arguments).

An `Opaque` entry is [a user custom formatter's payload](#a-user-custom-formatters-payload-is-an-opaque-block):
it records the formatter's name and whether the value is written inline, and **no member list**.

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

## A user custom formatter's payload is an opaque block

A type the application writes through a **user custom formatter** is described by a manifest entry of
kind `Opaque` and a payload that is a **declared length followed by that many bytes**:

```
OpaqueBlock := int32 length (little-endian, fixed 4 bytes) | length bytes of the formatter's own output
```

and the schema section's entry for that type carries exactly two things:

```
Opaque entry := kind=4 | internedFormatterName (4-byte schema-string-table index) | flags (1 byte)
                flags bit 0: 1 = the value is written inline (no reference marker in front of the block)
```

Everything the entry could otherwise say, it deliberately does not: **no member list, no enum values, no
arguments, no fixed byte size.** That is the whole point. A custom formatter owns its bytes, so the
generated members of the type it overrides are not what the payload holds, and recording them is the lie
this entry replaces — with them recorded, a migration reader walks fields that are not there and
desynchronises every value after it.

**A reference-typed value keeps its reference marker.** The bytes of a class value with a custom
formatter are `marker, length, payload`, exactly as a generated class value is `marker, members`, and
the object-cache slot is reserved in the same place: the marker is the writer's own reference wrapper's
doing, not part of the frame. A value type's bytes are `length, payload` with no marker. That decision
is a property of the **type**, not of the position, and it is recorded in the entry because a reader
that no longer has the type has nothing else to go on: the collection and array formatters inject
`GetReferenceFormatterRequired(TItem)`, so a class *element* is marked and registered like any other
class value, and a `Nullable`/tuple item or a struct is inline wherever it appears.

The frame is what makes a custom payload **version-tolerant at all**. A later build that removed the
member, or the type, can still advance by the declared length and leave the stream exactly where the
next value begins — which is the thing the previous format could not do, and the reason a user custom
formatter was refused in a robust context at all.

### Which formatter, and what the name does not distinguish

The name is the canonical name of the formatter's **CLR type** (`TypeIdentity.GetCanonicalName`), the
same identity rule every manifest name follows: no assembly version, no culture, no public key token, so
two builds of one application agree on it. It is in the schema section and in the layout fingerprint, so
all four of these are different layouts and all four take the migration path rather than the generated
one:

| build | what changed | fingerprint |
|---|---|---|
| A → B | the type gained a custom formatter | the entry's kind changed (`Class` → `Opaque`) and its content with it |
| A → B | the type lost it | the reverse |
| A → B | one custom formatter was replaced by another | the name changed; the kind did not |
| A → B | the type became a value type, or the reverse | the inline flag changed |

**What the name does not distinguish** is two builds using the *same* formatter class with different
behaviour — a formatter that reads a configuration flag, or one whose encoding depends on state the
application controls. Nothing in the schema section or the fingerprint can see that, and the frame's
declared length catches only the *size* half of it (a formatter that reads a different number of bytes
than the block declares is refused as corruption). This is the same class of limit as the
`SchemaRegistry.Register` forge below: the manifest is trustworthy to the extent its author is. A caller
who needs the fingerprint to distinguish two configurations of one formatter class has to make them two
classes.

### Recognising a formatter, and the forge that remains

Ceras recognises the context's **own** generated formatters by **identity**, not by name, by namespace,
or by any flag a caller can set: the generator emits a static `Schema` field on every formatter class and
registers that very instance for the type, so the manifest entry and the installed formatter's own
declaration are the same object. A formatter that declares that field is *held to it* — a declaration
that is not the instance the manifest holds is refused, because a schema that contradicts the bytes
cannot be made truthful by any framing. Only a formatter that declares no such field is a user custom
formatter, and only those are recorded as `Opaque`. A user class declared in `Ceras.Generated` with a
generated-looking name is therefore simply a user custom formatter, and is framed like one.

`SchemaRegistry.Register` is public, so a caller can register a schema of its own and thereby satisfy
the identity check with a hand-written formatter and a hand-written (possibly untrue) schema. That forge
is pre-existing, is **not** widened by this mechanism (a forged registration is not an opaque selection,
so it neither gains the frame nor loses anything), and is documented rather than closed. Closing it
means verifying a schema against the type's real members.

### Where a custom payload is written, and read

The wrap is installed as the **specific formatter** for the type (`CerasSerializer.WrapOpaqueIfSelected`),
which is what every writer and reader of a type resolves. A custom payload is therefore framed on every
path that writes the type — a root, a member, a collection element, a dictionary key or value, a tuple
item, a polymorphic slot — and read back through the same frame, with no generated code and no
collection/array special case. Two conditions, both required: the config is a robust one, and the
manifest holds an opaque selection for the type (recorded by `RobustCustomFormatterPolicy.Reconcile` at
context setup and again before every `Save`/`Load`). The re-run exists because a serializer seals its
config when it is constructed — so a direct assignment afterwards is *refused*, not ignored — while
`OnConfigNewType` is the one door that still installs a formatter, for precisely the type the callback is
handed and only while it runs. A selection made through that door is honoured only until the manifest is
sealed and baked by an earlier `Save`/`Load`; see [What is still refused](#what-is-still-refused) below.

A **raw/transient** context is unaffected: nothing is described there and nothing has to be accounted
for, so its custom formatter's bytes are exactly what they always were.

### What is still refused

Three shapes, all of them places where the format has nowhere to record the selection:

| refused | why | where |
|---|---|---|
| a custom formatter for a type the **reserved table** answers by name — a scalar, `string`, `Type`, `System.Array`, `System.Object` | a reserved name is a fixed one-byte id with no room for formatter-selection metadata, and `SchemaSection.ResolveId` consults that table before the manifest. A *closed* built-in collection is **not** one of these: the reserved table deliberately does not answer a closed canonical name, because such a type is a manifest entry of its own, so an opaque entry carries the selection and the formatter is honoured | `SchemaRegistry.RequireOpaqueSelectionsHaveARecordableIdentity`, at setup and before every `Save`/`Load` |
| a custom formatter for a type **outside the context's manifest** after the bake | there is no entry to record the selection in, and a value of it cannot be described in a save at all. Asked *after* the bake rather than before it, because a bake is what registers a shape nothing declared (a closed collection reached as a member) — a selection that looks undescribable before the bake can be perfectly describable after it | `SchemaRegistry.RequireOpaqueSelectionsAreDescribable`, inside the bake and before any of the frozen region is published |
| a formatter that declares the generator's static `Schema` field but **is not** the instance the manifest holds for the type | it is a generated formatter whose declared schema contradicts the entry; a schema that does not describe the bytes is the defect itself | `RobustCustomFormatterPolicy.Classify`, at setup and before every `Save`/`Load` |

A *primitive*'s formatter is refused earlier and by a different rule:
`TypeConfig.ThrowIfTypeIsNotConfigurable` refuses it at assignment, because the built-in encoding is chosen
before any user configuration is consulted, so such a formatter would be silently ignored. `string` and
`Type` are the two exceptions there, and a robust context owns both of them itself
(`RobustStringFormatter` / `RobustTypeFormatter`).

A formatter selection may also not be changed once the manifest has been sealed and baked: the schema
section and the fingerprint are both computed once, from the manifest as it stood at that moment, so a
formatter selected afterwards cannot be described by either. **Which layer refuses depends on what the
context already knows about the type, and the two are not interchangeable.** A type configured before the
serializer was constructed has a type configuration that closes the moment the serializer exists, so the
change is refused there (`ConfigurationSealedException`) and the registry is never reached. The one type
that can still be given a formatter afterwards is the one `OnConfigNewType` is handed — that window is
opened and closed around the call, for that one type — and there the registry's refusal is the one that
fires. A type that has not joined the manifest cannot be given an entry after the seal either
(`SchemaRegistry.Register`), so one that first appears late can be given neither a formatter nor a place in
the manifest.

### **Not expressible: rebasing a value against load time.**

A custom formatter sees the value and the buffer, and nothing else — in particular it is not told when
the load happens, and the loader does not hand a "now" to anyone. So a value that must be stored
*relative to the moment of the save and come back relative to the moment of the load* — "expires in 5 s",
a cooldown, a deadline — is **not** expressible as an `IFormatter<T>`, and never was, in a raw context or
a robust one. A formatter can only read the clock itself, which makes the stored number a function of
when the file happened to be written rather than of what the value means. That is a **post-load pass**
over the loaded graph, or a value that is stored as a duration and re-anchored by application code, and
nothing in this format can decide for you: the frame says how many bytes a value occupies, never what
they mean.

### Migration: consume, or authorize losing it

One question decides every case, and it is asked from both directions so that read and skip cannot
disagree about a value (`SchemaTraversalEngine.CustomPayloadVerdict`): on the **read** direction, is this
build's formatter for the current type the **same formatter the file recorded**? Compared by name, because
a file and a build are two processes. That is what makes a formatter's identity part of the format: a
payload written by one formatter and read by another is a different payload, and no framing can say
whether the two agree.

| the verdict | read direction | skip direction |
|---|---|---|
| the same formatter | read through the frame, exactly as the generated reader would | nothing is *delivered* here, so the match does not help: the value is lost, and is dropped only when `CerasRobustConfig.AllowDroppingCustomPayloads` says so |
| not readable, and `CerasRobustConfig.AllowDroppingCustomPayloads` is set | the frame is skipped, the load continues, and the slot keeps its declared default | the frame is skipped, the load continues, and the member is reported through `MigrationContext.SkippedFields` like any other skipped one |
| not readable, and it is not set | **refused** with a `CerasSchemaException` naming the type, the position, the formatter the file recorded and what this build has instead | **refused**, the same way |

The first row is the one worth reading twice. On the read side a matching formatter is the whole answer,
but the skip direction exists for a value that **nothing will receive** — a member the model no longer
has, an element of one, a type value naming a type this build does not have — and there is nowhere to put
a value even if it could be decoded. So the skip direction does not consult the formatter at all: it asks
only whether the caller authorized the loss, and that is the entire difference between the two
directions.

Three consequences worth stating separately:

- **A member the application has since removed is the case the frame exists for, and it is still gated.**
  The bytes are accounted for byte-exactly, by the declared length and nothing else — so the skip can
  never desynchronize the rest of the save — but the value is *lost*, and losing a value a custom
  formatter owns needs the caller's permission. The default asks; one setting answers for every position.
  This is deliberately *narrower* than the standing skip-path asymmetry, which never refuses a **described**
  value: deleting a member of an ordinary described type is still a plain skip, because the file's schema
  says what that value was and the application can reason about losing it from the member name. For a
  custom payload the file records only that *a formatter* wrote some bytes, which is not enough to reason
  about the loss, so the default is to ask rather than to assume.
- **A dropped value is a default, not an invented one.** A reference slot gets the instance the reader
  allocated and registered for it, whose members keep their defaults — the same thing a field added since
  the save gets. What it is not is a value the current formatter produced. The *observable* of a drop is
  therefore that default; a **removed** member additionally appears in `MigrationContext.SkippedFields`,
  because the member really was skipped, while a **retained** one does not (it was not skipped, it was
  filled with nothing). There is no separate "dropped custom payload" report, and adding one is a public
  API decision rather than a format one; the loud refusal is the default, and reaching the drop at all
  means the caller asked for it.
- **A renamed or moved formatter class is a format change.** The name the file recorded has to be the name
  this build has, so keeping a formatter's full name stable is part of maintaining its payloads'
  compatibility. The refusal says so, by naming both formatters.

An unframed value cannot occur in a version 5 file: the writer frames every custom payload it writes, and
a version 4 or 3 file is rejected outright. So the "fail loudly" case the contract names is the refusal
above, and it names the type and the member.

### What "not readable" can be, and why the refusal words it

The refusal names what *this build* has, and there are three answers, not one — because "this build cannot
read that block" has three causes with three different fixes, and a caller who is told the wrong one is
sent to do work that cannot help:

| this build has | said as | the fix |
|---|---|---|
| the formatter the file recorded | *(no refusal — the value is read through the frame)* | — |
| a **different** custom formatter for the type | "the custom formatter 'X'" | change the encoding, or keep the old name |
| the type, and **no** custom formatter for it | "no custom formatter for 'T'" | install a formatter for `T` |
| **no such type** | "no such type at all" | bring the type back (it was removed or renamed) |

A fourth refusal exists and is *earlier*: while the load resolves the file's schema section, a recorded
name that resolves to no CLR type is refused there, by `CerasRobustSerializer.BuildIdToType`. That is a
resolution problem, not a payload problem, and it is why the "no such type" row above is reachable mainly
for a *nested* position — a removed member, a dropped collection element — rather than for a name the
load has already looked up. Which of the two the caller sees depends on how the type name failed to
resolve: `ITypeBinder` documents both shapes, and a binder that **throws** (the default
`SimpleTypeBinder` does) produces the earlier resolution refusal, while a binder that **returns null** is
normalized to "no such type" and lets the load reach the payload verdict
(`SchemaRegistry.ResolveThroughBinder`, `SchemaTraversalEngine.ResolveFullName`). Both answers are
correct for the binder that produced them.

## Load order and what is rejected

1. Magic and format version, then the section-offset envelope. These three are the **envelope**: they
   say how the rest of the header is to be read at all, so they are validated *before* the checksum.
   That is the order the checks run in, not the reach of the checksum: the magic, the version byte and
   all three offsets are inside the range the CRC32 is computed over like every other byte, so a
   corrupted one of them is still a corrupt file (the version byte excepted — it is reported as a
   version, because a reader that cannot read the format has no standing to go on and interpret the
   rest of the block), and a block that clears these three and then fails the checksum is still
   refused as corrupted. Validating them first is what keeps the refusals actionable rather than
   merely correct: a version this build does not read is refused with the reason — an older
   experimental version has to be recreated — instead of as a complaint about the rest of the header.
   The offsets are bounded here, and used for nothing yet.
2. **CRC32 over the whole block.** Integrity is checked before any field it protects is interpreted,
   so a corrupted build-timestamp byte is reported as corruption rather than as "produced by a newer
   build", a corrupted flags byte as corruption rather than as an unknown compression flag, and a
   corrupted application-version byte as corruption rather than as "written by a newer application".
3. Semantic header fields: application version, compression flag, producer build timestamp, layout
   fingerprint. Each is a *comparison against this build* rather than a fact about the format, which
   is exactly why a corrupted one has to be reported as corruption and not as the verdict it would
   have produced.
4. Matching fingerprint → generated readers (no schema parsing). Mismatched → the migration
   interpreter, which refuses rather than guessing:
   - a retyped field with no defined conversion (e.g. `double` → `decimal`) throws
     `CerasSchemaException` naming the field and both types, instead of silently dropping the value
     and leaving the declared default;
   - a member — **or any of its declared arguments** — whose array-ness changed between a concrete array
     and a type the writer reaches through a reference wrapper throws `CerasSchemaException` naming the
     member, the position in its shape, the type the file stores there and the type this build declares.
     `int[] Slot` becoming `Array Slot`, `object Slot`, a collection or a class, or the reverse, is the
     member itself; `List<int[]>` becoming `List<object>`, a dictionary's value argument, or the array two
     levels down in a `List<List<int[]>>` are the same change one level in. The two positions do not start
     at the same byte, so there is no offset at which the stored value can be read as the current one; the
     two grammars are tabulated in
     [A declared `System.Array`](#a-declared-systemarray-is-that-same-slot-under-a-different-declared-type-id).
     Above the argument nothing can see the change: the member's own bytes — a marker, a biased count, the
     elements — are identical either way. What can is the *argument tree*, because the schema section
     records every closed generic as its own entry with ordered argument type ids and every structural
     reader pairs each id with the current CLR argument, so one walk covers a collection element, a
     dictionary key and value, an array element, a tuple item, a KeyValuePair and a Nullable alike
     (`MigrationPlanBuilder.RejectMovedArrayPayload`).
     The walk runs while the old-to-current plans are built, **before the root type id is even read**,
     because both facts it needs are schema facts. Only the save file knows the first of them, so it is
     necessarily a load-time check: nothing at generator time can inspect that file. The advice is to keep
     the position that can hold the stored grammar under its own stable name and build the other shape from
     it in application code. It compares *array-ness* and nothing else; a position that is an array on both
     sides, a *removed* member (skipped by its own file-side grammar, with no current declaration to
     disagree with) and a jagged array in an `object`/`System.Array` slot (one polymorphic value, not
     declared array members) are unaffected;
   - a **structural argument** of a mapped member that no longer names the same current CLR type throws
     `CerasSchemaException` naming the member, the position in its shape, and both types
     (`MigrationPlanBuilder.RejectChangedStructuralArgument`). The definition still matches, so the check
     above accepts `List<int>` against `List<long>`, `List<List<int>>` against `List<int[]>` and
     `Dictionary<string,int>` against `Dictionary<int,int>`; the argument walk is what sees them, and the
     failure each replaces is a bare reflection `ArgumentException` from assigning the *file's* closed
     generic to the current member, not a desync — a plan's type comes from resolving the file's own name,
     and `CurrentTypeOf` prefers it over the current member's declared type. The only thing that makes a
     position acceptable is that both sides resolve to the same current CLR type, which is what keeps the
     working cases working without an allow-list: a renamed argument type resolves through its
     `[PreviousTypeName]` alias to this build's type, and an unchanged one resolves to itself. A *direct*
     member's widening and an array member's element widening are different paths and stay supported — a
     scalar is not a closed generic and an array is not a generic type in reflection, so neither is ever an
     argument here;
   - a **position neither well-known nor described by the file's own schema section** is refused
     (`CerasSchemaException`) rather than skipped: nothing says how such a value is written, and a reader has
     to assume it takes no bytes, which is the silent desync these checks exist to prevent. A real writer
     only records ids from its own manifest, so this is a doctored or truncated file. A described shape
     nested deeper than the format's own bound (64 levels, the same as the `Type` value grammar) is refused
     for the same reason — an entry naming itself as its own argument reaches that bound — rather than left
     unchecked (`MigrationPlanBuilder.DescendIntoArguments`). A position whose name only the configured
     `TypeBinder` resolves (an external type bound by name) is *not* refused: the reader consumes it
     structurally. A name nothing resolves is refused earlier still, by
     `CerasRobustSerializer.BuildIdToType`, before the plans are built and with no payload interpreted;
   - a **collection/array/tuple description that carries no readable definition or arguments** is refused
     before the stored root type id is read, and identically on the read and on the skip path — see
     [the structural invariant](#the-structural-invariant-a-collection-description-has-to-carry-its-arguments).
     A definition of the format's own without exactly that definition's argument count is a
     `CerasDataCorruptionException` from the section parser, because the section contradicts itself; a
     definition or an argument identity the file does not describe is a `CerasSchemaException` from the
     plan builder. Each of these was previously silent, a wrong value, or a bare reflection exception: a
     `List<int>` whose entry records no argument loaded its elements back as zeros, a `Dictionary` recorded
     with only its key threw `ArgumentNullException` out of the BCL, and every malformed shape
     desynchronized the *skip* used for a member the application has since removed, because the element
     count was consumed and then nothing;
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
   - a **type that declares a `fixed` buffer** is refused at **formatter-construction** time on every
     path, with a `CerasSchemaException` naming the member, its declared element count and its byte
     length (`Formatters/FixedBufferGuard`). It is the one member shape the format cannot describe
     member-by-member: the compiler's declared length lives in a field attribute, nowhere in the
     member's type, so the reflection fallback wrote a single element for the whole buffer — 5 bytes
     for a 32-byte value, with the save succeeding and the rest of the buffer loading back as zeros.
     The source generator refuses the same declaration at compile time under an id of its own
     (`CERAS035`), not `CERAS029`'s: a `byte*` is all the member looks like to the framework-type
     rule, whose story is a `System.*` type, and the reason this shape is refused is the one above.
     The raw path's blit is declined for the type too, so it cannot be the one path that happens to
     round-trip — see
     [Which types the raw path may reinterpret](#which-types-the-raw-path-may-reinterpret). A member
     the payload does not contain (`[Exclude]`, `[NonSerialized]`) is not this rule's business, and a
     custom `IFormatter<T>` for the declaring type is still accepted, because Ceras then hands it the
     type whole and never inspects the buffer;
   - a **custom formatter's payload** the current build cannot read is refused, and dropped only when
     `CerasRobustConfig.AllowDroppingCustomPayloads` says so — see
     [A user custom formatter's payload is an opaque block](#a-user-custom-formatters-payload-is-an-opaque-block).
     The refusal is `CerasSchemaException` and names the type, the position, the formatter the file
     recorded and what this build has instead; the drop is reported through `MigrationContext.SkippedFields`.
     Both the read and the skip direction ask the same question, so a value in a member the application has
     since removed is refused on the same terms: the declared length accounts for its bytes exactly, which
     is what keeps the rest of the save aligned, but the value itself is lost and losing it is the
     caller's decision;
   - an **unframed block where this build expects a frame** is refused as
     `CerasDataCorruptionException` naming the declared length and what the formatter actually
     consumed, on the fast path and in the interpreter alike. That is the case where a value written
     some other way — a generated member list, a payload from a different formatter — meets a framed
     position. The opposite direction, a framed block where the reader expects a described value, is
     the fingerprint's job and not the frame's: a build whose manifest describes a value differently
     than the file that claims to match it is a lying header, which is the pre-existing forge below;
   - a **file written before the format could describe collection and tuple shapes structurally**
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
   read" from a silent success into a loud failure. What *is* checked before any of it runs is the
   structural invariant above — a collection description that carries no readable definition or
   arguments — so a shape of that particular kind is refused at its own door rather than being left
   for this detector to notice. The other kinds of lie (a member list that is not the type's, a name
   that is not the type's) are still only this.

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
- **What the custom-payload mechanism above deliberately does not do**, stated here so it is not read as
  an oversight:
  - **It does not make a custom payload *migratable*.** The frame is a length; a value whose formatter has
    been replaced, or removed, is refused (or dropped when authorized). There is no conversion and no
    partial read, because a length says nothing about contents.
  - **It does not distinguish two builds of one formatter class.** The fingerprint covers the
    formatter's *type*, not its configuration; a formatter whose behaviour depends on application state
    is invisible to it, and only a size change is caught (by the frame boundary check).
  - **It does not cover a value rebased against load time**, which is not expressible as a formatter at
    all — see the section above.
  - **It does not close the `SchemaRegistry.Register` forge**, which is pre-existing and independent.
  - **It does not make the generated reader verify the file's schema section.** A matching fingerprint
    is still the whole claim on the fast path (no schema is parsed there), and a header that lies about
    it is a forge; what the fast path *can* check without parsing the section is the frame boundary of
    every value this build expects to be a frame, and it does.
- An inspectable old-data graph. The interpreter still streams: it reads an old value and assigns it
  to the current member, so application migration code cannot examine the old graph as data.
  Reparenting a type, inlining/removed types and lossy conversions are consequently refused rather
  than transformed.
- A member — or any of its declared arguments — whose declared type changes *between* a concrete array
  and a polymorphic slot. That change moves the reference marker, so the two layouts have no common
  reading: the file's type id says "a count, no marker" while the current declaration says "a marker",
  and `SchemaTraversalEngine.ReadAndApplyFields` decides the array case from the *current* field type. It
  is therefore **refused up front**, while the old-to-current plans are built and before the root type id is
  even read, with a `CerasSchemaException` naming the member, the position, the type the file stores it as
  and the type this build declares (`MigrationPlanBuilder.RejectMovedArrayPayload`; see step 4 of
  [Load order](#load-order-and-what-is-rejected)). The same two grammars meet at every *declared argument*
  of a member — a `List<int[]>` element is a marker and a bare count where a `List<object>` element is a
  marker and a `Type` value — so the check walks the recorded argument tree as well, and the failure it
  replaces there was a bare reflection `ArgumentException` mid-assignment rather than a desync. Making the
  change *migratable* is still open, and needs a decision about which side wins — the file's grammar, as
  everywhere else, or an explicit representation of the value — rather than a reader tweak. (The
  generated fast path is never reached for such a file anyway: a changed member type changes the
  fingerprint, so the interpreter is always the reader.)
- **Neighbouring shapes these refusals deliberately do not cover**, because no schema-level fact separates
  them safely:
  - a change between two non-array declarations that disagree about the marker — `object` and a concrete
    class are refused by the "no defined conversion" rule above, but `object` and a *sealed* class are not,
    and a concrete array and a collection argument are not either. The array↔collection pair is why no
    symmetric rule can exist: `List<List<int>>` against `List<int[]>` migrates correctly today (each inner
    list becomes an array of its elements), so refusing the pair would refuse a working migration;
  - `List<int>` against `List<long>`, and any other change of a collection's argument *type* with the same
    shape. The reader has an element-coercion path written for the widening set and it cannot be reached,
    so there is no working conversion to preserve here; making one work would be a migration feature rather
    than a format guarantee, so these are refused with the direct-member widening set named instead.
- **The array-name parse, and why it is not a wire change.** `TypeIdentity.TryParseCanonicalName` used to
  build a rank-1 array with `MakeArrayType(1)`, which is a *different* `Type` from `typeof(T[])` — it names
  its rank as unspecified, `Int32[*]` rather than `Int32[]` — and is therefore not assignable to it. Every
  declaration resolved from a canonical name is then used as a field's declared type, a generic argument or
  an instance to construct, all of which go through an assignability or a cast check, so the wrong `Type`
  failed much later and far from the parse: a bare `ArgumentException` out of `FieldInfo.SetValue`, naming a
  type that appears in neither model. A member declared `List<int[]>` did not round-trip in this build at
  all, with or without a schema change. It now parses `T[]` to the vector array. The *grammar* did not
  change and neither did any byte: `GetCanonicalName` writes `T[]` for rank 1 and `T[,]` above it, and so
  does the generator's `BuildRobustName`, so `[]` can only ever have meant the vector and there is no
  separate spelling to preserve. The layout fingerprint is unchanged, which is the check that matters.
- **The element-coercion path for a collection's argument stays unreachable, deliberately.** The reader has
  code to coerce each element of a `List<int>` read as a `List<long>` through the defined widening set, and
  `CurrentTypeOf` preferring the plan's resolved type is why it cannot be reached. Changing that preference
  would make those migrations work — but a save that only loads because the *old* type happened to be
  assignable is a migration nobody asked for, and the same preference is what makes a type rename resolve.
  So the change is refused with an actionable error instead, and the preference is left alone. Direct-member
  and array-element widening are unaffected and remain supported: a scalar is not a closed generic and an
  array is not a generic type in reflection, so neither is ever an argument.
