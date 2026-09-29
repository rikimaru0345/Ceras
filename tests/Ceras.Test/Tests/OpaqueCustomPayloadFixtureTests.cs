// A real old file whose custom payload is read by this build.
//
// The bytes come from fixtures/Ceras.HistoricalWriter (V1OpaqueModel.cs, context "HistoricalV1Opaque"),
// which this assembly does not reference, so the v1 CLR types are absent: the reader can only see the
// bytes and the current model below. That is what makes these cases different from
// Tests/OpaqueCustomPayloadTests, which drive the format from one assembly and therefore always have the
// v1 *type* available. Here the payload type is a name in a file — "Ceras.HistoricalWriter.V1Opaque.RecipeRef"
// — that this build cannot resolve to a CLR type at all, and the only way to read the block is to
// configure a formatter whose canonical name is the one the file recorded.
//
// Declaring that formatter in the v1 namespace (see the last block in this file) is not a trick: the
// recorded name is a canonical CLR name, and the canonical name of the writer's formatter class is
// exactly that string. Keeping a formatter's full name stable across builds *is* the compatibility
// contract for the payloads it wrote — the same contract a rename of any other type obeys.
//
// Two fixtures, in the two shapes a later build has to cope with:
//   * OpaqueFrameHolder.bin         — the member is still there, so the frame is read;
//   * OpaqueDroppedMemberHolder.bin — the member is gone, so the declared length is the only thing that
//     can account for those bytes.
// A sentinel comes first in both, so a skip that is off by anything at all shows up as a wrong number.
//
// See the folder README for how to regenerate, and Format/LAYOUT.md for the wire grammar.
using System;
using System.Buffers;
using System.IO;
using System.Reflection;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Formatters;
using Ceras.Generated;
using Xunit;

// The v1 formatter is declared under v1's own namespace at the bottom of this file, so that this build's
// canonical name for it is the name the fixture recorded. See there.
using V1 = Ceras.HistoricalWriter.V1Opaque;

namespace Ceras.Test.OpaqueFixtures
{
    /// <summary>
    /// v1's <c>RecipeRef</c> as this build models it. The custom formatter is what reads the frame, so
    /// the member's declared type only has to be a type the manifest describes — the same split the
    /// format makes: the schema says a type, the payload says a formatter.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Opaque.RecipeRef")]
    [CerasContext("OpaqueFixtureCurrent")]
    [CerasSerializable]
    public class RecipeRef
    {
        public int RecipeId;
    }

    /// <summary>
    /// The holder whose custom-formatted member survived, mapped onto v1's name. This is the case that
    /// works, and the reason the format changed.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Opaque.FrameHolder")]
    [CerasContext("OpaqueFixtureCurrent")]
    [CerasSerializable]
    public class FrameHolder
    {
        public int ASentinel;
        public RecipeRef Target;
    }

    /// <summary>
    /// The holder whose custom-formatted member is <em>gone</em>. This is the case the frame exists for:
    /// the bytes are accounted for by the declared length, so everything after them is still found, and
    /// the value is lost.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Opaque.DroppedMemberHolder")]
    [CerasContext("OpaqueFixtureCurrent")]
    [CerasSerializable]
    public class DroppedMemberHolder
    {
        public int ASentinel;

        [OnAfterDeserialize]
        private void After(MigrationContext context) => LastContext = context;

        public static MigrationContext LastContext;
    }

    /// <summary>
    /// The same holder, and the same payload type, in a context where the payload type declares
    /// <b>no</b> <see cref="PreviousTypeName"/>. That one omission is what makes the file's recorded type
    /// name unresolvable, which is the real shape of a removed type: the v1 <c>RecipeRef</c> does not exist
    /// in this assembly, and without an alias nothing maps that name to anything here.
    /// <para>
    /// The holder's <em>own</em> alias is kept, and so is one for the other fixture's shape: a load
    /// resolves every entry the file's schema section names, not only those on the path to the root, so
    /// a context missing an alias for an unrelated type fails earlier and for an unrelated reason. This
    /// context differs from <see cref="Ceras.Test.OpaqueFixtures.Ctx"/> in the payload type's alias and
    /// nothing else.
    /// </para>
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Opaque.FrameHolder")]
    [CerasContext("OpaqueFixtureNoAlias")]
    [CerasSerializable]
    public class UnaliasedFrameHolder
    {
        public int ASentinel;
        public UnaliasedRecipeRef Target;
    }

