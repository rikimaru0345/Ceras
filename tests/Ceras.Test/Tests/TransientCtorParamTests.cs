using Xunit;
using Ceras.Generated;

namespace Ceras.Test
{
    /// <summary>
    /// A constructor parameter that does not correspond to a serializable member is "transient":
    /// it is construction-time input only and is not stored in the save. As long as it has a
    /// default value, the generator passes that default when it invokes the constructor, and the
    /// real state is then assigned from the deserialized members.
    /// </summary>
    [CerasSerializable]
    public class TransientCtorModel
    {
        public int Value;
        public string Name;

        public static bool LastTransientFlag;

        [CerasConstructor]
        public TransientCtorModel(int value, string name, bool transientFlag = true)
        {
            // transientFlag deliberately does not map to any member.
            LastTransientFlag = transientFlag;
            Value = value;
            Name = name;
        }
    }

    public class TransientCtorParamTests : TestBase
    {
        [Fact]
        public void OptionalCtorParam_IsNotSerialized_AndRoundTrips()
        {
            var ceras = new CerasRobustSerializer(config => config.UseGeneratedFormatters());

            var original = new TransientCtorModel(42, "hello");
            var data = ceras.Save(original, 1);

            TransientCtorModel.LastTransientFlag = false;
            var clone = ceras.Load<TransientCtorModel>(data, 1);

            // The members round-trip and the constructor ran with its declared default (true).
            Assert.True(TransientCtorModel.LastTransientFlag);
            Assert.Equal(42, clone.Value);
            Assert.Equal("hello", clone.Name);
        }
    }
}
