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

    /// <summary>
    /// v1's <c>Mode.Removed</c> is gone here, so a file holding that value has no member to map it to.
    /// The raw number has to survive: an enum value that no longer has a name is still a number, and
    /// inventing a default would silently change game state.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1.Mode")]
    public enum Mode : int
    {
        Idle = 0,
        Legacy = 1,
    }

    /// <summary>
    /// A struct whose <c>Z</c> was removed. Pinning the skip of a removed field inside a *fixed-size*
    /// value: the bytes are a fixed layout, so a wrong skip corrupts everything after it rather than
    /// just losing a field.
    /// </summary>
    [CerasContext("HistoricalCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1.Coord")]
    public struct Coord
    {
        public int X;
        public int Y;
    }

    /// <summary>v1's settings type, renamed.</summary>
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

    /// <summary>v1's <c>Creature</c>, the base of a polymorphic slot.</summary>
    [CerasContext("HistoricalCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1.Creature")]
    public class Being
    {
        public int HitPoints;
    }

    /// <summary>v1's <c>Monster</c>: the concrete type of a polymorphic slot, renamed.</summary>
    [CerasContext("HistoricalCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1.Monster")]
    public class Monster : Being
    {
        public string Species;
    }

    /// <summary>Fixed-size values inside collections, including a nested collection and a keyed one.</summary>
    [CerasContext("HistoricalCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1.Inventory")]
    public class Inventory
    {
        public List<Coord> Slots;
        public Dictionary<string, Coord> Named;
        public List<List<int>> Groups;
    }

    [CerasContext("HistoricalCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1.SaveGame")]
    public class SaveGame
    {
        // Deliberately *not* in v1's declaration order. v1 wrote Version, Chapter, Players, ...; if the
        // reader followed this order instead of the file's schema, every field after the reordering point
        // would come out wrong. The file's order is the only one that counts.
        public int Version;
        public List<Actor> Players;

        [PreviousName("Main")]   // v1: Main was the *same instance* as Players[0]
        public Actor Main;

        [PreviousName("Stats")]  // v1's field name for what is now Options
        public GameSettings Options;

        public int[] Slots;
        public Dictionary<string, int> Scores;
        public Mode Difficulty;

        public string Chapter;   // v1 wrote this second; here it is declared last
        public Inventory Bag;
        public Being Owner;
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
            foreach (var name in new[] { "SaveGame.bin", "PlayerCycle.bin", "Inventory.bin" })
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
        public void Fields_Are_Read_In_The_Files_Order_Not_The_Current_Declaration_Order()
        {
            // v1 wrote Chapter second, between Version and Players. The current model declares it last, so
            // a reader that followed the current member order would take Players' value for Chapter and
            // desynchronise everything after it.
            var game = Reader().Load<SaveGame>(Fixture("SaveGame.bin"), 1);

            Assert.Equal(3, game.Version);
            Assert.Equal("chapter-2", game.Chapter);
            Assert.Equal(2, game.Players.Count);
            Assert.Equal("Ayla", game.Players[0].Name);
        }

        [Fact]
        public void A_Removed_Collection_Field_Leaves_The_FIELds_After_It_Aligned()
        {
            // v1 wrote, in this order, three fields the current model does not have - a string, a
            // fixed-size struct and a List<int> - and then Slots, Scores, Difficulty, Chapter, Bag and Owner.
            // A removed field is consumed rather than decoded, which is where the read/skip grammar has to
            // agree: each removal advances by a different amount (a string-table index; the struct's
            // declared fixed size rather than a field walk; a marker then a count then the elements), and
            // getting any of them wrong shifts everything after it. The Mono flag in the schema means "no
            // type tag", not "no reference marker" - the marker is still there for a reference-typed value.
            var game = Reader().Load<SaveGame>(Fixture("SaveGame.bin"), 1);

            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, game.Slots);
            Assert.Equal(100, game.Scores["Ayla"]);
            Assert.Equal(250, game.Scores["Bryn"]);
            Assert.Equal("chapter-2", game.Chapter);
            Assert.Equal(2, game.Players.Count);
        }

        [Fact]
        public void A_Value_Whose_Enum_Member_Was_Deleted_Keeps_Its_Number()
        {
            // v1 wrote Mode.Removed; the current enum has no such member. The number has to survive: an
            // enum value that lost its name is still that number, and defaulting it would silently change
            // game state.
            var game = Reader().Load<SaveGame>(Fixture("SaveGame.bin"), 1);

            Assert.Equal(2, (int)game.Difficulty);
        }

        [Fact]
        public void Fixed_Size_Values_Inside_Collections_Load_And_Skip_Removed_Fields()
        {
            var game = Reader().Load<SaveGame>(Fixture("SaveGame.bin"), 1);

            // v1's Coord had X, Y and Z; the current one dropped Z, so its bytes have to be skipped
            // inside a *fixed-width* layout - a wrong skip would corrupt the next coordinate.
            Assert.Equal(2, game.Bag.Slots.Count);
            Assert.Equal(1, game.Bag.Slots[0].X);
            Assert.Equal(2, game.Bag.Slots[0].Y);
            // The second coordinate is the real check: its bytes only line up if exactly Z's worth were
            // skipped for the first one.
            Assert.Equal(4, game.Bag.Slots[1].X);
            Assert.Equal(5, game.Bag.Slots[1].Y);

            Assert.Equal(7, game.Bag.Named["home"].X);
            Assert.Equal(8, game.Bag.Named["home"].Y);

            // A collection nested in a collection, including an empty inner one.
            Assert.Equal(3, game.Bag.Groups.Count);
            Assert.Equal(new[] { 1, 2 }, game.Bag.Groups[0]);
            Assert.Empty(game.Bag.Groups[1]);
            Assert.Equal(new[] { 3 }, game.Bag.Groups[2]);
        }

        [Fact]
        public void A_Polymorphic_Slot_Resolves_A_Renamed_Concrete_Type()
        {
            var game = Reader().Load<SaveGame>(Fixture("SaveGame.bin"), 1);

            // The file records v1's Monster; the current model calls it Monster too but through a
            // [PreviousTypeName] alias, and it derives from the renamed Being.
            var monster = Assert.IsType<Monster>(game.Owner);
            Assert.Equal(17, monster.HitPoints);
            Assert.Equal("slime", monster.Species);
        }

        [Fact]
        public void The_Inventory_Loads_As_A_Root_Of_Its_Own()
        {
            // Not every boundary needs the save root, so the nested-collection paths are pinned
            // independently too.
            var inventory = Reader().Load<Inventory>(Fixture("Inventory.bin"), 1);

            Assert.Equal(2, inventory.Slots.Count);
            Assert.Equal(1, inventory.Slots[0].X);
            Assert.Equal(3, inventory.Groups.Count);
            Assert.Equal(new[] { 1, 2 }, inventory.Groups[0]);
            Assert.Empty(inventory.Groups[1]);
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
