# Historical writer — checked-in golden fixtures

`Ceras.HistoricalWriter` holds the **v1** models of a save file and writes the byte-exact fixtures that
the cross-version tests load — `tests/Ceras.Test/Tests/HistoricalFixtureTests.cs` for the
`HistoricalV1` context, `HistoricalConstructionFixtureTests.cs` for the `HistoricalV1Pooled` one and
`EnumWireNameMigrationTests.cs` for the `HistoricalV1EnumNames` one.

It is deliberately **not** referenced by `tests/Ceras.Test`. The v1 CLR types must be absent from the
reader build, so a reader test can only see the *bytes* plus the current model — which is what makes
these real cross-version tests rather than a second reader of the current schema.

## Regenerating (only when intentionally updating a fixture)

```sh
dotnet run --project fixtures/Ceras.HistoricalWriter -c Release -- --out tests/Ceras.Test/Fixtures/HistoricalV1
```

The output is deterministic: the build timestamp is pinned to "unknown" (`SetUnknownBuildTimestamp`),
so re-running with unchanged inputs produces identical bytes — verified by regenerating every
fixture of every context and comparing SHA-256 against the checked-in files. **Every byte in the diff
is a real
change to what old saves look like** — if a regeneration produces a diff you did not expect, that is
a finding, not noise. See the note on `Loadout.bin` below before regenerating that one.

All fixtures are at format version 5. The five that predate the enum wire-name work were
regenerated when the format version advanced from 3 to 4 (a robust context refused a user custom
formatter then, so a v3 file could hold opaque bytes that its own schema did not describe and that its
layout fingerprint could not reveal), and `Loadout.bin` and the polymorphic fixtures below were
re-stamped the same way when they joined them. Version 5 is the opposite move — a user custom formatter
is describable again, and this project writes two fixtures that use one — so the same nine bytes moved
again: the version byte, the high half of the layout fingerprint (which hashes the format version), and
the CRC32 that covers them. No schema or payload byte changed in either bump: the `Opaque` entry kind adds
nothing to the encoding of any kind that was already there.

`OpaqueFrameHolder.bin` and `OpaqueDroppedMemberHolder.bin` are new in version 5, and are the only
fixtures whose schema section holds an `Opaque` entry.

Ordinary CI never builds or runs this project; it only reads the checked-in files.

## What the fixtures pin

| Aspect | Fixture | Old (v1) | Current |
| --- | --- | --- | --- |
| Type rename | `SaveGame.bin` | `V1.Settings` | `GameSettings` via `[PreviousTypeName]` |
| Field rename | `SaveGame.bin` | `Stats` | `Options` via `[PreviousName]` |
| Removed field | `SaveGame.bin` | `Player.Health : int` | gone — the bytes are skipped |
| Retyped field (widening) | `SaveGame.bin` | `Player.Mana : int` | `Mana : long` — coerced |
| Following field | `SaveGame.bin` | — | `Player.Level` — takes its declared default |
| Enum member rename, value above `long.MaxValue` | both | `Rank.Max = ulong.MaxValue` | `Rank.Highest` via `[PreviousName]` |
| Enum member pinned to a wire name | `Loadout.bin` | `[WireAce] Ace = 2` | `[WireAce] Ace = 7` — matched by the wire name |
| Enum member renumbered only | `Loadout.bin` | `Silver = 1` | `Silver = 6` — matched by the name |
| Enum member renamed, no wire name | `Loadout.bin` | `Adept = 3` | `Veteran = 5` via `[PreviousName]` |
| Enum member renamed, wire name kept | `Loadout.bin` | `[WireCrown] Crown = 4` | `[WireCrown] Scepter = 9` |
| Shared reference | `SaveGame.bin` | `Main` **is** `Players[0]` | one instance after loading |
| Reference cycle | both | `main.Friend = buddy`, `buddy.Friend = main` | both directions restored |
| Collection evolution | `SaveGame.bin` | 5 slots, 2 scores | lengths from the file, not from defaults |
| Polymorphic member, renamed base and subtype | `SaveGame.bin` | v1's `Being` / `Monster` | current `Being` / `Monster` via `[PreviousTypeName]` |
| Deleted enum member | `SaveGame.bin` | `Mode.Removed = 2` | no member to map to; the number `2` survives |
| Removed field inside a fixed-width struct | `SaveGame.bin` | `Coord.X, Y, Z` | `Coord.X, Y` — exactly `Z`'s bytes skipped |
| Nested collection shapes | `Inventory.bin` | `List<Coord>`, `Dictionary<string, Coord>`, `List<List<int>>` | all three rebuilt from the file |
| Root other than the save | `PlayerCycle.bin`, `Inventory.bin` | `Player` / `Inventory` as the root | each loaded on its own |
| Construction through a factory/pool | `RelicVault.bin`, `Relic.bin` | v1's `Relic.AncientName` / `Charm.Sigil` | removed; `Charge` `int` → `long`; a **self-cycle** through the pooled type |
| Sealed type first met through a polymorphic slot | `Relic.bin` | `Relic.Owner : object` holding the `Charm` | the same instance is built once and shared with `RelicVault.Token` |
| A `[CerasConstructor]` that rewrites what it is handed | `CtorLedgerBook.bin`, `CtorLedgerRow.bin` | v1's `Ledger` had no constructor; `Amount = -5` | a clamping constructor — the file's value is what the object ends up with, on both readers |
| A constructor that drops a *reference* argument | `CtorLedgerBook.bin` | `Cover.Inner` **is** `Main`, and `Main.Twin` is `Main` | the shared instance and its cycle survive the constructor |
| Independent id spaces | all | one context per model, `HistoricalV1`, `HistoricalV1Pooled`, `HistoricalV1Ctor`, `HistoricalV1Positional`, … | each model is a context of its own, so its ids cannot shift another context's fixtures |

