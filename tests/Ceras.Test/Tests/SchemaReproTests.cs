
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Helpers;

namespace Ceras.Test
{
    public class SchemaReproTests : TestBase
    {
        [CerasSerializable]
        public class ReproModel
        {
            // We need a type that Ceras can't resolve by name.
            // Using a full name that definitely doesn't exist.
            public List<NonExistentType> MyList;
        }

        // We can't easily make a field of a non-existent type in C#,
        // but we can manually register a schema that claims to have one.
        
        [CerasSerializable]
        public class MockType { }

        [Fact]
        public void DumpSchema_ShowsID0_ForUnresolvedFieldTypes()
        {
            var config = new CerasRobustConfig();
            CerasRobust.Configure(config);
            
            
            var registry = ((CerasRobustConfig)config).SchemaRegistry;

            // 1. Manually register a type with a field pointing to a non-existent type
            var info = new SchemaTypeInfo(
                stableName: "Ceras.Test.MockType",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Class,
                fields: new[] {
                    new SchemaFieldInfo("UnresolvedField", Array.Empty<string>(), "Some.Missing.Type", true)
                },
                fixedByteSize: 0
            );

            registry.Register(typeof(MockType), info);

            // 2. Serialize something (this writes the schema section)
            var ceras = new CerasSerializer(config);
            var data = CerasRobust.Save(new MockType(), 1, ceras);

            // 3. Dump the schema from the bytes
            var dump = CerasRobust.DumpSchema(data);

            // 4. Check if it contains [ID:0]
            // The expected output format from DumpEntry (via DumpSchema) is:
            // [TypeName] (Mono/Poly) FieldName
            // If TypeName is unresolved, it becomes ID:0
            Assert.Contains("[ID:0]  Mono  UnresolvedField", dump);
        }

        [Fact]
        public void DumpSchema_ShowsID0_ForUnresolvedGenericArguments()
        {
            var config = new CerasRobustConfig();
            CerasRobust.Configure(config);
            
            
            var registry = ((CerasRobustConfig)config).SchemaRegistry;

            // Register a generic instantiation with an unresolved argument
            var info = new SchemaTypeInfo(
                stableName: "System.Collections.Generic.List`1[Some.Missing.Type]",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.GenericInstantiation,
                fields: Array.Empty<SchemaFieldInfo>(),
                fixedByteSize: 0,
                underlyingTypeName: "System.Collections.Generic.List`1",
                argTypeNames: new[] { "Some.Missing.Type" }
            );

            registry.Register(typeof(List<object>), info);

            var ceras = new CerasSerializer(config);
            var data = CerasRobust.Save(new List<object>(), 1, ceras);

            var dump = CerasRobust.DumpSchema(data);

            // Generic arguments are dumped too. 
            // We need to see how they are dumped.
            // In CerasRobust.cs: ArgTypeNames = fs.ArgTypeIds?.Select(Resolve).ToArray()
            
            Assert.Contains("ID:0", dump);
        }
    }

    // Placeholder for type-system tricks
    public class NonExistentType { }
}
