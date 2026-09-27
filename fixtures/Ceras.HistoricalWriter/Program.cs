// Writes the checked-in golden fixtures for the cross-version tests.
//
// This is deliberately NOT a test project and is not referenced by tests/Ceras.Test: the old CLR types
// (Ceras.HistoricalWriter.V1.*) must be absent from the reader build, so a reader test can only ever
// see the bytes and the current model.
//
// Run it only when intentionally updating the fixtures:
//
//     dotnet run --project fixtures/Ceras.HistoricalWriter -c Release -- --out tests/Ceras.Test/Fixtures/HistoricalV1
//
// Then review the diff: every byte change is a change to what old saves look like.
using System;
using System.Collections.Generic;
using System.IO;
using Ceras;
using Ceras.Generated;
using Ceras.HistoricalWriter.V1;
using Ceras.HistoricalWriter.V1EnumNames;
using Ceras.HistoricalWriter.V1Poly;
using Ceras.HistoricalWriter.V1Pooled;

namespace Ceras.HistoricalWriter
{
    static class Program
    {
        const string Context = "HistoricalV1";
        const string PooledContext = "HistoricalV1Pooled";
        const string EnumNamesContext = "HistoricalV1EnumNames";
        const string PolyContext = "HistoricalV1Poly";

        static int Main(string[] args)
        {
            string outDir = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--out") outDir = args[i + 1];

            if (outDir == null)
            {
                Console.Error.WriteLine("usage: --out <directory>");
                return 2;
            }

            Directory.CreateDirectory(outDir);
            foreach (var (name, bytes) in Fixtures())
            {
                string path = Path.Combine(outDir, name);
                File.WriteAllBytes(path, bytes);
                Console.WriteLine($"wrote {path} ({bytes.Length} bytes, sha256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16]}…)");
            }
            return 0;
        }