    /// <summary>
    /// v1's payload type as this build models it, deliberately without v1's name as an alias. With the
    /// alias there is nothing to say but "no custom formatter"; without it the name resolves to no type
    /// at all, and the refusal has to say *that* instead.
    /// </summary>
    [CerasContext("OpaqueFixtureNoAlias")]
    [CerasSerializable]
    public class UnaliasedRecipeRef
    {
        public int RecipeId;
    }

    /// <summary>
    /// v1's other holder, so the second schema entry the same file carries resolves here. A load resolves
    /// every entry the file's schema section names, not only the ones on the path to the root, so leaving
    /// this name unresolvable as well would fail the load while resolving the section — a refusal about type
    /// resolution, before any payload is reached, which is a different question from the one these tests
    /// are about. Only the payload type's name is declined here.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Opaque.DroppedMemberHolder")]
    [CerasContext("OpaqueFixtureNoAlias")]
    [CerasSerializable]
    public class UnaliasedDroppedMemberHolder
    {
        public int ASentinel;
    }

    /// <summary>
    /// v1's dropped-member holder in a context of its own, with the custom member <em>gone</em> as well.
    /// That last part is what lets the refusal be about the payload rather than about the member: a member
    /// still in the model is refused earlier and for a different reason, because the plan builder cannot map
    /// a file's opaque block onto a field whose type this build does not have — and that refusal is correct,
    /// since it is the member migration that failed.
    /// <para>
    /// Its own context, because a manifest name is claimed by exactly one type
    /// (SchemaRegistry.RequireClaimableName) and the kept-member holder above already claims v1's name for
    /// this one. Two claims would bind a save to whichever type registered first, silently, so the two
    /// shapes are separated by a context rather than sharing one.
    /// </para>
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Opaque.DroppedMemberHolder")]
    [CerasContext("OpaqueFixtureGone")]
    [CerasSerializable]
    public class UnaliasedGoneMemberHolder
    {
        public int ASentinel;
    }

    /// <summary>
    /// v1's dropped-member holder as a *name* this context still has to resolve. The load resolves every
    /// entry the file's schema section carries, not only the ones on the path to the root, so a context that
    /// omits it fails earlier and for an unrelated reason — the name would simply be unresolvable, and the
    /// refusal would be about resolution rather than about a payload.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Opaque.FrameHolder")]
    [CerasContext("OpaqueFixtureGone")]
    [CerasSerializable]
    public class UnaliasedFrameHolderForGone
    {
        public int ASentinel;
    }

    /// <summary>
    /// A binder that answers for every name except one, which it declines by returning null.
    /// <para>
    /// <c>ITypeBinder.GetTypeFromBase</c> documents throwing when it cannot name a type, and the default
    /// <see cref="SimpleTypeBinder"/> does exactly that — which is why a genuinely removed type usually
    /// fails <em>earlier</em> and elsewhere, while Ceras resolves the file's schema section, with a message
    /// about resolution rather than about a payload. Returning null is the other documented shape, and
    /// Ceras normalizes it to "no such type"
    /// (SchemaRegistry.ResolveThroughBinder, SchemaTraversalEngine.ResolveFullName), so the load proceeds
    /// and the refusal about the payload is the one the caller sees.
    /// </para>
    /// <para>
    /// It is a real configuration — a binder that binds a plugin's types and declines everything else is
    /// exactly the shape an application with a type binder has — and it is the only way to reach the "no
    /// such type" wording from inside one assembly, where every type in the file happens to still exist.
    /// </para>
    /// </summary>
    sealed class DecliningBinder : ITypeBinder
    {
        readonly ITypeBinder _inner = new SimpleTypeBinder();
        readonly string _declined;

        public DecliningBinder(string declined) => _declined = declined;

        public string GetBaseName(Type type) => _inner.GetBaseName(type);

        public Type GetTypeFromBase(string baseTypeName)
            => baseTypeName == _declined ? null : _inner.GetTypeFromBase(baseTypeName);

