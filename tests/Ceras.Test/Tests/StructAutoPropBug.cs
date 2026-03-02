
using System;
using Xunit;
using Ceras;
using Ceras.Format;

namespace Ceras.Test
{
    [CerasSerializable]
    public struct CostItem
    {
        public string ItemName;
        public int Count;

        public CostItem(string itemName, int count)
        {
            ItemName = itemName;
            Count = count;
        }
    }

    public class StructAutoPropBug : TestBase
    {
        public class Container
        {
            public CostItem Cost;
        }

        [Fact]
        public void StructWithAutoProps_RobustSerialization_FailsWithoutFix()
        {
            var robust = new CerasRobustSerializer(config => config.AllowReflectionFallback = true);

            var original = new Container
            {
                Cost = new CostItem("Iron", 10)
            };

            // This should trigger the warning if we don't fix it,
            // or fail to serialize the properties.
            var data = robust.Save(original, 1);
            var clone = robust.Load<Container>(data, 1);

            Assert.Equal("Iron", clone.Cost.ItemName);
            Assert.Equal(10, clone.Cost.Count);
        }
    }
}
