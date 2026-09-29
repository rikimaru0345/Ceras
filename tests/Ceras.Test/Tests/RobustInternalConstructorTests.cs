// ─────────────────────────────────────────────────────────────────────────────
// The internal `CerasRobustSerializer(CerasRobustConfig)` constructor used to skip
// `InitializeRobustFormatters`, so a serializer built from a caller's config fell back to
// `StringFormatter` (a varint length prefix) and `TypeFormatter` (a binder-produced name).
// A `Save` through that door therefore wrote a v5 file whose payload did not match the
// grammar the format declares for those positions — a 4-byte string-table index
// (Format/LAYOUT.md, "Primitive encodings") that was really a length prefix, with an empty
// payload string table — and the read failed afterwards, by which time the bad file existed.
//
// These tests pin that the config-built serializer is now set up exactly like the public
// ones: the same robust string/Type formatters, the same bytes, and a refusal rather than a
// file the format cannot describe when a caller asks for something else.
//
// Note what "the same formatters" also closes: the raw `Serialize`/`Deserialize` pair is
// `protected internal` on CerasSerializer, so a friend assembly could reach it on a robust
// serializer, and that is a second way to take a robust context outside its own grammar. The
// five tests that did exactly that — the four in DiscoveryTests and the one in
// SchemaRegistryTests — now use `Save`/`Load`, which is what a robust context is for.
//
// Run only these with:
//
//     dotnet test tests/Ceras.Test -f net10.0 --filter "FullyQualifiedName~RobustInternalConstructor"
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Buffers;
using System.Collections.Generic;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Formatters;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test
{
    [CerasContext("RobustInternalCtor")]
    [CerasSerializable]
    public class RicHolder
    {
        public string Text;
        public Type Kind;
        public int After;
    }

    /// <summary>A string and nothing else, so which member a write reaches first cannot matter.</summary>
    [CerasContext("RobustInternalCtor")]
    [CerasSerializable]
    public class RicStringOnly
    {
        public string Text;
    }

    /// <summary>A user formatter for <see cref="string"/>, i.e. exactly what a robust context refuses.</summary>
    public class RicUserStringFormatter : FormatterBase<string>
    {
        public override void Serialize(IBufferWriter<byte> writer, string value)
            => SerializerBinary.WriteString(writer, "ric:" + value);

        public override void Deserialize(ref SequenceReader<byte> reader, ref string value)
        {
            var read = SerializerBinary.ReadString(ref reader);
            value = read != null && read.StartsWith("ric:", StringComparison.Ordinal) ? read.Substring("ric:".Length) : read;
        }
    }

    public class RobustInternalConstructorTests
    {
        const string Ctx = "RobustInternalCtor";

        /// <summary>
        /// A config the way a caller builds one: fully configured, then handed over. The owning
        /// assembly is stated explicitly because this path discovers nothing (see the constructor's
        /// own comment); without it the build is honestly unknown and the header records 0, which
        /// would make the byte comparison against the public constructor meaningless.
        /// </summary>
        static CerasRobustConfig Config()
        {
            var config = new CerasRobustConfig();
            config.UseGeneratedFormatters(Ctx);
            config.OwningAssembly = typeof(RobustInternalConstructorTests).Assembly;
            // Deterministic, so two saves of the same graph are comparable byte for byte.
            config.StringTableCompression = StringTableCompression.Disabled;
            return config;
        }

        static CerasRobustSerializer New() => new CerasRobustSerializer(Config());

        static CerasRobustSerializer NewPublic() => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Ctx);
            c.OwningAssembly = typeof(RobustInternalConstructorTests).Assembly;
            c.StringTableCompression = StringTableCompression.Disabled;
        });

        static RicHolder Sample() => new RicHolder
        {
            Text = "config-built-interned",
            // A well-known closed generic: its value is a definition id, an argument count and a
            // string id, and nothing that names a CLR type is written.
            Kind = typeof(List<string>),
            After = 7,
        };

        [Fact]
        public void Internal_Constructor_Installs_The_Robust_String_And_Type_Formatters()
        {
            // The resolved formatters, not the config's: these are the objects that decide the bytes,
            // and a serializer resolves and caches them once, in its constructor.
            var serializer = New();

            Assert.IsType<RobustStringFormatter>(serializer.GetSpecificFormatter(typeof(string)));
            Assert.IsType<RobustTypeFormatter>(serializer.GetSpecificFormatter(typeof(Type)));
        }

        [Fact]
        public void Internal_Constructor_Writes_What_The_Public_Constructors_Write()
        {
            // The whole claim, in one assertion: the config-built serializer is not merely able to
            // save something, it produces the same file the public path does. Anything the missing
            // initialization cost — a different string or Type formatter, a different binder, a
            // different manifest entry — would show up as a byte difference.
            var internalBytes = New().Save(Sample(), 1);
            var publicBytes = NewPublic().Save(Sample(), 1);

            Assert.Equal(publicBytes, internalBytes);
        }

        [Fact]
        public void Internal_Constructor_Saves_Through_The_String_Table_And_The_Type_Grammar()
        {
            var bytes = New().Save(Sample(), 1);

            RobustHeader.Read(bytes, out _, out _, out _, out long dataOffset, out long stringTableOffset, out _);

            // The payload string table is where the format says a string value lives, and it is
            // empty when the value was written as a length prefix instead. It also has to be
            // non-empty for a reader to be able to resolve the index at all.
            Assert.True(stringTableOffset > dataOffset,
                $"the payload string table is empty ({stringTableOffset} == {dataOffset}), so the string was not written as a table index");

            var table = StringTable.Load(bytes, (int)stringTableOffset, bytes.Length - (int)stringTableOffset,
                StringTableCompression.Disabled);

            // Entry 0 is the null a string value can hold, so a save whose only string is interned
            // has exactly two entries and the value is the second. A save written through a length
            // prefix instead has a table of just that null, and no index to resolve.
            Assert.Equal(new[] { null, "config-built-interned" }, table);

            // And no CLR type name is in the payload: the Type value is the grammar's tag, not a name.
            // (Ceras.Format has its own `Encoding`, so this one is named in full.)
            var payload = System.Text.Encoding.UTF8.GetString(bytes, (int)dataOffset, (int)(stringTableOffset - dataOffset));
            Assert.DoesNotContain("System.Collections.Generic.List", payload, StringComparison.Ordinal);
        }

        [Fact]
        public void Internal_Constructor_RoundTrips_A_File_A_Fresh_Serializer_Can_Load()
        {
            var sample = Sample();
            var bytes = New().Save(sample, 1);

            // Two readers, and they are the two shapes this defect made differ. Before the fix the
            // file was self-consistent (the same missing formatters wrote and read it) and only a
            // *correctly* configured context could see the problem: reading it with one failed with
            // RobustStringFormatter's "Invalid string table index", naming neither the string nor the
            // misconfiguration, on a file the writer had already reported as saved.
            var viaPublic = NewPublic().Load<RicHolder>(bytes, 1);
            Assert.Equal(sample.Text, viaPublic.Text);
            Assert.Equal(sample.Kind, viaPublic.Kind);
            Assert.Equal(sample.After, viaPublic.After);

            // And the "next session" shape: a second, independent config-built context.
            var viaConfigBuilt = New().Load<RicHolder>(bytes, 1);
            Assert.Equal(sample.Text, viaConfigBuilt.Text);
            Assert.Equal(sample.Kind, viaConfigBuilt.Kind);
            Assert.Equal(sample.After, viaConfigBuilt.After);
        }

        [Fact]
        public void The_Raw_Serialize_Door_Is_Not_A_Way_Out_Of_The_Robust_Grammar()
        {
            // There is no context behind a bare `Serialize`, and the robust string formatter needs
            // one. On the public constructors this has always been so; through the config-built one it
            // used not to matter, because the raw StringFormatter was what got installed. Now the door
            // says so instead of writing a payload no reader of this format can read. Which exception
            // carries it is the reference wrapper's business, not this test's.
            var serializer = New();

            var ex = Record.Exception(() => serializer.Serialize(new RicStringOnly { Text = "no-context" }));

            Assert.NotNull(ex);
            Assert.Contains("RobustContext not active", ex.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Internal_Constructor_Refuses_A_String_Formatter_The_File_Could_Not_Describe()
        {
            // Worth pinning on this door specifically, because the fix could have gone the other way:
            // installing the robust formatters unconditionally would have replaced the caller's
            // formatter and answered with a working serializer, so the configuration they wrote would
            // have been silently discarded. It is left alone, and RobustCustomFormatterPolicy - run by
            // the config's Seal() from inside the base constructor - refuses the combination by name,
            // which is the same answer the public path gives for the same configuration.
            var config = Config();
            config.ConfigType<string>().CustomFormatter = new RicUserStringFormatter();

            var ex = Assert.Throws<CerasSchemaException>(() => new CerasRobustSerializer(config));

            Assert.Contains("System.String", ex.Message, StringComparison.Ordinal);
            Assert.IsType<RicUserStringFormatter>(config.ConfigType<string>().CustomFormatter);
        }
    }
}
