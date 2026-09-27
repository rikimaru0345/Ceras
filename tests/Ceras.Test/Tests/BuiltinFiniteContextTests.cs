using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

[assembly: CerasKnownType(typeof(Ceras.Test.BuiltinShapes.BsHiddenKnown), "BuiltinShapes")]

namespace Ceras.Test.BuiltinShapes
{
    // An open generic declared as a root; only Pair<int,string> is reachable/declared.
    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsOpenGenericRoot
    {
        public BsPair<int, string> Declared;
    }

    [CerasSerializable]
    public class BsPair<K, V>
    {
        public K Key;
        public V Value;
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsListRoot
    {
        public List<int> Items;
    }

    [CerasSerializable]
    public class BsHiddenKnown
    {
        public int Value;
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsHiddenHolder
    {
        public object Hidden;
    }

    /// <summary>
    /// TODO 0.5 item 4: the finite-context rule must hold through structural registration. Knowing an
    /// open generic (List<>, or a user generic) does not authorize every closed combination: only the
    /// closed generic reachable from a context root (or declared with [CerasKnownType]) is registered.
    /// </summary>
    public class BuiltinFiniteContextTests : TestBase
    {
        const string Ctx = "BuiltinShapes";

        // A context has one finite manifest, so the allowed structural root shapes are declared up
        // front (an undeclared root is rejected with exactly this kind of message).
        static CerasRobustSerializer New() => New(typeof(List<string>), typeof(List<int>));

        static CerasRobustSerializer New(params Type[] roots) => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Ctx);
            c.DeclareRoots(roots);
        });

        [Fact]
        public void Declared_Closed_Collection_Root_RoundTrips_Both_Paths()
        {
            var s = New();
            var bytes = s.Save(new List<int> { 1, 2, 3 }, 1);
            Assert.Equal(new[] { 1, 2, 3 }, s.Load<List<int>>(bytes, 1));
            Assert.Equal(new[] { 1, 2, 3 }, s.Load<List<int>>(SlowPath(bytes), 1));
        }

        [Fact]
        public void Builtin_Structural_Root_Is_Allowed_And_Undeclared_User_Generic_Is_Rejected()
        {
            // List<string> is not reachable from a root, but built-in collections are described purely
            // structurally (definition + element), so they are safe as roots. A user generic whose closed
            // combination is not reachable is a different matter: knowing the open generic must not
            // authorize every closed combination.
            var s = New();
            var listBytes = s.Save(new List<string> { "x" }, 1);
            Assert.Equal(new[] { "x" }, s.Load<List<string>>(listBytes, 1));

            // An undeclared closed user generic is a declaration bug, so it is an actionable
            // CerasSchemaException (the strong form of this assertion lives in ReviewFindingsTests as
            // Undeclared_Closed_User_Generic_Throws_An_Actionable_Schema_Error).
            var ex = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(
                () => s.Save(new BsPair<int, int> { Key = 1, Value = 2 }, 1));
            Assert.Contains("BsPair", ex.Message, StringComparison.Ordinal);
            Assert.Contains("context", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Declared_User_Generic_RoundTrips_Both_Paths()
        {
            var s = New();
            var value = new BsOpenGenericRoot { Declared = new BsPair<int, string> { Key = 1, Value = "v" } };
            var bytes = s.Save(value, 1);
            foreach (var clone in new[] { s.Load<BsOpenGenericRoot>(bytes, 1), s.Load<BsOpenGenericRoot>(SlowPath(bytes), 1) })
            {
                Assert.NotNull(clone);
                Assert.NotNull(clone.Declared);
                Assert.Equal(1, clone.Declared.Key);
                Assert.Equal("v", clone.Declared.Value);
            }
        }

        [Fact]
        public void Registration_After_Seal_Throws()
        {
            var s = New();
            s.Save(new BsListRoot { Items = new List<int> { 1 } }, 1);
            var registry = s.Config.SchemaRegistry;
            Assert.True(registry.IsSealed);
            var ex = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(
                () => registry.Register(typeof(BsPair<int, int>), new SchemaTypeInfo(
                    "Ceras.Test.BuiltinShapes.BsPair`2<System.Int32,System.Int32>", Array.Empty<string>(),
                    SchemaTypeKind.Class, Array.Empty<SchemaFieldInfo>(), 0)));
            Assert.Contains("sealed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Hidden_Known_Type_Behind_Object_RoundTrips_Both_Paths()
        {
            // BsHiddenKnown is hidden behind an 'object' member; it is declared with [CerasKnownType] so
            // it is part of the context and can be written polymorphically.
            var s = New();
            object value = new BsHiddenKnown { Value = 77 };
            var bytes = s.Save(value, 1);
            var fast = Assert.IsType<BsHiddenKnown>(s.Load<object>(bytes, 1));
            var slow = Assert.IsType<BsHiddenKnown>(s.Load<object>(SlowPath(bytes), 1));
            Assert.Equal(77, fast.Value);
            Assert.Equal(77, slow.Value);
        }

        [Fact]
        public void First_Use_Ordering_Of_Structural_Types_Is_Deterministic()
        {
            // Two serializers in the same context must assign the same id to a structural collection
            // regardless of the order in which member types are first encountered.
            var s1 = New();
            var b1 = s1.Save(new BsListRoot { Items = new List<int> { 1 } }, 1);
            var s2 = New();
            var b2 = s2.Save(new BsOrderedRoot { First = new List<int> { 1 }, Second = new[] { 2 } }, 1);

            Assert.True(s1.Config.SchemaRegistry.TryGetId(typeof(List<int>), out int id1));
            Assert.True(s2.Config.SchemaRegistry.TryGetId(typeof(List<int>), out int id2));
            Assert.Equal(id1, id2);
            Assert.NotEqual(b1, b2);
        }
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsOrderedRoot
    {
        public List<int> First;
        public int[] Second;
    }
}
