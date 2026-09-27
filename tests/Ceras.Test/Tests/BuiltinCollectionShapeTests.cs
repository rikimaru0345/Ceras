using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.BuiltinShapes
{
    // ── Collection subclasses (annotated and unannotated) ───────────────────────────────
    [CerasSerializable]
    public class BsAnnotatedList<T> : List<T> { }

    public class BsPlainList<T> : List<T> { }

    public class BsPlainDict : Dictionary<string, int> { }

    public class BsPlainSet : HashSet<int> { }

    public class BsPlainQueue : Queue<int> { }

    public class BsPlainStack : Stack<int> { }


    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsCollectionSubclassHolder
    {
        public BsAnnotatedList<int> Annotated;
        public BsPlainList<int> Plain;
        public BsPlainDict Dict;
        public BsPlainSet Set;
        public int After;
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsTwoSubclassHolder
    {
        public BsPlainList<int> First;
        public BsAnnotatedList<string> Second;
        public int After;
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsJaggedHolder
    {
        public int[][] Jagged;
        public int After;
    }


    /// <summary>
    /// TODO 0.5 item 1: every collection subclass admitted to a manifest must have an accurate
    /// collection schema (not an empty class schema), so its payload is describable and the interpreter
    /// can migrate it. Annotated and unannotated subclasses are covered, including a following field to
    /// prove the stream stays aligned.
    /// </summary>
    public class BuiltinCollectionShapeTests : TestBase
    {
        const string Ctx = "BuiltinShapes";

        // The allowed structural root shapes, declared up front for the finite manifest.
        static CerasRobustSerializer New() => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Ctx);
            c.DeclareRoots(typeof(int[]), typeof(int[][]), typeof(List<int>),
                typeof(BsAnnotatedList<int>), typeof(BsPlainList<int>), typeof(BsPlainList<string>),
                typeof(BsPlainDict), typeof(BsPlainSet));
        });

        [Fact]
        public void CollectionSubclasses_Schema_Describes_Collection_Payload()
        {
            var s = New();
            // Save once so the structural entries are baked into the registry.
            s.Save(new BsCollectionSubclassHolder { Plain = new BsPlainList<int> { 1 } }, 1);

            Assert.True(s.Config.SchemaRegistry.TryGetInfo(typeof(BsPlainList<int>), out var info));
            Assert.Equal(SchemaTypeKind.GenericInstantiation, info.Kind);
            Assert.Equal("System.Collections.Generic.List`1", info.UnderlyingTypeName);
            Assert.Equal(new[] { "System.Int32" }, info.ArgTypeNames);

            Assert.True(s.Config.SchemaRegistry.TryGetInfo(typeof(BsAnnotatedList<int>), out var annotatedInfo));
            Assert.Equal(SchemaTypeKind.GenericInstantiation, annotatedInfo.Kind);
            Assert.Equal("System.Collections.Generic.List`1", annotatedInfo.UnderlyingTypeName);

            Assert.True(s.Config.SchemaRegistry.TryGetInfo(typeof(BsPlainDict), out var dictInfo));
            Assert.Equal(SchemaTypeKind.GenericInstantiation, dictInfo.Kind);
            Assert.Equal("System.Collections.Generic.Dictionary`2", dictInfo.UnderlyingTypeName);
            Assert.Equal(new[] { "System.String", "System.Int32" }, dictInfo.ArgTypeNames);
        }

        [Fact]
        public void CollectionSubclasses_RoundTrip_Both_Paths_With_Following_Field()
        {
            var s = New();
            var holder = new BsCollectionSubclassHolder
            {
                Annotated = new BsAnnotatedList<int> { 1, 2, 3 },
                Plain = new BsPlainList<int> { 4, 5 },
                Dict = new BsPlainDict { ["a"] = 6, ["b"] = 7 },
                Set = new BsPlainSet { 8, 9 },
                After = 4242,
            };

            var bytes = s.Save(holder, 1);
            var fast = s.Load<BsCollectionSubclassHolder>(bytes, 1);
            var slow = s.Load<BsCollectionSubclassHolder>(SlowPath(bytes), 1);

            foreach (var clone in new[] { fast, slow })
            {
                Assert.NotNull(clone);
                Assert.Equal(new[] { 1, 2, 3 }, clone.Annotated);
                Assert.Equal(new[] { 4, 5 }, clone.Plain);
                Assert.Equal(6, clone.Dict["a"]);
                Assert.Equal(7, clone.Dict["b"]);
                Assert.Equal(new[] { 8, 9 }, clone.Set.OrderBy(x => x));
                Assert.Equal(4242, clone.After);
            }
        }

        [Fact]
        public void Queue_And_Stack_Subclasses_Are_Rejected_With_Actionable_Error()
        {
            // A Queue<>/Stack<> subclass cannot be described by the built-in collection formatter (it has
            // no Add, and the built-in QueueFormatter would lose the subclass). It must fail loudly when
            // the manifest is baked, not silently misread at load (TODO 0.5).
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
            var value = new BsPlainList<string> { "x", "y" };
            var bytes = s.Save(value, 1);
            Assert.Equal(new[] { "x", "y" }, s.Load<BsPlainList<string>>(bytes, 1));
            Assert.Equal(new[] { "x", "y" }, s.Load<BsPlainList<string>>(SlowPath(bytes), 1));
        }

        [Fact]
        public void Two_Collection_Subclasses_Preserve_Order_And_Alignment()
        {
            var s = New();
            var holder = new BsTwoSubclassHolder
            {
                First = new BsPlainList<int> { 1 },
                Second = new BsAnnotatedList<string> { "two" },
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

        // ── Jagged / multidimensional arrays ────────────────────────────────────────────

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
