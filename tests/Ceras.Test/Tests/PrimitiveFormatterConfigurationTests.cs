namespace Ceras.Test
{
    using System;
    using System.Buffers;
    using Ceras;
    using Ceras.Exceptions;
    using Ceras.Formatters;
    using Ceras.Resolvers;
    using Xunit;

    /// <summary>
    /// Ceras resolves a built-in formatter for anything
    /// <c>CerasSerializer.IsPrimitiveType</c> reports as primitive, and it does so <em>before</em>
    /// consulting any user configuration. A formatter configured for such a type was therefore
    /// accepted, stored, and then ignored - the code read as though it had taken effect while the
    /// payload said otherwise.
    /// <para>
    /// These tests pin that this is now refused loudly instead, that the two types whose encodings
    /// are genuinely not fixed (<c>string</c> and <c>Type</c>) keep working, and that the suggested
    /// alternative - a type of one's own - does work.
    /// </para>
    /// </summary>
    public class PrimitiveFormatterConfigurationTests
    {
        sealed class RecordingIntFormatter : FormatterBase<int>
        {
            public int Writes;
            public int Reads;

            public override void Serialize(IBufferWriter<byte> writer, int value)
            {
                Writes++;
                SerializerBinary.WriteInt32Fixed(writer, value);
            }

            public override void Deserialize(ref SequenceReader<byte> reader, ref int value)
            {
                Reads++;
                value = SerializerBinary.ReadInt32Fixed(ref reader);
            }
        }

        public static TheoryData<Type> FixedWidthPrimitives => new()
        {
            { typeof(int) }, { typeof(uint) }, { typeof(long) }, { typeof(ulong) },
            { typeof(short) }, { typeof(ushort) }, { typeof(byte) }, { typeof(sbyte) },
            { typeof(float) }, { typeof(double) }, { typeof(bool) }, { typeof(char) },
        };

        [Theory]
        [MemberData(nameof(FixedWidthPrimitives))]
        public void SettingAFormatterForAPrimitive_IsRefused(Type type)
        {
            var ex = Assert.Throws<CerasException>(() => SetProperty(type));
            Assert.Contains("cannot use a custom formatter", ex.Message, StringComparison.Ordinal);
            Assert.Contains("type of its own", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheGenericSetFormatterRoute_IsRefusedToo()
        {
            // SetFormatter is a separate entry point to the same storage, reached through TypeConfig<T>.
            var ex = Assert.Throws<CerasException>(
                () => new CerasRawConfig().ConfigType<int>().SetFormatter(new RecordingIntFormatter()));
            Assert.Contains("cannot use a custom formatter", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void SettingAResolverForAPrimitive_IsRefused()
        {
            // The resolver is a separate hook from the formatter, and it was silently ignored too.
            var ex = Assert.Throws<CerasException>(
                () => ConfigFor(typeof(int)).CustomResolver = (s, t) => new RecordingIntFormatter());
            Assert.Contains("cannot use a custom formatter", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnArrayOfPrimitives_IsAlsoRefused()
        {
            // IsPrimitiveType recurses into arrays, so int[] is configured just as dead as int.
            var ex = Assert.Throws<CerasException>(() => ConfigFor(typeof(int[])).CustomFormatter = new ArrayFormatterStub());
            Assert.Contains("cannot use a custom formatter", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void StringAndType_StillAcceptAFormatter()
        {
            // The deliberate exceptions. A string's length limit and a Type's schema-id encoding are
            // both implemented by substituting the formatter, so refusing these would break real
            // configuration (including the robust serializer's own installation of them).
            var stringConfig = new CerasRawConfig().ConfigType<string>();
            stringConfig.CustomFormatter = new StringFormatter();

            var typeConfig = new CerasRawConfig().ConfigType<Type>();
            typeConfig.CustomFormatter = new TypeFormatter(new CerasFastSerializer());
        }

        [Fact]
        public void ANonPrimitiveType_IsUnaffected()
        {
            var config = new CerasRawConfig();
            config.ConfigType<System.Text.StringBuilder>().SetFormatter(new StringBuilderFormatter());
        }

        [Fact]
        public void ATypeOfYourOwn_IsTheDocumentedAlternative_AndItWorks()
        {
            // The error message tells people to do this, so it had better actually work.
            var ser = new CerasFastSerializer(c => c.ConfigType<Celsius>().SetFormatter(new CelsiusFormatter()));
            var value = new Celsius { Tenths = 2150 };

            Assert.Equal(2150, ser.Deserialize<Celsius>(ser.Serialize(value)).Tenths);
        }

        /// <summary>A user type with a hand-written encoding - the documented way to get one.</summary>
        [CerasSerializable]
        public struct Celsius
        {
            public int Tenths;
        }

        sealed class CelsiusFormatter : FormatterBase<Celsius>
        {
            public override void Serialize(IBufferWriter<byte> writer, Celsius value) =>
                SerializerBinary.WriteInt32Fixed(writer, value.Tenths);

            public override void Deserialize(ref SequenceReader<byte> reader, ref Celsius value)
            {
                value = new Celsius { Tenths = SerializerBinary.ReadInt32Fixed(ref reader) };
            }
        }

        [Fact]
        public void AnEnum_IsNotTreatedAsAPrimitive()
        {
            // Type.IsPrimitive is false for enums, so a custom enum encoding - as a string, say -
            // is a legitimate and supported thing to want.
            var config = new CerasRawConfig();
            config.ConfigType<DayOfWeek>().SetFormatter(new EnumAsStringFormatter<DayOfWeek>());
        }

        sealed class ArrayFormatterStub : FormatterBase<int[]>
        {
            public override void Serialize(IBufferWriter<byte> writer, int[] value) { }
            public override void Deserialize(ref SequenceReader<byte> reader, ref int[] value) => value = Array.Empty<int>();
        }

        sealed class StringBuilderFormatter : FormatterBase<System.Text.StringBuilder>
        {
            public override void Serialize(IBufferWriter<byte> writer, System.Text.StringBuilder value) { }
            public override void Deserialize(ref SequenceReader<byte> reader, ref System.Text.StringBuilder value) => value = new();
        }

        static global::Ceras.TypeConfig SetProperty(Type type)
        {
            var config = new CerasRawConfig();
            var tc = config.GetTypeConfig(type, false);
            if (type == typeof(int[])) tc.CustomFormatter = new ArrayFormatterStub();
            else tc.CustomFormatter = new RecordingIntFormatter();
            return tc;
        }

        static global::Ceras.TypeConfig ConfigFor(Type type) => new CerasRawConfig().GetTypeConfig(type, false);
    }
}
