using System;
using System.Buffers;
using Ceras;
using Ceras.Formatters;
using Ceras.Generated;
using Xunit;

// Regressions for two config-sealing gaps (TODO "Review later"):
//  1. CerasRobustConfig.StringTableCompression could be changed after Seal().
//  2. A standalone custom formatter installed from a config's Seal() override was ignored for the
//     formatters the base constructor resolves eagerly (string / System.Type).
namespace Ceras.Test.Tests
{
    // ── Shared models / formatters ──────────────────────────────────────────────────────────────
    [CerasContext("Tests")]
    [CerasSerializable]
    public class SealingProbeModel
    {
        public int Value;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class SealingStringModel
    {
        public string Text;
        public int N;
    }

    public class SealingProbeModelFormatter : FormatterBase<SealingProbeModel>
    {
        public static int SerializeCalls;
        public static int DeserializeCalls;

        public override void Serialize(IBufferWriter<byte> writer, SealingProbeModel value)
        {
            SerializeCalls++;
            SerializerBinary.WriteInt32Fixed(writer, value.Value + 1000);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref SealingProbeModel value)
        {
            DeserializeCalls++;
            value = new SealingProbeModel { Value = SerializerBinary.ReadInt32Fixed(ref reader) - 1000 };
        }
    }

    public class SealingProbeStringFormatter : FormatterBase<string>
    {
        public static int SerializeCalls;
        public static int DeserializeCalls;

        public override void Serialize(IBufferWriter<byte> writer, string value)
        {
            SerializeCalls++;
            SerializerBinary.WriteString(writer, value == null ? null : "probe:" + value);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref string value)
        {
            DeserializeCalls++;
            value = SerializerBinary.ReadString(ref reader);
            if (value != null && value.StartsWith("probe:", StringComparison.Ordinal))
                value = value.Substring("probe:".Length);
        }
    }

    public class SealingProbeTypeFormatter : FormatterBase<Type>
    {
        public static int SerializeCalls;
        public static int DeserializeCalls;

        public override void Serialize(IBufferWriter<byte> writer, Type value)
        {
            SerializeCalls++;
            SerializerBinary.WriteString(writer, value?.AssemblyQualifiedName);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Type value)
        {
            DeserializeCalls++;
            var name = SerializerBinary.ReadString(ref reader);
            value = name == null ? null : Type.GetType(name, throwOnError: true);
        }
    }

    // A raw config that installs custom formatters from its Seal() override.
    public class SealingRawConfig : CerasRawConfig
    {
        internal override void Seal()
        {
            ConfigType<string>().CustomFormatter = new SealingProbeStringFormatter();
            ConfigType<Type>().CustomFormatter = new SealingProbeTypeFormatter();
            base.Seal();
        }
    }

    // A robust config that installs a custom string formatter from its Seal() override. The override
    // runs before the base Seal(), so the formatter is in place when the robust custom-formatter check
    // runs — this is the same code path a user would hit at context setup, just later.
    public class SealingRobustConfig : CerasRobustConfig
    {
        internal override void Seal()
        {
            ConfigType<string>().CustomFormatter = new SealingProbeStringFormatter();
            base.Seal();
        }
    }

    public class ConfigSealingTests
    {
        // ── FIX 1: StringTableCompression is sealed ─────────────────────────────────────────────

        [Fact]
        public void StringTableCompression_AfterSeal_Throws_EvenForSameValue()
        {
            var robust = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters("Tests");
                c.StringTableCompression = StringTableCompression.Disabled;
            });

            // Sealed by construction: both a real change and a no-op same-value write must throw.
            Assert.Throws<InvalidOperationException>(() => robust.Config.StringTableCompression = StringTableCompression.Deflate);
            Assert.Throws<InvalidOperationException>(() => robust.Config.StringTableCompression = StringTableCompression.Disabled);
        }

