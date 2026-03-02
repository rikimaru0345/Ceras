using System;
using System.Buffers;
using System.Reflection;
using Ceras;
using Ceras.Formatters;
using Xunit;

namespace Ceras.Test.Tests {
	public partial class RobustCustomFormatterTests {
		[Fact]
		public void Robust_Respects_CustomFormatter() {
			var robust = new CerasRobustSerializer(config =>
			{
				config.ConfigType<MyCustomStruct>().CustomFormatter = new MyCustomStructFormatter();
				config.UseGeneratedFormatters();
			});

			var original = new ClassWithCustomStruct {
				Id = 42,
				MyStruct = new MyCustomStruct { MagicValue = "CustomSerialized" }
			};

			MyCustomStructFormatter.WasSerializedCalled = false;
			MyCustomStructFormatter.WasDeserializedCalled = false;

			var data = robust.Serialize(original);

			// It should be properly deserialized using the custom formatter
			var loaded = robust.Deserialize<ClassWithCustomStruct>(data);

			Assert.Equal(42, loaded.Id);
			Assert.Equal("CustomSerialized", loaded.MyStruct.MagicValue);
			Assert.True(MyCustomStructFormatter.WasSerializedCalled, "Custom Serialize was not called");
			Assert.True(MyCustomStructFormatter.WasDeserializedCalled, "Custom Deserialize was not called");
		}

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
