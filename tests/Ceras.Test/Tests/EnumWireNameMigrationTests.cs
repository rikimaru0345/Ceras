// The name an enum member is *identified* by across a migration, and the file that proved it did not work.
//
// An enum value travels through a save as a raw number; the schema records one name per member, and
// that name is what the migration matches on. Three spellings can be involved and they have to agree:
//   * the C# member name,
//   * the wire name — [CerasStableName] when the member has one, the C# name when it does not,
//   * the [PreviousName] aliases of a member that was renamed without a wire name.
// The writer and the migration used to disagree: the generated schema (and the runtime-derived one for
// a declared/blacklisted enum) recorded the C# member name while MigrationPlanBuilder.BuildEnumMap
// matched the wire name. A member pinned with [CerasStableName] therefore matched nothing at all, and
// because a renumbered value is not an error but an unnamed number in the current enum, a save came
// back holding a plausible, wrong value.
//
// The members below cover the three ways a member can be identified across a migration, each pinned by a
// number the current enum does not have, so a member that fails to match cannot be mistaken for one
// that matched:
//
//   v1 Tier                        current Tier                     matched by
//   ─────────                      ───────────                     ──────────
//   Bronze = 0                     Bronze = 0                      nothing changed (control)
//   Silver = 1                     Silver = 6                      the name (no attribute: it *is* the wire name)
//   [WireAce] Ace = 2              [WireAce] Ace = 7               the wire name
//   Adept = 3                      [PreviousName("Adept")] Veteran = 5  an explicit alias
//   [WireCrown] Crown = 4          [WireCrown] Scepter = 9        the wire name
//
// No v1 number is a current number, so every value below is decisive: a member that failed to match
// would arrive as an unnamed number the current enum does not declare.
//
// The cross-version half is the checked-in fixture Loadout.bin, written by the separate compilation in
// fixtures/Ceras.HistoricalWriter (so none of the v1 CLR types exist here). The C#-name half — a file
// written *before* the schema recorded the wire name, which recorded the C# name instead — cannot come
// out of today's writer, so it is built here from a hand-authored old manifest through the real schema
// encoder, exactly like EnumBitsTests does for a renamed unsigned member. And the last case states the
// limit rather than hiding it: a member renamed in C# *and* pinned to a new wire name is not reachable
// from a file that recorded the old C# name, because nothing in such a file records the wire name.
//
// The runtime-declared half of the fix (SchemaRegistry.BuildDeclaredSchema, for a member-only enum the
// generator has no entry for) is pinned in the same file, so the two schema paths cannot drift.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Xunit;

// A namespace the generator must not generate formatters for, so the two enums below have no generated
// schema and the runtime derives their entries from CLR metadata instead. This is the engine-type case:
// a member type the model does not own, served without a generated formatter.
[assembly: CerasGeneratorBlacklist("Ceras.Test.RuntimeEnumSchema*")]

namespace Ceras.Test.RuntimeEnumSchema
{
    /// <summary>
    /// A member-only enum, so nothing registers it: the bake derives the schema, and the recorded name
    /// has to be the wire name for the same reason the generated one does.
    /// </summary>
    public enum Ladder
    {
        Rung = 0,
        [CerasStableName("WireTop")]
        Top = 3,
    }

    [CerasContext("RuntimeEnumSchema")]
    [CerasSerializable]
    public class Rungs
    {
        public Ladder Steps;
        public int Tail;
    }

    /// <summary>Two members that would be recorded under one name, which the schema cannot express.</summary>
    public enum Climb
    {
        [CerasStableName("SameStep")]
        Up = 1,
        [CerasStableName("SameStep")]
        Down = 2,
    }

    // Its own context, so the duplicate above is only reached when *this* context is baked: a context's
    // manifest is finite, and every type in it is walked by the same bake.
    [CerasContext("RuntimeEnumSchemaClash")]
    [CerasSerializable]
    public class Climbs
    {
        public Climb Steps;
    }
}

namespace Ceras.Test.EnumWireNames
{
    // ── The current model ────────────────────────────────────────────────────────────────────────────
    // The type was renamed, and so were two of its members, with every stored number renumbered to a
    // value the current enum does not declare.

