// The member order a *closed* generic's schema has to record.
//
// Every closure of a generic definition is written by one emitted class - the definition's - so the
// payload order is the order that definition's members sort in, computed from their **declared** types.
// A member declared as a type parameter therefore sorts under its parameter's name (`T Value` lands
// after `System.Int32 Tag`), while the same member of a closed instantiation sorts under the substituted
// type (`Legacy.Coord Value` lands before `System.Int32 Tag`). The per-closed schema used to list its
// fields in the closed order, so it described a layout no writer ever produces.
//
// Nothing notices while the layout matches, because the fast path reads no schema. On a mismatch the
// migration reader walks a file's fields in the order the file's schema lists them, so it read the wrong
// members: an int came from the neighbouring member, a reference member came back null, and a stream
// could run out of alignment entirely.
//
// The fix keeps the shared writer and changes only what the schema records: the per-closed field list is
// the definition's order, restricted to the members that closure has. Payload bytes are therefore
// unchanged; what changes is the schema section's field order - and with it the layout fingerprint - for
// the closures whose two orders differ. `Format/LAYOUT.md` has the rule and the cost.
//
// What is pinned here (the emitted code itself is pinned by GeneratedGenericSchemaOrderTests):
//   * the file's own schema section lists `Wrap<Coord>`'s fields in the order they are written, checked
//     against the first bytes of the payload rather than against a hard-coded list;
//   * the migration reader, on a real version-separated file: a v1 assembly that this build does not
//     reference wrote it, through a writer shared by every closure of its definition;
//   * the generated reader, on the fast path, so the ordering cannot quietly swap two members there;
//   * that a closure whose two orders already agree is untouched;
//   * that a wrong root is still rejected by name.
//
// The model and the readers for the v1 bytes are in GenericAliasFixture.
using System;
using System.Collections.Generic;
using System.Linq;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.GenericMemberOrder
{
    // The v2 side of the fixture lives in Ceras.Test.GenericAliasFixtures: v1's `Legacy.Wrap<T>` and
    // `Legacy.Sleeve` are its `Wrap<T>` and `Sleeve`.

    public class GenericMemberOrderTests : TestBase
    {
        const string OldWrap = "Legacy.Wrap`1<Legacy.Coord>";
        const string OldSleeve = "Legacy.Sleeve";

        static CerasRobustSerializer Reader() => GenericAliasFixture.Reader();
        static byte[] Fixture(string name) => GenericAliasFixture.Fixture(name);

        static string[] FieldNamesOf(OldTypeSchema schema)
            => schema.Fields.Select(f => f.StableName).ToArray();

        // ── The schema records the written order ─────────────────────────────────────────────────

        [Fact]
        public void The_File_Lists_A_Closed_Generic_Fields_In_The_Order_They_Are_Written()
        {
            var fileSchemas = GenericAliasFixture.ReadFileSchema("Wrap.bin");
            Assert.Contains(OldWrap, fileSchemas.Keys);

            // [Tag, Value] - the definition's order, because the writer emits `Tag` (an int, whose type
            // name sorts before the parameter name "T") before `Value`. The closed order would be
            // [Value, Tag], since "Legacy.Coord" sorts before "System.Int32".
            Assert.Equal(new[] { "Tag", "Value" }, FieldNamesOf(fileSchemas[OldWrap]));

            // … and that is what the bytes say, not just what the schema claims: the payload is the root's
            // type id, the root's reference marker, the `Wrapped` member's reference marker, and then
            // Tag = 42 as a fixed 4-byte int. Under the closed order these four bytes would be read as a
            // coordinate instead - and the coordinate that follows them read as a tag.
            byte[] payload = GenericAliasFixture.PayloadOf("Wrap.bin");
            Assert.Equal(0x87, payload[0]);                     // leb128 root id (135 = Legacy.Sleeve)
            Assert.Equal(0x01, payload[1]);
            Assert.Equal(2, payload[2]);                        // root reference marker
            Assert.Equal(2, payload[3]);                        // `Wrapped` member reference marker
            Assert.Equal(42, BitConverter.ToInt32(payload, 4)); // Tag, written first
            Assert.Equal(1, BitConverter.ToInt32(payload, 8));  // Value.X, written second
        }

        [Fact]
        public void A_Closure_Whose_Two_Orders_Already_Agree_Is_Untouched()
        {
            // A single-member generic cannot disagree with itself, so its schema is what it always was:
            // the fix only moves the closures where the orders actually differ.
            var fileSchemas = GenericAliasFixture.ReadFileSchema("Save.bin");

            Assert.Equal(new[] { "Value" }, FieldNamesOf(fileSchemas["Legacy.Box`1<Legacy.Coord>"]));
            Assert.Equal(new[] { "Payload" }, FieldNamesOf(fileSchemas["Legacy.Box`1+Cell`1<System.Int32,Legacy.Coord>"]));

            // The same on this side: the current manifest records the order its own writer emits, so a save
            // of the current model describes exactly what it wrote.
            var registry = Reader().Config.SchemaRegistry;
            registry.Seal();
            Assert.True(registry.TryGetInfo(typeof(GenericAliasFixtures.Wrap<GenericAliasFixtures.Coord>), out var info));
            Assert.Equal(new[] { "Added", "Tag", "Value" }, info.Fields.Select(f => f.StableName).ToArray());
        }

        // ── The migration reader, on a real version-separated file ───────────────────────────────

        [Fact]
        public void The_Migration_Reader_Reads_A_Renamed_Generic_Whose_Order_Depends_On_Its_Argument()
        {
            // v1 wrote `Legacy.Wrap<Legacy.Coord>` through the shared writer its whole definition shares
            // (Tag, then Value) and recorded those fields in that order; v2 renamed the type, kept both
            // members and added one, so the fingerprint differs and this can only be read through the
            // interpreter. The trailing sentinel is what a desynchronised read would not reach.
            var sleeve = Reader().Load<GenericAliasFixtures.Sleeve>(Fixture("Wrap.bin"), 1);

            Assert.Equal(42, sleeve.Wrapped.Tag);
            Assert.Equal(1, sleeve.Wrapped.Value.X);
            Assert.Equal(2, sleeve.Wrapped.Value.Y);
            Assert.Equal(0, sleeve.Wrapped.Added);      // the member v2 added
            Assert.Equal(GenericAliasFixture.Tail, sleeve.Tail);

            // The stored root is v1's `Legacy.Sleeve`, so the identity is still resolved and checked by
            // name - the two fixes are independent, and this is where they meet.
            Assert.Equal(typeof(GenericAliasFixtures.Sleeve), GenericAliasFixture.Resolve(OldSleeve));
        }

        [Fact]
        public void A_Wrong_Root_Is_Still_Rejected_By_Name()
        {
            // The order change must not have made the root check lenient: this file's root is v1's
            // `Legacy.Sleeve`, so asking for the generic itself has to be refused by name.
            var ex = Assert.Throws<CerasSchemaException>(
                () => Reader().Load<GenericAliasFixtures.Wrap<GenericAliasFixtures.Coord>>(Fixture("Wrap.bin"), 1));
            Assert.Contains("does not match the requested load type", ex.Message);
        }

        // ── The generated reader ─────────────────────────────────────────────────────────────────

        [Fact]
        public void The_Generated_Reader_Still_Reads_What_The_Generated_Writer_Wrote()
        {
            // The fast path never looks at a schema, so it is unaffected by the ordering change - which
            // is exactly why the ordering change stayed invisible until a migration. Pinned so the two
            // halves cannot drift apart unnoticed.
            var serializer = Reader();
            var sleeve = new GenericAliasFixtures.Sleeve
            {
                Wrapped = new GenericAliasFixtures.Wrap<GenericAliasFixtures.Coord>
                {
                    Value = new GenericAliasFixtures.Coord { X = 5, Y = 6 },
                    Tag = 43,
                    Added = 44,
                },
                Tail = GenericAliasFixture.Tail,
            };

            var bytes = serializer.Save(sleeve, 1);
            Assert.Equal(serializer.Config.SchemaRegistry.ComputeFingerprint(),
                BitConverter.ToUInt64(bytes, Ceras.RobustHeader.FingerprintOffset));

            var back = serializer.Load<GenericAliasFixtures.Sleeve>(bytes, 1);
            Assert.Equal(43, back.Wrapped.Tag);
            Assert.Equal(5, back.Wrapped.Value.X);
            Assert.Equal(6, back.Wrapped.Value.Y);
            Assert.Equal(44, back.Wrapped.Added);
            Assert.Equal(GenericAliasFixture.Tail, back.Tail);
        }
    }
}
