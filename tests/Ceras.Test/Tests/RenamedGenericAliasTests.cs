// Renamed *open* generics: `[PreviousTypeName("Legacy.Box`1")]` on a generic class.
//
// A save file names a type by its canonical identity, so a closed generic is named
// `Legacy.Box`1<System.Int32>` - never `Legacy.Box`1`, which is all an attribute argument on an open
// generic can say. The alias was indexed verbatim, so it never matched what the file actually contained
// and every save holding a renamed generic failed to migrate. Two places needed it: resolving a file's
// type ids onto the current model (CerasRobustSerializer.BuildIdToType, through SchemaRegistry), and
// naming the current root type as the file knows it (FileSchemaView.ResolveStableName) - the second one
// returned null and the load produced a default-constructed root instead of an error.
//
// The current model and the readers for the v1 bytes are in GenericAliasFixture; the v1 types
// themselves live in a separate writer assembly that this build does not reference, so this is the real
// cross-version path rather than this version's interpreter reading this version's schema.
//
// What is pinned here:
//   * the invariant that made the bug possible: the file names *closed* canonical names while the
//     current build records only the *open* alias, so the two strings differ by construction;
//   * the load, for every closure in the file - a plain argument, a second argument, a renamed
//     *argument* type, a different arity, a generic nested *inside* the renamed generic, a closure inside
//     a built-in collection, and a renamed generic behind an `object` member;
//   * the two names in that file whose text overlaps (`Legacy.Box`1<...>` and
//     `Legacy.Box`1+Cell`1<...>`), resolved to two different types by identity rather than by prefix, and
//     no id the file declares left unmapped;
//   * strictness, as theories over single-name files: a name that merely starts the same way resolves to
//     nothing (other arity, extended name, wrong argument count, truncated, other namespace, no arity, a
//     neighbour inside the arguments), while a closure and the shapes built on it still resolve;
//   * that a *nested closure* (`Legacy.Box`1<Legacy.Box`1<System.Int32>>`) resolves to the right type -
//     at the identity level, which is what this is about. Its *payload* is a separate matter: a closed
//     generic's members are written by the definition's shared formatter while the file's schema used to
//     record the order the *closure's* substituted members sort in. That is fixed in the generator and
//     pinned by GenericMemberOrderTests;
//   * that a root which is a *different* closure is still rejected by name, and that an alias two
//     definitions claim is refused at bake time rather than resolved by registration order.
using System;
using System.Collections.Generic;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.GenericAliasFixtures
{
    public class RenamedGenericAliasTests : TestBase
    {
        // v1's names, as the file spells them, and the open aliases the current build records instead.
        const string OldBoxInt = "Legacy.Box`1<System.Int32>";
        const string OldBoxString = "Legacy.Box`1<System.String>";
        const string OldBoxCoord = "Legacy.Box`1<Legacy.Coord>";
        const string OldCell = "Legacy.Box`1+Cell`1<System.Int32,Legacy.Coord>";
        const string OldCrate = "Legacy.Crate`2<System.Int32,System.String>";
        const string OldCoord = "Legacy.Coord";
        const string AliasBox = "Legacy.Box`1";

        static CerasRobustSerializer Reader() => GenericAliasFixture.Reader();
        static byte[] Fixture(string name) => GenericAliasFixture.Fixture(name);

        // ── The invariant ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void The_File_Names_Closed_Generics_While_The_Alias_Is_Open()
        {
            var fileSchemas = GenericAliasFixture.ReadFileSchema("Save.bin");

            // The file's names are closed canonical identities - the argument is part of the name.
            Assert.Contains(OldBoxInt, fileSchemas.Keys);
            Assert.Contains(OldBoxString, fileSchemas.Keys);
            Assert.Contains(OldBoxCoord, fileSchemas.Keys);

            // … and the alias an attribute can carry is only the open name, a *different* string. This
            // mismatch is the whole bug: no amount of exact dictionary lookup on the alias can find a file
            // that only ever writes the closed name. `Legacy.Box`1+Cell`1<…>` starts with it too, which is
            // why matching the text by prefix is not an option.
            Assert.NotEqual(AliasBox, OldBoxInt);
            Assert.StartsWith(AliasBox, OldBoxInt, StringComparison.Ordinal);
            Assert.StartsWith(AliasBox, OldCell, StringComparison.Ordinal);

            // The current manifest records the open alias, verbatim, for every closure.
            var registry = Reader().Config.SchemaRegistry;
            registry.Seal();
            Assert.True(registry.TryGetInfo(typeof(Box<int>), out var info));
            Assert.Contains(AliasBox, info.PreviousTypeNames);
            Assert.Equal("Ceras.Test.GenericAliasFixtures.Box`1<System.Int32>", info.StableName);
        }

        // ── The load ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_Renamed_Generic_Loads_Every_Closure_The_File_Holds()
        {
            var save = Reader().Load<Save>(Fixture("Save.bin"), 1);

            Assert.Equal(3, save.Version);
            Assert.Equal(GenericAliasFixture.Tail, save.Tail);   // last thing written: nothing desynchronised

            // A closure inside a built-in collection: the element's identity has to be rebuilt while the
            // collection is read.
            Assert.Equal(2, save.Ints.Count);
            Assert.Equal(11, save.Ints[0].Payload);
            Assert.Equal(22, save.Ints[1].Payload);

            // The same generic with another argument: a string closure must not be read as the int one.
            Assert.IsType<Box<string>>(save.Text);
            Assert.Equal("three", save.Text.Payload);

            // A renamed *argument* type inside a renamed generic: the argument's own alias has to resolve
            // while the closed name around it is being closed with it.
            Assert.IsType<Box<Coord>>(save.Point);
            Assert.Equal(5, save.Point.Payload.X);
            Assert.Equal(6, save.Point.Payload.Y);

            // A different arity, and both of its members renamed.
            Assert.Equal(8, save.Pair.Head);
            Assert.Equal("eight", save.Pair.Tail);
            Assert.Equal(0, save.Pair.Added);

            // A generic nested inside the renamed generic, with a renamed argument of its own.
            Assert.IsType<Box<int>.Cell<Coord>>(save.Cell);
            Assert.Equal(9, save.Cell.Content.X);
            Assert.Equal(10, save.Cell.Content.Y);

            // The same generic reached through an `object` member, i.e. a polymorphic type tag.
            Assert.IsType<Box<int>>(save.Slot);
            Assert.Equal(1000, ((Box<int>)save.Slot).Payload);

            // The ordinary, non-generic alias, on its own and as the argument above. v1's Z is gone, so
            // the fields after it inside the fixed-width struct still line up.
            Assert.Equal(11, save.Marker.X);
            Assert.Equal(12, save.Marker.Y);
        }

        [Fact]
        public void A_Renamed_Generic_As_A_Root_And_As_A_Collection_Root_Load()
        {
            // The root is the identity that has to match, so the rename is checked where it matters most:
            // `Legacy.Box`1<System.String>` read as `Box<string>`. Before the fix this returned a
            // default-constructed root, because nothing matched the file's name for it.
            var text = Reader().Load<Box<string>>(Fixture("TextBox.bin"), 1);
            Assert.NotNull(text);
            Assert.Equal("three", text.Payload);

            // The collection of closures as a root of its own, so the element identity is rebuilt without
            // the save root being involved.
            var boxes = Reader().Load<List<Box<int>>>(Fixture("IntBoxes.bin"), 1);
            Assert.Equal(2, boxes.Count);
            Assert.Equal(11, boxes[0].Payload);
            Assert.Equal(22, boxes[1].Payload);
        }

        [Fact]
        public void A_Root_That_Is_Another_Closure_Is_Rejected_By_Name()
        {
            // The stored root is v1's `Legacy.Box`1<System.String>`. Because the alias now resolves, the
            // rejection can name the *resolved* identity - which is the proof that resolution happened and
            // that the check is not merely seeing an unmapped id.
            var ex = Assert.Throws<CerasSchemaException>(() => Reader().Load<Save>(Fixture("TextBox.bin"), 1));
            Assert.Contains("does not match the requested load type", ex.Message);
            Assert.Contains("Ceras.Test.GenericAliasFixtures.Box`1<System.String>", ex.Message);

            // The other direction: a renamed closure cannot be read as the save root either.
            Assert.Throws<CerasSchemaException>(() => Reader().Load<Box<string>>(Fixture("Save.bin"), 1));
        }

        // ── Identity, through the file's own ids ────────────────────────────────────────────────

        /// <summary>
        /// The names the file declares, and the current type each one's id has to map to. Two of them start
        /// with the same text - `Legacy.Box`1&lt;…&gt;` and `Legacy.Box`1+Cell`1&lt;…&gt;` - and they are
        /// different definitions, which a prefix match cannot tell apart.
        /// </summary>
        static readonly (string FileName, string CurrentType)[] FileNames =
        {
            (OldBoxInt,    "Ceras.Test.GenericAliasFixtures.Box`1<System.Int32>"),
            (OldBoxString, "Ceras.Test.GenericAliasFixtures.Box`1<System.String>"),
            (OldBoxCoord,  "Ceras.Test.GenericAliasFixtures.Box`1<Ceras.Test.GenericAliasFixtures.Coord>"),
            (OldCell,      "Ceras.Test.GenericAliasFixtures.Box`1+Cell`1<System.Int32,Ceras.Test.GenericAliasFixtures.Coord>"),
            (OldCrate,     "Ceras.Test.GenericAliasFixtures.Crate`2<System.Int32,System.String>"),
            (OldCoord,     "Ceras.Test.GenericAliasFixtures.Coord"),
            ("Legacy.Save", "Ceras.Test.GenericAliasFixtures.Save"),
        };

        [Fact]
        public void Every_Type_Id_The_File_Declares_Maps_To_The_Current_Model()
        {
            var fileSchemas = GenericAliasFixture.ReadFileSchema("Save.bin");
            var idToType = GenericAliasFixture.IdToType(fileSchemas);

            foreach (var (fileName, currentType) in FileNames)
            {
                int id = fileSchemas[fileName].TypeId;
                Assert.Equal(currentType, GenericAliasFixture.Canonical(idToType, id));
            }

            // … and nothing the file names is left dangling: an unmapped id is how this bug presented
            // itself, as a member quietly left at its declared default.
            foreach (var schema in fileSchemas.Values)
                Assert.True(idToType.ContainsKey(schema.TypeId),
                    $"file type id {schema.TypeId} ('{schema.StableName}') did not resolve");
        }

        /// <summary>
        /// A closed name resolves to the current type it names, with its arguments preserved - including
        /// the shapes built on a closure (an array of one, a generic with an array argument) and a nested
        /// closure, where the argument is the renamed generic again and has to survive the round trip.
        /// </summary>
        [Theory]
        [InlineData("Legacy.Box`1<System.Int32>", "Ceras.Test.GenericAliasFixtures.Box`1<System.Int32>")]
        [InlineData("Legacy.Box`1<System.String>", "Ceras.Test.GenericAliasFixtures.Box`1<System.String>")]
        [InlineData("Legacy.Box`1<Legacy.Coord>", "Ceras.Test.GenericAliasFixtures.Box`1<Ceras.Test.GenericAliasFixtures.Coord>")]
        [InlineData("Legacy.Box`1<Legacy.Box`1<System.Int32>>", "Ceras.Test.GenericAliasFixtures.Box`1<Ceras.Test.GenericAliasFixtures.Box`1<System.Int32>>")]
        [InlineData("Legacy.Box`1+Cell`1<System.Int32,System.String>", "Ceras.Test.GenericAliasFixtures.Box`1+Cell`1<System.Int32,System.String>")]
        [InlineData("Legacy.Box`1<System.Int32>[]", "Ceras.Test.GenericAliasFixtures.Box`1<System.Int32>[]")]
        [InlineData("Legacy.Box`1<System.Int32[]>", "Ceras.Test.GenericAliasFixtures.Box`1<System.Int32[]>")]
        [InlineData("Legacy.Box`1<Legacy.Crate`2<System.Int32,System.String>>", "Ceras.Test.GenericAliasFixtures.Box`1<Ceras.Test.GenericAliasFixtures.Crate`2<System.Int32,System.String>>")]
        public void A_Closed_Name_Resolves_To_The_Type_It_Names(string fileName, string expected)
            => Assert.Equal(expected, GenericAliasFixture.IdResolvesTo(fileName));

        /// <summary>
        /// A file's schema section is untrusted text. Every name here is the open alias plus something, or
        /// a longer name that starts the same way, and none of them is a closure of the renamed generic - so
        /// none of them may resolve. A "does the name start with the alias" rule resolves all of them; so
        /// does anything that ignores the rest of the name, the argument count, or the namespace. One name
        /// per file, so no case can be satisfied by a neighbour.
        /// </summary>
        [Theory]
        [InlineData("Legacy.Box`10<System.Int32>")]                    // the same name with a different arity
        [InlineData("Legacy.Box`1Extra<System.Int32>")]                // a different type whose name extends it
        [InlineData("Legacy.Box`1<System.Int32,System.String>")]       // more arguments than the alias has
        [InlineData("Legacy.Box`1<System.Int32")]                      // truncated: the arguments never close
        [InlineData("Somewhere.Legacy.Box`1<System.Int32>")]           // another namespace that ends the same
        [InlineData("Legacy.Box")]                                     // the open name without its arity
        [InlineData("Legacy.Box`1<Legacy.Box`1Extra<System.Int32>>")]  // a neighbour *inside* the arguments
        [InlineData("Legacy.Box`1+Other`1<System.Int32>")]             // the nested shape without its arguments
        public void A_Name_That_Only_Starts_The_Same_Way_Resolves_To_Nothing(string fileName)
            => Assert.Equal("<unmapped>", GenericAliasFixture.IdResolvesTo(fileName));

        // ── Names on their own terms ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A name resolves to exactly what it names: an open alias to the whole family it stands for (not
        /// to whichever closure happened to register first, which every closure indexes), an ordinary alias
        /// to the renamed type, and a name nothing answers for to nothing at all.
        /// </summary>
        [Theory]
        [InlineData("Legacy.Box`1", "Ceras.Test.GenericAliasFixtures.Box`1")]                     // the family
        [InlineData("Legacy.Box`1+Cell`1", "Ceras.Test.GenericAliasFixtures.Box`1+Cell`1")]         // its nested one
        [InlineData("Legacy.Crate`2", "Ceras.Test.GenericAliasFixtures.Crate`2")]                // another arity
        [InlineData("Legacy.Coord", "Ceras.Test.GenericAliasFixtures.Coord")]                        // an ordinary alias
        [InlineData("Ceras.Test.GenericAliasFixtures.Coord", "Ceras.Test.GenericAliasFixtures.Coord")] // the current name
        [InlineData("Legacy.Coordinates", "<unmapped>")]                                             // only resembles it
        public void A_Name_Resolves_To_Exactly_What_It_Names(string name, string expected)
        {
            var resolved = GenericAliasFixture.Resolve(name);
            Assert.Equal(expected, resolved == null ? "<unmapped>" : TypeIdentity.GetCanonicalName(resolved));
        }

        // ── The current model ───────────────────────────────────────────────────────────────────

        [Fact]
        public void The_Current_Model_Still_Round_Trips_On_The_Fast_Path()
        {
            // The alias is resolution metadata: it must not change what a save of the *current* model
            // contains, and a matching fingerprint must still take the generated path.
            var serializer = Reader();
            var save = new Save
            {
                Version = 9,
                Ints = new List<Box<int>> { new Box<int> { Payload = 2, Added = 3 } },
                Text = new Box<string> { Payload = "four" },
                Point = new Box<Coord> { Payload = new Coord { X = 1, Y = 2 } },
                Pair = new Crate<int, string> { Head = 1, Tail = "one" },
                Cell = new Box<int>.Cell<Coord> { Content = new Coord { X = 2, Y = 3 } },
                Slot = new Box<int> { Payload = 10 },
                Marker = new Coord { X = 3, Y = 4 },
                Tail = GenericAliasFixture.Tail,
            };

            var bytes = serializer.Save(save, 1);
            Assert.Equal(serializer.Config.SchemaRegistry.ComputeFingerprint(),
                BitConverter.ToUInt64(bytes, Ceras.RobustHeader.FingerprintOffset));

            var back = serializer.Load<Save>(bytes, 1);
            Assert.Equal("four", back.Text.Payload);
            Assert.Equal(2, back.Ints[0].Payload);
            Assert.Equal(3, back.Ints[0].Added);
            Assert.Equal(2, back.Cell.Content.X);
            Assert.Equal(3, back.Cell.Content.Y);
            Assert.IsType<Box<int>>(back.Slot);
            Assert.Equal(GenericAliasFixture.Tail, back.Tail);
        }

        [Fact]
        public void An_Alias_Two_Different_Definitions_Claim_Is_Rejected_At_The_Bake()
        {
            // Structural matching is what makes an open alias a *name for a family*, and that only works
            // while the family is one definition. Two unrelated generics claiming the same open name would
            // otherwise be resolved by registration order - a silent mis-binding, on a save that is
            // otherwise fine, visible only as a wrong value much later. The bake is where every alias is
            // known, so that is where it is refused.
            var registry = new CerasRobustSerializer(c => c.UseGeneratedFormatters("GenericAliasAmbiguous"))
                .Config.SchemaRegistry;

            var ex = Assert.Throws<CerasSchemaException>(() => registry.Seal());

            Assert.Contains("Legacy.Box`1", ex.Message);
            Assert.Contains("Ceras.Test.GenericAliasAmbiguity.First`1<System.Int32>", ex.Message);
            Assert.Contains("Ceras.Test.GenericAliasAmbiguity.Second`1<System.String>", ex.Message);
        }
    }
}
