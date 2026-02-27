using Ceras;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// Tests that verify the source-generator produces formatters that actually work.
    /// The types below are marked with [CerasSerializable] so the Ceras.SourceGenerator
    /// will emit formatter code into RobustFormatters.g.cs at compile time.
    /// </summary>
    public class SourceGeneratorFormatters : TestBase
    {
        [Fact]
        public void GeneratedFormatter_RoundTrips_SimplePublicFields()
        {
            var config = new SerializerConfig();
            RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasSerializer(config);

            var original = new SimpleFieldModel
            {
                Name = "Alice",
                Age  = 30,
            };

            var clone = ceras.Deserialize<SimpleFieldModel>(ceras.Serialize(original));

            Assert.Equal(original.Name, clone.Name);
            Assert.Equal(original.Age,  clone.Age);
        }

        [Fact]
        public void GeneratedFormatter_RoundTrips_PublicProperties()
        {
            var config = new SerializerConfig();
            RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasSerializer(config);

            var original = new PropertyModel { X = 3.14f, Y = -1.0f };

            var clone = ceras.Deserialize<PropertyModel>(ceras.Serialize(original));

            Assert.Equal(original.X, clone.X);
            Assert.Equal(original.Y, clone.Y);
        }

        [Fact]
        public void GeneratedFormatter_ProducesIdenticalBytes_To_ReflectionFallbackFormatter()
        {
            // Serialize once with the generated formatter, once with ReflectionFallbackFormatter.
            // Bytes must be identical (same schema, same member order).

            var generatedConfig = new SerializerConfig();
            RobustFormatters.UseRobustFormatters(generatedConfig);
            var generatedCeras = new CerasSerializer(generatedConfig);

            var dynamicConfig = new SerializerConfig();
            var dynamicCeras  = new CerasSerializer(dynamicConfig);

            var obj = new SimpleFieldModel { Name = "Bob", Age = 42 };

            byte[] generatedBytes = generatedCeras.Serialize(obj);
            byte[] dynamicBytes   = dynamicCeras.Serialize(obj);

            Assert.Equal(dynamicBytes, generatedBytes);
        }

        [Fact]
        public void GeneratedFormatter_CrossDeserialize()
        {
            // Bytes written by ReflectionFallbackFormatter can be read by the generated formatter.
            var dynamicConfig = new SerializerConfig();
            var dynamicCeras  = new CerasSerializer(dynamicConfig);

            var generatedConfig = new SerializerConfig();
            RobustFormatters.UseRobustFormatters(generatedConfig);
            var generatedCeras = new CerasSerializer(generatedConfig);

            var original = new SimpleFieldModel { Name = "Cross", Age = 99 };
            byte[] bytes = dynamicCeras.Serialize(original);
            var clone = generatedCeras.Deserialize<SimpleFieldModel>(bytes);

            Assert.Equal(original.Name, clone.Name);
            Assert.Equal(original.Age,  clone.Age);
        }

        [Fact]
        public void GeneratedFormatter_RoundTrips_ReadonlyCtorModel()
        {
            var config = new SerializerConfig();
            RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasSerializer(config);

            var original = new ReadonlyCtorModel(42);
            var clone = ceras.Deserialize<ReadonlyCtorModel>(ceras.Serialize(original));

            Assert.Equal(original.Value, clone.Value);
        }

        [Fact]
        public void GeneratedFormatter_RoundTrips_InitCtorModel()
        {
            var config = new SerializerConfig();
            RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasSerializer(config);

            var original = new InitCtorModel(123);
            var clone = ceras.Deserialize<InitCtorModel>(ceras.Serialize(original));

            Assert.Equal(original.Value, clone.Value);
        }
    }

    // -------------------------------------------------------------------------
    // Test types — marked with [CerasSerializable] so the source generator
    // emits formatters for them at compile time.
    // -------------------------------------------------------------------------

    [CerasSerializable]
    public class SimpleFieldModel
    {
        public string Name;
        public int    Age;
    }

    [CerasSerializable]
    [MemberConfig(TargetMember.PublicProperties)]
    public class PropertyModel
    {
        public float X { get; set; }
        public float Y { get; set; }
    }

    [CerasSerializable]
    public class ReadonlyCtorModel
    {
        public readonly int Value;
        
        [CerasConstructor]
        public ReadonlyCtorModel(int value)
        {
            Value = value;
        }
    }

    [CerasSerializable]
    public class InitCtorModel
    {
        public int Value { get; init; }
        
        [CerasConstructor]
        public InitCtorModel(int value)
        {
            Value = value;
        }
    }
}
