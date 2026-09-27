
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Helpers;
using Ceras.Generated;

namespace Ceras.Test
{
    public class SchemaReproTests : TestBase
    {
        [CerasContext("Tests")]
        [CerasSerializable]
        public class ReproModel
        {
            // We need a type that Ceras can't resolve by name.
            // Using a full name that definitely doesn't exist.
            public List<NonExistentType> MyList;
        }

        // We can't easily make a field of a non-existent type in C#,
        // but we can manually register a schema that claims to have one.
        
        [CerasContext("Tests")]
        [CerasSerializable]
        public class MockType { }

        [Fact]
        public void Bake_FailsLoudly_ForUnresolvedFieldTypes()
        {
            var robust = new CerasRobustSerializer(config =>
            {
                // Manually register a type with a field pointing to a non-existent type.
                var info = new SchemaTypeInfo(
                    stableName: "Ceras.Test.MockType",
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.Class,
                    fields: new[] {
                        new SchemaFieldInfo("UnresolvedField", Array.Empty<string>(), "Some.Missing.Type", true)
                    },
                    fixedByteSize: 0
                );
                config.SchemaRegistry.Register(typeof(MockType), info);
            });

            // Baking must fail loudly instead of silently writing type id 0.
            var ex = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(() => robust.Save(new MockType(), 1));
            Assert.Contains("MockType", ex.Message);
            Assert.Contains("UnresolvedField", ex.Message);
            Assert.Contains("Some.Missing.Type", ex.Message);
        }

        [Fact]
        public void Bake_FailsLoudly_ForUnresolvedGenericArguments()
        {
            var robust = new CerasRobustSerializer(config =>
            {
                // Register a generic instantiation with an unresolved argument.
                var info = new SchemaTypeInfo(
                    stableName: "System.Collections.Generic.List`1[Some.Missing.Type]",
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.GenericInstantiation,
                    fields: Array.Empty<SchemaFieldInfo>(),
                    fixedByteSize: 0,
                    underlyingTypeName: "System.Collections.Generic.List`1",
                    argTypeNames: new[] { "Some.Missing.Type" }
                );
                config.SchemaRegistry.Register(typeof(List<object>), info);
            });

            // Baking must fail loudly instead of writing type id 0 for the argument.
            var ex = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(() => robust.Save(new List<object>(), 1));
            Assert.Contains("Some.Missing.Type", ex.Message);
            Assert.Contains("generic argument 0", ex.Message);
        }

        [Fact]
        public void Bake_FailsLoudly_ForUndeclaredFieldType()
        {
            // Strict mode: no reflection fallback, so a field type that is not in the generated
            // manifest and is not a structural/BCL shape is a declaration bug.
            var robust = new CerasRobustSerializer(config =>
            {
                config.AllowReflectionFallback = false;
                config.SchemaRegistry.Register(typeof(StrictHolder), new SchemaTypeInfo(
                    stableName: "Ceras.Test.SchemaReproTests+StrictHolder",
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.Class,
                    fields: new[] {
                        new SchemaFieldInfo("Value", Array.Empty<string>(),
                            "Ceras.Test.SchemaReproTests+UndeclaredLeaf", true)
                    },
                    fixedByteSize: 0));
            });

            var ex = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(() => robust.Save(new StrictHolder(), 1));
            Assert.Contains("StrictHolder", ex.Message);
            Assert.Contains("Value", ex.Message);
            Assert.Contains("UndeclaredLeaf", ex.Message);
            Assert.Contains("CerasKnownType", ex.Message);
        }

        /// <summary>
        /// A normal model with enum + nested built-in collection fields must still bake and
        /// round-trip: the fail-loud check only fires for genuinely undeclared types.
        /// </summary>
        [Fact]
        public void EnumAndNestedCollectionModel_StillBakes_AndRoundTrips()
        {
            var robust = new CerasRobustSerializer(config => config.UseGeneratedFormatters("Tests"));

            var model = new NormalModel
            {
                Kind = NormalKind.Second,
                Lookup = new Dictionary<string, List<int>>
                {
                    ["a"] = new List<int> { 1, 2, 3 },
                    ["b"] = new List<int> { },
                }
            };

            var bytes = robust.Save(model, 1);
            var loaded = new CerasRobustSerializer(config => config.UseGeneratedFormatters("Tests"))
                .Load<NormalModel>(bytes, 1);

            Assert.Equal(NormalKind.Second, loaded.Kind);
            Assert.Equal(2, loaded.Lookup.Count);
            Assert.Equal(new[] { 1, 2, 3 }, loaded.Lookup["a"]);
            Assert.Empty(loaded.Lookup["b"]);
        }

        [CerasContext("Tests")]
        [CerasSerializable]
        public class StrictHolder
        {
            public UndeclaredLeaf Value;
        }

        // Deliberately not declared in any context.
        public class UndeclaredLeaf
        {
            public int X;
        }

        public enum NormalKind { First, Second }

        [CerasContext("Tests")]
        [CerasSerializable]
        public class NormalModel
        {
            public NormalKind Kind;
            public Dictionary<string, List<int>> Lookup;
        }
    }

    // Placeholder for type-system tricks
    public class NonExistentType { }
}
