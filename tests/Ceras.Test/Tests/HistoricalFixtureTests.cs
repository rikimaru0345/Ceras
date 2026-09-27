// Cross-version tests against checked-in golden files.
//
// The bytes come from a *separate historical writer* (fixtures/Ceras.HistoricalWriter) that holds the
// v1 CLR model. This assembly does not reference it, so the v1 types are absent at load time: what is
// exercised here is the real cross-version path — the file's own schema section is decoded and mapped
// onto the current model — rather than this version's interpreter reading this version's schema.
//
// The old type name and field names are matched through [PreviousTypeName] / [PreviousName], which is
// the supported way to evolve a save format.
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Generated;

namespace Ceras.Test.HistoricalFixtures
{
    // ── The current model ──────────────────────────────────────────────────────────────────────────
    // v1's `Rank.Max` (ulong.MaxValue) is now `Highest`; the value itself is unchanged. The enum
    // *type* was renamed too, which needs its own alias ([PreviousTypeName] allows enums).

    [PreviousTypeName("Ceras.HistoricalWriter.V1.Rank")]
    public enum Rank : ulong
    {
        Recruit = 0,

        [PreviousName("Max")]
        Highest = ulong.MaxValue,
    }

    /// <summary>v1 called this <c>Settings</c>.</summary>
    [CerasContext("HistoricalCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1.Settings")]
    public class GameSettings
    {
        public bool Fullscreen;
        public int Volume;
        public string Profile;
    }

    /// <summary>
    /// v1's <c>Player</c>: the type was renamed, <c>Health</c> was removed, <c>Mana</c> became
    /// <c>long</c>, and <c>Level</c> was added after it.
    /// </summary>
    [CerasContext("HistoricalCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1.Player")]
    public class Actor
    {
        public string Name;

        [PreviousName("Mana")]  // v1: int Mana → long Mana (a widening coercion)
        public long Mana;

        [PreviousName("Rank")]
        public Rank Rank;

        [PreviousName("Friend")]
        public Actor Buddy;

        public int Level;        // new in the current model: takes its declared default
    }

    [CerasContext("HistoricalCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1.SaveGame")]
    public class SaveGame
    {
        public int Version;
        public List<Actor> Players;

        [PreviousName("Main")]   // v1: Main was the *same instance* as Players[0]
        public Actor Main;

        [PreviousName("Stats")]  // v1's field name for what is now Options
        public GameSettings Options;

        public int[] Slots;
        public Dictionary<string, int> Scores;
    }

    /// <summary>
    /// Every fixture is pinned by a checked-in file, so a regression shows up as a value assertion
    /// failure rather than as "the interpreter happened to agree with itself".
    /// </summary>
    public class HistoricalFixtureTests : TestBase
    {
        const string Ctx = "HistoricalCurrent";

        static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1");

        static byte[] Fixture(string name)
        {
            string path = Path.Combine(FixtureDir, name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'. " +
                "Run fixtures/Ceras.HistoricalWriter to regenerate it (see its README.md).");
            return File.ReadAllBytes(path);
        }

        static CerasRobustSerializer Reader() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        [Fact]
        public void The_Fixtures_Exist_And_Are_Not_The_Current_Format()
        {
            // Sanity: the bytes really are an old file, so the migration path is what runs.
            foreach (var name in new[] { "SaveGame.bin", "PlayerCycle.bin" })
            {
                var bytes = Fixture(name);
                Assert.NotEmpty(bytes);
                var dump = CerasRobustSerializer.DumpSchema(bytes);
                Assert.Contains("Ceras.HistoricalWriter.V1", dump);   // the old stable names
                Assert.DoesNotContain("Ceras.Test.HistoricalFixtures", dump);
            }
        }