        public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments)
            => baseTypeName == _declined ? null : _inner.GetTypeFromBaseAndArguments(baseTypeName, genericTypeArguments);
    }

    /// <summary>
    /// A formatter for the same type under a name the file did <em>not</em> record. The compatibility
    /// contract is the full name, so this is a different formatter even though it encodes identically —
    /// which is what makes the refusal testable: an encoding that merely happened to match would
    /// otherwise be indistinguishable from a correct read.
    /// </summary>
    public class RenamedRecipeRefFormatter : IFormatter<RecipeRef>
    {
        public void Serialize(IBufferWriter<byte> writer, RecipeRef value) => new V1.RecipeRefFormatter().Serialize(writer, value);
        public void Deserialize(ref SequenceReader<byte> reader, ref RecipeRef value) => new V1.RecipeRefFormatter().Deserialize(ref reader, ref value);
        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (RecipeRef)value);
        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            RecipeRef value = null;
            Deserialize(ref reader, ref value);
            if (member is FieldInfo field) field.SetValue(container, value);
            else throw new NotSupportedException("This formatter writes through a field.");
        }
    }

    public class OpaqueCustomPayloadFixtureTests : TestBase
    {
        const string Ctx = "OpaqueFixtureCurrent";
        const string V1FormatterName = "Ceras.HistoricalWriter.V1Opaque.RecipeRefFormatter";

        static byte[] Fixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1", name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'. " +
                "Run fixtures/Ceras.HistoricalWriter to regenerate it (see its README.md).");
            return File.ReadAllBytes(path);
        }

        /// <summary>
        /// The reading context, with the formatter that shares the file's recorded name. The name is a
        /// canonical CLR name and <c>V1.RecipeRefFormatter</c> has it because it is *declared* under v1's
        /// namespace: a build that kept v1's class name keeps its payloads readable, and one that renamed
        /// or moved it does not.
        /// </summary>
        static CerasRobustSerializer Reader(bool allowDropping = false)
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.AllowDroppingCustomPayloads = allowDropping;
                c.ConfigType<RecipeRef>().CustomFormatter = new V1.RecipeRefFormatter();
            });

        [Fact]
        public void The_Fixture_Really_Is_An_Old_File_With_An_Opaque_Entry()
        {
            var bytes = Fixture("OpaqueFrameHolder.bin");
            var dump = CerasRobustSerializer.DumpSchema(bytes);

            // The v1 stable names, and no trace of this build's — so the migration path is what runs, not
            // the generated reader over a matching fingerprint.
            Assert.Contains("Ceras.HistoricalWriter.V1Opaque", dump);
            Assert.DoesNotContain("Ceras.Test.OpaqueFixtures", dump);

            // The entry is opaque, and it records the *v1* formatter's name — a name this build does not
            // have as a type at all, only as a formatter it was handed.
            Assert.Contains("OPAQUE", dump, StringComparison.Ordinal);
            Assert.Contains(V1FormatterName, dump, StringComparison.Ordinal);
            Assert.Contains("declared length", dump, StringComparison.Ordinal);
            Assert.Contains("behind a reference marker", dump, StringComparison.Ordinal);
        }

        [Fact]
        public void Both_Shapes_Of_The_Same_Model_Are_Checked_In()
        {
            // Two fixtures over one model: the member kept and the member gone. Both must be readable, and
            // the difference between them is the whole subject of the format change.
            Assert.NotEmpty(Fixture("OpaqueFrameHolder.bin"));
            Assert.NotEmpty(Fixture("OpaqueDroppedMemberHolder.bin"));
        }

        [Fact]
        public void A_Kept_Member_Is_Read_Through_The_Frame()
        {
            var clone = Reader().Load<FrameHolder>(Fixture("OpaqueFrameHolder.bin"), 1);

            // The sentinel is the value v1 wrote, and it is only right if the block was consumed exactly:
            // one byte off and this number would be wrong.
            Assert.Equal(0x0BADF00D, unchecked((int)clone.ASentinel));

            // And the payload came back through the custom formatter, not through the generated one: v1
            // wrote the recipe id as a tag plus a width this build's generated formatter does not produce.
            Assert.NotNull(clone.Target);
            Assert.Equal(4001, clone.Target.RecipeId);
        }

        [Fact]
        public void A_Removed_Member_Is_Refused_By_Default()
        {
            // The bytes are accounted for and the value is still not delivered to anything. The refusal is
            // the default because the save records only that *a formatter* wrote these bytes — not enough
            // to reason about what is being given up — and this is the one shape where a value disappears
            // without leaving a member behind to show a default.
            var error = Assert.Throws<CerasSchemaException>(
                () => Reader().Load<DroppedMemberHolder>(Fixture("OpaqueDroppedMemberHolder.bin"), 1));

            Assert.Contains(V1FormatterName, error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(CerasRobustConfig.AllowDroppingCustomPayloads), error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AllowDropping_Skips_The_Block_And_Reports_The_Member()
        {
            DroppedMemberHolder.LastContext = null;
            var clone = Reader(allowDropping: true).Load<DroppedMemberHolder>(Fixture("OpaqueDroppedMemberHolder.bin"), 1);

            // The value is lost and the load continues: the sentinel is found, which is only possible if the
            // skip advanced by the declared length and nothing else.
            Assert.Equal(0x0BADF00E, unchecked((int)clone.ASentinel));

            // And the member is reported, because a removed member is the one drop that leaves nothing
            // behind in the model to observe.
            Assert.NotNull(DroppedMemberHolder.LastContext);
            Assert.Contains("Target", DroppedMemberHolder.LastContext.SkippedFields);
        }

        [Fact]
        public void A_Different_Formatter_Name_Is_Refused_And_Both_Are_Named()
        {
            // The same type, the same encoding, a different class name. A payload written by one formatter
            // and read by another is a different payload — no framing can say whether the two agree — so
            // the refusal names both and the load stops. This is what makes a formatter's *name* part of
            // the format rather than an implementation detail, and it is the reason a rename of a
            // formatter class is a format change.
            var reader = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.ConfigType<RecipeRef>().CustomFormatter = new RenamedRecipeRefFormatter();
            });

            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<FrameHolder>(Fixture("OpaqueFrameHolder.bin"), 1));

            Assert.Contains(V1FormatterName, error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(RenamedRecipeRefFormatter), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(CerasRobustConfig.AllowDroppingCustomPayloads), error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AMissing_Formatter_Is_Reported_As_A_Missing_Formatter()
        {
            // The holder declares [PreviousTypeName] on its payload type, which is how a build that renamed
            // or moved the type still recognises it — the same rule every other type in a migration obeys,
            // and the reason the file's type name resolves here at all. So with no formatter installed the
            // accurate complaint is about the *formatter*, and it names the type this build has. "Install
            // the formatter" is the fix a caller can act on, and "no such type" would send them looking
            // for a type that is right there.
            var reader = new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<FrameHolder>(Fixture("OpaqueFrameHolder.bin"), 1));

            Assert.Contains("no custom formatter for", error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(RecipeRef), error.Message, StringComparison.Ordinal);
            Assert.Contains(V1FormatterName, error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AType_This_Build_Does_Not_Have_Says_So_Rather_Than_Complaining_About_A_Formatter()
        {
            // The real shape of a removed type, and the one the two refusal wordings exist to tell apart.
            // The context declares no alias for the payload type, so the file's recorded name
            // "Ceras.HistoricalWriter.V1Opaque.RecipeRef" resolves to no type here — there is nothing in
            // this assembly for it to resolve to.
            //
            // The default binder throws for a name it cannot find, and a load turns that into an earlier
            // refusal about resolving the file's schema section, which is a different message and a
            // different question. A binder that declines the name by returning null is the other
            // documented shape and is normalized to "no such type", so the load proceeds and the refusal
            // the caller sees is the one about the payload.
            //
            // The distinction matters because the two have different fixes: one is a formatter to
            // install, the other is a type (or a former name for it) to bring back. A caller who read
            // "no custom formatter" here would install a formatter against a type that does not exist and
            // get nowhere.
            var reader = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters("OpaqueFixtureGone");
                c.TypeBinder = new DecliningBinder("Ceras.HistoricalWriter.V1Opaque.RecipeRef");
            });

            var error = Assert.Throws<CerasSchemaException>(
                () => reader.Load<UnaliasedGoneMemberHolder>(Fixture("OpaqueDroppedMemberHolder.bin"), 1));

            Assert.Contains("no such type", error.Message, StringComparison.Ordinal);
            Assert.Contains("Ceras.HistoricalWriter.V1Opaque.RecipeRef", error.Message, StringComparison.Ordinal);
            Assert.Contains(V1FormatterName, error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(CerasRobustConfig.AllowDroppingCustomPayloads), error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Kept_Member_Whose_Type_Is_Gone_Is_Refused_As_A_Member_Migration()
        {
            // The other shape, and the reason the case above removes the member as well: the member is
            // still in the model, so the plan builder has to map the file's value onto a field, and it
            // cannot — the field's type is a type this build does not have. That refusal is the correct one
            // and it is a different one, naming the member and the two types. The value is never interpreted
            // in either case; what differs is which question the load answers first, and a caller told
            // "no such type" where the real problem is a member that no longer has a matching type would be
            // sent to bring back a type that is present and correct.
            var reader = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters("OpaqueFixtureNoAlias");
                c.TypeBinder = new DecliningBinder("Ceras.HistoricalWriter.V1Opaque.RecipeRef");
            });

            var error = Assert.Throws<CerasSchemaException>(
                () => reader.Load<UnaliasedFrameHolder>(Fixture("OpaqueFrameHolder.bin"), 1));

            Assert.Contains("Target", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("no such type at all", error.Message, StringComparison.Ordinal);
        }
    }
}

