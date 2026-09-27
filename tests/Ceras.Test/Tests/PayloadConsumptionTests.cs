// TODO P0, "Detect trailing migration payload bytes": the exact-payload-consumption detector.
//
// A robust save's data section is, by construction, exactly the declared root type id followed by the
// root's payload — Save starts the payload string table at the byte right after the last payload byte
// and never pads the region (Format/LAYOUT.md, "Envelope"). So a reader that stops before the end of
// the declared region has left part of the save unread, and the object graph it hands back is only as
// trustworthy as the part it did read. Before the detector, a schema mismatch reached that state
// silently: the interpreter skips a field it cannot map or a payload its grammar does not describe, then
// returns, and the caller gets a half-populated object with no error at all.
//
// Every malformed file here has a **valid CRC**. That is the whole point: a checksum that covers the
// whole block cannot tell a coherent-but-wrong layout from a correct one, so the reader has to.
//
// What these tests do *not* claim: the detector says the reader covered the whole declared data
// section, nothing more. A desync that happens to land exactly on the end of the region passes it and
// still decodes wrong values, and a deliberately lying SchemaRegistry.Register-ed schema matches its
// own fingerprint either way. It is a detector, not a substitute for truthful format/schema metadata.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.PayloadConsumption
{
    /// <summary>
    /// The root every payload here is written for. The <c>Items</c> list is what the "modified schema"
    /// cases drop from the file's own schema, because a <c>List&lt;int&gt;</c>'s payload is a
    /// reference marker, a biased count and 4 bytes per element — a leftover big enough to be obvious
    /// and made of values that would be obviously wrong if they were silently reinterpreted.
    /// </summary>
    [CerasContext("PayloadConsumption")]
    [CerasSerializable]
    public class PcHolder
    {
        public int Id;
        public string Name;
        public List<int> Items;

        public static int AfterCallbackCalls;

        [OnAfterDeserialize]
        private void After() => AfterCallbackCalls++;
    }

    public class PayloadConsumptionTests : TestBase
    {
        const string Ctx = "PayloadConsumption";

        // The payload string table has to hold "hello" (the root's Name), so the string below has a
        // table index of 1 — index 0 is the null slot.
        const uint NameTableIndex = 1;

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        static PcHolder Sample() => new PcHolder { Id = 7, Name = "hello", Items = new List<int> { 10, 20 } };

        // ── 1. Trailing bytes inside the declared data section, on both readers ─────────────────

        [Fact]
        public void Trailing_Data_Section_Bytes_Are_Rejected_By_The_Generated_Reader()
        {
            // The matching-fingerprint path, and the exception type proves it: only the generated
            // reader reports a leftover as corruption, so this cannot silently pass for the slow path.
            // The file claims this build's own layout, so the generated reader read the root and then
            // stopped, and nothing in the format permits bytes after the root's payload.
            var bytes = New().Save(Sample(), 1);
            var extended = WithExtraDataSectionBytes(bytes, 5);

            var ex = Record.Exception(() => New().Load<PcHolder>(extended, 1));
            var corruption = Assert.IsType<CerasDataCorruptionException>(ex);

            var (dataStart, stringTable) = DataSectionOffsets(extended);
            AssertOffsetsInMessage(corruption.Message, dataStart, stringTable, 5);
            Assert.Contains("PcHolder", corruption.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Trailing_Data_Section_Bytes_Are_Rejected_By_The_Interpreter()
        {
            // The same bytes through the migration interpreter (SlowPath flips the fingerprint), where
            // the leftover is a *schema/layout* problem rather than a byte problem: the file's own schema
            // section was decoded and mapped onto this build's model, and the two disagree — which is the
            // case a caller can act on.
            var bytes = New().Save(Sample(), 1);
            var extended = SlowPath(WithExtraDataSectionBytes(bytes, 5));

            var ex = Record.Exception(() => New().Load<PcHolder>(extended, 1));
            var schema = Assert.IsType<CerasSchemaException>(ex);

            var (dataStart, stringTable) = DataSectionOffsets(extended);
            AssertOffsetsInMessage(schema.Message, dataStart, stringTable, 5);
            Assert.Contains("PcHolder", schema.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Rejected_Interpreter_Load_Does_Not_Run_The_Deferred_After_Callback()
        {
            // Ordering is the point: the consumption check runs *before* the deferred
            // [OnAfterDeserialize] flush, because a load that did not read the whole data section is
            // not a successful load and must not hand the file's half-described graph to user code.
            // (The generated path flushes those callbacks inside Deserialize, so it cannot get this
            // ordering without re-implementing Deserialize; CerasRobustSerializer documents that.)
            var bytes = New().Save(Sample(), 1);
            var extended = SlowPath(WithExtraDataSectionBytes(bytes, 5));

            PcHolder.AfterCallbackCalls = 0;
            var ex = Record.Exception(() => New().Load<PcHolder>(extended, 1));

            Assert.IsType<CerasSchemaException>(ex);
            Assert.Equal(0, PcHolder.AfterCallbackCalls);
        }

        // ── 2. A file whose *schema* describes one member less than its payload ──────────────────

        [Fact]
        public void A_Schema_That_Describes_One_Member_Less_Leaves_The_Other_Unread()
        {
            // The historical failure mode, reproduced exactly: the file's schema section lists Id and
            // Name, the payload also carries Items, and the interpreter used to return a holder with a
            // null Items and no error at all — 13 payload bytes never looked at.
            var bytes = FileDescribingHolderFields(describeItems: false);

            var ex = Record.Exception(() => New().Load<PcHolder>(bytes, 1));
            var schema = Assert.IsType<CerasSchemaException>(ex);

            var (dataStart, stringTable) = DataSectionOffsets(bytes);
            // marker + count + 2 * 4 bytes: the Items payload the file's schema does not describe.
            AssertOffsetsInMessage(schema.Message, dataStart, stringTable, 1 + 1 + 8);
        }

        [Fact]
        public void The_Same_File_With_Its_Third_Member_Described_Loads_And_Consumes_Everything()
        {
            // The control for the test above, and the reason the pair is worth more than either half:
            // the two files differ only in the one schema entry, so the rejection above is caused by the
            // schema describing less than the payload — not by the file being hand-built, and not by
            // the detector refusing something it cannot prove. (That this load succeeds at all also
            // proves the interpreter is the reader: the file's root id 900 is not in this build's
            // manifest, so the matching-layout reader would have rejected the file outright.)
            var loaded = New().Load<PcHolder>(FileDescribingHolderFields(describeItems: true), 1);

            Assert.Equal(7, loaded.Id);
            Assert.Equal("hello", loaded.Name);
            Assert.Equal(new[] { 10, 20 }, loaded.Items);
        }

        // ── 3. The boundary is the *declared* data section, not the buffer ──────────────────────

        [Fact]
        public void The_Same_Bytes_Are_Accepted_After_The_String_Table_And_Rejected_Before_It()
        {
            // A differential control on where the detector's boundary is. The reader is bounded to
            // [data-section offset, payload-string-table offset), so five bytes spliced in *before*
            // that boundary are an unread tail (rejected above) and the identical five bytes spliced
            // in *after* it are not part of the declared data section at all — the header's offsets
            // still say where the data ends. This is a statement about the measurement, not an
            // endorsement of accepting trailing garbage after a string table.
            var bytes = New().Save(Sample(), 1);

            var before = WithExtraDataSectionBytes(bytes, 5);
            Assert.IsType<CerasDataCorruptionException>(Record.Exception(() => New().Load<PcHolder>(before, 1)));

            var after = WithExtraBytesAfterEverything(bytes, 5);
            var loaded = New().Load<PcHolder>(after, 1);
            Assert.Equal(7, loaded.Id);
            Assert.Equal("hello", loaded.Name);
            Assert.Equal(new[] { 10, 20 }, loaded.Items);
        }

        // ── 4. Valid files must not be caught by it ────────────────────────────────────────────

        [Fact]
        public void A_Plain_Round_Trip_Loads_On_Both_Readers()
        {
            // The matching-layout control. The same bytes load through the generated reader (matching
            // fingerprint) and through the interpreter (fingerprint flipped by SlowPath), so the
            // detector cannot be satisfied by rejecting only one reader or only one of them.
            var bytes = New().Save(Sample(), 1);

            var generated = New().Load<PcHolder>(bytes, 1);
            var interpreted = New().Load<PcHolder>(SlowPath(bytes), 1);

            foreach (var loaded in new[] { generated, interpreted })
            {
                Assert.Equal(7, loaded.Id);
                Assert.Equal("hello", loaded.Name);
                Assert.Equal(new[] { 10, 20 }, loaded.Items);
            }
        }

        [Fact]
        public void Every_Checked_In_Historical_Fixture_Still_Loads_Through_The_Interpreter()
        {
            // Every golden file in Fixtures/HistoricalV1, through the reader this detector guards. They
            // are real migrations (their fingerprint does not match this build), so they are exactly
            // the files whose real layouts could have disagreed with the interpreter and left bytes
            // behind. If one of them ever does, the fixture — not the detector — is what has to change,
            // and this is where that shows up.
            var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1");

            var game = NewIn("HistoricalCurrent").Load<HistoricalFixtures.SaveGame>(Fixture(fixtures, "SaveGame.bin"), 1);
            Assert.Equal(3, game.Version);
            Assert.Equal(2, game.Players.Count);
            Assert.Equal("chapter-2", game.Chapter);

            var actor = NewIn("HistoricalCurrent").Load<HistoricalFixtures.Actor>(Fixture(fixtures, "PlayerCycle.bin"), 1);
            Assert.Equal("Ayla", actor.Name);
            Assert.Same(actor, actor.Buddy.Buddy);        // the two-node cycle survived

            var inventory = NewIn("HistoricalCurrent").Load<HistoricalFixtures.Inventory>(Fixture(fixtures, "Inventory.bin"), 1);
            Assert.Equal(2, inventory.Slots.Count);
            Assert.Equal(new[] { 1, 2 }, inventory.Groups[0]);

            var vault = NewIn("HistoricalConstruction").Load<HistoricalConstruction.RelicVault>(Fixture(fixtures, "RelicVault.bin"), 1);
            Assert.Equal(4, vault.Version);
            Assert.Same(vault.Kept.Owner, vault.Token);

            var relic = NewIn("HistoricalConstruction").Load<HistoricalConstruction.Relic>(Fixture(fixtures, "Relic.bin"), 1);
            Assert.IsType<HistoricalConstruction.Charm>(relic.Owner);
        }

        [Fact]
        public void A_Historical_Fixture_Also_Loads_When_It_Is_Pushed_Through_Both_Readers()
        {
            // The fixtures are already interpreted, so flipping the fingerprint is a no-op for the
            // result — but it is the only way to prove the *generated* reader leaves no tail on real
            // migration bytes either. Same values, either reader.
            var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1");
            var bytes = Fixture(fixtures, "SaveGame.bin");

            var straight = NewIn("HistoricalCurrent").Load<HistoricalFixtures.SaveGame>(bytes, 1);
            var flipped = NewIn("HistoricalCurrent").Load<HistoricalFixtures.SaveGame>(SlowPath(bytes), 1);

            Assert.Equal(straight.Version, flipped.Version);
            Assert.Equal(straight.Chapter, flipped.Chapter);
            Assert.Equal(straight.Players.Select(p => p.Name), flipped.Players.Select(p => p.Name));
            Assert.Same(flipped.Players[0], flipped.Main);
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────

        static CerasRobustSerializer NewIn(string context) => new CerasRobustSerializer(c => c.UseGeneratedFormatters(context));

        static byte[] Fixture(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'. " +
                "Run fixtures/Ceras.HistoricalWriter to regenerate it (see its README.md).");
            return File.ReadAllBytes(path);
        }

        static (long dataStart, long stringTable) DataSectionOffsets(byte[] bytes)
        {
            RobustHeader.Read(bytes, out _, out _, out _, out long dataStart, out long stringTable, out _);
            return (dataStart, stringTable);
        }

        /// <summary>
        /// Splices <paramref name="extra"/> bytes into the end of the declared data section and moves
        /// the payload-string-table offset to match, so the *declared* data section really is longer
        /// than the root's payload. Every other header field and both string tables stay valid, and the
        /// CRC is recomputed: the file is a well-formed Ceras block whose data section simply holds
        /// more than this build's layout accounts for.
        /// </summary>
        static byte[] WithExtraDataSectionBytes(byte[] bytes, int extra)
        {
            RobustHeader.Read(bytes, out _, out _, out _, out _, out long stringOffset, out _);

            var result = new byte[bytes.Length + extra];
            Array.Copy(bytes, 0, result, 0, (int)stringOffset);
            for (int i = 0; i < extra; i++) result[(int)stringOffset + i] = (byte)(0xA0 + i);
            Array.Copy(bytes, (int)stringOffset, result, (int)stringOffset + extra, bytes.Length - (int)stringOffset);

            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(RobustHeader.StringTableOffsetOffset), stringOffset + extra);
            FixRobustChecksum(result);
            return result;
        }

        /// <summary>
        /// Appends bytes after the payload string table, leaving every declared section offset alone.
        /// See the test that uses it: this is the other side of the boundary, not a supported file shape.
        /// </summary>
        static byte[] WithExtraBytesAfterEverything(byte[] bytes, int extra)
        {
            var result = new byte[bytes.Length + extra];
            Array.Copy(bytes, result, bytes.Length);
            for (int i = 0; i < extra; i++) result[bytes.Length + i] = (byte)(0xA0 + i);
            FixRobustChecksum(result);
            return result;
        }

        static void AssertOffsetsInMessage(string message, long dataStart, long stringTable, int unread)
        {
            // The report has to be actionable, so it names the region, where the reader stopped inside
            // it, and how many bytes were never read. Nothing else in a load error prints the declared
            // data-section bounds, so this is also what pins the message to *this* check.
            Assert.Contains($"[{dataStart}, {stringTable})", message, StringComparison.Ordinal);
            Assert.Contains($"stopped at file offset {stringTable - unread}", message, StringComparison.Ordinal);
            Assert.Contains($"{unread} byte(s)", message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A hand-built but otherwise valid robust file whose payload is exactly the sample holder's,
        /// with a schema section that describes <c>Id</c> and <c>Name</c> — and, when
        /// <paramref name="describeItems"/> is set, the <c>Items</c> list as well. The schema is written
        /// field by field rather than baked from a registry, because the point is to emit a schema that
        /// deliberately does *not* match the payload.
        /// </summary>
        static byte[] FileDescribingHolderFields(bool describeItems)
        {
            const int RootId = 900, ListId = 901;
            const int HolderId = 7;
            const uint HolderNameIndex = NameTableIndex;
            var holderItems = new[] { 10, 20 };

            var buffer = new byte[512];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);

            var intern = new StringInternTable();
            var schema = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);

            SchemaSection.WriteLeb128(schema, (uint)(describeItems ? 2 : 1));   // the root, plus the list when described

            // The root, named after the *current* type so the load's root-identity check resolves it.
            SchemaSection.WriteLeb128(schema, RootId);
            SchemaSection.WriteInternedString(schema, "Ceras.Test.PayloadConsumption.PcHolder", intern);
            SchemaSection.WriteLeb128(schema, 0);
            schema.GetSpan(1)[0] = (byte)SchemaTypeKind.Class; schema.Advance(1);
            SchemaSection.WriteLeb128(schema, (uint)(describeItems ? 3 : 2));
            (string Name, uint TypeId)[] fields = describeItems
                ? new[] { ("Id", (uint)WellKnownTypes.Int32), ("Name", (uint)WellKnownTypes.String), ("Items", (uint)ListId) }
                : new[] { ("Id", (uint)WellKnownTypes.Int32), ("Name", (uint)WellKnownTypes.String) };
            foreach (var (name, typeId) in fields)
            {
                SchemaSection.WriteInternedString(schema, name, intern);
                SchemaSection.WriteLeb128(schema, 0);
                SchemaSection.WriteLeb128(schema, typeId);
                schema.GetSpan(1)[0] = 0; schema.Advance(1);   // not monomorphic: a reference marker is written
            }
            SchemaSection.WriteLeb128(schema, 0);               // FixedByteSize

            if (describeItems)
            {
                // List<int>, described structurally: the well-known List definition id plus Int32.
                SchemaSection.WriteLeb128(schema, ListId);
                SchemaSection.WriteInternedString(schema, "System.Collections.Generic.List`1<System.Int32>", intern);
                SchemaSection.WriteLeb128(schema, 0);
                schema.GetSpan(1)[0] = (byte)SchemaTypeKind.GenericInstantiation; schema.Advance(1);
                SchemaSection.WriteLeb128(schema, (uint)WellKnownTypes.List);
                SchemaSection.WriteLeb128(schema, 1);
                SchemaSection.WriteLeb128(schema, (uint)WellKnownTypes.Int32);
                SchemaSection.WriteLeb128(schema, 0);
                SchemaSection.WriteLeb128(schema, 0);
            }

            int schemaStringOffset = schema.Offset;
            intern.Flush(ref schema.Buffer, ref schema.Offset);
            int dataStart = schema.Offset;

            var payload = new CerasSerializer.FixedArrayBufferWriter(schema.Buffer, schema.Offset);
            SchemaSection.WriteLeb128(payload, RootId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteInt32Fixed(payload, HolderId);
            SerializerBinary.WriteUInt32Fixed(payload, HolderNameIndex);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);  // the list
            SerializerBinary.WriteUInt32Bias(payload, holderItems.Length, 1);                          // biased count
            foreach (int element in holderItems) SerializerBinary.WriteInt32Fixed(payload, element);

            int stringTableStart = payload.Offset;
            var payloadStrings = new StringInternTable();
            payloadStrings.Intern("hello");   // so Name's table index 1 resolves
            payloadStrings.Flush(ref payload.Buffer, ref payload.Offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(payload.Buffer, payload.Offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return payload.Buffer[..payload.Offset];
        }
    }
}
