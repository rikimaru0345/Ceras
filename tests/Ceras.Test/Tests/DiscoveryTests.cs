
using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Formatters;
using Ceras.Helpers;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test
{
    // A root type explicitly marked for serialization
    [CerasSerializable]
    [MemberConfig(TargetMember.PublicFields)]
    public partial class DiscoveryRoot
    {
        public DiscoveryChild Child;
        public List<DiscoveryNestedChild> NestedChildren;
        public IPolymorphicBase Polymorphic;
        public DiscoveryRecord Record;
    }

    // Discovered via 'Child' field
    public class DiscoveryChild
    {
        public string Name;
    }

    // Discovered via 'List<DiscoveryNestedChild>'
    public class DiscoveryNestedChild
    {
        public int Id;
    }

    // Discovered via 'Polymorphic' field
    public interface IPolymorphicBase {}
    
    // Discovered because it implements IPolymorphicBase which was found via propagation
    public class PolymorphicImpl : IPolymorphicBase 
    { 
        public int X; 

        public override bool Equals(object obj) => obj is PolymorphicImpl other && X == other.X;
        public override int GetHashCode() => X.GetHashCode();
    }

    // Records should use their primary constructor automatically.
    // They are discovered via propagation from DiscoveryRoot.
    public record DiscoveryRecord(int Id, string Name);

    public class DiscoveryTests : TestBase
    {
        [Fact]
        public void RecursiveDiscovery_And_Records_Work()
        {
            var config = new SerializerConfig();
            // This registers all formatters that the source generator produced
            GeneratedFormatters.UseFormatters(config);

            var ceras = new CerasSerializer(config);

            // 1. Verify all expected types have generated formatters assigned
            // (Note: ConfigType<T> returns TypeConfig<T> which has CustomFormatter property)
            Assert.NotNull(config.ConfigType<DiscoveryRoot>().CustomFormatter);
            Assert.NotNull(config.ConfigType<DiscoveryChild>().CustomFormatter);
            Assert.NotNull(config.ConfigType<DiscoveryNestedChild>().CustomFormatter);
            Assert.NotNull(config.ConfigType<PolymorphicImpl>().CustomFormatter);
            Assert.NotNull(config.ConfigType<DiscoveryRecord>().CustomFormatter);

            // 2. Full Round-trip test
            var original = new DiscoveryRoot
            {
                Child = new DiscoveryChild { Name = "Child" },
                NestedChildren = new List<DiscoveryNestedChild> 
                { 
                    new DiscoveryNestedChild { Id = 1 }, 
                    new DiscoveryNestedChild { Id = 2 } 
                },
                Polymorphic = new PolymorphicImpl { X = 42 },
                Record = new DiscoveryRecord(123, "RecordItem")
            };

            var data = ceras.Serialize(original);
            var clone = ceras.Deserialize<DiscoveryRoot>(data);

            // 3. Assert equality
            Assert.NotNull(clone.Child);
            Assert.Equal(original.Child.Name, clone.Child.Name);
            
            Assert.Equal(original.NestedChildren.Count, clone.NestedChildren.Count);
            Assert.Equal(original.NestedChildren[0].Id, clone.NestedChildren[0].Id);
            Assert.Equal(original.NestedChildren[1].Id, clone.NestedChildren[1].Id);

            Assert.IsType<PolymorphicImpl>(clone.Polymorphic);
            Assert.Equal(original.Polymorphic, clone.Polymorphic);

            Assert.Equal(original.Record, clone.Record);
            Assert.Equal(123, clone.Record.Id);
            Assert.Equal("RecordItem", clone.Record.Name);
        }
    }
}
