// A user custom formatter on both paths, for a type the context's manifest describes.
//
// The raw half is unchanged from the beginning. The robust half used to be a refusal: a custom formatter
// writes opaque bytes, and neither the file's schema section nor the layout fingerprint said so. It is
// now accepted, because the format says it: the manifest entry for a custom-formatted type is
// <c>Opaque</c> — no member list, the formatter's identity — and the payload is a declared length in front
// of the formatter's own bytes. The behaviour in detail is in Tests/OpaqueCustomPayloadTests.cs (the
// format's own contract) and Tests/OpaqueCustomPayloadFixtureTests.cs (a real old file read by this
// build); this file keeps the original, smallest shape of the case on both paths.
using System;
using System.Buffers;
using Ceras;
using Ceras.Formatters;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.Tests {
	public partial class RobustCustomFormatterTests {
		[Fact]
		public void Robust_Frames_A_CustomFormatter_And_Raw_Still_Honors_It() {
			// Robust: the formatter's own encoding survives the round trip. The generated formatter would
			// write the one field as a string table index and come back with exactly the same text, so the
			// encoding is deliberately *not* the field's — a value that comes back with the prefix is proof
			// that the custom formatter ran, and that its bytes were framed rather than merely tolerated.
			MyCustomStructFormatter.WasSerializedCalled = false;
			MyCustomStructFormatter.WasDeserializedCalled = false;

			var model = new ClassWithCustomStruct {
				Id = 42,
				MyStruct = new MyCustomStruct { MagicValue = "CustomSerialized" }
			};

			var robust = new CerasRobustSerializer(config => {
				config.UseGeneratedFormatters("Tests");
				config.ConfigType<MyCustomStruct>().CustomFormatter = new MyCustomStructFormatter();
			});

			var bytes = robust.Save(model, 1);
			var robustClone = robust.Load<ClassWithCustomStruct>(bytes, 1);

			Assert.Equal(42, robustClone.Id);
			Assert.Equal("custom:CustomSerialized", robustClone.MyStruct.MagicValue);
			Assert.True(MyCustomStructFormatter.WasSerializedCalled, "Custom Serialize was not called");
			Assert.True(MyCustomStructFormatter.WasDeserializedCalled, "Custom Deserialize was not called");

			// Raw/transient: unchanged, the custom formatter is installed and used, and its bytes are
			// exactly what it wrote — no frame, because a raw context describes nothing and so has nothing
			// to account for.
			MyCustomStructFormatter.WasSerializedCalled = false;
			MyCustomStructFormatter.WasDeserializedCalled = false;

			var raw = new CerasFastSerializer(config => {
				config.ConfigType<MyCustomStruct>().CustomFormatter = new MyCustomStructFormatter();
				config.UseGeneratedFormatters("Tests");
			});

			var rawClone = raw.Deserialize<ClassWithCustomStruct>(raw.Serialize(model));

			Assert.Equal(42, rawClone.Id);
			Assert.Equal("custom:CustomSerialized", rawClone.MyStruct.MagicValue);
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
				SerializerBinary.WriteString(writer, "custom:" + (value.MagicValue ?? ""));
			}

			public override void Deserialize(ref SequenceReader<byte> reader, ref MyCustomStruct value) {
				WasDeserializedCalled = true;
				value.MagicValue = SerializerBinary.ReadString(ref reader);
			}
		}
	}
}
