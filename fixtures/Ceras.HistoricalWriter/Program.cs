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

namespace Ceras.HistoricalWriter
{
    static class Program
    {
        const string Context = "HistoricalV1";

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
        /// One serializer, so every fixture shares the same id space and the same fingerprint. The build
        /// timestamp is pinned to "unknown" so regenerating with unchanged inputs produces identical
        /// bytes: a fixture diff then always means a real change.
        /// </summary>
        static CerasRobustSerializer NewSerializer() => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Context);
            c.SetUnknownBuildTimestamp();
        });

        static IEnumerable<(string Name, byte[] Bytes)> Fixtures()
        {
            var serializer = NewSerializer();

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
    }
}