        /// <summary>
        /// One serializer per context, so every fixture of a context shares that context's id space and
        /// fingerprint. The build timestamp is pinned to "unknown" so regenerating with unchanged inputs
        /// produces identical bytes: a fixture diff then always means a real change.
        /// </summary>
        static CerasRobustSerializer NewSerializer(string context) => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(context);
            c.SetUnknownBuildTimestamp();
        });

        static IEnumerable<(string Name, byte[] Bytes)> Fixtures()
        {
            foreach (var fixture in SaveGameFixtures()) yield return fixture;
            foreach (var fixture in PooledFixtures()) yield return fixture;
            foreach (var fixture in EnumNameFixtures()) yield return fixture;
            foreach (var fixture in PolymorphicFixtures()) yield return fixture;
        }

        static IEnumerable<(string Name, byte[] Bytes)> SaveGameFixtures()
        {
            var serializer = NewSerializer(Context);

            // A full save: a shared reference (Main is Players[0]), a two-node cycle (the buddies point
            // at each other), the removed/added/retyped fields, the renamed type and field, an unsigned
            // enum above long.MaxValue, an array and two collections.
            var main = new Player
            {
                Name = "Ayla",
                Health = 80,
                Mana = 1234,
                Rank = Rank.Max,
            };
            var buddy = new Player
            {
                Name = "Bryn",
                Health = 55,
                Mana = -7,             // a negative value, so the int -> long widening is observable
                Rank = Rank.Recruit,
            };
            main.Friend = buddy;
            buddy.Friend = main;      // the cycle closes here

            var game = new SaveGame
            {
                Version = 3,
                Chapter = "chapter-2",   // declared between Version and Players on purpose
                Players = new List<Player> { main, buddy },
                Main = main,            // same instance as Players[0]: must load back shared
                Stats = new Settings { Fullscreen = true, Volume = 42, Profile = "default" },
                RetiredText = "gone",                  // removed in the current model
                Home = new Coord { X = 20, Y = 21, Z = 22 },   // removed, and a fixed-size struct
                Retired = new List<int> { 9, 8, 7 },   // removed in the current model: a reference-typed collection
                Slots = new[] { 1, 2, 3, 4, 5 },     // the field right after it, so a bad skip is visible
                Scores = new Dictionary<string, int> { ["Ayla"] = 100, ["Bryn"] = 250 },
                Difficulty = Mode.Removed,   // a member the current model has deleted

                // Fixed-size values inside collections, including a nested collection and a keyed one.
                Bag = new Inventory
                {
                    Slots = new List<Coord> { new Coord { X = 1, Y = 2, Z = 3 }, new Coord { X = 4, Y = 5, Z = 6 } },
                    Named = new Dictionary<string, Coord> { ["home"] = new Coord { X = 7, Y = 8, Z = 9 } },
                    Groups = new List<List<int>> { new List<int> { 1, 2 }, new List<int>(), new List<int> { 3 } },
                },

                // A polymorphic slot whose concrete type the current model renames.
                Owner = new Monster { HitPoints = 17, Species = "slime" },
            };

            yield return ("SaveGame.bin", serializer.Save(game, 1));

            // The same inventory as a root of its own, so the nested-collection paths do not depend on
            // the save root being loadable.
            yield return ("Inventory.bin", serializer.Save(game.Bag, 1));

            // The same player graph as a root of its own, so a load is not coupled to the save root.
            yield return ("PlayerCycle.bin", serializer.Save(main, 1));
        }

        /// <summary>
        /// The types a loader obtains through a configured factory or pool, so the construction contract
        /// can be checked on a real cross-version file instead of only on a same-layout one. It is a
        /// separate context, so its type ids cannot shift the three fixtures above.
        /// </summary>
        static IEnumerable<(string Name, byte[] Bytes)> PooledFixtures()
        {
            var serializer = NewSerializer(PooledContext);

            var token = new Charm { Id = 2, Sigil = "sun" };
            var kept = new Relic { Id = 1, AncientName = "dust", Charge = 7, Owner = token };
            kept.Twin = kept;                                     // a self-cycle through the pooled type

            var vault = new RelicVault
            {
                Version = 4,
                Kept = kept,
                Token = token,                                      // the same instance as Kept.Owner
            };

            yield return ("RelicVault.bin", serializer.Save(vault, 1));

            // The same relic as a root of its own, so its polymorphic Owner slot is where the Charm is
            // *first* met — the reader then has to build a sealed type it only knows through System.Object.
            yield return ("Relic.bin", serializer.Save(kept, 1));
        }

        /// <summary>
        /// An enum whose members are renamed and renumbered in the current model. Every stored number is
        /// one the current enum does not have, so each value proves its member was matched by name
        /// rather than reinterpreted as whatever the raw number happens to be.
        /// </summary>
        static IEnumerable<(string Name, byte[] Bytes)> EnumNameFixtures()
        {
            var serializer = NewSerializer(EnumNamesContext);

            var loadout = new Loadout
            {
                Bronze = Tier.Bronze,   // the control: unchanged name, unchanged number
                Silver = Tier.Silver,   // renumbered only
                Ace = Tier.Ace,         // renumbered, recorded as the pinned wire name WireAce
                Adept = Tier.Adept,     // renamed in the current model
                Crown = Tier.Crown,     // renamed in the current model, recorded as WireCrown
                Tail = 99,
            };

            yield return ("Loadout.bin", serializer.Save(loadout, 1));
        }

        /// <summary>
        /// A closed built-in generic, an array or a tuple in an <c>object</c> slot. The writer records the
        /// concrete runtime type of such a value as a <c>System.Type</c> <em>value</em> — a well-known
        /// definition id followed by its argument type values, recursively — rather than as a schema type id,
        /// so these files are what the migration interpreter's polymorphic tag path has to read. Each crate
        /// carries a fixed-width trailing sentinel, so a slot that is mis-decoded shows up as a wrong number
        /// instead of as a load that happens to succeed. Its own context keeps their ids clear of the other
        /// fixtures'.
        /// </summary>
        static IEnumerable<(string Name, byte[] Bytes)> PolymorphicFixtures()
        {
            var serializer = NewSerializer(PolyContext);
            const uint Sentinel = 0xA5A5A5A5u;

            // Nested built-in generics: the argument of the outer List is itself a complete type value.
            yield return ("PolyNestedList.bin", serializer.Save(new PolyCrate
            {
                Slot = new List<List<int>> { new List<int> { 10, 20 }, new List<int>(), new List<int> { 30 } },
                Marker = Sentinel,
            }, 1));

            // A jagged array: two levels of Array, each level behind its own reference marker.
            yield return ("PolyJagged.bin", serializer.Save(new PolyCrate
            {
                Slot = new[] { new[] { 1, 2 }, new[] { 3 }, Array.Empty<int>() },
                Marker = Sentinel,
            }, 1));

            // A renamed user type as a nested *argument* of a built-in generic: the id in the type value has
            // to resolve to the current type through the file's own schema section.
            yield return ("PolyRenamedNested.bin", serializer.Save(new PolyCrate
            {
                Slot = new List<PolyNode>
                {
                    new PolyNode { X = 1, Y = 2, Label = "start" },
                    new PolyNode { X = 3, Y = 4, Label = "end" },
                },
                Marker = Sentinel,
            }, 1));

            // A queue of queues: the inner count is the plain fixed 4-byte element count, so this pins the
            // composed Queue/Stack count encoding at a *second* level of nesting.
            var queueOfQueues = new Queue<Queue<int>>();
            queueOfQueues.Enqueue(new Queue<int>(new[] { 7, 8 }));
            queueOfQueues.Enqueue(new Queue<int>());
            yield return ("PolyQueueOfQueue.bin", serializer.Save(new PolyCrate
            {
                Slot = queueOfQueues,
                Marker = Sentinel,
            }, 1));

            // A dictionary of jagged arrays: a payload that is neither "a count and then elements of one
            // kind" nor an array, so key/value entries whose values are themselves arrays behind markers.
            yield return ("PolyMixedShapes.bin", serializer.Save(new PolyCrate
            {
                Slot = new Dictionary<string, int[][]>
                {
                    ["row"] = new[] { new[] { 1, 2 }, new[] { 3 } },
                    ["col"] = Array.Empty<int[]>(),
                },
                Marker = Sentinel,
            }, 1));

            // The control: a value the file's schema section *does* describe, so its tag is a manifest id
            // and nothing about the built-in-generic path applies. It has to keep working unchanged.
            yield return ("PolyUserType.bin", serializer.Save(new PolyCrate
            {
                Slot = new PolyBeast { Hit = 17 },
                Marker = Sentinel,
            }, 1));

            // A bare `new object()`. Its runtime type *is* `System.Object`, so the writer takes the
            // same-type marker and writes **no type tag and no payload at all** — the value is one byte, and
            // the file is a two-byte `object` root. A reader that assumed a well-known declared identity
            // could not be a polymorphic value would refuse this perfectly good save, so both directions
            // are pinned: here it is read, and PolyRemovedObjectMid has it skipped.
            yield return ("PolyObjectKept.bin", serializer.Save(new PolyCrate
            {
                Slot = new object(),
                Marker = Sentinel,
            }, 1));

            // The skip side: the same shapes, in a slot the current model has removed.
            yield return ("PolyRemovedSlot.bin", serializer.Save(new PolyEmptyCrate
            {
                Slot = new List<List<int>> { new List<int> { 10, 20 }, new List<int>() },
                Marker = Sentinel,
            }, 1));

            // Removed *and* nested, between a retained ordinary class and the sentinel: the neighbours are
            // the witnesses that only the middle value's bytes were consumed.
            yield return ("PolyRemovedNested.bin", serializer.Save(new PolyMixedCrate
            {
                Head = new PolyBeast { Hit = 42 },
                Mid = new Dictionary<string, int[][]> { ["a"] = new[] { new[] { 5 } } },
                Marker = Sentinel,
            }, 1));

            // The same skip path with a payload that is *empty*: `object` has no members, so a member
            // removed from between two retained ones must leave both neighbours untouched having consumed
            // exactly the one marker byte.
            yield return ("PolyRemovedObjectMid.bin", serializer.Save(new PolyMixedCrate
            {
                Head = new PolyBeast { Hit = 7 },
                Mid = new object(),
                Marker = Sentinel,
            }, 1));
        }
    }
}
