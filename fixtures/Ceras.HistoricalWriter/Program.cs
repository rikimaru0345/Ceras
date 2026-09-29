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
using Ceras.HistoricalWriter.V1ArrayShape.FromArray;
using Ceras.HistoricalWriter.V1ArrayShape.FromObject;
using Ceras.HistoricalWriter.V1ArrayShape.ToList;
using Ceras.HistoricalWriter.V1ArrayShape.ToObject;
using Ceras.HistoricalWriter.V1ArrayShapeSame;
using Ceras.HistoricalWriter.V1ArraySlot;
using Ceras.HistoricalWriter.V1Nested.DeepToObject;
using Ceras.HistoricalWriter.V1Nested.DictKeyChange;
using Ceras.HistoricalWriter.V1Nested.DeepArgChange;
using Ceras.HistoricalWriter.V1Nested.DictToObject;
using Ceras.HistoricalWriter.V1Nested.FromObject;
using Ceras.HistoricalWriter.V1Nested.ListToArray;
using Ceras.HistoricalWriter.V1Nested.RemovedArg;
using Ceras.HistoricalWriter.V1Nested.Same;
using Ceras.HistoricalWriter.V1Opaque;
using Ceras.HistoricalWriter.V1Nested.ToObject;
using Ceras.HistoricalWriter.V1Nested.Widened;
using Ceras.HistoricalWriter.V1EnumNames;
using Ceras.HistoricalWriter.V1Inherited;
using Ceras.HistoricalWriter.V1InheritedNarrow;
using Ceras.HistoricalWriter.V1Abstract;
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
        const string OpaqueContext = "HistoricalV1Opaque";
        const string ArraySlotContext = "HistoricalV1ArraySlot";

        // One context per rejected direction, plus one for the shapes that must keep loading — see
        // V1ArrayShapeModel.cs for why a rejected type needs a context of its own.
        const string ArrayShapeToObjectContext = "HistoricalV1ArrayShapeToObject";
        const string ArrayShapeFromArrayContext = "HistoricalV1ArrayShapeFromArray";
        const string ArrayShapeFromObjectContext = "HistoricalV1ArrayShapeFromObject";
        const string ArrayShapeToListContext = "HistoricalV1ArrayShapeToList";
        const string ArrayShapeSameContext = "HistoricalV1ArrayShapeSame";

        // One context per rejected argument-shape direction, plus one for the argument trees that must keep
        // loading — see V1NestedArrayModel.cs for why a rejected type needs a context of its own.
        const string NestedToObjectContext = "HistoricalV1NestedToObject";
        const string NestedFromObjectContext = "HistoricalV1NestedFromObject";
        const string NestedDictToObjectContext = "HistoricalV1NestedDictToObject";
        const string NestedDeepArgContext = "HistoricalV1NestedDeepArgChange";
        const string NestedDeepToObjectContext = "HistoricalV1NestedDeepToObject";
        const string NestedWidenedContext = "HistoricalV1NestedWidened";
        const string NestedListToArrayContext = "HistoricalV1NestedListToArray";
        const string NestedDictKeyChangeContext = "HistoricalV1NestedDictKeyChange";
        const string NestedRemovedArgContext = "HistoricalV1NestedRemovedArg";
        const string NestedSameContext = "HistoricalV1NestedSame";

        // A renamed *and* widened member declared on a base type, reached through a nested collection,
        // plus the fixed-size struct element whose width changed. The direction that has to be refused
        // lives in a context of its own, because the plans are built for every type a file's section
        // describes — see V1InheritedMemberModel.cs.
        const string InheritedContext = "HistoricalV1Inherited";
        const string InheritedNarrowContext = "HistoricalV1InheritedNarrow";

        // A base type that the current model makes *abstract* (or keeps an interface) and renames at the
        // same time — see V1AbstractBaseModel.cs for why that reaches the manifest through a different
        // registration than every other base here.
        const string AbstractContext = "HistoricalV1Abstract";

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

        /// <summary>
        /// The opaque context is the only one whose serializer carries a user custom formatter, because it
        /// is the only model whose values one owns. Everything else is a generated manifest, so the
        /// difference is visible in the fixture bytes: these files are the only ones whose schema section
        /// holds an <c>Opaque</c> entry, and the only ones whose payload contains a declared length in
        /// front of a value.
        /// </summary>
        static CerasRobustSerializer NewOpaqueSerializer() => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(OpaqueContext);
            c.SetUnknownBuildTimestamp();
            c.ConfigType<RecipeRef>().CustomFormatter = new RecipeRefFormatter();
        });

        static IEnumerable<(string Name, byte[] Bytes)> Fixtures()
        {
            foreach (var fixture in SaveGameFixtures()) yield return fixture;
            foreach (var fixture in PooledFixtures()) yield return fixture;
            foreach (var fixture in EnumNameFixtures()) yield return fixture;
            foreach (var fixture in PolymorphicFixtures()) yield return fixture;
            foreach (var fixture in ArraySlotFixtures()) yield return fixture;
            foreach (var fixture in ArrayShapeFixtures()) yield return fixture;
            foreach (var fixture in NestedArrayShapeFixtures()) yield return fixture;
            foreach (var fixture in InheritedMemberFixtures()) yield return fixture;
            foreach (var fixture in AbstractBaseFixtures()) yield return fixture;
            foreach (var fixture in OpaqueFixtures()) yield return fixture;
        }

        /// <summary>
        /// Two files over one model, in the two shapes a later build has to cope with.
        /// <list type="bullet">
        /// <item><c>OpaqueFrameHolder</c> keeps its custom-formatted member, so the current build reads
        /// the frame through the same formatter — the case that works, and the reason the format changed;</item>
        /// <item><c>OpaqueDroppedMemberHolder</c> loses it, so the frame's declared length is the only
        /// thing that can account for those bytes. A sentinel before the member in both, so a skip that is
        /// off by anything at all shows up as a wrong number.</item>
        /// </list>
        /// </summary>
        static IEnumerable<(string Name, byte[] Bytes)> OpaqueFixtures()
        {
            var serializer = NewOpaqueSerializer();

            yield return ("OpaqueFrameHolder.bin", serializer.Save(new FrameHolder
            {
                ASentinel = 0x0BADF00D,
                Target = new RecipeRef { RecipeId = RecipeRefFormatter.FirstRecipeId },
            }, 1));

            yield return ("OpaqueDroppedMemberHolder.bin", serializer.Save(new DroppedMemberHolder
            {
                ASentinel = 0x0BADF00E,
                Target = new RecipeRef { RecipeId = RecipeRefFormatter.FirstRecipeId + 1 },
            }, 1));
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

        /// <summary>
        /// The same polymorphic grammar reached through the <c>System.Array</c> base class instead of
        /// <c>object</c>. A member declared <c>System.Array</c> names no element type, so the writer
        /// reaches it through <c>ReferenceFormatter&lt;Array&gt;</c> and writes a reference marker, the
        /// concrete array type as a <c>Type</c> value (Array, element type) and then the array payload —
        /// byte for byte the grammar above, under a declared-type id (<c>System.Array</c> = 16) that is
        /// also the <c>Array</c> tag inside a type value and the <c>Array</c> definition id of a
        /// <c>GenericInstantiation</c> entry, and is a different identity from a member declared as a
        /// concrete <c>T[]</c> (a manifest entry written by <c>ArrayFormatter</c> with no marker at all).
        /// Its own context, its own trailing sentinel, and the same reason for it.
        /// </summary>
        static IEnumerable<(string Name, byte[] Bytes)> ArraySlotFixtures()
        {
            var serializer = NewSerializer(ArraySlotContext);
            const uint Sentinel = 0x5A5A5A5Au;

            // The ordinary case: a rank-1 array of primitives, behind a marker and a type value.
            yield return ("ArraySlotInts.bin", serializer.Save(new ArrayCrate
            {
                Slot = new[] { 10, 20, 30 },
                Marker = Sentinel,
            }, 1));

            // A jagged array: two levels of Array, each level behind its own reference marker, so the
            // element's declared type is an array in its own right. One row is empty and one row is a
            // different length, so a flatten-and-rebuild reader cannot pass by accident.
            yield return ("ArraySlotJagged.bin", serializer.Save(new ArrayCrate
            {
                Slot = new[] { new[] { 1, 2 }, new int[0], new[] { 3 } },
                Marker = Sentinel,
            }, 1));

            // A non-primitive element type: the element identity is in the type value, not in the slot,
            // so this pins that the reader takes the element type from the tag (and reads the elements
            // through the payload string table) rather than assuming the slot's own shape.
            yield return ("ArraySlotStrings.bin", serializer.Save(new ArrayCrate
            {
                Slot = new[] { "left", "right" },
                Marker = Sentinel,
            }, 1));

            // An empty array and a null array, which are different on the wire: a null is the reference
            // marker alone, an empty one is the marker, the type value and a count of zero.
            yield return ("ArraySlotEmpty.bin", serializer.Save(new ArrayCrate
            {
                Slot = new int[0],
                Marker = Sentinel,
            }, 1));
            yield return ("ArraySlotNull.bin", serializer.Save(new ArrayCrate
            {
                Slot = null,
                Marker = Sentinel,
            }, 1));

            // The same instance in two slots: written once and then as a back-reference, because the
            // reference wrapper registered it in the object cache. A reader that did not reserve the
            // slot the writer reserved would resolve the back-reference to whatever came first.
            var shared = new[] { 5, 6 };
            yield return ("ArraySlotShared.bin", serializer.Save(new ArrayPair
            {
                First = shared,
                Second = shared,
                Marker = Sentinel,
            }, 1));

            // The skip side: a slot the current model has removed. Its marker, its type value, its
            // element count and every element have to be consumed, and the sentinel after them has to
            // hold the written number.
            yield return ("ArraySlotRemoved.bin", serializer.Save(new ArrayEmptyCrate
            {
                Slot = new[] { 7, 8, 9 },
                Marker = Sentinel,
            }, 1));
        }

        /// <summary>
        /// A member whose declared type changes *between a concrete array and a polymorphic slot* — the two
        /// shapes whose reference markers differ, so neither reader can follow the file and the current model
        /// at once. Each crate keeps the same trailing sentinel as the slot fixtures above, which is what
        /// makes a load that ignored the change visible as a wrong number instead of a success.
        /// <para>
        /// A serializer per direction, because a rejected type needs a context of its own: the migration plans
        /// are built for every type the file describes, so a context holding two rejected types could only
        /// report whichever of them came first. See <c>V1ArrayShapeModel.cs</c>.
        /// </para>
        /// </summary>
        static IEnumerable<(string Name, byte[] Bytes)> ArrayShapeFixtures()
        {
            const uint Sentinel = 0xC3C3C3C3u;

            // ── Refused: the declared type changed shape, in both directions ─────────────────────────

            // Concrete array -> object. The file records a manifest `int[]` entry and writes a biased count
            // where the current model expects a reference marker.
            yield return ("ArrayShapeToObject.bin", NewSerializer(ArrayShapeToObjectContext).Save(
                new ConcreteArrayCrate { Slot = new[] { 4, 5, 6 }, Marker = Sentinel }, 1));

            // System.Array -> concrete array. Here the *file* has the marker and the count, and the current
            // member has only a count, so the marker is what a following reader would have to swallow. The
            // value is a plain rank-1 int[] so the refusal cannot be blamed on the shape.
            yield return ("ArrayShapeFromArray.bin", NewSerializer(ArrayShapeFromArrayContext).Save(
                new ArraySlotCrate { Slot = new[] { 7, 8 }, Marker = Sentinel }, 1));

            // The same, through the declared type `object`.
            yield return ("ArrayShapeFromObject.bin", NewSerializer(ArrayShapeFromObjectContext).Save(
                new ObjectSlotCrate { Slot = new[] { 9, 10, 11, 12 }, Marker = Sentinel }, 1));

            // ── Where the refusal stops ──────────────────────────────────────────────────────────────

            // object -> List<int>: a declared-type change with no array in it, so nothing about the
            // array-shape refusal applies. The reader test compares the two refusals side by side.
            yield return ("ArrayShapeToList.bin", NewSerializer(ArrayShapeToListContext).Save(
                new ObjectToListCrate { Slot = new List<int> { 6, 7 }, Marker = Sentinel }, 1));

            // ── Allowed: the array-ness is unchanged, so these still load ───────────────────────────

            var unchanged = NewSerializer(ArrayShapeSameContext);

            // Concrete array on both sides, across a type rename. A real array...
            yield return ("ArrayShapeKept.bin", unchanged.Save(
                new SameArrayCrate { Slot = new[] { 1, 2, 3 }, Marker = Sentinel }, 1));

            // ...and a null one, which on this grammar is the biased count -1 and *not* the reference marker
            // a polymorphic slot would use for null. A rule that keyed on "is the value an array" rather than
            // on the declared type would refuse this one.
            yield return ("ArrayShapeKeptNull.bin", unchanged.Save(
                new SameArrayCrate { Slot = null, Marker = Sentinel }, 1));

            // A concrete array member the current model removed: the skip path, which has no current
            // declaration to disagree with.
            yield return ("ArrayShapeRemoved.bin", unchanged.Save(
                new RemovedArrayCrate { Slot = new[] { 3, 1, 4 }, Marker = Sentinel }, 1));

            // A jagged array in a polymorphic slot, kept polymorphic in the current model: the shape that is
            // already supported and that the refusal must not reach.
            yield return ("ArrayShapeJaggedSlot.bin", unchanged.Save(
                new JaggedSlotCrate { Slot = new[] { new[] { 1, 2 }, new int[0], new[] { 3 } }, Marker = Sentinel }, 1));
        }
        /// <summary>
        /// The same array-payload shape change as <c>ArrayShapeFixtures</c>, one level in: the member keeps
        /// its collection family on both sides and the <b>argument</b> changes instead. The member's grammar
        /// is therefore identical in both builds — a biased count and the elements — so only the argument
        /// tree can tell that anything moved.
        /// </summary>
        static IEnumerable<(string Name, byte[] Bytes)> NestedArrayShapeFixtures()
        {
            const uint Sentinel = 0x3C3C3C3Cu;

            // ── Refused: the argument changed shape ──────────────────────────────────────────────────

            // List<int[]> -> List<object>: each element is a bare biased count here and a reference marker
            // plus a type value there. Two elements, so a per-element misread shows up in the sentinel.
            yield return ("NestedListToObject.bin", NewSerializer(NestedToObjectContext).Save(
                new ListOfArraysCrate { Slot = new List<int[]> { new[] { 1, 2 }, new[] { 3 } }, Marker = Sentinel }, 1));

            // The mirror: each element is a polymorphic slot here and a concrete array there.
            yield return ("NestedListFromObject.bin", NewSerializer(NestedFromObjectContext).Save(
                new ListOfObjectsCrate { Slot = new List<object> { new[] { 4, 5 }, new[] { 6, 7, 8 } }, Marker = Sentinel }, 1));

            // A keyed shape: only the value argument moves, so the key stays a plain string-table index and
            // pins that the walk is per argument position rather than per member.
            yield return ("NestedDictToObject.bin", NewSerializer(NestedDictToObjectContext).Save(
                new DictOfArraysCrate
                {
                    Slot = new Dictionary<string, int[]> { ["a"] = new[] { 9 }, ["b"] = new[] { 10, 11 } },
                    Marker = Sentinel,
                }, 1));

            // Two levels down: the outer list and the inner list are unchanged, and the array is the inner
            // list's element. A check that only compared the outermost argument would let this through.
            yield return ("NestedDeepToObject.bin", NewSerializer(NestedDeepToObjectContext).Save(
                new DeepListCrate
                {
                    Slot = new List<List<int[]>> { new List<int[]> { new[] { 12 } }, new List<int[]>() },
                    Marker = Sentinel,
                }, 1));

            // List<int> -> List<long>: same family, different argument type. The reader has an element
            // coercion written for it, but nothing can reach it (see the model's own note), so this is
            // refused rather than becoming an accidental migration feature.
            yield return ("NestedWidenedList.bin", NewSerializer(NestedWidenedContext).Save(
                new WidenedListCrate { Slot = new List<int> { 21, 22 }, Marker = Sentinel }, 1));

            // List<List<int>> -> List<int[]>: the definition matches, the single argument does not.
            yield return ("NestedListToArray.bin", NewSerializer(NestedListToArrayContext).Save(
                new ListOfListsCrate { Slot = new List<List<int>> { new List<int> { 31, 32 } }, Marker = Sentinel }, 1));

            // Dictionary<string,int> -> Dictionary<int,int>: argument 0 changed, argument 1 did not.
            yield return ("NestedDictKeyChange.bin", NewSerializer(NestedDictKeyChangeContext).Save(
                new DictKeyCrate { Slot = new Dictionary<string, int> { ["a"] = 51, ["b"] = 52 }, Marker = Sentinel }, 1));

            // List<List<int>> -> List<List<long>>: the outer argument is unchanged and only the inner one
            // moved, so this is what a check that stopped at the first argument level would let through.
            yield return ("NestedDeepArgChange.bin", NewSerializer(NestedDeepArgContext).Save(
                new DeepArgCrate { Slot = new List<List<int>> { new List<int> { 71, 72 } }, Marker = Sentinel }, 1));

            // ── Allowed: an argument type this build no longer has ──────────────────────────────────

            // List<Vanished> -> List<int>, where `Vanished` has no counterpart in the current model at all.
            // Not a schema change and not refused: the reader consumes a removed type structurally, so the
            // member's element becomes the current type's default and the sentinel after it still lines up.
            yield return ("NestedRemovedArg.bin", NewSerializer(NestedRemovedArgContext).Save(
                new RemovedArgCrate { Slot = new List<Vanished> { new Vanished { Beat = 61 } }, Marker = Sentinel }, 1));

            // ── Allowed: the argument tree is unchanged, or already migrates ─────────────────────────

            var same = NewSerializer(NestedSameContext);

            // The same list, unchanged, across a type rename.
            yield return ("NestedSameList.bin", same.Save(
                new SameListOfArraysCrate { Slot = new List<int[]> { new[] { 1, 2 }, new[] { 3 } }, Marker = Sentinel }, 1));

            // The same dictionary, unchanged.
            yield return ("NestedSameDict.bin", same.Save(
                new SameDictOfArraysCrate
                {
                    Slot = new Dictionary<string, int[]> { ["a"] = new[] { 9 }, ["b"] = new[] { 10, 11 } },
                    Marker = Sentinel,
                }, 1));

            // List<List<int>>, unchanged: two argument levels deep, and the one nested control that reads
            // its values back rather than only proving the guard stayed quiet.
            yield return ("NestedSameNestedList.bin", same.Save(
                new SameNestedListCrate { Slot = new List<List<int>> { new List<int> { 41, 42 }, new List<int>() }, Marker = Sentinel }, 1));

            // A renamed element type: List<Ping> -> List<Pong>, reached through [PreviousTypeName].
            yield return ("NestedRenamedArgList.bin", same.Save(
                new ListOfRenamedCrate { Slot = new List<Ping> { new Ping { Beat = 33 } }, Marker = Sentinel }, 1));

            // The same instance in two elements. Unlike a member declared as a concrete `T[]`, an element
            // goes through ReferenceFormatter, so the writer registers it: the second element is a
            // back-reference and the reader has to resolve it to the *same* array.
            var shared = new[] { 7, 8 };
            yield return ("NestedSameListShared.bin", same.Save(
                new SameListOfArraysCrate { Slot = new List<int[]> { shared, shared }, Marker = Sentinel }, 1));

            // A removed member: skipped by the file's own grammar, with no current argument tree to compare.
            yield return ("NestedRemovedList.bin", same.Save(
                new RemovedListOfArraysCrate { Slot = new List<int[]> { new[] { 5, 6 } }, Marker = Sentinel }, 1));
        }

        /// <summary>
        /// An **inherited** member that is renamed and widened, reached through a nested collection, and a
        /// fixed-size struct whose own member width changed while the struct is a collection element.
        /// <list type="bullet">
        /// <item><c>InheritedWiden</c> is the direction that has to keep working. v1's <c>Power</c> is an
        /// <c>int</c> declared on a <b>base type the file never instantiates</b>, so the reader has to
        /// resolve the base's own schema entry through a rename and coerce the value on every instance —
        /// of which there are three references to two objects, because <c>Leader</c> and <c>Squad[0]</c>
        /// were one instance. <c>Ammo.Count</c> is widened too, on the list and the keyed positions, so
        /// the declared byte size of the struct this build would write (12) disagrees with the file's (8)
        /// with no marker or length anywhere to recover from.</item>
        /// <item><c>InheritedNarrowRefused</c> is the direction that must be refused: the same nested
        /// position, with the struct's member narrowed to a <c>short</c>, which is not one of the
        /// conversions Ceras performs. It is a context of its own so its refusal cannot reach the file
        /// above.</item>
        /// </list>
        /// </summary>
        static IEnumerable<(string Name, byte[] Bytes)> InheritedMemberFixtures()
        {
            const uint RosterSentinel = 0x5EED5EEDu;
            const uint CrateSentinel = 0x0DDBA11u;

            // 0x12345678 and a negative, so the int -> long widening is observable in both directions and a
            // reader that read the wrong width or the wrong member would land on a different number.
            var leader = new Soldier { Name = "Ayla", Power = 0x12345678, Armour = 55 };
            var other = new Soldier { Name = "Bryn", Power = -9, Armour = 12 };

            yield return ("InheritedWiden.bin", NewSerializer(InheritedContext).Save(new Roster
            {
                Leader = leader,                                  // the same instance as Squad[0]
                Reserve = new Dictionary<string, Ammo>
                {
                    ["slugs"] = new Ammo { Count = 300, Kind = 1 },
                    ["shells"] = new Ammo { Count = 1200, Kind = 2 },
                },
                Loadout = new List<Ammo>
                {
                    new Ammo { Count = 120, Kind = 7 },
                    new Ammo { Count = -4, Kind = 9 },            // a negative value through the widening
                },
                Squad = new List<Soldier> { leader, other },
                Marker = RosterSentinel,
            }, 1));

            // The refusal side. The value is a real one: the point is that this file is a perfectly good
            // v5 save and this build's model of it is not migratable, and that says so before the payload.
            yield return ("InheritedNarrowRefused.bin", NewSerializer(InheritedNarrowContext).Save(new Crate
            {
                Loads = new List<Weight>
                {
                    new Weight { Grams = 1250, Kind = 1 },
                    new Weight { Grams = 7, Kind = 2 },
                },
                Marker = CrateSentinel,
            }, 1));
        }

        /// <summary>
        /// A save whose two base types are renamed in the current model, one of them turned <c>abstract</c>
        /// on the way and the other kept as an interface — see <c>V1AbstractBaseModel.cs</c>.
        /// <para>
        /// No value in the payload has either base's runtime type: every value is a <c>Wraith</c>, a
        /// <c>Golem</c> or a <c>Sentinel</c>, and the concrete type is what a polymorphic slot records. The
        /// bases are in the file's <em>schema section</em> and nowhere in the payload, which is the whole
        /// claim: this file can only be read by a build that can resolve two names it never sees a value
        /// for.
        /// </para>
        /// <para>
        /// The wraith is written twice (<c>Leader</c> and <c>Wave[0]</c>), so the file's second occurrence
        /// is a back-reference and a reader that rebuilt an object per slot would pass every value
        /// assertion below while losing the sharing.
        /// </para>
        /// </summary>
        static IEnumerable<(string Name, byte[] Bytes)> AbstractBaseFixtures()
        {
            const uint BattleSentinel = 0xAB57AB57u;

            var wraith = new Wraith { HitPoints = 0x01020304, Faction = "undead", Name = "Ayla" };
            var golem = new Golem { HitPoints = -7, Faction = "earth", Charge = 42 };

            yield return ("AbstractBaseRename.bin", NewSerializer(AbstractContext).Save(new Battle
            {
                Leader = wraith,
                Wave = new List<Threat> { wraith, golem },   // the first element is Leader, as a back-reference
                Focus = new Sentinel { Armour = 12 },
                Marker = BattleSentinel,
            }, 1));
        }
    }
}
