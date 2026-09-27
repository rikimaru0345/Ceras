# Historical writer — checked-in golden fixtures

`Ceras.HistoricalWriter` holds the **v1** models of a save file and writes the byte-exact fixtures that
the cross-version tests load — `tests/Ceras.Test/Tests/HistoricalFixtureTests.cs` for the
`HistoricalV1` context and `HistoricalConstructionFixtureTests.cs` for the `HistoricalV1Pooled`
one.

It is deliberately **not** referenced by `tests/Ceras.Test`. The v1 CLR types must be absent from the
reader build, so a reader test can only see the *bytes* plus the current model — which is what makes
these real cross-version tests rather than a second reader of the current schema.

## Regenerating (only when intentionally updating a fixture)

```sh
dotnet run --project fixtures/Ceras.HistoricalWriter -c Release -- --out tests/Ceras.Test/Fixtures/HistoricalV1
```

The output is deterministic: the build timestamp is pinned to "unknown" (`SetUnknownBuildTimestamp`),
so re-running with unchanged inputs produces identical bytes — verified by regenerating all five
fixtures and comparing SHA-256 against the checked-in files. **Every byte in the diff is a real
change to what old saves look like** — if a regeneration produces a diff you did not expect, that is
a finding, not noise.

All five fixtures were regenerated when the format version advanced from 3 to 4 (a robust context
now refuses a user custom formatter, so a v3 file can hold opaque bytes that its own schema does not
describe and that its layout fingerprint cannot reveal). Each diff is exactly nine bytes: the version
byte, the high half of the layout fingerprint (which hashes the format version), and the CRC32 that
covers them. No schema or payload byte changed.

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
| Two contexts, independent id spaces | all | `HistoricalV1` and `HistoricalV1Pooled` | the pooled model is a separate context, so its ids cannot shift the other three |

## Changing a fixture

1. Change the old model: `V1Model.cs` for the `HistoricalV1` context, `V1PooledModel.cs` for the
   `HistoricalV1Pooled` one. The two live in separate contexts on purpose, so adding a type to the
   pooled model cannot renumber the ids in the other three fixtures.
2. Change the current model in the matching reader test to match, keeping the `[PreviousName]` /
   `[PreviousTypeName]` aliases the migration needs.
3. Regenerate (command above) and review the byte diff.
4. Run `dotnet test tests/Ceras.Test -f net10.0`.
