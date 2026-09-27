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
so re-running with unchanged inputs produces identical bytes — verified by regenerating all six
fixtures and comparing SHA-256 against the checked-in files. **Every byte in the diff is a real
change to what old saves look like** — if a regeneration produces a diff you did not expect, that is
a finding, not noise. See the note on `Loadout.bin` below before regenerating that one.

All six fixtures are at format version 4. The five that predate the enum wire-name work were
regenerated when the format version advanced from 3 to 4 (a robust context now refuses a user custom
formatter, so a v3 file can hold opaque bytes that its own schema does not describe and that its
layout fingerprint cannot reveal), and `Loadout.bin` was re-stamped the same way when it joined
them. Each diff is exactly nine bytes: the version byte, the high half of the layout fingerprint
(which hashes the format version), and the CRC32 that covers them. No schema or payload byte changed.

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
| Three contexts, independent id spaces | all | `HistoricalV1`, `HistoricalV1Pooled`, `HistoricalV1EnumNames` | each model is a context of its own, so its ids cannot shift another context's fixtures |

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

## Changing a fixture

1. Change the old model: `V1Model.cs` for the `HistoricalV1` context, `V1PooledModel.cs` for the
   `HistoricalV1Pooled` one, `V1EnumNamesModel.cs` for the `HistoricalV1EnumNames` one. The three
   live in separate contexts on purpose, so adding a type to one of them cannot renumber the ids in
   the fixtures written through another.
2. Change the current model in the matching reader test to match, keeping the `[PreviousName]` /
   `[PreviousTypeName]` / `[CerasStableName]` attributes the migration needs
   (`tests/Ceras.Test/Tests/HistoricalFixtureTests.cs` for the `HistoricalV1` fixtures,
   `HistoricalConstructionFixtureTests.cs` for the pooled ones, `EnumWireNameMigrationTests.cs` for
   `Loadout.bin`).
3. Regenerate (command above) and review the byte diff.
4. Run `dotnet test tests/Ceras.Test -f net10.0`.
