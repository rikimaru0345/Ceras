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
    public class RegressionTests : TestBase
    {
        [Fact]
        public void SourceGenerator_Collections_MaintainReferenceTracking()
        {
            // This test verifies that the Source Generator wraps collections in ReferenceFormatter
            // so that if the same collection instance is seen twice, it is correctly shared.
            // Before the fix, SG used CollectionFormatter directly, which bypasses reference tracking.

            var ceras = new CerasFastSerializer(config => {
                RobustFormatters.UseRobustFormatters(config, "Tests");
            });

            var list = new List<int> { 1, 2, 3 };
            var original = new CollectionSharingModel
            {
                A = list,
                B = list
            };

            var bytes = ceras.Serialize(original);
            var clone = ceras.Deserialize<CollectionSharingModel>(bytes);

            Assert.Same(clone.A, clone.B);
            Assert.Equal(original.A, clone.A);
        }

        [Fact]
        public void RobustMode_PolymorphicRoundTrip()
        {
            // This test verifies that CerasRobust correctly handles types 
            // when encountered in a polymorphic context (e.g. through an interface).

            var ceras = new CerasRobustSerializer(config => {
                RobustFormatters.UseRobustFormatters(config, "Tests");
                
                // Manual registration for types used in Robust mode
                var registry = config.SchemaRegistry;
                registry.Register(typeof(RobustRoot), null);
                registry.Register(typeof(RobustChild), null);
                registry.Register(typeof(IRobustInterface), new SchemaTypeInfo("IRobustInterface", Array.Empty<string>(), SchemaTypeKind.Class, Array.Empty<SchemaFieldInfo>(), 0));
            });

            var original = new RobustRoot
            {
                Child = new RobustChild { Value = 42 }
            };

            var bytes = ceras.Save(original, 1);
            var clone = ceras.Load<RobustRoot>(bytes, 1);

            Assert.IsType<RobustChild>(clone.Child);
            Assert.Equal(((RobustChild)original.Child).Value, ((RobustChild)clone.Child).Value);
        }

        [Fact]
        public void RobustMode_InvalidCastException_Repro()
        {
            // This test reproduces the specific scenario that led to the InvalidCastException:
            // 1. An object (List) is first encountered through a polymorphic field.
            // 2. The same object is later encountered through a typed field.
            // If reference tracking is broken for collections in SG, the ID mapping gets desynced.

            var ceras = new CerasRobustSerializer(config => {
                RobustFormatters.UseRobustFormatters(config, "Tests");

                var registry = config.SchemaRegistry;
                registry.Register(typeof(DesyncModel), null);
                registry.Register(typeof(DesyncChild), null);
                registry.Register(typeof(List<DesyncChild>), null);
                registry.Register(typeof(DesyncChild[]), null);
                registry.Register(typeof(IDesyncInterface), new SchemaTypeInfo("IDesyncInterface", Array.Empty<string>(), SchemaTypeKind.Class, Array.Empty<SchemaFieldInfo>(), 0));
            });

            var child = new DesyncChild { Name = "Child" };
            var list = new List<DesyncChild> { child };
            
            var original = new DesyncModel
            {
                PolymorphicList = list,
                TypedList = list
            };

            var bytes = ceras.Save(original, 1);
            var clone = ceras.Load<DesyncModel>(bytes, 1);

            Assert.Same(clone.PolymorphicList, clone.TypedList);
            Assert.Single((List<DesyncChild>)clone.PolymorphicList);
            Assert.Equal("Child", ((List<DesyncChild>)clone.PolymorphicList)[0].Name);
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CollectionSharingModel
    {
        public List<int> A;
        public List<int> B;
    }

    public interface IRobustInterface { }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class RobustChild : IRobustInterface
    {
        public int Value;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class RobustRoot
    {
        public IRobustInterface Child;
    }

    public interface IDesyncInterface { }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class DesyncChild : IDesyncInterface
    {
        public string Name;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class DesyncModel
    {
        public object PolymorphicList; // List<DesyncChild> as object
        public List<DesyncChild> TypedList;
    }
}