    [PreviousTypeName("Ceras.HistoricalWriter.V1EnumNames.Tier")]
    public enum Tier : int
    {
        Bronze = 0,

        /// <summary>v1: 1. Renumbered only — matched by the C# name, which is also the wire name.</summary>
        Silver = 6,

        /// <summary>
        /// v1: <c>[CerasStableName("WireAce")] Ace = 2</c>. Renumbered, and the wire name is what the
        /// file records. The C# name is kept unchanged on purpose: that is what makes this member
        /// readable from the files written before the schema recorded the wire name, which said "Ace".
        /// </summary>
        [CerasStableName("WireAce")]
        Ace = 7,

        /// <summary>
        /// v1: <c>Adept = 3</c>. Renamed with no wire name anywhere, so the alias is the only way in.
        /// </summary>
        [PreviousName("Adept")]
        Veteran = 5,

        /// <summary>v1: <c>[CerasStableName("WireCrown")] Crown = 4</c>. Renamed, wire name kept.</summary>
        [CerasStableName("WireCrown")]
        Scepter = 9,
    }

    [CerasContext("HistoricalCurrentEnumNames")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1EnumNames.Loadout")]
    public class Loadout
    {
        // The field names are deliberately untouched, so the only thing migrating is the enum members.
        public Tier Bronze;
        public Tier Silver;
        public Tier Ace;
        public Tier Adept;
        public Tier Crown;

        /// <summary>After every enum member, so a value read from the wrong place is visible here.</summary>
        public int Tail;
    }

    public class EnumWireNameMigrationTests : TestBase
    {
        const string Ctx = "HistoricalCurrentEnumNames";

        static CerasRobustSerializer Reader() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1");

        static byte[] Fixture(string name)
        {
            string path = Path.Combine(FixtureDir, name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'. " +
                "Run fixtures/Ceras.HistoricalWriter to regenerate it (see its README.md).");
            return File.ReadAllBytes(path);
        }

        /// <summary>
        /// The <c>name = value</c> lines of every ENUM block in a schema dump, trimmed. Exact line
        /// comparison, because one name is a suffix of another here ("WireAce" ends in "Ace") and a
        /// substring assertion would pass either way.
        /// </summary>
        static string[] EnumEntries(string dump) => dump
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains(" = ") && !line.Contains('['))
            .ToArray();

        // ── The cross-version fixture ───────────────────────────────────────────────────────────────────

        [Fact]
        public void The_Fixture_Records_The_Wire_Name_And_Not_The_CSharp_Name()
        {
            var entries = EnumEntries(CerasRobustSerializer.DumpSchema(Fixture("Loadout.bin")));

            // The two members pinned with [CerasStableName] are recorded under it...
            Assert.Contains("WireAce = 2", entries);
            Assert.Contains("WireCrown = 4", entries);
            // ... and neither C# spelling of the current model is in the file: the current model renames
            // Crown to Scepter, and "Ace" here is a *different* member's name, not this one's alias.
            Assert.DoesNotContain("Crown = 4", entries);
            Assert.DoesNotContain("Ace = 2", entries);
            // The control: a member with no attribute is recorded under its C# name, as before.
            Assert.Contains("Bronze = 0", entries);
            Assert.Contains("Silver = 1", entries);
            Assert.Contains("Adept = 3", entries);
        }

        [Fact]
        public void The_Fixture_Is_Not_The_Current_Layout()
        {
            // Otherwise the tests below would be same-version round trips.
            var reader = Reader();
            reader.Load<Loadout>(Fixture("Loadout.bin"), 1);   // seals, so the fingerprint is final
            var fileFingerprint = BitConverter.ToUInt64(Fixture("Loadout.bin"), RobustHeader.FingerprintOffset);
            Assert.NotEqual(reader.Config.SchemaRegistry.ComputeFingerprint(), fileFingerprint);
        }

        [Fact]
        public void Every_Member_Of_The_Renumbered_Enum_Migrates_To_Its_Own_Number()
        {
            var loadout = Reader().Load<Loadout>(Fixture("Loadout.bin"), 1);

            // One assertion per mechanism, and every one of them is decisive: no v1 number (0, 1, 2, 3, 4)
            // is a number this enum declares (0, 6, 7, 5, 9), so a member that failed to match would
            // arrive as an unnamed number rather than as the value asserted here.
            Assert.Equal(Tier.Bronze, loadout.Bronze);   // unchanged: the control
            Assert.Equal(Tier.Silver, loadout.Silver);   // 1 -> 6, matched by the C#/wire name
            Assert.Equal(Tier.Ace, loadout.Ace);         // 2 -> 7, matched by [CerasStableName]
            Assert.Equal(Tier.Veteran, loadout.Adept);   // 3 -> 5, matched by [PreviousName]
            Assert.Equal(Tier.Scepter, loadout.Crown);  // 4 -> 9, matched by [CerasStableName]

            // …and the number each one arrived as is the current one, not v1's.
            Assert.Equal(0, (int)loadout.Bronze);
            Assert.Equal(6, (int)loadout.Silver);
            Assert.Equal(7, (int)loadout.Ace);
            Assert.Equal(5, (int)loadout.Adept);
            Assert.Equal(9, (int)loadout.Crown);
        }

        [Fact]
        public void The_Field_After_The_Enum_Members_Still_Lines_Up()
        {
            // Nothing about the members' width or order may change: only the numbers they resolve to.
            Assert.Equal(99, Reader().Load<Loadout>(Fixture("Loadout.bin"), 1).Tail);
        }

        // ── The generation that recorded the C# name ──────────────────────────────────────────────────

        [Fact]
        public void A_File_That_Recorded_The_CSharp_Name_Still_Migrates_Without_An_Alias()
        {
            // This is the regression the whole change exists for. Today's writer records "WireAce", so the
            // file that says "Ace" cannot be produced by it: a build before the schema recorded the wire
            // name wrote the C# member name. Everything else in this file is byte-for-byte what such a
            // writer produced, because the recorded names are the only difference between the two
            // generations.
            var loadout = Reader().Load<Loadout>(
                FileRecordingTheCSharpNames(adept: 3, ace: 2, crown: 12), 1);

            // "Ace" matched the current C# member, whose wire name is "WireAce": 2 -> 7.
            Assert.Equal(Tier.Ace, loadout.Ace);
            Assert.Equal(7, (int)loadout.Ace);
            // "Silver" and "Adept" are matched exactly as they were before this change.
            Assert.Equal(Tier.Silver, loadout.Silver);
            Assert.Equal(Tier.Veteran, loadout.Adept);
            Assert.Equal(99, loadout.Tail);
        }

        [Fact]
        public void A_Member_Renamed_In_CSharp_And_Repinned_Is_Not_Reachable_From_The_CSharp_Name()
        {
            // The documented limit, asserted rather than glossed over — and it is the same file as the test
            // above, which is what makes the boundary visible: v1's "Ace" member migrated (2 -> 7) while
            // v1's "Crown" member did not. The difference is not "renamed" but derivable: the current model
            // still calls one of them Ace, while Crown is now Scepter pinned to "WireCrown", and a file
            // that recorded "Crown" contains no spelling that reaches it. The number survives as 12,
            // unnamed — a number this enum does not declare at all; no name is invented for it.
            //
            // The supported spelling of the same evolution is an explicit [PreviousName("Crown")], and the
            // rule is the general one: a name the current model cannot derive has to be declared.
            var loadout = Reader().Load<Loadout>(FileRecordingTheCSharpNames(adept: 3, ace: 2, crown: 12), 1);

            Assert.Equal(12, (int)loadout.Crown);
            Assert.False(Enum.IsDefined(typeof(Tier), loadout.Crown));

            // Every other member in the same file still migrated, and the fields after it line up.
            Assert.Equal(Tier.Ace, loadout.Ace);
            Assert.Equal(Tier.Veteran, loadout.Adept);
            Assert.Equal(99, loadout.Tail);
        }

        // ── The current model, on both readers ────────────────────────────────────────────────────────

        [Fact]
        public void The_Current_Model_Records_The_Wire_Name_And_Round_Trips_On_Both_Readers()
        {
            var serializer = Reader();
            var original = new Loadout
            {
                Bronze = Tier.Bronze,
                Silver = Tier.Silver,
                Ace = Tier.Ace,
                Adept = Tier.Veteran,
                Crown = Tier.Scepter,
                Tail = 7,
            };

            var bytes = serializer.Save(original, 1);

            // The schema of a *current* save records the wire name as well, so the next build is migrated
            // through the same names this test reads.
            var entries = EnumEntries(CerasRobustSerializer.DumpSchema(bytes));
            Assert.Contains("WireAce = 7", entries);
            Assert.Contains("WireCrown = 9", entries);
            Assert.DoesNotContain("Ace = 7", entries);      // the C# name is never the recorded one
            Assert.DoesNotContain("Scepter = 9", entries);
            Assert.Contains("Veteran = 5", entries);

            // Matching layout: the interpreter runs the same map over a file that already matches, so
            // every member has to come back unchanged through both readers.
            foreach (var clone in new[] { serializer.Load<Loadout>(bytes, 1), serializer.Load<Loadout>(SlowPath(bytes), 1) })
            {
                Assert.Equal(Tier.Bronze, clone.Bronze);
                Assert.Equal(Tier.Silver, clone.Silver);
                Assert.Equal(Tier.Ace, clone.Ace);
                Assert.Equal(Tier.Veteran, clone.Adept);
                Assert.Equal(Tier.Scepter, clone.Crown);
                Assert.Equal(7, clone.Tail);
            }
        }

        // ── The runtime-declared / blacklisted half of the fix ────────────────────────────────────────

        [Fact]
        public void A_Runtime_Derived_Enum_Schema_Records_The_Wire_Name_Too()
        {
            // Nothing registers Ceras.Test.RuntimeEnumSchema.Ladder: the generator has no entry for it, so
            // the bake builds the schema from CLR metadata. That is a second implementation of "the name
            // an enum member is recorded under", and it used to record the C# name.
            var serializer = RuntimeSchema();
            Assert.True(serializer.Config.SchemaRegistry.IsDeclared(typeof(RuntimeEnumSchema.Rungs)));
            Assert.False(serializer.Config.SchemaRegistry.IsDeclared(typeof(RuntimeEnumSchema.Ladder)));

            var bytes = serializer.Save(new RuntimeEnumSchema.Rungs { Steps = RuntimeEnumSchema.Ladder.Top, Tail = 5 }, 1);

            Assert.True(serializer.Config.SchemaRegistry.TryGetInfo(typeof(RuntimeEnumSchema.Ladder), out var info));
            Assert.Equal(SchemaTypeKind.Enum, info.Kind);
            Assert.Equal(new[] { "Rung", "WireTop" }, info.EnumValues.Select(ev => ev.Name));

            var entries = EnumEntries(CerasRobustSerializer.DumpSchema(bytes));
            Assert.Contains("WireTop = 3", entries);
            Assert.DoesNotContain("Top = 3", entries);

            foreach (var clone in new[] { serializer.Load<RuntimeEnumSchema.Rungs>(bytes, 1), serializer.Load<RuntimeEnumSchema.Rungs>(SlowPath(bytes), 1) })
            {
                Assert.Equal(RuntimeEnumSchema.Ladder.Top, clone.Steps);
                Assert.Equal(5, clone.Tail);
            }
        }

        [Fact]
        public void A_Runtime_Derived_Enum_Schema_Rejects_Two_Members_Under_One_Name()
        {
            // The name is the member's identity, so a schema that cannot tell them apart is refused at the
            // bake rather than written and matched arbitrarily later. The generated path reports the same
            // declaration as CERAS009 at compile time.
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("RuntimeEnumSchemaClash"));
            var ex = Assert.Throws<CerasSchemaException>(
                () => serializer.Save(new RuntimeEnumSchema.Climbs { Steps = RuntimeEnumSchema.Climb.Up }, 1));

            Assert.Contains("'Up'", ex.Message);
            Assert.Contains("'Down'", ex.Message);
            Assert.Contains("SameStep", ex.Message);
            Assert.Contains("CerasStableName", ex.Message);
        }