        [Fact]
        public void SaveGame_Loads_With_Renames_Retypes_And_Added_Fields()
        {
            var game = Reader().Load<SaveGame>(Fixture("SaveGame.bin"), 1);

            Assert.Equal(3, game.Version);
            Assert.Equal(2, game.Players.Count);
            Assert.Equal(2, game.Scores.Count);

            var ayla = game.Players[0];
            Assert.Equal("Ayla", ayla.Name);
            Assert.Equal(1234L, ayla.Mana);        // v1 wrote int 1234; read back as long
            Assert.Equal(Rank.Highest, ayla.Rank);  // the renamed member, value above long.MaxValue
            Assert.Equal(0, ayla.Level);            // the field the current model added

            // The removed field's bytes were skipped, so the fields *after* it still line up.
            var bryn = game.Players[1];
            Assert.Equal("Bryn", bryn.Name);
            Assert.Equal(-7L, bryn.Mana);           // a negative value survives the widening

            // The renamed type and field.
            Assert.True(game.Options.Fullscreen);
            Assert.Equal(42, game.Options.Volume);
            Assert.Equal("default", game.Options.Profile);

            // Collection evolution: the counts come from the file, not from the current defaults.
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, game.Slots);
            Assert.Equal(100, game.Scores["Ayla"]);
            Assert.Equal(250, game.Scores["Bryn"]);
        }

        [Fact]
        public void Shared_Reference_Stays_Shared_And_The_Cycle_Stays_Closed()
        {
            var game = Reader().Load<SaveGame>(Fixture("SaveGame.bin"), 1);

            // `Main` and `Players[0]` were the same instance in v1, so they are one object here.
            Assert.Same(game.Players[0], game.Main);

            // The two-node cycle is restored in both directions, not flattened.
            Assert.Same(game.Players[1], game.Players[0].Buddy);
            Assert.Same(game.Players[0], game.Players[1].Buddy);
            Assert.Equal("Ayla", game.Players[1].Buddy.Name);
        }

        [Fact]
        public void A_Player_Root_Loads_On_Its_Own()
        {
            // The root of this file is a v1 Player, which the current model calls Actor: the root
            // identity is resolved through the type's rename alias, not its numeric id.
            var actor = Reader().Load<Actor>(Fixture("PlayerCycle.bin"), 1);

            Assert.Equal("Ayla", actor.Name);
            Assert.Equal(1234L, actor.Mana);
            Assert.Equal(Rank.Highest, actor.Rank);
            Assert.Equal(0, actor.Level);
            Assert.Equal("Bryn", actor.Buddy.Name);
            Assert.Same(actor, actor.Buddy.Buddy);
        }

        [Fact]
        public void The_Fixtures_Really_Do_Not_Match_The_Current_Layout()
        {
            // Otherwise these tests would silently degrade into same-version round trips, which is
            // exactly what the historical writer exists to prevent.
            var saveGame = Reader();
            saveGame.Load<SaveGame>(Fixture("SaveGame.bin"), 1);   // seals, so the fingerprint is final
            Assert.NotEqual(saveGame.Config.SchemaRegistry.ComputeFingerprint(), FingerprintOf("SaveGame.bin"));

            var cycle = Reader();
            cycle.Load<Actor>(Fixture("PlayerCycle.bin"), 1);
            Assert.NotEqual(cycle.Config.SchemaRegistry.ComputeFingerprint(), FingerprintOf("PlayerCycle.bin"));
        }

        static ulong FingerprintOf(string name)
            => BitConverter.ToUInt64(Fixture(name), Ceras.RobustHeader.FingerprintOffset);

        [Fact]
        public void A_Migration_Is_Deterministic()
        {
            var first = Reader().Load<SaveGame>(Fixture("SaveGame.bin"), 1);
            var second = Reader().Load<SaveGame>(Fixture("SaveGame.bin"), 1);

            Assert.Equal("Ayla", first.Players[0].Name);
            Assert.Equal("Ayla", second.Players[0].Name);
            Assert.Same(first.Players[0], first.Main);
            Assert.Same(second.Players[0], second.Main);
        }

        [Fact]
        public void Flipping_The_Fingerprint_Of_A_Fixture_Changes_Nothing()
        {
            // TestBase.SlowPath forces the interpreter on a file whose layout already matches. These files
            // are *already* interpreted, so flipping the stored fingerprint is a no-op here — which is
            // itself a useful check that the migration does not depend on the fingerprint value.
            var straight = Reader().Load<SaveGame>(Fixture("SaveGame.bin"), 1);
            var flipped = Reader().Load<SaveGame>(SlowPath(Fixture("SaveGame.bin")), 1);

            Assert.Equal(straight.Version, flipped.Version);
            Assert.Equal(straight.Players[0].Name, flipped.Players[0].Name);
            Assert.Equal(straight.Players[0].Rank, flipped.Players[0].Rank);
            Assert.Same(flipped.Players[0], flipped.Main);
            Assert.Same(flipped.Players[0], flipped.Players[0].Buddy.Buddy);
        }
    }
}
