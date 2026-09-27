using System.Collections.Generic;
using Xunit;

namespace Ceras.Test
{
    // Promoted from samples/LiveTesting/Program.cs :: ReadonlyTest, subcases #2B, #3 and #4.
    //
    // Reusing an existing object (Deserialize(ref existing, data)) on the reflection-based path:
    // readonly members are written through reflection, a readonly reference member of the wrong
    // runtime type is replaced rather than merged into, excluded members keep the existing value,
    // and a readonly collection member is refilled in place instead of being appended to.
    public class ReadonlyMemberReuseTests
    {
        [Fact]
        public void ExcludedMembers_KeepTheExistingValue_AndTheIncludedOneIsReusedInPlace()
        {
            var config = new CerasRawConfig();
            config.ConfigType<ReadonlyFields>()
                  .ConfigMember(f => f.Int).Exclude()
                  .ConfigMember(f => f.String).Exclude()
                  .ConfigMember(f => f.Container).Include();

            var ceras = new CerasFastSerializer(config);

            var original = new ReadonlyFields(5, "55555", new ContainerA { Setting1 = 555555, Setting2 = "555555555" });
            var data = ceras.Serialize(original);

            var existingContainer = new ContainerA { Setting1 = -1, Setting2 = "this should get overwritten" };
            var target = new ReadonlyFields(6, "66666", existingContainer);

            ceras.Deserialize(ref target, data);

            // The excluded members still hold what the existing instance held.
            Assert.Equal(6, target.Int);
            Assert.Equal("66666", target.String);

            // The included reference member was reused, not replaced ...
            Assert.Same(existingContainer, target.Container);
            // ... and its content was overwritten in place.
            Assert.Equal(555555, existingContainer.Setting1);
            Assert.Equal("555555555", existingContainer.Setting2);
        }

        [Fact]
        public void ReusedInstance_GetsReadonlyMembersWritten_EvenWhenTheExistingTypeIsWrong()
        {
            var ceras = new CerasFastSerializer();

            var original = new ReadonlyFields(5, "55555", new ContainerA { Setting1 = 324, Setting2 = "1134" });
            var data = ceras.Serialize(original);

            var target = new ReadonlyFields(123, null, new ContainerB());
            ceras.Deserialize(ref target, data);

            Assert.NotSame(original, target);

            // Readonly value- and reference-typed members are written like any other.
            Assert.Equal(5, target.Int);
            Assert.Equal("55555", target.String);

            // The existing container was of the wrong type, so a correctly typed instance is
            // created and written into the readonly field instead.
            var container = Assert.IsType<ContainerA>(target.Container);
            Assert.Equal(324, container.Setting1);
            Assert.Equal("1134", container.Setting2);
        }

        [Fact]
        public void ReadonlyCollectionMember_IsClearedAndRefilledInPlace()
        {
            var ceras = new CerasFastSerializer();

            var original = new ReadonlyListHolder();
            original.Numbers.Clear();
            original.Numbers.Add(234);

            var data = ceras.Serialize(original);

            var target = new ReadonlyListHolder();
            var targetList = target.Numbers;
            ceras.Deserialize(ref target, data);

            // The collection created by the constructor is reused ...
            Assert.Same(targetList, target.Numbers);
            // ... and refilled, not appended to (the field initializer put 4 entries in it).
            Assert.Single(target.Numbers);
            Assert.Equal(234, target.Numbers[0]);
        }

        public class ReadonlyFields
        {
            public readonly int Int = 1;
            public readonly string String = "a";
            public readonly ReadonlyContainerBase Container = null;

            public ReadonlyFields()
            {
            }

            public ReadonlyFields(int i, string s, ReadonlyContainerBase c)
            {
                Int = i;
                String = s;
                Container = c;
            }
        }

        public abstract class ReadonlyContainerBase
        {
        }

        public class ContainerA : ReadonlyContainerBase
        {
            public int Setting1 = 2;
            public string Setting2 = "b";
        }

        public class ContainerB : ReadonlyContainerBase
        {
            public float Float = 1;
            public byte Byte = 1;
            public string String = "c";
        }

        public class ReadonlyListHolder
        {
            // The field initializer stands in for "a collection the constructor already filled".
            public readonly List<int> Numbers = new List<int> { -1, -1, -1, -1 };
        }
    }
}