        static CerasRobustSerializer RuntimeSchema() =>
            new CerasRobustSerializer(c => c.UseGeneratedFormatters("RuntimeEnumSchema"));

        // ── Building a file that recorded the C# names ─────────────────────────────────────────────────

        /// <summary>
        /// A complete robust file whose schema section names the enum members the way a build *before* the
        /// schema recorded the wire name did — by their C# spelling — and holds v1's numbers. The current
        /// model is otherwise untouched, so the enum member names are the only difference from a file this
        /// build would write.
        /// <para>
        /// Written through the real encoder (<c>RobustHeader</c>, <c>SchemaSection</c>,
        /// <c>StringInternTable</c>) with a hand-authored manifest, which is the only way to produce a
        /// generation today's writer can no longer emit. The payload is in the file's field order (the
        /// schema's member order), because the interpreter walks the file's order rather than the current
        /// declaration order.
        /// </para>
        /// </summary>
        /// <param name="adept">The stored number of the member the file calls "Adept" (v1: 3).</param>
        /// <param name="ace">The stored number of the member the file calls "Ace" (v1: 2).</param>
        /// <param name="crown">The stored number of the member the file calls "Crown" (v1: 4).</param>
        static byte[] FileRecordingTheCSharpNames(int adept, int ace, int crown)
        {
            const string tierName = "Ceras.Test.EnumWireNames.Tier";
            const string loadoutName = "Ceras.Test.EnumWireNames.Loadout";

            var oldRegistry = new CerasRobustConfig().SchemaRegistry;
            oldRegistry.Register(typeof(Loadout), new SchemaTypeInfo(
                loadoutName, Array.Empty<string>(), SchemaTypeKind.Class,
                new[]
                {
                    new SchemaFieldInfo("Ace", Array.Empty<string>(), tierName, true),
                    new SchemaFieldInfo("Adept", Array.Empty<string>(), tierName, true),
                    new SchemaFieldInfo("Bronze", Array.Empty<string>(), tierName, true),
                    new SchemaFieldInfo("Crown", Array.Empty<string>(), tierName, true),
                    new SchemaFieldInfo("Silver", Array.Empty<string>(), tierName, true),
                    new SchemaFieldInfo("Tail", Array.Empty<string>(), "System.Int32", true),
                },
                0));
            oldRegistry.Register(typeof(Tier), new SchemaTypeInfo(
                tierName, Array.Empty<string>(), SchemaTypeKind.Enum, null, 0,
                new[]
                {
                    new SchemaEnumValueInfo("Bronze", 0),
                    new SchemaEnumValueInfo("Silver", 1),
                    new SchemaEnumValueInfo("Ace", (ulong)ace),
                    new SchemaEnumValueInfo("Adept", (ulong)adept),
                    new SchemaEnumValueInfo("Crown", (ulong)crown),
                },
                underlyingTypeName: "System.Int32"));
            Assert.True(oldRegistry.TryGetId(typeof(Loadout), out int oldLoadoutId));

            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);
            var intern = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, oldRegistry, intern);
            int schemaStringOffset = offset;
            intern.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)oldLoadoutId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            // The payload follows the file's own field order, with v1's numbers.
            foreach (var value in new[] { ace, adept, /*Bronze*/ 0, crown, /*Silver*/ 1 })
                SerializerBinary.WriteInt32Fixed(payload, value);
            SerializerBinary.WriteInt32Fixed(payload, 99);   // Tail

            var data = payload.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;
            int stringTableStart = offset;
            new StringInternTable().Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            // Fingerprint 0: the file's layout cannot match the current one, which is what selects the
            // interpreter. (The reader rejects a *newer* build timestamp, not an unknown one, and the
            // timestamp written above is 0 = unknown.)
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);

            return buffer[..offset];
        }
    }
}
