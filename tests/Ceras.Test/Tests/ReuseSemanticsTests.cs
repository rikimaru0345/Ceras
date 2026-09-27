using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Generated;

namespace Ceras.Test.Reuse
{
    [CerasSerializable]
    public class ReuseCollectionModel
    {
        public List<int> Items = new();
        public List<int> Other = new();
    }

    [CerasSerializable]
    public class DiffNode
    {
        public int Id;
        public string Name;
        public DiffNode Next;
        public List<int> Values = new();
    }

    [CerasSerializable]
    public class DiffRoot
    {
        public DiffNode First;
        public DiffNode Second;
        public DiffNode Cycle;
        public List<DiffNode> All = new();
    }

    /// <summary>
    /// Pins the confirmed <c>Load(ref existing)</c> reuse contract (TODO Phase 3.2):
    /// an existing non-null member instance of the exact stored type is reused in place;
    /// collections are cleared and refilled. The generated fast path must match the
    /// reflection/interpreter path for identity and content.
    /// </summary>
    public class ReuseSemanticsTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters());

        [Fact]
        public void ReusedCollection_IsClearedAndRefilled_InPlace()
        {
            var writer = Generated();
            var original = new ReuseCollectionModel
            {
                Items = new List<int> { 1, 2, 3, 4, 5 },
                Other = new List<int> { 7 }
            };
            var bytes = writer.Save(original, 1);

            // Pre-existing collections with different content AND different size.
            var existingItems = new List<int> { 9, 9, 9, 9, 9, 9, 9 };
            var existing = new ReuseCollectionModel
            {
                Items = existingItems,
                Other = new List<int> { 8, 8 }
            };

            Generated().Load(ref existing, bytes, 1);

            // Identity preserved (reused, not replaced) and content fully overwritten (clear + refill).
            Assert.Same(existingItems, existing.Items);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, existing.Items);
            Assert.Equal(new[] { 7 }, existing.Other);
        }

        [Fact]
        public void GeneratedAndInterpreter_LoadIdenticalGraphs_AndIdentity()
        {
            var a = new DiffNode { Id = 1, Name = "a" };
            a.Next = a; // cycle
            a.Values.AddRange(new[] { 1, 2, 3 });

            var b = new DiffNode { Id = 2, Name = "b" };

            var root = new DiffRoot
            {
                First = a,
                Second = a,     // shared reference
                Cycle = a,      // shared reference
                All = new List<DiffNode> { a, b }
            };

            var writer = Generated();
            var bytes = writer.Save(root, 1);

            var generated = Generated().Load<DiffRoot>(bytes, 1);

            // Force the interpreter path (fingerprint mismatch) over the same bytes. This is the
            // generated-reader vs SchemaTraversalEngine cross-check; a self-roundtrip cannot reveal a
            // shared format mistake. Mutating the stored fingerprint is required: re-registering the
            // identical deterministic manifest would hit the fast path instead.
            var interpBytes = (byte[])bytes.Clone();
            interpBytes[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(interpBytes);
            var interpreter = Generated().Load<DiffRoot>(interpBytes, 1);

            AssertEquivalent(generated);
            AssertEquivalent(interpreter);

            AssertIdentity(generated);
            AssertIdentity(interpreter);
        }

        static void AssertEquivalent(DiffRoot r)
        {
            Assert.NotNull(r.First);
            Assert.Equal(1, r.First.Id);
            Assert.Equal("a", r.First.Name);
            Assert.Equal(new[] { 1, 2, 3 }, r.First.Values);

            Assert.Equal(2, r.All.Count);
            Assert.Equal(2, r.All[1].Id);
            Assert.Equal("b", r.All[1].Name);
        }

        static void AssertIdentity(DiffRoot r)
        {
            Assert.Same(r.First, r.Second);
            Assert.Same(r.First, r.Cycle);
            Assert.Same(r.First, r.All[0]);
            Assert.Same(r.First.Next, r.First);
        }
    }
}
