
using System;
using Xunit;
using Ceras;
using Ceras.Format;

namespace Ceras.Test
{
    public class StructAutoPropBug : TestBase
    {
        public struct CostItem
        {
            public string ItemName { get; set; }
            public int Count { get; set; }

            public CostItem(string itemName, int count)
            {
                ItemName = itemName;
                Count = count;
            }
        }

        public class Container
        {
            public CostItem Cost;
        }

        [Fact]
        public void StructWithAutoProps_RobustSerialization_FailsWithoutFix()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            config.Advanced.AllowReflectionFallback = true;
            var ceras = new CerasSerializer(config);

            var original = new Container
            {
                Cost = new CostItem("Iron", 10)
            };

            // This should trigger the warning if we don't fix it,
            // or fail to serialize the properties.
            var data = CerasRobust.Save(original, 1, ceras);
            var clone = CerasRobust.Load<Container>(data, 1, ceras);

            Assert.Equal("Iron", clone.Cost.ItemName);
            Assert.Equal(10, clone.Cost.Count);
        }
    }
}
