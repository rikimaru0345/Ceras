# Historical writer — checked-in golden fixtures

`Ceras.HistoricalWriter` holds the **v1** model of a save file and writes the byte-exact fixtures that
the cross-version tests in `tests/Ceras.Test/Tests/HistoricalFixtureTests.cs` load.

It is deliberately **not** referenced by `tests/Ceras.Test`. The v1 CLR types must be absent from the
reader build, so a reader test can only see the *bytes* plus the current model — which is what makes
these real cross-version tests rather than a second reader of the current schema.

## Regenerating (only when intentionally updating a fixture)

```sh
dotnet run --project fixtures/Ceras.HistoricalWriter -c Release -- --out tests/Ceras.Test/Fixtures/HistoricalV1
```

The output is deterministic: the build timestamp is pinned to "unknown", so re-running with unchanged
inputs produces identical bytes. **Every byte in the diff is a real change to what old saves look
like** — if a regeneration produces a diff you did not expect, that is a finding, not noise.

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
| Root other than the save | `PlayerCycle.bin` | `Player` as the root | loaded on its own |

## Changing a fixture

1. Change `V1Model.cs` (the old model).
2. Change the current model in `tests/Ceras.Test/Tests/HistoricalFixtureTests.cs` to match, keeping the
   `[PreviousName]` / `[PreviousTypeName]` aliases the migration needs.
3. Regenerate (command above) and review the byte diff.
4. Run `dotnet test tests/Ceras.Test -f net10.0`.
