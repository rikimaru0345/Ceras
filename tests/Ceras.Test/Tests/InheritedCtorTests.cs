using System;
using Xunit;
using Ceras;
using Ceras.Generated;

namespace Ceras.Test.Tests
{
    public struct TestVector3
    {
        public float X, Y, Z;
        public TestVector3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    [CerasSerializable]
    public abstract class BaseVehicle
    {
        public TestVector3 Position { get; set; }
        
        // This simulates a field that MUST be initialized
        [Exclude]
        public bool InitializedByCtor = false;

        protected BaseVehicle(TestVector3 position)
        {
            Position = position;
            InitializedByCtor = true;
        }
    }

    [CerasSerializable]
    public class DerivedWagon : BaseVehicle
    {
        public string WagonName { get; set; }

        [CerasConstructor]
        public DerivedWagon(TestVector3 position, string wagonName) : base(position)
        {
            WagonName = wagonName;
        }
    }

    [CerasSerializable]
    public class RecordBase
    {
        public int Id { get; init; }
    }

    [CerasSerializable]
    public class RecordDerived : RecordBase
    {
        public string Name { get; init; }

        [CerasConstructor]
        public RecordDerived(int id, string name)
        {
            Id = id;
            Name = name;
        }
    }

    [CerasSerializable]
    public class PrivateCtorModel
    {
        public int Value { get; set; }

        private PrivateCtorModel()
        {
            Value = 123;
        }

        public static PrivateCtorModel Create() => new PrivateCtorModel();
    }

    public class InheritedCtorTests : TestBase
    {
        [Fact]
        public void TestPrivateConstructorSupport()
        {
            var config = new CerasRawConfig();
            config.AllowReflectionFallback = false;
            RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasFastSerializer(config);

            var original = PrivateCtorModel.Create();
            original.Value = 456;
            var bytes = ceras.Serialize(original);
            var cloned = ceras.Deserialize<PrivateCtorModel>(bytes);

            Assert.Equal(original.Value, cloned.Value);
        }

        [Fact]
        public void TestInheritedPropertyMapping()
        {
            var config = new CerasRawConfig();
            config.AllowReflectionFallback = false;
            RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasFastSerializer(config);

            var original = new DerivedWagon(new TestVector3(1, 2, 3), "TestWagon");
            var bytes = ceras.Serialize(original);
            var cloned = ceras.Deserialize<DerivedWagon>(bytes);

            Assert.Equal(original.Position.X, cloned.Position.X);
            Assert.Equal(original.Position.Y, cloned.Position.Y);
            Assert.Equal(original.Position.Z, cloned.Position.Z);
            Assert.Equal(original.WagonName, cloned.WagonName);
            Assert.True(cloned.InitializedByCtor, "Constructor was not called or field was not initialized");
        }

        [Fact]
        public void TestInheritedInitOnlyPropertyMapping()
        {
            var config = new CerasRawConfig();
            config.AllowReflectionFallback = false;
            RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasFastSerializer(config);

            var original = new RecordDerived(42, "TestRecord");
            var bytes = ceras.Serialize(original);
            var cloned = ceras.Deserialize<RecordDerived>(bytes);

            Assert.Equal(original.Id, cloned.Id);
            Assert.Equal(original.Name, cloned.Name);
        }
    }
}
