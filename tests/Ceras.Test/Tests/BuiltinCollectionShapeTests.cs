// Collection *subclasses* the built-in collection formatter serves: their payload is an explicit
// element count, so the manifest must describe them structurally and both readers must refill them.
//
// The set of subclasses that are allowed shrank deliberately. ReadOnlyCollection<T> /
// ReadOnlyDictionary<TKey,TValue> and every List<T> / Dictionary<TKey,TValue> subclass are refused with
// an actionable error (see UnsupportedCollectionPolicyTests and Format/UnsupportedCollectionPolicy):
// a List/Dictionary subclass with only a copying constructor came back from a migration holding zero
// elements and no error, and a ReadOnlyCollection<T> subclass could not be written at all. What stays
// is everything else the built-in formatter serves — here a HashSet<T> subclass, annotated and
// unannotated, plus the exact built-in List<T>/Dictionary<TKey,TValue>, which the policy keeps and this
// file therefore keeps pinning.
//
// The refused families are still *declared* below (BsRefusedList, BsRefusedDict) so their types exist to
// be asserted on; they are simply not members of any annotated context, because a member of one is a
// compile error (CERAS033) and a context root of one is a compile error (CERAS034).
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.BuiltinShapes
{
    // ── Collection subclasses the policy keeps (HashSet<T> is not a List/Dictionary family) ──
    [CerasSerializable]
    public class BsAnnotatedSet<T> : HashSet<T> { }

    public class BsPlainSet : HashSet<int> { }

    public class BsPlainStringSet : HashSet<string> { }

    public class BsPlainQueue : Queue<int> { }

    public class BsPlainStack : Stack<int> { }

    // Refused families, kept as types only. Nothing in a [CerasContext] may name them.
    public class BsRefusedList<T> : List<T> { }

    public class BsRefusedDict : Dictionary<string, int> { }

    public class BsRefusedReadOnlyList : System.Collections.ObjectModel.ReadOnlyCollection<int>
    {
        public BsRefusedReadOnlyList(IList<int> inner) : base(inner) { }
    }


    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsCollectionSubclassHolder
    {
        public BsAnnotatedSet<int> Annotated;
        public BsPlainSet Plain;
        public List<int> ExactList;
        public Dictionary<string, int> ExactDict;
        public int After;
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsTwoSubclassHolder
    {
        public BsAnnotatedSet<int> First;
        public BsPlainStringSet Second;
        public int After;
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsJaggedHolder
    {
        public int[][] Jagged;
        public int After;
    }


    public class BuiltinCollectionShapeTests : TestBase
    {
        const string Ctx = "BuiltinShapes";

        // The allowed structural root shapes, declared up front for the finite manifest.
        static CerasRobustSerializer New() => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Ctx);
            c.DeclareRoots(typeof(int[]), typeof(int[][]), typeof(List<int>),
                typeof(BsAnnotatedSet<int>), typeof(BsPlainSet), typeof(BsPlainStringSet));
        });

        [Fact]
        public void CollectionSubclasses_Schema_Describes_Collection_Payload()
        {
            var s = New();
            // Save once so the structural entries are baked into the registry.
            s.Save(new BsCollectionSubclassHolder { Plain = new BsPlainSet { 1 } }, 1);

            Assert.True(s.Config.SchemaRegistry.TryGetInfo(typeof(BsPlainSet), out var info));
            Assert.Equal(SchemaTypeKind.GenericInstantiation, info.Kind);
            Assert.Equal("System.Collections.Generic.HashSet`1", info.UnderlyingTypeName);
            Assert.Equal(new[] { "System.Int32" }, info.ArgTypeNames);

            Assert.True(s.Config.SchemaRegistry.TryGetInfo(typeof(BsAnnotatedSet<int>), out var annotatedInfo));
            Assert.Equal(SchemaTypeKind.GenericInstantiation, annotatedInfo.Kind);
            Assert.Equal("System.Collections.Generic.HashSet`1", annotatedInfo.UnderlyingTypeName);
        }

        [Fact]
        public void CollectionSubclasses_RoundTrip_Both_Paths_With_Following_Field()
        {
            var s = New();
            var holder = new BsCollectionSubclassHolder
            {
                Annotated = new BsAnnotatedSet<int> { 1, 2, 3 },
                Plain = new BsPlainSet { 4, 5 },
                ExactList = new List<int> { 6 },
                ExactDict = new Dictionary<string, int> { ["a"] = 7, ["b"] = 8 },
                After = 4242,
            };

            var bytes = s.Save(holder, 1);
            var fast = s.Load<BsCollectionSubclassHolder>(bytes, 1);
            var slow = s.Load<BsCollectionSubclassHolder>(SlowPath(bytes), 1);

            foreach (var clone in new[] { fast, slow })
            {
                Assert.NotNull(clone);
                Assert.Equal(new[] { 1, 2, 3 }, clone.Annotated.OrderBy(x => x));
                Assert.Equal(new[] { 4, 5 }, clone.Plain.OrderBy(x => x));
                Assert.Equal(new[] { 6 }, clone.ExactList);
                Assert.Equal(7, clone.ExactDict["a"]);
                Assert.Equal(8, clone.ExactDict["b"]);
                Assert.Equal(4242, clone.After);
            }

            // The subclass survives, which is the part a structural schema alone would not prove.
            Assert.IsType<BsAnnotatedSet<int>>(fast.Annotated);
            Assert.IsType<BsPlainSet>(fast.Plain);
        }

        [Fact]
        public void Queue_And_Stack_Subclasses_Are_Rejected_With_Actionable_Error()
        {
            // A Queue<>/Stack<> subclass cannot be described by the built-in collection formatter (it has
            // no Add, and the built-in QueueFormatter would lose the subclass). It must fail loudly when
            // the manifest is baked, not silently misread at load.
            var q = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(
                () => SchemaRegistry.TryBuildStructuralSchema(typeof(BsPlainQueue), out _));
            Assert.Contains("Queue", q.Message);
            Assert.Contains("Stack", q.Message);

            var st = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(
                () => SchemaRegistry.TryBuildStructuralSchema(typeof(BsPlainStack), out _));
            Assert.Contains("Queue", st.Message);
        }

        [Fact]
        public void Unannotated_Collection_Subclass_Works_As_Root_Both_Paths()
        {
            var s = New();
            var value = new BsPlainStringSet { "x", "y" };
            var bytes = s.Save(value, 1);
            Assert.Equal(new[] { "x", "y" }, s.Load<BsPlainStringSet>(bytes, 1).OrderBy(x => x));
            Assert.Equal(new[] { "x", "y" }, s.Load<BsPlainStringSet>(SlowPath(bytes), 1).OrderBy(x => x));
        }

        [Fact]
        public void Two_Collection_Subclasses_Preserve_Order_And_Alignment()
        {
            var s = New();
            var holder = new BsTwoSubclassHolder
            {
                First = new BsAnnotatedSet<int> { 1 },
                Second = new BsPlainStringSet { "two" },
                After = 5,
            };
            var bytes = s.Save(holder, 1);
            foreach (var clone in new[] { s.Load<BsTwoSubclassHolder>(bytes, 1), s.Load<BsTwoSubclassHolder>(SlowPath(bytes), 1) })
            {
                Assert.Equal(new[] { 1 }, clone.First);
                Assert.Equal(new[] { "two" }, clone.Second);
                Assert.Equal(5, clone.After);
            }
        }

        // ── Jagged / multidimensional arrays ────────────────────────────────────────────────

        [Fact]
        public void Jagged_Arrays_RoundTrip_Both_Paths()
        {
            var s = New();
            var holder = new BsJaggedHolder { Jagged = new[] { new[] { 1 }, new[] { 2, 3 } }, After = 8 };
            var bytes = s.Save(holder, 1);
            foreach (var clone in new[] { s.Load<BsJaggedHolder>(bytes, 1), s.Load<BsJaggedHolder>(SlowPath(bytes), 1) })
            {
                Assert.Equal(2, clone.Jagged.Length);
                Assert.Equal(new[] { 1 }, clone.Jagged[0]);
                Assert.Equal(new[] { 2, 3 }, clone.Jagged[1]);
                Assert.Equal(8, clone.After);
            }
        }

        [Fact]
        public void Jagged_Array_With_Empty_Inner_RoundTrips_Both_Paths()
        {
            var s = New();
            var value = new[] { Array.Empty<int>(), new[] { 4, 5 } };
            var bytes = s.Save(value, 1);
            foreach (var clone in new[] { s.Load<int[][]>(bytes, 1), s.Load<int[][]>(SlowPath(bytes), 1) })
            {
                Assert.Empty(clone[0]);
                Assert.Equal(new[] { 4, 5 }, clone[1]);
            }
        }

        [Fact]
        public void Empty_OneDimensional_Array_RoundTrips_Both_Paths()
        {
            var s = New();
            var bytes = s.Save(Array.Empty<int>(), 1);
            Assert.Empty(s.Load<int[]>(bytes, 1));
            Assert.Empty(s.Load<int[]>(SlowPath(bytes), 1));
        }

        [Fact]
        public void Multidimensional_Array_Type_Is_Rejected_With_Actionable_Error()
        {
            // 0.5 item 2: multidimensional arrays are written by MultiDimensionalArrayFormatter but the
            // structural/interpreter reader describes only one-dimensional arrays, so they must be
            // rejected loudly instead of silently misreading. The generator emits CERAS027 for a member;
            // registering one directly must also fail with the same actionable message.
            var ex = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(
                () => SchemaRegistry.TryBuildStructuralSchema(typeof(int[,]), out _));
            Assert.Contains("one-dimensional", ex.Message);

            var ex2 = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(
                () => SchemaRegistry.TryBuildStructuralSchema(typeof(int[,,]), out _));
            Assert.Contains("rank 3", ex2.Message);
        }
    }
}