namespace Ceras.HistoricalWriter.V1Opaque
{
    using Ceras.Test.OpaqueFixtures;

    /// <summary>
    /// v1's formatter, declared in v1's namespace so that *this* build's canonical name for it is the name
    /// the file recorded — which is the whole compatibility contract for a custom payload.
    /// <para>
    /// The encoding is v1's exactly: a tag byte, then the recipe id in the width that tag names — a
    /// zigzag varint for an even id, a fixed 32-bit value for an odd one, so the two fixtures carry
    /// payloads of different widths and a reader that ignored the declared length would have to be right
    /// about both by accident. A formatter that was only half-compatible would produce a value that looks
    /// right and is not, and an unknown tag is refused rather than guessed at.
    /// </para>
    /// <para>
    /// It is declared over <em>this build's</em> <see cref="RecipeRef"/>, not v1's: the v1 type does not
    /// exist in this assembly, and the formatter is what the manifest consults — the file's type name is
    /// resolved separately, and here it resolves to nothing at all, which is the case the refusal wording
    /// is about.
    /// </para>
    /// </summary>
    public class RecipeRefFormatter : IFormatter<RecipeRef>
    {
        public const byte NullTag = 0x00;
        public const byte CompactTag = 0xA1;
        public const byte WideTag = 0xA2;

