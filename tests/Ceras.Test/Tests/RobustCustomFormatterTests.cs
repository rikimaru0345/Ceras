using System;
using System.Buffers;
using System.Reflection;
using Ceras;
using Ceras.Exceptions;
using Ceras.Formatters;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.Tests {
	// A user custom formatter is no longer honoured in a robust context: its bytes are opaque to the
	// file's schema section and invisible to the layout fingerprint, so a later build cannot skip them
	// and silently returns wrong data (see RobustCustomFormatterPolicy). The model and the formatter
	// below are the shape that used to reproduce it, kept so the rejection is pinned against the exact
	// case — and so the raw path keeps the coverage that the custom formatter is still honoured where
	// it is supported.
	public partial class RobustCustomFormatterTests {
		[Fact]
		public void Robust_Rejects_CustomFormatter_And_Raw_Still_Honors_It() {
			// Robust: refused, naming the type, the formatter and the way out.
			var rejection = Assert.Throws<CerasSchemaException>(() => new CerasRobustSerializer(config =>
			{
				config.ConfigType<MyCustomStruct>().CustomFormatter = new MyCustomStructFormatter();
				config.UseGeneratedFormatters("Tests");
			}));

			Assert.Contains("MyCustomStruct", rejection.Message, StringComparison.Ordinal);
			Assert.Contains(nameof(MyCustomStructFormatter), rejection.Message, StringComparison.Ordinal);
			Assert.Contains("temporary", rejection.Message, StringComparison.OrdinalIgnoreCase);

			// Raw/transient: unchanged, the custom formatter is installed and used.
			MyCustomStructFormatter.WasSerializedCalled = false;
			MyCustomStructFormatter.WasDeserializedCalled = false;

			var original = new ClassWithCustomStruct {
				Id = 42,
				MyStruct = new MyCustomStruct { MagicValue = "CustomSerialized" }
			};

			var raw = new CerasFastSerializer(config =>
			{
				config.ConfigType<MyCustomStruct>().CustomFormatter = new MyCustomStructFormatter();
				config.UseGeneratedFormatters("Tests");
			});

			var loaded = raw.Deserialize<ClassWithCustomStruct>(raw.Serialize(original));

			Assert.Equal(42, loaded.Id);
			Assert.Equal("CustomSerialized", loaded.MyStruct.MagicValue);
			Assert.True(MyCustomStructFormatter.WasSerializedCalled, "Custom Serialize was not called");
			Assert.True(MyCustomStructFormatter.WasDeserializedCalled, "Custom Deserialize was not called");
		}

		[CerasContext("Tests")]
		[CerasSerializable]
		public class ClassWithCustomStruct {
			public int Id { get; set; }
			public MyCustomStruct MyStruct { get; set; }
		}

		public partial struct MyCustomStruct {
			public string MagicValue { get; set; }
		}

		public class MyCustomStructFormatter : FormatterBase<MyCustomStruct> {
			public static bool WasSerializedCalled = false;
			public static bool WasDeserializedCalled = false;

			public override void Serialize(IBufferWriter<byte> writer, MyCustomStruct value) {
				WasSerializedCalled = true;
				SerializerBinary.WriteString(writer, value.MagicValue);
			}

			public override void Deserialize(ref SequenceReader<byte> reader, ref MyCustomStruct value) {
				WasDeserializedCalled = true;
				value.MagicValue = SerializerBinary.ReadString(ref reader);
			}
		}
	}
}
