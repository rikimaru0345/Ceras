
using System;
using System.Collections.Generic;
using System.Linq;
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
        public partial class DiscoveryRoot
    {
        public DiscoveryChild Child;
        public List<DiscoveryNestedChild> NestedChildren;
        public IPolymorphicBase Polymorphic;
        public DiscoveryRecord Record;
        public BannedType Banned;
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

    // Discovery through getter-only members (Reference types)
    [CerasSerializable]
        public partial class GetterOnlyRoot
    {
        // Reference type field initialized in-place
        public readonly DiscoveryChild ChildField = new DiscoveryChild { Name = "FieldInit" };
        
        // Reference type property initialized in-place
        public DiscoveryChild ChildProp { get; } = new DiscoveryChild { Name = "PropInit" };

        // Reference type initialized in ctor
        public readonly DiscoveryChild ChildCtor;

        // Value types should be IGNORED even if they are readonly/getter-only
        // (because Ceras can't populate them in-place)
        public readonly int IgnoredInt = 42;
        public float IgnoredFloat { get; } = 3.14f;

        public GetterOnlyRoot()
        {
            ChildCtor = new DiscoveryChild { Name = "CtorInit" };
        }
    }

    // A type that has private members but DOES NOT need to be partial
    // because all private members are handled via ctor or populating in-place
    [CerasSerializable]
    public class NoPartialNeeded
    {
        [Include]
        public string Name { get; private set; } // Read accessible! Write handled by ctor

        [Include]
        public DiscoveryChild Child { get; } = new DiscoveryChild(); // Read accessible! Handled by in-place pop

        public NoPartialNeeded(string name)
        {
            Name = name;
        }
    }

    [Exclude]
    public class BannedType
    {
        public int Secret;
    }

    public class DiscoveryTests : TestBase
    {
        [Fact]
        public void NoPartialNeeded_WhenMembersHandledByCtorOrPop()
        {
            var config = new CerasRobustConfig();
            RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasSerializer(config);

            // 1. Verify discovery worked
            Assert.NotNull(config.ConfigType<NoPartialNeeded>().CustomFormatter);

            // 2. Round-trip test
            var original = new NoPartialNeeded("OriginalName");
            original.Child.Name = "OriginalChild";

            var data = ceras.Serialize(original);
            var clone = ceras.Deserialize<NoPartialNeeded>(data);

            // 3. Assert values
            Assert.Equal("OriginalName", clone.Name);
            Assert.Equal("OriginalChild", clone.Child.Name);
        }

        [Fact]
        public void GetterOnlyDiscovery_WorksForReferenceTypes()
        {
            var config = new CerasRobustConfig();
            RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasSerializer(config);

            // 1. Verify that ChildField, ChildProp, and ChildCtor were all discovered
            // (meaning DiscoveryChild has a generated formatter assigned)
            Assert.NotNull(config.ConfigType<DiscoveryChild>().CustomFormatter);
            Assert.NotNull(config.ConfigType<GetterOnlyRoot>().CustomFormatter);

            // 2. Round-trip test
            var original = new GetterOnlyRoot();
            original.ChildField.Name = "ModifiedField";
            original.ChildProp.Name = "ModifiedProp";
            original.ChildCtor.Name = "ModifiedCtor";

            var data = ceras.Serialize(original);
            
            // Create a fresh instance for deserialization to simulate a real scenario
            // where the ctor has already run and we want to populate the existing objects.
            var clone = new GetterOnlyRoot();
            ceras.Deserialize(ref clone, data);

            // 3. Assert population worked
            Assert.Equal("ModifiedField", clone.ChildField.Name);
            Assert.Equal("ModifiedProp", clone.ChildProp.Name);
            Assert.Equal("ModifiedCtor", clone.ChildCtor.Name);
        }

        [Fact]
        public void RecursiveDiscovery_And_Records_Work()
        {
            var config = new CerasRobustConfig();
            // This registers all formatters that the source generator produced
            RobustFormatters.UseRobustFormatters(config);

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

        [Fact]
        public void BannedTypeWorks()
        {
            var config = new CerasRobustConfig();
            RobustFormatters.UseRobustFormatters(config);

            var rootType = typeof(DiscoveryRoot);
            var tc = config.ConfigType(rootType);
            
            var bannedMember = tc.Members.First(m => m.Member.Name == "Banned");
            Assert.False(bannedMember.ComputeFinalInclusionFast());

            // Round-trip check
            var ceras = new CerasSerializer(config);
            var original = new DiscoveryRoot { Banned = new BannedType { Secret = 999 } };
            var data = ceras.Serialize(original);
            var clone = ceras.Deserialize<DiscoveryRoot>(data);

            Assert.Null(clone.Banned);
        }
    }
}