The constructor-argument and positional families each have their own section below, with the full tables.

Each model has its own serialization context (`HistoricalV1`, `HistoricalV1Pooled`,
`HistoricalV1EnumNames`), because a context has one finite manifest: adding types to one of them would
renumber every type id in the fixtures written through it.

## Regenerating `Loadout.bin`

`Loadout.bin` is the one fixture whose bytes depend on *how* the writer names enum members, so it has
an extra constraint the others do not: it may only be regenerated by a writer that records the wire
name (`[CerasStableName]`, or the C# name for a member without one). A writer that records the C#
member name instead produces a file the current model cannot migrate through the pinned members — the
test `The_Fixture_Records_The_Wire_Name_And_Not_The_CSharp_Name` says so by failing. So when a format
or generator change comes with a fixture regeneration, this file is the canary: regenerate it and read
the diff.

It is also the fixture most exposed to a **format version** bump, because a version bump makes every
other fixture need re-stamping too. Since format version 4 the build refuses to read a version 3 file
at all (a v3 file can hold custom-formatter bytes its own schema does not describe), so a
`Loadout.bin` generated before such a bump must be regenerated after it — the load then fails with
"Unsupported robust format version 3", which is the same canary in a louder form.

## The polymorphic-slot fixtures (`V1PolyModel.cs`)

A polymorphic slot records the concrete runtime type of its value as a **recursive `System.Type`
value** (a well-known definition id followed by its argument type values), not as a schema type id, so
these shapes have no representation in the fixtures above. They live in their own context
(`HistoricalV1Poly`) so the ids of the other fixtures cannot move, and every crate ends in a fixed-width
`uint` sentinel written *after* the slot, so a slot that is mis-decoded shows up as a wrong number
rather than as a load that happens to succeed.

| Aspect | Fixture | Old (v1) | Current |
| --- | --- | --- | --- |
| Nested built-in generic in a slot | `PolyNestedList.bin` | `List<List<int>>` (3 outer, one empty inner) | rebuilt element by element |
| Jagged array in a slot | `PolyJagged.bin` | `int[][]` (3 rows, one empty) | every row rebuilt |
| Renamed type as a nested argument | `PolyRenamedNested.bin` | `List<V1Poly.PolyNode>` | `List<Waypoint>` via `[PreviousTypeName]` |
| Plain count at a nested level | `PolyQueueOfQueue.bin` | `Queue<Queue<int>>` | inner queue keeps the fixed 4-byte count |
| Keyed shape with an array value | `PolyMixedShapes.bin` | `Dictionary<string, int[][]>` | keys, both rows and the empty value |
| Control: a described user type | `PolyUserType.bin` | `V1Poly.PolyBeast` | `Creature` via `[PreviousTypeName]` |
| A declared `object` holds `new object()` | `PolyObjectKept.bin` | one marker byte, no type tag, no payload | loaded as an `object`; the sentinel intact |
| Removed slot: nested generic | `PolyRemovedSlot.bin` | `object Slot` holding `List<List<int>>` | slot gone — every byte consumed, sentinel intact |
| Removed slot between two retained members | `PolyRemovedNested.bin` | `object Head`, `object Mid`, `uint Marker` | only `Mid` consumed; both neighbours intact |
| Removed slot whose payload is empty | `PolyRemovedObjectMid.bin` | `object Mid` holding `new object()` | one marker byte consumed; both neighbours intact |

The reader-side counterpart is `tests/Ceras.Test/Tests/HistoricalPolymorphicFixtureTests.cs`.

## The `System.Array`-slot fixtures (`V1ArraySlotModel.cs`)

A member declared as the abstract `System.Array` base class reaches the writer through
`ReferenceFormatter<Array>` rather than `ReferenceFormatter<object>`: a reference marker, the concrete
array type as a `Type` value, then that array's own payload. The bytes are the same grammar as the
polymorphic fixtures above, so they live in their own context (`HistoricalV1ArraySlot`) purely to keep
the other fixtures' ids still — and their crates end in the same fixed-width `uint` sentinel, for the
same reason. They exist because the interpreter did not follow this slot at all: it read the declared
type `System.Array` as an unrecognized well-known id, consumed nothing, and left the slot's bytes in
the stream. (The declared type `System.Array` is the well-known id `16`, which is *also* the `Array`
tag inside a `Type` value and the `Array` definition id of a `GenericInstantiation` entry — see
`Format/LAYOUT.md`, "A declared `System.Array` is that same slot, under a different declared-type id".)

| Aspect | Fixture | Old (v1) | Current |
| --- | --- | --- | --- |
| Rank-1 array in a `System.Array` slot | `ArraySlotInts.bin` | `int[]` (3 elements) behind a marker and a type value | rebuilt element by element; sentinel intact |
| Jagged array in a `System.Array` slot | `ArraySlotJagged.bin` | `int[][]` (3 rows, one empty) | every row rebuilt |
| Non-primitive element | `ArraySlotStrings.bin` | `string[]` | rebuilt from the payload string table |
| Empty array is not a null one | `ArraySlotEmpty.bin` | `int[0]`: marker, type value, count `0` | a zero-length array, not null |
| Null array is the marker alone | `ArraySlotNull.bin` | `null` | null; the marker is the whole value |
| Shared array in two slots | `ArraySlotShared.bin` | the same `int[]` twice | one instance; sentinel intact |
| Removed `System.Array` slot | `ArraySlotRemoved.bin` | `Array Slot` + `uint Marker` | slot gone — marker, type value, count and elements all consumed, sentinel intact |

The reader-side counterpart is `tests/Ceras.Test/Tests/SystemArraySlotMigrationTests.cs`, which also
carries the matching-layout controls (one set of bytes, read by both readers) and the reuse case, since
neither needs a cross-version file.

## The declared-array-shape fixtures (`V1ArrayShapeModel.cs`, `V1ArrayShapeSameModel.cs`)

A member declared as a concrete `T[]` and a member declared as `System.Array` or `object` do not start at
the same byte: the concrete array is written by `ArrayFormatter` as a bare biased element count, and the
polymorphic slot goes through a `ReferenceFormatter` and is preceded by a reference marker. A member whose
declared type changes between them therefore has no common reading, and the load is refused up front rather
than desynchronizing everything after the slot. The bytes below are what makes that claim checkable rather
than asserted: each one is a real old-build save, and the reader test walks its payload byte by byte.

**One context per rejected direction** (`HistoricalV1ArrayShapeToObject`, `…FromArray`, `…FromObject`,
`…ToList`). That is not cosmetic: the migration plans are built for every type the file's schema section
describes, and a rejected change aborts the whole load, so a context holding two rejected types could only
ever report whichever of them came first. The shapes that must keep loading live together in
`HistoricalV1ArrayShapeSame`, where nothing is rejected.

| Aspect | Fixture | Old (v1) | Current |
| --- | --- | --- | --- |
| Concrete array → polymorphic slot | `ArrayShapeToObject.bin` | `int[] Slot` — a bare biased count | `object Slot` — **refused** |
| Polymorphic slot → concrete array | `ArrayShapeFromArray.bin` | `Array Slot` — marker, then a `Type` value | `int[] Slot` — **refused** |
| …through the declared `object` | `ArrayShapeFromObject.bin` | `object Slot` — marker, then a `Type` value | `int[] Slot` — **refused** |
| Where the refusal stops | `ArrayShapeToList.bin` | `object Slot` | `List<int> Slot` — refused by the *existing* "no defined conversion" rule, not by the array one |
| Unchanged shape: array both sides | `ArrayShapeKept.bin` | `int[] Slot` (3 elements) | `int[] Slot` — loaded, sentinel intact |
| …with a null array | `ArrayShapeKeptNull.bin` | `int[] Slot` = `null`, the biased count `-1` | `int[] Slot` — null, not a marker |
| Removed array member | `ArrayShapeRemoved.bin` | `int[] Slot` + `uint Marker` | slot gone — count and elements consumed, sentinel intact |
| Jagged array in a polymorphic slot | `ArrayShapeJaggedSlot.bin` | `Array Slot` holding `int[][]` | still `Array Slot` — every row rebuilt |

The reader-side counterpart is `tests/Ceras.Test/Tests/ArrayShapeChangeMigrationTests.cs`, which also
carries the matching-layout controls and asserts that an allowed shape keeps the exact bytes and the exact
fingerprint the v1 writer gave it.

## The nested-argument fixtures (`V1NestedArrayModel.cs`)

The same change one level in: the member keeps its collection family on both sides and a **generic
argument** moves between a concrete array and a polymorphic slot. The member's own bytes are identical
either way — a `List<X>` member is a marker, a biased count and then the elements in both builds — so
nothing above the argument can see that anything moved. What can is the argument tree, because the schema
section records every closed generic as its own entry with ordered argument type ids. One context per
rejected direction here too, for the same reason as above.

| Aspect | Fixture | Old (v1) | Current |
| --- | --- | --- | --- |
| Element changed shape | `NestedListToObject.bin` | `List<int[]>` — each element a bare biased count | `List<object>` — **refused** |
| …the other direction | `NestedListFromObject.bin` | `List<object>` holding arrays — marker *and* a `Type` value | `List<int[]>` — **refused** |
| Only the value argument moves | `NestedDictToObject.bin` | `Dictionary<string, int[]>` | `Dictionary<string, object>` — **refused** |
| Two argument levels down | `NestedDeepToObject.bin` | `List<List<int[]>>` | `List<List<object>>` — **refused** |
| Argument is no longer the same type | `NestedWidenedList.bin` | `List<int>` | `List<long>` — **refused** (the reader's element coercion cannot be reached, so it is not an accidental feature) |
| …with a matching definition | `NestedListToArray.bin` | `List<List<int>>` | `List<int[]>` — **refused** |
| Only the key argument changed | `NestedDictKeyChange.bin` | `Dictionary<string, int>` | `Dictionary<int, int>` — **refused** |
| Argument tree unchanged | `NestedSameList.bin`, `NestedSameDict.bin` | `List<int[]>` / `Dictionary<string, int[]>` | the same — loads and reads back |
| A repeated array element | `NestedSameListShared.bin` | the same `int[]` in two elements | one instance after loading (the writer registers it) |
| Two levels, no array anywhere | `NestedSameNestedList.bin` | `List<List<int>>` | the same — loads and reads back |
| Renamed argument type | `NestedRenamedArgList.bin` | `List<Ping>` | `List<Pong>` via `[PreviousTypeName]` — loads and reads back |
| Removed member | `NestedRemovedList.bin` | `List<int[]>` + `uint Marker` | slot gone — count and elements consumed, sentinel intact |

The two refusals are told apart by their wording: the array-payload one is about a **reference marker
moving** between a declared array and a polymorphic slot, and the identity one about a position **not
naming the same current type**. `List<List<int>>` against `List<int[]>` is both, and is reported by the
more specific of the two. The only thing that satisfies the identity check is that both sides resolve to
the same current CLR type — which is why a renamed argument type is fine (an alias resolves to the very type
the current declaration has). A *direct* member's or an array element's widening is a different path
(a scalar is not a closed generic, an array is not a generic type in reflection) and stays supported.

The reader-side counterpart is `tests/Ceras.Test/Tests/ArrayShapeNestedArgumentMigrationTests.cs`. The
shared fixture plumbing both reader test files use lives in `tests/Ceras.Test/Base/HistoricalFixtureFiles.cs`.

## The custom-formatter fixtures (`V1OpaqueModel.cs`)

The two files written through a **user custom formatter**, in the two shapes a later build has to cope
with. They are in a context of their own (`HistoricalV1Opaque`) — the reason every fixture here has a
context of its own — and the formatter is the shape a real consumer uses: a lookup into a content
registry, so the loaded value is the registry-identical instance rather than a copy of a record. It writes
one fixed-width id, which is what makes the frame's declared-length check meaningful on the reader side.

| Aspect | Fixture | Old (v1) | Current |
| --- | --- | --- | --- |
| Member kept, same formatter | `OpaqueFrameHolder.bin` | `RecipeRef Target` behind a `marker, length, 4 bytes` frame | read through this build's formatter, which records the same name |
| Member removed | `OpaqueDroppedMemberHolder.bin` | the same frame, then `uint ASentinel` | slot gone — the frame accounted for by its declared length, sentinel intact |
| Where the refusal happens | both, with a mismatched formatter | — | a formatter this build does not have, or a differently *named* one, is refused before a byte is read (`AllowDroppingCustomPayloads` authorizes the drop) |

The sentinel is a member of the *same type* as the custom-formatted one, so it sorts after it in the
member order (member order is by member type name, and `System.Int32` sorts after the recipe type) — a
skip that is off by anything at all therefore shows up as a wrong number rather than as a load that
happens to succeed.

The reader-side counterpart is
`tests/Ceras.Test/Tests/OpaqueCustomPayloadFixtureTests.cs`, and the same-version behaviour (a round
trip, a swapped formatter, a dropped value) is in `tests/Ceras.Test/Tests/OpaqueCustomPayloadTests.cs`.

## The inherited-member fixtures (`V1InheritedMemberModel.cs`)

Two evolutions a game makes constantly, on a save where the affected type is instantiated more than once
and reached through a nested collection. Two contexts, for the reason every other pair here is split: the
migration plans are built for *every* type a file's schema section describes, so a single rejected type in
a shared context aborts the load of the file that has nothing to do with it.

| Aspect | Fixture | Old (v1) | Current |
| --- | --- | --- | --- |
| Inherited member renamed **and** widened | `InheritedWiden.bin` | `Unit.Power : int`, declared on a base the file never instantiates | `Stat.Rating : long` via `[PreviousName]`, on a base renamed by `[PreviousTypeName]` |
| Inherited member removed | `InheritedWiden.bin` | `Unit.Armour : int` | gone — the four bytes are skipped between the renamed one and the derived `Name` |
| Base type's own entry has to resolve | `InheritedWiden.bin` | an entry for `Unit` with no instance of it in the payload | resolved through the alias, or the load is refused before a byte is read |
| Widened member of a **struct element** | `InheritedWiden.bin` | `Ammo { int Count; int Kind; }` — 8 bytes inline | `Count` is a `long` — 12 bytes in this build, and the file's 8 are what the reader must use |
| …on the keyed position too | `InheritedWiden.bin` | `Dictionary<string, Ammo> Reserve` | both entries rebuilt, keys from the payload string table |
| Added field on an instantiated type | `InheritedWiden.bin` | v1 wrote no `Morale` | `Morale = 100` — the *declared* default, on all three references |
| Sharing across a rename, a widening and an added field | `InheritedWiden.bin` | `Leader` **is** `Squad[0]` | one instance; the second element is a back-reference in the file |
| Where the refusal happens | `InheritedNarrowRefused.bin` | `Weight { int Grams; int Kind; }` | `Grams` is a `short` — **refused** by name, before the payload |
| …in a position with no crate member to point at | `InheritedNarrowRefused.bin` | `Crate.Loads : List<Weight>` | the message names `Weight.Grams`, which is where the change is |

`InheritedNarrowRefused.bin` is a well-formed v5 save that this build's model of it cannot migrate, and the
sentinel at the end of each crate is what makes that checkable: a member whose elements were mis-decoded
shows up as a wrong number rather than as a load that happens to succeed.

The base type is a plain class on both sides, and that is deliberate rather than cosmetic: a base this build
declares **abstract** (or as an interface) reaches the manifest through a different registration — the
generator's field-less placeholder schema, `CerasFormatterGenerator.AppendMinimalRegistration` — so it is a
different identity question with its own file, `V1AbstractBaseModel.cs` and `AbstractBaseRename.bin`. That
registration used to pass `previousTypeNames: Array.Empty<string>()`, which dropped a user-declared
`[PreviousTypeName]` on an abstract or interface type; a renamed base therefore had no alias for a file to
resolve, `CerasRobustSerializer.BuildIdToType` handed the name to the configured `ITypeBinder` — which is
contracted to throw rather than answer null — and the load was refused before a payload byte. The alias is
now carried there, as it always was on a generated schema, and `[PreviousTypeName]` accepts an interface.

The reader-side counterpart is
`tests/Ceras.Test/Tests/HistoricalInheritedMemberFixtureTests.cs`.

## The abstract/interface base fixtures (`V1AbstractBaseModel.cs`)

`AbstractBaseRename.bin` is the file for the one identity question the model above deliberately does not
cover: a base type this build **renames and makes abstract** (`V1Abstract.Threat` → `Menace`), plus a second
base that is an **interface** on both sides and is renamed (`V1Abstract.ITargetable` → `ITarget`). Both are
ordinary refactors — "we finally made the base abstract", "the contract interface got a better name" — and
both reach a context's manifest through the field-less placeholder registration, which is where the rename
used to be dropped.

Its own context (`HistoricalV1Abstract`), for the reason every other model here has one: the new types must
not renumber any existing fixture.

| Aspect | Fixture | Old (v1) | Current |
| --- | --- | --- | --- |
| Renamed base turned `abstract` | `AbstractBaseRename.bin` | `V1Abstract.Threat`, a plain class | `Menace`, `abstract`, via `[PreviousTypeName]` |
| Renamed interface base | `AbstractBaseRename.bin` | `V1Abstract.ITargetable` | `ITarget`, an interface, via `[PreviousTypeName]` |
| A base's entry the payload never instantiates | `AbstractBaseRename.bin` | entries for `Threat` and `ITargetable`, with **no value of either type** in the payload | both resolve through their aliases, before a payload byte is read |
| Inherited member renamed **and** widened | `AbstractBaseRename.bin` | `Threat.HitPoints : int` | `Menace.Rating : long` via `[PreviousName]`, on the base |
| Inherited member removed | `AbstractBaseRename.bin` | `Threat.Faction : string` | gone — the bytes are skipped on the way to the derived member |
| Polymorphic slot through the abstract base | `AbstractBaseRename.bin` | `Battle.Leader` declared `Threat` | resolved to the concrete type the file recorded |
| …through the interface | `AbstractBaseRename.bin` | `Battle.Focus` declared `ITargetable` | resolved to `Warden` |
| Sharing across both bases | `AbstractBaseRename.bin` | `Leader` **is** `Wave[0]` | one instance; the second occurrence is a back-reference in the file |
| Added field on an instantiated type | `AbstractBaseRename.bin` | v1 wrote no `Dread` | `Dread = 100` — the *declared* default, on both references |

No value in the payload has either base's runtime type: every value is a `Wraith`, a `Golem` or a
`Sentinel`, and a polymorphic slot records the *concrete* type. Both bases are therefore in the file's
schema section — which records every type a context registers — and absent from its payload, so this file
is a pure identity test: it can only be read by a build that resolves two names it never sees a value for.

The reader-side counterpart is
`tests/Ceras.Test/Tests/HistoricalAbstractBaseFixtureTests.cs`.

## The positional fixtures (`V1PositionalModel.cs`)

The three structural value types the format describes **positionally** rather than by member name: a
`Nullable<T>`, a `KeyValuePair<K,V>` and the `Tuple`/`ValueTuple` families. The file records no name for
a nullable's value, a pair's key or value, or a tuple's items, so a graph that has to describe these
faithfully can only record positions. One context, `HistoricalV1Positional`, for the reason every other
family here gets its own: a context's manifest is fingerprinted as a whole.

| Aspect | Fixture |
| --- | --- |
| `Nullable<T>`: a presence byte, then the value only when it is there | `PositionalNullable.bin` |
| …over a primitive vs. over a struct (a whole member list, not a fixed width) | `PositionalNullable.bin` |
| A nullable as a collection element and as a dictionary value | `PositionalNullable.bin` |
| A null `List<int>` that is *not* a `Nullable<List<int>>` | `PositionalNullable.bin` |
| `KeyValuePair<K,V>`: a key and then a value, with no count in front of them | `PositionalKeyValuePair.bin` |
| …with *both* positions reference-typed, so each takes its own reference marker | `PositionalKeyValuePair.bin` |
| A null string key: string-table index 0 — an indexed null value, not a recorded null | `PositionalKeyValuePair.bin` |
| The family split: `ValueTuple` is a struct (inline, no slot), `Tuple` is a class (a slot) | `PositionalTuple.bin` |
| Sharing of a `Tuple` across a member and a collection element | `PositionalTuple.bin` |
| The control for that sharing: two *equal* `ValueTuple`s stay two distinct values | `PositionalValueTupleRepeat.bin` |
| A two-node cycle closing through a pair's key and through a tuple's item | `PositionalCycle.bin` |
| Both tuple families reached through a polymorphic slot | `PositionalPolyValueTuple.bin`, `PositionalPolyTuple.bin` |
| A tuple of eight, which the CLR spells as a seven-argument `ValueTuple` whose seventh item is a tuple | `PositionalTupleWithTail.bin` |

Every crate ends in a fixed-width `uint` sentinel written *after* the positional members, so a positional
read that is even one byte out shows up as a wrong number rather than as a load that happens to succeed.
Two properties of this family are worth stating explicitly, because they are what the file is for:

* **Member order here is the generator's, not the author's.** Nothing in these models pins a member
  order, so the bytes move whenever the writer's sort key changes. These eight fixtures were regenerated
  when `554a036` ("Unify generated and reflection member ordering by canonical type names") changed the
  sort key from a Roslyn display string to the canonical type name, which reorders every crate in this
  context — reverting only that key reproduces the previous bytes exactly. That is the honest reason
  they changed: the crate types sort by canonical name, and `List<int>`, `Span?` and `int?` sort
  differently under the two spellings. No other fixture in this directory moved, under either spelling.
* **The old-data graph does not depend on that order.** The reader-side counterpart,
  `tests/Ceras.Test/Tests/OldPositionalGraphTests.cs`, decodes these files through the graph builder,
  which reads each position in the order the file's own schema section records, so the same test suite
  passes against either member order. The fixtures still had to be regenerated: they are golden bytes
  of the real writer, and the real writer now writes the canonical order.

A cycle through a `Nullable<T>` is impossible and is not pinned: a `Nullable<T>` is a struct and takes no
slot at all.

`PositionalTupleWithTail.bin` is written through the **root** door, and the reason is coverage rather
than necessity. A tuple of more than seven elements is spelled by the CLR as a *seven*-argument type whose
seventh argument is itself a tuple, and the format's reserved id table stops at arity seven — so the tail
is not an eighth argument, it is item 6 holding a tuple of its own. Writing it as the root value puts that
value at the one position with no member list around it: the positional payload *is* the root, so the
decoder's root-position path is exercised over a positional shape rather than only its member and
element paths. `OldPositionalGraphTests.ATupleTailIsOneMorePositionAndNotAnEighthArgument` is the reader
side of that.

A declared **member** of the same type bakes fine, and did once the canonical-name lookup stopped
treating a name the CLR's parser refuses as a fatal error: `SchemaRegistry.ResolveClrName` answers such a
name with a miss so the canonical-name parser gets its turn, and a closed generic with three or more
arguments resolves that way. Before that change the member door really was closed — the bake handed the
name straight to `Type.GetType`, which *throws* rather than declining for a name whose argument list has
three or more entries — and the root was the only door left. That was a writer-side limitation, reported
rather than worked around here, and it is now gone.

## The constructor-argument fixtures (`V1CtorModel.cs`)

A type that has gained a `[CerasConstructor]` whose parameters come out of the payload, in its own context
(`HistoricalV1Ctor`). The rule these files exist for is that a `[CerasConstructor]` is a **construction
input, not an assignment**: the generated reader reads every member into a local, runs the constructor, and
then writes *all* of the locals back ("if it was a constructor parameter, it's already set (for classes).
But for consistency and to handle readonly fields… we write it anyway"), so a constructor that normalizes,
clamps, drops or reorders what it was handed cannot change the result. The migration interpreter used to
hold back the members a parameter fed, which made the same save load into two different objects depending on
which reader read it.

`Ledger`'s constructor **clamps** and `Cover`'s **drops** its argument, and the drop is the case a value
comparison cannot make: `Cover.Inner` is a *reference* the file shares with `LedgerBook.Main`, so the only
evidence that the stored value was put back is that the shared instance came back as the shared instance,
with the self-cycle through it intact. A reader that left the drop standing returned a load that reported
success with the graph's sharing broken.

The file has to be a real cross-version one, so the current model changes the schema in a way the
**fingerprint** notices: `Retired` is removed and `Label` is added. A rename alone would not have been
enough — the layout fingerprint hashes the member list and each member's name and type, but *not* its
`previousNames`, so a renamed member on its own fingerprints the same and would have taken the generated
reader. Its own context for the reason every other family here has one: a context's manifest is fingerprinted
and id-numbered as a whole, so a type added to a shared context would change the bytes of every fixture
already written through it. `HistoricalV1Ctor` is independent of `HistoricalV1Positional` above, so neither
family's fixtures can move the other's ids.

| Aspect | Fixture | Old (v1) | Current |
| --- | --- | --- | --- |
| Constructor that normalizes its argument | `CtorLedgerBook.bin`, `CtorLedgerRow.bin` | `Ledger.Amount = -5`, and v1's `Ledger` had **no** constructor at all | `Ledger(int amount)` clamps a negative value to `0` — the file's `-5` is what the object ends up with |
| Constructor that drops a *reference* argument | `CtorLedgerBook.bin` | `Cover.Inner` **is** `LedgerBook.Main` | `Cover(Ledger inner)` sets `Inner = null`; the shared instance is assigned back after the constructor returns |
| Constructor that rewrites a member it was *not* handed | `CtorLedgerBook.bin` | `Cover.Count = 3` | `Cover`'s constructor sets `Count = -1` — the file's `3` is what the object ends up with |
| Member renamed | both | `Ledger.Note` | `Memo` via `[PreviousName]`, *behind* the removed member |
| Member removed | both | `Ledger.Retired : int` | gone — its four bytes are skipped, and `Memo` behind them is the witness |
| Member added | both | v1 wrote no `Label` | `Label = "current"` — assigned by nothing, so it keeps the construction's value |
| Sharing across the constructor | `CtorLedgerBook.bin` | one ledger row in `Main`, in `Wrap.Inner`, and as its own `Twin` | one instance, all three references, cycle intact |
| The same, as a root | `CtorLedgerRow.bin` | the row on its own | the claim does not depend on the crate being read — the reason `PlayerCycle.bin` exists |

The reader-side counterpart is `tests/Ceras.Test/Tests/HistoricalCtorFixtureTests.cs`. The member shapes a
v1 file cannot carry — a `readonly` field, a get-only and an init-only auto-property, a private field behind
a private constructor — are the same-layout cross-check in
`tests/Ceras.Test/Tests/ConstructorRewriteMemberTests.cs`.

## Changing a fixture

1. Change the old model: `V1Model.cs` for the `HistoricalV1` context, `V1OpaqueModel.cs` for the
   `HistoricalV1Opaque` one, `V1PooledModel.cs` for the
   `HistoricalV1Pooled` one, `V1EnumNamesModel.cs` for the `HistoricalV1EnumNames` one,
   `V1PolyModel.cs` for the `HistoricalV1Poly` one, `V1ArraySlotModel.cs` for the
   `HistoricalV1ArraySlot` one, `V1ArrayShapeModel.cs` / `V1ArrayShapeSameModel.cs` for the
   declared-array-shape ones, `V1NestedArrayModel.cs` for the nested-argument ones,
   `V1AbstractBaseModel.cs` for the abstract/interface-base one,
   `V1CtorModel.cs` for the constructor-argument one,
   `V1PositionalModel.cs` for the `HistoricalV1Positional` one and
   `V1InheritedMemberModel.cs` for the inherited-member ones (two contexts). Each lives in its own context(s) on purpose: a context's manifest is
   fingerprinted as a whole, so adding a type to one of them would change the bytes of every fixture
   already written through it.
2. Change the current model in the matching reader test to match, keeping the `[PreviousName]` /
   `[PreviousTypeName]` / `[CerasStableName]` attributes the migration needs
   (`tests/Ceras.Test/Tests/HistoricalFixtureTests.cs` for the `HistoricalV1` fixtures,
   `HistoricalConstructionFixtureTests.cs` for the pooled ones, `EnumWireNameMigrationTests.cs` for
   `Loadout.bin`, `HistoricalPolymorphicFixtureTests.cs` for the polymorphic ones,
   `SystemArraySlotMigrationTests.cs` for the `System.Array`-slot ones,
   `ArrayShapeChangeMigrationTests.cs` for the declared-array-shape ones,
   `ArrayShapeNestedArgumentMigrationTests.cs` for the nested-argument ones,
   `HistoricalAbstractBaseFixtureTests.cs` for the abstract/interface-base one,
   `HistoricalCtorFixtureTests.cs` for the constructor-argument one,
   `OldPositionalGraphTests.cs` for the positional ones and
   `HistoricalInheritedMemberFixtureTests.cs` for the inherited-member ones).
3. Regenerate (command above) and review the byte diff.
4. Run `dotnet test tests/Ceras.Test -f net10.0`.
