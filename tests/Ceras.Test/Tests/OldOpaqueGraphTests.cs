// A framed custom payload in the old-data graph, against the two real checked-in fixtures and against
// this build's own writer.
//
// A type the application writes through a user custom formatter is described by the file's own
// `Opaque` entry and a payload of "declared length, then that many bytes" (Format/LAYOUT.md). Everything
// the file can say about such a value, it says: which formatter wrote it, whether a reference marker
// stands in front of the frame, and how many bytes the frame declares. That is enough to place the value
// in the arena, to route a back-reference to it and to keep the stream aligned — and it is *not* enough
// to say what the bytes mean. `OldOpaqueNode` is the node kind that stops there: it records those three
// facts and the bytes, and refuses to invent a member list, an empty object, or a decoded value.
//
// Two fixtures, and the two are the reason this is honest rather than convenient:
//
//   * OpaqueFrameHolder.bin         — the member is still in the current model, so the block is read;
//   * OpaqueDroppedMemberHolder.bin — the member is gone from it, so the declared length is the only
//     thing that accounts for those bytes. The graph holds no current model, so it records the member
//     either way: what a *load* delivers and what a *file* says are two different questions, and only
//     the second one is asked here.
//
// The two fixtures are also the only two whose frames are different widths (a tag plus a zigzag varint,
// and a tag plus a fixed int32), so a decoder that guessed a width instead of reading the declared
// length would be right about one of them by accident.
//
// The other half of this file is what this build's own writer can say that v1's model cannot: an
// *inline* frame (a value type with a custom formatter, which no marker stands in front of), a framed
// value in the root position, and a framed instance reached twice — which is the case that shows the
// writer's object-cache numbering and the arena's are still two index spaces, with the graph holding
// the mapping between them.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Formatters;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.OpaqueGraph
{
    /// <summary>
    /// A reference-typed custom payload: the file records the block <em>behind a reference marker</em>,
    /// and the value is the writer's, so a second occurrence of the same instance is a back-reference.
    /// </summary>
    [CerasContext("OldOpaqueGraph")]
    [CerasSerializable]
    public class FramedRef
    {
        public int Id;
    }

    /// <summary>
    /// A value-typed custom payload: the file records the block <em>inline</em>, with no marker in front
    /// of it, and there is no instance to share, so two members holding equal values are two blocks.
    /// </summary>
    [CerasContext("OldOpaqueGraph")]
    [CerasSerializable]
    public struct FramedInline
    {
        public int Id;
    }

    /// <summary>
    /// One holder for every position that matters: a framed value the file writes inline, a second one
    /// of those, a framed value behind a reference marker, a repeat of that same instance, and two plain
    /// integers — so a frame that is a single byte out shows up in a value rather than in a refusal.
    /// </summary>
    [CerasContext("OldOpaqueGraph")]
    [CerasSerializable]
    public class FramedHolder
    {
        public FramedInline Inline;
        public FramedInline Inline2;
        public FramedRef Target;
        public FramedRef Shared;
        public int ASentinel;
        public int ZSentinel;
    }

    /// <summary>
    /// A hand-written formatter, so the manifest records this type as an <c>Opaque</c> entry rather than
    /// as this context's own (<c>RobustCustomFormatterPolicy.Classify</c>). Its encoding is deliberately
    /// not the generated one — a tag byte and a fixed-width id — so the bytes in the file are the
    /// formatter's and not something a generated reader would have produced.
    /// </summary>
    public sealed class FramedRefFormatter : IFormatter<FramedRef>
    {
        public const byte Tag = 0xA2;

        public void Serialize(IBufferWriter<byte> writer, FramedRef value)
        {
            if (value == null) { SerializerBinary.WriteByte(writer, 0); return; }
            SerializerBinary.WriteByte(writer, Tag);
            SerializerBinary.WriteInt32Fixed(writer, value.Id);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref FramedRef value)
            => value = new FramedRef { Id = SerializerBinary.ReadInt32Fixed(ref reader) };

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (FramedRef)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            var value = new FramedRef { Id = SerializerBinary.ReadInt32Fixed(ref reader) };
            if (member is FieldInfo field) field.SetValue(container, value);
            else throw new NotSupportedException("This formatter writes through a field.");
        }
    }

    /// <summary>The same encoding for the value-typed half, under a tag of its own so the two are tellable apart.</summary>
    public sealed class FramedInlineFormatter : IFormatter<FramedInline>
    {
        public const byte Tag = 0xB1;

        public void Serialize(IBufferWriter<byte> writer, FramedInline value)
        {
            SerializerBinary.WriteByte(writer, Tag);
            SerializerBinary.WriteInt32Fixed(writer, value.Id);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref FramedInline value)
            => value = new FramedInline { Id = SerializerBinary.ReadInt32Fixed(ref reader) };

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (FramedInline)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            var value = new FramedInline { Id = SerializerBinary.ReadInt32Fixed(ref reader) };
            if (member is FieldInfo field) field.SetValue(container, value);
            else throw new NotSupportedException("This formatter writes through a field.");
        }
    }

    public class OldOpaqueGraphTests
    {
        const string Ctx = "OldOpaqueGraph";

        /// <summary>The formatter v1 recorded, exactly as the file's own schema section spells it.</summary>
        const string V1FormatterName = "Ceras.HistoricalWriter.V1Opaque.RecipeRefFormatter";

        /// <summary>The type v1's formatter wrote, which this assembly does not have at all.</summary>
        const string V1PayloadTypeName = "Ceras.HistoricalWriter.V1Opaque.RecipeRef";

        // ── 1. The two real fixtures ────────────────────────────────────────────────────────────

        /// <summary>
        /// The two checked-in opaque files, with everything a reader can be wrong about by bytes: the
        /// holder the file records, the width of its block, the block's exact bytes, and the plain value
        /// the file wrote after the block.
        /// </summary>
        public static IEnumerable<object[]> RealOpaqueFixtures() => new[]
        {
            // v1's formatter chose the width by the parity of the id: a zigzag varint for an even one
            // and a fixed int32 for an odd one, so these two blocks are 3 and 5 bytes wide.
            new object[]
            {
                "OpaqueDroppedMemberHolder.bin", "Ceras.HistoricalWriter.V1Opaque.DroppedMemberHolder", 0x0BADF00E, 3,
                new byte[] { 0xA1, 0xC4, 0x3E },
            },
            new object[]
            {
                "OpaqueFrameHolder.bin", "Ceras.HistoricalWriter.V1Opaque.FrameHolder", 0x0BADF00D, 5,
                new byte[] { 0xA2, 0xA1, 0x0F, 0x00, 0x00 },
            },
        };

        [Theory]
        [MemberData(nameof(RealOpaqueFixtures))]
        public void A_Framed_Payload_Is_A_Value_And_The_Bytes_Around_It_Are_Still_The_Files_Own(
            string fixture, string holderName, int sentinel, int declaredLength, byte[] payload)
        {
            var bytes = HistoricalFixtureFiles.Fixture(fixture);
            HistoricalFixtureFiles.AssertIntactChecksum(bytes);

            // The refusal this used to be: the same call, the same file, and the graph now decodes it.
            // Nothing here consults the current model, and the fixture's payload type is a name the file
            // records and this assembly does not have (asserted below), so nothing here could have read
            // the block even by accident.
            var graph = OldFileGraph.Read(bytes);
            var root = Assert.IsType<OldObjectNode>(graph.Root);

            // The file's own holder, with v1's spelling, at the root.
            Assert.Equal(holderName, root.Type.StableName);
            Assert.Equal(SchemaTypeKind.Class, root.Type.Kind);
            Assert.Equal(0, graph.IndexOf(root));

            // The file's payload order, and it is the order that makes the block checkable: the framed
            // member comes first, so the integer after it was decoded from the byte after the frame. A
            // block accounted for by anything but its declared length moves that number, and the builder
            // refuses anything but a data section consumed exactly, so a wrong stop is a refusal.
            Assert.Equal(new[] { "Target", "ASentinel" }, root.RecordedMembers.Select(m => m.Recorded!.StableName).ToArray());
            Assert.Equal(sentinel, (int)((OldScalar)Member(root, "ASentinel")).Value);

            // The value itself: the file's identity for the framed type, the formatter it recorded, the
            // file's own record of how the value was written, the length the frame declared, and the
            // bytes — all of it, and nothing invented.
            var opaque = Assert.IsType<OldOpaqueNode>(Member(root, "Target"));
            Assert.Equal(V1PayloadTypeName, opaque.Type.StableName);
            Assert.Equal(SchemaTypeKind.Opaque, opaque.Type.Kind);
            Assert.True(opaque.Type.TypeId >= WellKnownTypes.UserTypeBase, "an opaque entry is a manifest entry, not a well-known id");
            Assert.Equal(V1FormatterName, opaque.FormatterName);
            Assert.False(opaque.WrittenInline);              // a reference type: a marker stands in front of the frame
            Assert.Equal(declaredLength, opaque.DeclaredLength);
            Assert.Equal(declaredLength, opaque.Payload.Count);
            Assert.Equal(payload, opaque.Payload.ToArray());

            // A value like any other: it owns an arena index, so a later occurrence of the same instance
            // can be a back-reference to it. The holder, the framed value and the integer are all of it.
            Assert.Equal(1, graph.IndexOf(opaque));
            Assert.Same(opaque, graph.Node(1));
            Assert.Equal(3, graph.NodeCount);
        }

        [Fact]
        public void The_Block_Is_Not_An_Empty_Object_And_Not_A_Member_List()
        {
            // The lie the file's own Opaque entry replaced. Recording a framed value as an object with no
            // members says the payload contains no fields; recording it as one with this build's members
            // says it contains fields the file never wrote. The graph holds the bytes instead, and the
            // member the block sits in keeps the name and the type identity the file gave it.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("OpaqueFrameHolder.bin"));
            var root = Assert.IsType<OldObjectNode>(graph.Root);

            Assert.IsNotType<OldObjectNode>(Member(root, "Target"));
            Assert.IsNotType<OldCollectionNode>(Member(root, "Target"));

            var member = root.RecordedMembers.Single(m => m.Recorded!.StableName == "Target");
            Assert.Equal(V1PayloadTypeName, member.Recorded!.Type.StableName);
            Assert.Equal(SchemaTypeKind.Opaque, member.Recorded!.Type.Kind);

            // Two members, the two the file wrote: the block did not become a member of its own, and no
            // member was invented for the payload's contents.
            Assert.Equal(2, root.RecordedMembers.Count);
        }

        [Fact]
        public void The_Payload_Type_Resolves_To_No_Type_Here_So_The_Block_Could_Not_Have_Been_Interpreted()
        {
            // The payload type is a name in the file, and in this assembly it is a name nothing resolves
            // to — which is what makes the claim checkable rather than merely stated: the decode below
            // cannot have consulted a formatter for that type, because there is no such type here to
            // consult one about.
            Assert.Null(Type.GetType(V1PayloadTypeName, throwOnError: false));

            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("OpaqueFrameHolder.bin"));
            var opaque = Assert.IsType<OldOpaqueNode>(Member((OldObjectNode)graph.Root!, "Target"));

            // The bytes are the file's, undecoded: the first is v1's wide tag, and a reader that had
            // interpreted the block would have had a value to hand back and no reason to keep this.
            Assert.Equal(0xA2, opaque.Payload[0]);
        }

        [Fact]
        public void The_Block_Is_Copied_Out_Of_The_File_Rather_Than_Aliased_By_It()
        {
            // The graph outlives the buffer it was built from. The caller's byte array is the save file,
            // which it may edit, reuse or release; a node that pointed into it would change under a
            // reader that never wrote to it.
            var bytes = HistoricalFixtureFiles.Fixture("OpaqueFrameHolder.bin");
            var graph = OldFileGraph.Read(bytes);
            var opaque = Assert.IsType<OldOpaqueNode>(Member((OldObjectNode)graph.Root!, "Target"));

            var expected = opaque.Payload.ToArray();
            long dataStart = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(RobustHeader.DataSectionOffsetOffset));
            int at = IndexOf(HistoricalFixtureFiles.DataSection(bytes), expected);
            Assert.True(at >= 0, "the block's own bytes have to be findable in the file for this to mean anything");

            for (int i = 0; i < expected.Length; i++) bytes[(int)dataStart + at + i] = 0xFF;

            Assert.Equal(expected, opaque.Payload.ToArray());
            Assert.Equal(5, opaque.DeclaredLength);

            // And the property is a read-only list over this node's own array rather than anything a
            // caller could write through — not a byte[], not a Memory<byte>, and not the
            // ReadOnlyMemory<byte> that MemoryMarshal.TryGetArray unwraps. A mutable door here would be
            // one in a model whose whole claim is that the file's record cannot be written through.
            Assert.Equal(typeof(IReadOnlyList<byte>),
                typeof(OldOpaqueNode).GetProperty(nameof(OldOpaqueNode.Payload))!.PropertyType);
            Assert.IsType<ReadOnlyCollection<byte>>(opaque.Payload);
        }

        [Fact]
        public void The_Dropped_Members_File_Still_Records_What_It_Said()
        {
            // The fixture whose custom-formatted member this build's *model* dropped. The graph holds no
            // model, so it records the member, the value and its bytes exactly as the file wrote them —
            // which is the whole difference between "what the file says" and "what a load delivers". The
            // load path is not redirected by the node kind and is not exercised here; see
            // Tests/OpaqueCustomPayloadFixtureTests, which is where that question is asked.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("OpaqueDroppedMemberHolder.bin"));
            var root = Assert.IsType<OldObjectNode>(graph.Root);

            Assert.Equal(0x0BADF00E, (int)((OldScalar)Member(root, "ASentinel")).Value);

            var opaque = Assert.IsType<OldOpaqueNode>(Member(root, "Target"));
            Assert.Equal(3, opaque.DeclaredLength);
            Assert.Equal(new byte[] { 0xA1, 0xC4, 0x3E }, opaque.Payload.ToArray());

            // Nothing is bound to a current field: there is no current model to bind anything to, which
            // is a fact about this graph rather than a member that failed to map.
            Assert.All(root.RecordedMembers, m => Assert.Null(m.CurrentFieldName));
        }

        // ── 2. The frame: bounds, truncation, corruption ────────────────────────────────────────

        /// <summary>
        /// One hand-written opaque entry: no member list, a formatter name, and the file's own inline
        /// flag. The frame's grammar is the format's, so the cases below state only <em>what</em> is wrong
        /// with the bytes, never how a frame is laid out.
        /// </summary>
        static OldTypeSchema OpaqueEntry(int typeId, string name, bool inline, string formatterName = null!)
            => new(typeId, name, Array.Empty<string>(), SchemaTypeKind.Opaque,
                Array.Empty<OldFieldSchema>(), 0, 0, Array.Empty<int>(), Array.Empty<OldEnumValue>(),
                formatterName ?? name + "Formatter", inline);

        /// <summary>A root value of <paramref name="typeId"/> written behind a reference marker: its type id, then the marker.</summary>
        static byte[] MarkedRoot(int typeId) => Bytes(Leb((uint)typeId), Bias(Protocol.RefNewValueSameType, Protocol.RefBias));

        [Fact]
        public void A_Block_Larger_Than_What_Is_Left_Is_Refused_As_Corruption()
        {
            // The frame's whole job is accounting: it says there are ten bytes here, and there are
            // three. Refused rather than truncated, because a shorter block would be a value the file
            // does not contain and every value after it would decode from the wrong offset.
            var entry = OpaqueEntry(300, "V1.Framed", inline: false);
            var bytes = Bytes(MarkedRoot(300), Int32(10), new byte[] { 1, 2, 3 });

            var error = Assert.Throws<CerasDataCorruptionException>(() => Build(bytes, entry));
            Assert.Contains("10 bytes", error.Message);
            Assert.Contains("3 bytes are left", error.Message);
            Assert.Contains("V1.Framed", error.Message);
            Assert.Contains("V1.FramedFormatter", error.Message);
        }

        [Fact]
        public void A_Save_That_Ends_Inside_The_Length_Prefix_Is_Refused()
        {
            // Truncation rather than a length: the four bytes that say how long the block is are what
            // make a block skippable at all, so a save that ends inside them says nothing about where the
            // next value begins.
            var entry = OpaqueEntry(300, "V1.Framed", inline: false);
            var bytes = Bytes(MarkedRoot(300), new byte[] { 0x05, 0x00 });

            var error = Assert.Throws<CerasDataCorruptionException>(() => Build(bytes, entry));
            Assert.Contains("ended before the declared length", error.Message);
        }

        [Fact]
        public void A_Negative_Declared_Length_Is_Refused_As_Corruption()
        {
            // A declared length is written from the number of bytes the formatter produced, so it cannot
            // be negative; a negative one is a damaged field, not a block of some size.
            var entry = OpaqueEntry(300, "V1.Framed", inline: false);
            var bytes = Bytes(MarkedRoot(300), Int32(-1));

            var error = Assert.Throws<CerasDataCorruptionException>(() => Build(bytes, entry));
            Assert.Contains("declares a length of -1 bytes", error.Message);
        }

        [Fact]
        public void A_Block_That_Ends_Short_Of_The_Section_Leaves_Bytes_Unread_And_Is_Refused()
        {
            // The declared length is what accounts for the block, so a decoder that ignored it and read
            // what it liked would leave the rest of the file behind it. The builder's stop check is what
            // makes the length load-bearing rather than advisory: the value itself reads, and the save is
            // refused for the two bytes nothing claimed.
            var entry = OpaqueEntry(300, "V1.Framed", inline: false);
            var bytes = Bytes(MarkedRoot(300), Int32(1), new byte[] { 7, 8, 9 });

            var error = Assert.Throws<CerasDataCorruptionException>(() => Build(bytes, entry));
            Assert.Contains("left 2 unread", error.Message);
        }

        /// <summary>
        /// A section that records an opaque entry with no formatter name is a damaged section, and it is
        /// reported as one: from the file that is corruption, and the builder says so before it reads a
        /// payload byte — so a save whose frame is also broken is refused for its section rather than for
        /// its frame. The node keeps its own argument refusal for a caller building one directly, and
        /// neither of the two restates the other's wording.
        /// </summary>
        [Fact]
        public void An_Entry_That_Names_No_Formatter_Is_Refused_As_Corruption_Before_The_Frame_Is_Read()
        {
            // The section parser normalizes a missing name to the empty string, which is also what the
            // null sentinel interns to, so a doctored section reaches the builder looking like this.
            var entry = OpaqueEntry(300, "V1.Nameless", inline: false, formatterName: "");
            var bytes = Bytes(MarkedRoot(300), Int32(1), new byte[] { 9 });

            var error = Assert.Throws<CerasDataCorruptionException>(() => Build(bytes, entry));
            Assert.Contains("no formatter name", error.Message);
            Assert.Contains("V1.Nameless", error.Message);
        }

        [Fact]
        public void A_Block_Of_No_Bytes_Is_A_Record_And_Not_A_Missing_Value()
        {
            // A formatter is free to have written nothing, and the frame then says so with a length of
            // zero. That is a value the file wrote — it has an identity and an index — not an absence.
            var entry = OpaqueEntry(300, "V1.Empty", inline: false);
            var bytes = Bytes(MarkedRoot(300), Int32(0));

            var graph = Build(bytes, entry);
            var opaque = Assert.IsType<OldOpaqueNode>(graph.Root);

            Assert.Equal(0, opaque.DeclaredLength);
            Assert.Empty(opaque.Payload.ToArray());
            Assert.Equal(0, graph.IndexOf(opaque));
            Assert.Equal(1, graph.NodeCount);
        }

        [Fact]
        public void An_Inline_Block_Is_Read_With_No_Marker_In_Front_Of_It()
        {
            // The file's own inline flag decides this, not the position: a value type's frame is written
            // with no marker, so the first byte after the type id is already the length. A reader that
            // expected a marker here would read a length out of the low bits of the tag byte and produce
            // a value the file does not contain.
            var entry = OpaqueEntry(300, "V1.Inline", inline: true);
            var bytes = Bytes(Leb(300), Int32(3), new byte[] { 0xB1, 0x01, 0x00 });

            var graph = Build(bytes, entry);
            var opaque = Assert.IsType<OldOpaqueNode>(graph.Root);

            Assert.True(opaque.WrittenInline);
            Assert.Equal(3, opaque.DeclaredLength);
            Assert.Equal(new byte[] { 0xB1, 0x01, 0x00 }, opaque.Payload.ToArray());
            Assert.Equal(0, graph.IndexOf(opaque));
        }

        [Fact]
        public void A_Block_In_A_Member_Leaves_The_Members_Around_It_Exactly_Where_They_Are()
        {
            // The member position, on bytes a test writes by hand: a plain int, then the framed member,
            // then another plain int. The last number is the claim — a block accounted for by anything
            // other than its declared length lands the reader somewhere else, and the value after it
            // comes back as a different number rather than as a failure.
            var framed = OpaqueEntry(300, "V1.Framed", inline: false);
            var holder = new OldTypeSchema(301, "V1.Holder", Array.Empty<string>(), SchemaTypeKind.Class, new[]
            {
                new OldFieldSchema("Before", Array.Empty<string>(), WellKnownTypes.Int32, true),
                new OldFieldSchema("Target", Array.Empty<string>(), 300, false),
                new OldFieldSchema("After", Array.Empty<string>(), WellKnownTypes.Int32, true),
            }, 0);

            var bytes = Bytes(
                MarkedRoot(301),
                Int32(0x0BADF00D),
                Bias(Protocol.RefNewValueSameType, Protocol.RefBias), Int32(3), new byte[] { 0xA2, 0x01, 0x00 },
                Int32(0x0BADF00E));

            var graph = Build(bytes, holder, framed);
            var root = Assert.IsType<OldObjectNode>(graph.Root);

            Assert.Equal(0x0BADF00D, (int)((OldScalar)Member(root, "Before")).Value);
            Assert.Equal(0x0BADF00E, (int)((OldScalar)Member(root, "After")).Value);

            var opaque = Assert.IsType<OldOpaqueNode>(Member(root, "Target"));
            Assert.Equal(new byte[] { 0xA2, 0x01, 0x00 }, opaque.Payload.ToArray());
            Assert.False(opaque.WrittenInline);

            // The framed member owns an arena index of its own, and that index is not the writer's cache
            // slot: the writer numbered the holder 0 and this value 1, while the arena numbered the two
            // integers as well, so this value is the arena's 2.
            Assert.Equal(0, graph.IndexOf(root));
            Assert.Equal(2, graph.IndexOf(opaque));
        }

        // ── 3. What this build's own writer can say that v1's model cannot ──────────────────────

        static CerasRobustSerializer Writer(bool declareRoot = false)
            => new(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.SetUnknownBuildTimestamp();
                if (declareRoot) c.DeclareRoot<FramedRef>();
                c.ConfigType<FramedRef>().CustomFormatter = new FramedRefFormatter();
                c.ConfigType<FramedInline>().CustomFormatter = new FramedInlineFormatter();
            });

        static byte[] SaveHolder(FramedRef shared)
            => Writer().Save(new FramedHolder
            {
                Inline = new FramedInline { Id = 11 },
                Inline2 = new FramedInline { Id = 11 },      // the same id, so the two blocks are identical bytes
                Target = shared,
                Shared = shared,
                ASentinel = 0x0BADF00D,
                ZSentinel = 0x0BADF00E,
            }, 1);

        [Fact]
        public void A_Frame_Written_Inline_Is_A_Value_And_The_Members_After_It_Are_Still_Readable()
        {
            // The inline case with a real writer: a value type with a custom formatter, so the file's
            // entry records the inline flag and no marker stands in front of the frame. Both sentinels
            // come back, which is only possible if the frame was found at the offset the file's own flag
            // says and ended exactly where its declared length says.
            var graph = OldFileGraph.Read(SaveHolder(new FramedRef { Id = 7 }));
            var root = Assert.IsType<OldObjectNode>(graph.Root);

            // The file's own member order, which is not the declaration order: the generator orders a
            // context's members by its own rule, and the graph follows the file.
            Assert.Equal(new[] { "Inline2", "Inline", "Shared", "Target", "ASentinel", "ZSentinel" },
                root.RecordedMembers.Select(m => m.Recorded!.StableName).ToArray());
            Assert.Equal(0x0BADF00D, (int)((OldScalar)Member(root, "ASentinel")).Value);
            Assert.Equal(0x0BADF00E, (int)((OldScalar)Member(root, "ZSentinel")).Value);

            var inline = Assert.IsType<OldOpaqueNode>(Member(root, "Inline"));
            Assert.True(inline.WrittenInline);
            Assert.Equal(5, inline.DeclaredLength);
            Assert.Equal(new byte[] { FramedInlineFormatter.Tag, 11, 0, 0, 0 }, inline.Payload.ToArray());

            // A second inline value is a second block and a second node, and this one carries the same id
            // as the first, so the two are the same bytes and still two identities: a value type is
            // written wherever it appears, so there is no instance to share and nothing for a
            // back-reference to name. Two values that are indistinguishable by content are two nodes.
            var second = Assert.IsType<OldOpaqueNode>(Member(root, "Inline2"));
            Assert.Equal(inline.Payload.ToArray(), second.Payload.ToArray());
            Assert.NotSame(inline, second);
            Assert.NotEqual(graph.IndexOf(inline), graph.IndexOf(second));
        }

        [Fact]
        public void A_Framed_Value_Behind_A_Marker_Is_One_Node_And_A_Repeat_Of_It_Is_A_Back_Reference()
        {
            // The writer's object cache and the arena are two index spaces, and a framed value is in
            // both: the writer numbers the values it wrote behind a marker, the arena numbers every
            // value. So the repeat below is a reference to the first occurrence's *node*, and the two
            // inline frames before it are exactly the values that shift the numbering apart.
            var shared = new FramedRef { Id = 7 };
            var graph = OldFileGraph.Read(SaveHolder(shared));
            var root = Assert.IsType<OldObjectNode>(graph.Root);

            // Which of the two occurrences is the first is the file's answer and not the source's: this
            // file writes the shared member before the other one, so that is where the block is.
            var names = root.RecordedMembers.Select(m => m.Recorded!.StableName).ToArray();
            Assert.Equal(new[] { "Inline2", "Inline", "Shared", "Target", "ASentinel", "ZSentinel" }, names);
            Assert.True(Array.IndexOf(names, "Shared") < Array.IndexOf(names, "Target"),
                "this file writes the first occurrence of the shared instance into 'Shared'");

            var first = Assert.IsType<OldOpaqueNode>(Member(root, "Shared"));
            Assert.False(first.WrittenInline);
            Assert.Equal(5, first.DeclaredLength);
            Assert.Equal(new byte[] { FramedRefFormatter.Tag, 7, 0, 0, 0 }, first.Payload.ToArray());

            // The writer's cache numbered the holder 0 and this value 1; the arena numbered the holder 0,
            // each inline frame 1 and 2, and this value 3.
            Assert.Equal(3, graph.IndexOf(first));

            var repeat = Assert.IsType<OldReferenceNode>(Member(root, "Target"));
            Assert.Equal(graph.IndexOf(first), repeat.TargetIndex);
            Assert.Same(first, graph.Node(repeat.TargetIndex));

            // The whole save is the holder, the two inline blocks, the framed value, the reference to it
            // and the two integers: seven values, and the data section consumed exactly to get here.
            Assert.Equal(7, graph.NodeCount);
        }

        [Fact]
        public void A_Framed_Value_Can_Be_The_Saves_Root()
        {
            // The root position, which is the one no member list surrounds. A root is bound to its own
            // declared type, and a reference-typed one is written behind a marker like any other, so the
            // graph's root is the framed value itself and nothing else.
            var bytes = Writer(declareRoot: true).Save(new FramedRef { Id = 5 }, 1);

            var graph = OldFileGraph.Read(bytes);
            var opaque = Assert.IsType<OldOpaqueNode>(graph.Root);

            Assert.Equal(new byte[] { FramedRefFormatter.Tag, 5, 0, 0, 0 }, opaque.Payload.ToArray());
            Assert.Equal(5, opaque.DeclaredLength);
            Assert.Equal(0, graph.IndexOf(opaque));
            Assert.Equal(1, graph.NodeCount);
        }

        [Fact]
        public void Nothing_On_The_Load_Path_Reaches_The_Graph()
        {
            // The graph is a second reader, and this is what keeps it a second reader: the types the load
            // path is made of name neither the builder, nor the graph, nor the node, so a save cannot
            // arrive at a node kind by being loaded. Whether a build can read a framed value *back* is
            // the interpreter's question and it answers it by formatter name; the graph's answer is the
            // bytes, and nothing on the load path is asked it.
            foreach (var type in new[] { typeof(CerasRobustSerializer), typeof(CerasSerializer), typeof(SchemaTraversalEngine) })
            {
                foreach (var member in DeclaredMembers(type))
                {
                    Assert.NotEqual(typeof(OldDataGraphBuilder), member);
                    Assert.NotEqual(typeof(OldDataGraph), member);
                    Assert.NotEqual(typeof(OldOpaqueNode), member);
                }
            }
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every type the load path declares a member of, one level deep: fields, properties, and what
        /// its own methods take and return. Enough to place the graph in or out of that path.
        /// </summary>
        static IEnumerable<Type> DeclaredMembers(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                     | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var field in type.GetFields(flags))
            {
                yield return field.FieldType;
                if (!field.FieldType.IsGenericType) continue;
                foreach (var argument in field.FieldType.GetGenericArguments()) yield return argument;
            }

            foreach (var property in type.GetProperties(flags))
            {
                yield return property.PropertyType;
                if (!property.PropertyType.IsGenericType) continue;
                foreach (var argument in property.PropertyType.GetGenericArguments()) yield return argument;
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.ReturnType != typeof(void)) yield return method.ReturnType;
                foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
            }
        }

        static OldValue Member(OldObjectNode node, string name)
            => node.RecordedMembers.Single(m => m.Recorded!.StableName == name).Value;

        /// <summary>Builds from hand-written entries and a hand-written data section.</summary>
        static OldDataGraph Build(byte[] dataSection, params OldTypeSchema[] entries)
        {
            var byName = new Dictionary<string, OldTypeSchema>(StringComparer.Ordinal);
            foreach (var entry in entries) byName[entry.StableName] = entry;

            var builder = new OldDataGraphBuilder(new FileSchemaView(byName), new string[] { null!, "" }, OldFileGraphLimits.Default);
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(dataSection));
            return builder.Build(ref reader, dataSection.Length);
        }

        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length && match; j++) match = haystack[i + j] == needle[j];
                if (match) return i;
            }
            return -1;
        }

        // The format's own byte builders, so a test never restates an encoding.
        static byte[] Bytes(params byte[][] parts)
        {
            var all = new List<byte>();
            foreach (var part in parts) all.AddRange(part);
            return all.ToArray();
        }

        static byte[] Leb(uint value)
        {
            var bytes = new List<byte>();
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                bytes.Add(b);
            } while (value != 0);
            return bytes.ToArray();
        }

        static byte[] Bias(int value, int bias) => Leb((uint)(value + bias));

        static byte[] Int32(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            return bytes;
        }
    }
}
