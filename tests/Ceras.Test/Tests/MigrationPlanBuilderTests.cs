using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using Ceras;
using Ceras.Format;

namespace Ceras.Test
{
    public class MigrationPlanBuilderTests
    {
        [CerasSerializable]
        public class AutoPropertyClass
        {
            public int Map { get; internal set; }
        }

        [Fact]
        public void BuildCurrentFieldMap_ExtractsAutoPropertyNames()
        {
            var type = typeof(AutoPropertyClass);
            
            // We need to test the internal BuildCurrentFieldMap method. 
            // Since it's internal/private, we'll use reflection for the test.
            var method = typeof(SchemaRegistry).Assembly.GetType("Ceras.Format.MigrationPlanBuilder")
                .GetMethod("BuildCurrentFieldMap", BindingFlags.Static | BindingFlags.NonPublic);
            
            Assert.NotNull(method);
            
            var map = (Dictionary<string, FieldInfo>)method.Invoke(null, new object[] { type });
            
            Assert.NotNull(map);
            
            // Should contain "Map" (extracted from <Map>k__BackingField)
            Assert.True(map.ContainsKey("Map"), "Map should contain extracted property name 'Map'");
        }
    }
}