        public void Serialize(IBufferWriter<byte> writer, RecipeRef value)
        {
            if (value == null) { SerializerBinary.WriteByte(writer, NullTag); return; }

            if (value.RecipeId % 2 == 0)
            {
                SerializerBinary.WriteByte(writer, CompactTag);
                SerializerBinary.WriteInt32(writer, value.RecipeId);
            }
            else
            {
                SerializerBinary.WriteByte(writer, WideTag);
                SerializerBinary.WriteInt32Fixed(writer, value.RecipeId);
            }
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref RecipeRef value)
        {
            byte tag = SerializerBinary.ReadByte(ref reader);
            if (tag == NullTag) { value = null; return; }

            if (tag == CompactTag) value = new RecipeRef { RecipeId = SerializerBinary.ReadInt32(ref reader) };
            else if (tag == WideTag) value = new RecipeRef { RecipeId = SerializerBinary.ReadInt32Fixed(ref reader) };
            else throw new InvalidOperationException(
                $"v1's RecipeRefFormatter writes the tags 0x{NullTag:X2}, 0x{CompactTag:X2} and 0x{WideTag:X2}; this block starts with 0x{tag:X2}.");
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (RecipeRef)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            RecipeRef value = null;
            Deserialize(ref reader, ref value);
            if (member is FieldInfo field) field.SetValue(container, value);
            else if (member is PropertyInfo property) property.SetValue(container, value);
            else throw new NotSupportedException("This formatter writes through a field or a property.");
        }
    }
}
