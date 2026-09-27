using System.Buffers;
using Ceras.Formatters;
using Xunit;

namespace Ceras.Test
{
    // Promoted from the now-deleted samples/LiveTesting sandbox :: InjectSpecificFormatterTest.
    //
    // A formatter a user hands to Ceras (here through CerasConfig.OnResolveFormatter) is prepared
    // exactly like a built-in one: Ceras injects the serializer itself, the sub-formatters the
    // formatter declares, and a back-reference to the formatter into the matching fields.
    public class FormatterInjectionTests
    {
        [Fact]
        public void UserProvidedFormatter_GetsItsDependenciesInjected()
        {
            var resolveCalls = 0;
            var config = new CerasRawConfig();
            config.OnResolveFormatter.Add((c, t) =>
            {
                if (t != typeof(InjectedModel))
                    return null; // not ours, keep searching

                resolveCalls++;
                return new InjectedModelFormatter();
            });

            var ceras = new CerasFastSerializer(config);

            var f = Assert.IsType<InjectedModelFormatter>(ceras.GetSpecificFormatter(typeof(InjectedModel)));

            // The serializer itself.
            Assert.Same(ceras, f.Serializer);

            // A sub-formatter for a completely unrelated type, injected into a field that is typed
            // to the concrete formatter class (ReinterpretFormatter, not IFormatter<>).
            Assert.IsType<ReinterpretFormatter<InjectedEnum>>(f.EnumFormatter);

            // A field typed to the formatter's own class gets the formatter itself, so nested code
            // can hand its own instance around.
            Assert.Same(f, f.Self);

            // The result is cached: a second lookup returns the same prepared instance instead of
            // asking the user again.
            Assert.Same(f, ceras.GetSpecificFormatter(typeof(InjectedModel)));
            Assert.Equal(1, resolveCalls);
        }

        [Fact]
        public void UserProvidedFormatter_IsTheOneUsedForTheRoundTrip()
        {
            var custom = new InjectedModelFormatter();
            var config = new CerasRawConfig();
            config.OnResolveFormatter.Add((c, t) => t == typeof(InjectedModel) ? custom : null);

            var ceras = new CerasFastSerializer(config);

            var data = ceras.Serialize(new InjectedModel { Value = 42 });
            Assert.Equal(1, custom.SerializeCalls);

            var clone = ceras.Deserialize<InjectedModel>(data);

            Assert.Equal(1, custom.DeserializeCalls);
            Assert.Equal(42, clone.Value);
        }

        public class InjectedModel
        {
            public int Value;
        }

        // A blittable enum, so Ceras resolves a ReinterpretFormatter for it.
        public enum InjectedEnum : byte
        {
            A, B, C
        }

        public class InjectedModelFormatter : FormatterBase<InjectedModel>
        {
            // Injected by Ceras because the field type is CerasSerializer. Every field below is
            // assigned by PrepareFormatter through reflection, so the initializers only exist to
            // keep the compiler from reporting a field it cannot see assigned.
            public CerasSerializer Serializer = null!;
            // Injected by Ceras because the field is assignable from the resolved formatter of InjectedEnum.
            public ReinterpretFormatter<InjectedEnum> EnumFormatter = null!;
            // Injected by Ceras because the field is assignable from the formatter for InjectedModel.
            public InjectedModelFormatter Self = null!;
            // Injected by Ceras because the field is a plain IFormatter<>.
            IFormatter<int> _intFormatter = null!;

            public int SerializeCalls;
            public int DeserializeCalls;

            public override void Serialize(IBufferWriter<byte> writer, InjectedModel value)
            {
                SerializeCalls++;
                _intFormatter.Serialize(writer, value.Value);
            }

            public override void Deserialize(ref SequenceReader<byte> reader, ref InjectedModel value)
            {
                DeserializeCalls++;
                if (value == null)
                    value = new InjectedModel();
                _intFormatter.Deserialize(ref reader, ref value.Value);
            }
        }
    }
}
