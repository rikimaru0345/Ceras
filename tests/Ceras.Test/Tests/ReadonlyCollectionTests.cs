using System;
using System.Collections.Generic;
using Xunit;

namespace Ceras.Test.Tests
{
    public class ReadonlyCollectionTests : TestBase
    {
        SerializerConfig CreateSgConfig()
        {
            var config = new SerializerConfig();
            Ceras.Generated.GeneratedFormatters.UseFormatters(config);
            return config;
        }

        [Fact]
        public void ReadonlyListField_IsPopulatedInPlace()
        {
            var config = CreateSgConfig();
            var obj = new ReadonlyFieldObj();
            obj.items.Add(1.1f);
            
            var clone = Clone(obj, config);
            
            Assert.NotSame(obj, clone);
            Assert.NotNull(clone.items);
            Assert.Single(clone.items);
            Assert.Equal(1.1f, clone.items[0]);
        }

        [Fact]
        public void GetterOnlyProperty_IsPopulatedInPlace()
        {
            var config = CreateSgConfig();
            var obj = new GetterOnlyPropObj();
            obj.items.Add(2.2f);
            
            var clone = Clone(obj, config);
            
            Assert.NotSame(obj, clone);
            Assert.NotNull(clone.items);
            Assert.Single(clone.items);
            Assert.Equal(2.2f, clone.items[0]);
        }

        [Fact]
        public void ReadonlyDictionaryField_IsPopulatedInPlace()
        {
            var config = CreateSgConfig();
            var obj = new ReadonlyDictObj();
            obj.map.Add("test", 123);
            
            var clone = Clone(obj, config);
            
            Assert.NotSame(obj, clone);
            Assert.NotNull(clone.map);
            Assert.True(clone.map.ContainsKey("test"));
            Assert.Equal(123, clone.map["test"]);
        }

        [Fact]
        public void PrivateReadonlyCollection_WithPartial_IsPopulatedInPlace()
        {
            var config = CreateSgConfig();
            var obj = new PrivateReadonlyObj();
            obj.AddItem(42);
            
            var clone = Clone(obj, config);
            
            Assert.NotSame(obj, clone);
            Assert.Equal(1, clone.GetCount());
            Assert.Contains(42, clone.GetItems());
        }

        [Fact]
        public void ReadonlyField_Prepopulated_IsClearedAndRepopulated()
        {
            var obj = new ReadonlyFieldObj();
            obj.items.Add(1.1f);
            
            // The clone will have a new list instance (from the ctor), 
            // but ceras should clear it and add 1.1f.
            var clone = Clone(obj, CreateSgConfig());
            
            Assert.Single(clone.items);
            Assert.Equal(1.1f, clone.items[0]);

            // Add more to original
            obj.items.Add(2.2f);
            var clone2 = Clone(obj, CreateSgConfig());
            Assert.Equal(2, clone2.items.Count);
            Assert.Equal(1.1f, clone2.items[0]);
            Assert.Equal(2.2f, clone2.items[1]);
        }
    }

    [GenerateFormatter]
    public class ReadonlyFieldObj
    {
        public readonly List<float> items = new List<float>();
    }

    [GenerateFormatter]
    public class GetterOnlyPropObj
    {
        public List<float> items { get; } = new List<float>();
    }

    [GenerateFormatter]
    public class ReadonlyDictObj
    {
        public readonly Dictionary<string, int> map = new Dictionary<string, int>();
    }

    [GenerateFormatter]
    public partial class PrivateReadonlyObj
    {
        [Include]
        readonly List<int> _items = new List<int>();

        public void AddItem(int i) => _items.Add(i);
        public int GetCount() => _items.Count;
        public IEnumerable<int> GetItems() => _items;
    }
}
