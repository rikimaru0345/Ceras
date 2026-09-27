using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Generated;

// TASK 5: a member whose declared type is an open type parameter T must still honor the reuse
// contract when T is closed with a reference type. The closed instantiations are declared
// explicitly because the generic argument is not reachable through member walking alone.
[assembly: CerasKnownType<Ceras.Test.Reuse.Box<Ceras.Test.Reuse.ReuseNode>>]
[assembly: CerasKnownType<Ceras.Test.Reuse.Box<int>>]

namespace Ceras.Test.Reuse
{
    [CerasContext("Tests")]
    [CerasSerializable]
    public class ReuseNode
    {
        public int Id;
        public string Name;
    }

    /// <summary>
    /// A user generic type whose single member is declared as the open type parameter <c>T</c>.
    /// The generated open formatter template injects <c>IFormatter&lt;T&gt;</c> (a
    /// <c>ReferenceFormatter&lt;T&gt;</c> when the closed argument is a reference type), so seeding
    /// the deserialize local from the existing member must not be gated on the open symbol's
    /// <c>IsReferenceType</c> (always false for a type parameter).
    /// </summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public class Box<T>
    {
        public T Value;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ReuseCollectionModel
    {
        public List<int> Items = new();
        public List<int> Other = new();
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class DiffNode
    {
        public int Id;
        public string Name;
        public DiffNode Next;
        public List<int> Values = new();
    }

    [CerasContext("Tests")]
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
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

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

        // ── TASK 5: open type-parameter members follow the reuse contract ───────────────────

        [Fact]
        public void GenericReferenceMember_ExistingInstance_IsReusedInPlace()
        {
            var savedValue = new ReuseNode { Id = 1, Name = "saved" };
            var bytes = Generated().Save(new Box<ReuseNode> { Value = savedValue }, 1);

            // Distinct pre-existing instance of the exact stored type.
            var existingValue = new ReuseNode { Id = 99, Name = "existing" };
            var existing = new Box<ReuseNode> { Value = existingValue };

            Generated().Load(ref existing, bytes, 1);

            // The existing instance was reused in place, not replaced, and its contents overwritten.
            Assert.Same(existingValue, existing.Value);
            Assert.Equal(1, existing.Value.Id);
            Assert.Equal("saved", existing.Value.Name);
        }

        [Fact]
        public void GenericReferenceMember_NullExisting_AllocatesNewInstance()
        {
            var savedValue = new ReuseNode { Id = 7, Name = "fresh" };
            var bytes = Generated().Save(new Box<ReuseNode> { Value = savedValue }, 1);

            var existing = new Box<ReuseNode> { Value = null };

            Generated().Load(ref existing, bytes, 1);

            // No instance to reuse: a new one is created with the saved contents.
            Assert.NotNull(existing.Value);
            Assert.Equal(7, existing.Value.Id);
            Assert.Equal("fresh", existing.Value.Name);
        }

        [Fact]
        public void GenericValueTypeMember_StillRoundtrips()
        {
            // Guards against the seeding change breaking value semantics for a value-type T.
            var bytes = Generated().Save(new Box<int> { Value = 42 }, 1);

            var loaded = Generated().Load<Box<int>>(bytes, 1);
            Assert.Equal(42, loaded.Value);
        }
    }
}