        [Theory]
        [InlineData(StringTableCompression.Disabled)]
        [InlineData(StringTableCompression.Deflate)]
        public void StringTableCompression_BeforeSeal_IsApplied_AndRoundTrips(StringTableCompression compression)
        {
            var robust = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters("Tests");
                c.StringTableCompression = compression;
            });

            var model = new SealingStringModel { Text = "interned-value", N = 5 };
            var bytes = robust.Save(model, 1);

            // The value is written as the per-save header flag (not in the layout fingerprint).
            Assert.Equal((byte)compression, bytes[RobustHeader.FlagsOffset]);

            var clone = robust.Load<SealingStringModel>(bytes, 1);
            Assert.Equal("interned-value", clone.Text);
            Assert.Equal(5, clone.N);
        }

        // ── FIX 2: formatters installed during Seal() ───────────────────────────────────────────

        [Fact]
        public void CustomFormatter_InstalledDuringSeal_IsUsed_ForString()
        {
            SealingProbeStringFormatter.SerializeCalls = 0;
            SealingProbeStringFormatter.DeserializeCalls = 0;

            var ceras = new CerasFastSerializer(new SealingRawConfig());
            var clone = ceras.Deserialize<string>(ceras.Serialize("hello"));

            Assert.Equal("hello", clone);
            Assert.True(SealingProbeStringFormatter.SerializeCalls > 0, "custom string Serialize was not called");
            Assert.True(SealingProbeStringFormatter.DeserializeCalls > 0, "custom string Deserialize was not called");
        }

        [Fact]
        public void CustomFormatter_InstalledDuringSeal_IsUsed_ForSystemType()
        {
            SealingProbeTypeFormatter.SerializeCalls = 0;
            SealingProbeTypeFormatter.DeserializeCalls = 0;

            var ceras = new CerasFastSerializer(new SealingRawConfig());
            var clone = ceras.Deserialize<Type>(ceras.Serialize(typeof(SealingProbeModel)));

            Assert.Equal(typeof(SealingProbeModel), clone);
            Assert.True(SealingProbeTypeFormatter.SerializeCalls > 0, "custom Type Serialize was not called");
            Assert.True(SealingProbeTypeFormatter.DeserializeCalls > 0, "custom Type Deserialize was not called");
        }

        /// <summary>
        /// The robust counterpart of the sealing-gap fix above. A user custom formatter installed from
        /// Seal() is no longer *used* on the robust path — a robust context refuses it outright
        /// (RobustCustomFormatterPolicy), because a length-prefixed custom string is opaque to the file's
        /// schema section and to the layout fingerprint. Re-classified rather than deleted: the sealing
        /// gap itself is still covered by the two raw tests, and this pins that a robust context fails
        /// loud at setup instead of writing a file whose schema no longer describes its payload.
        /// </summary>
        [Fact]
        public void CustomFormatter_InstalledDuringSeal_IsRejected_OnRobustPath()
        {
            SealingProbeStringFormatter.SerializeCalls = 0;
            SealingProbeStringFormatter.DeserializeCalls = 0;

            var ex = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(() =>
            {
                var config = new SealingRobustConfig();
                config.UseGeneratedFormatters("Tests");
                return new CerasRobustSerializer(config);
            });

            Assert.Contains("System.String", ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(SealingProbeStringFormatter), ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, SealingProbeStringFormatter.SerializeCalls);
            Assert.Equal(0, SealingProbeStringFormatter.DeserializeCalls);
        }

        [Fact]
        public void CustomFormatter_ConfiguredBeforeConstruction_StillWorks()
        {
            SealingProbeModelFormatter.SerializeCalls = 0;
            SealingProbeModelFormatter.DeserializeCalls = 0;

            var ceras = new CerasFastSerializer(c => c.ConfigType<SealingProbeModel>().CustomFormatter = new SealingProbeModelFormatter());
            var clone = ceras.Deserialize<SealingProbeModel>(ceras.Serialize(new SealingProbeModel { Value = 3 }));

            Assert.Equal(3, clone.Value);
            Assert.True(SealingProbeModelFormatter.SerializeCalls > 0);
            Assert.True(SealingProbeModelFormatter.DeserializeCalls > 0);
        }

        /// <summary>
        /// Contract for formatters supplied after the serializer exists: the eagerly-resolved standalone
        /// formatters (string / System.Type) are already cached in serializer metadata, so a later change
        /// is silently ignored (the TypeConfig setter itself does not throw, because per-type configs
        /// seal on first use, not at config-seal time). This pins that behavior for the FIX 2 case.
        /// </summary>
        [Fact]
        public void CustomFormatter_AfterConstruction_IsIgnored_ForEagerlyResolvedString()
        {
            SealingProbeStringFormatter.SerializeCalls = 0;

            var ceras = new CerasFastSerializer();
            ceras.Config.ConfigType<string>().CustomFormatter = new SealingProbeStringFormatter();

            var clone = ceras.Deserialize<string>(ceras.Serialize("abc"));
            Assert.Equal("abc", clone);
            Assert.Equal(0, SealingProbeStringFormatter.SerializeCalls);
        }
    }
}
