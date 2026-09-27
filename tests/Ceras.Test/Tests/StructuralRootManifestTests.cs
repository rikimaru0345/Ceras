using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.StructuralRoots
{
    // The only declared type in this context, and it has no collection/array member, so nothing in
    // the manifest can ever pull an array or built-in collection into the registry: every structural
    // root used below has to be declared explicitly.
    [CerasContext("StructuralRoots")]
    [CerasSerializable]
    public class SrHolder
    {
        public int Id;
        public string Name;
    }

    // Deliberately *not* declared anywhere: an undeclared user type stays an error, before and after
    // the seal.
    public class SrUndeclared
    {
        public int Value;
    }

    /// <summary>
    /// A context has one finite manifest. Arrays and built-in closed collections are skipped by the
    /// generator, so no generated schema describes them: each allowed root shape is declared up front
    /// (<c>DeclareRoot</c>, or a context-scoped <c>[CerasKnownType]</c> at compile time) and folded
    /// into the manifest in canonical-name order at the first seal. Nothing may join the manifest
    /// afterwards, so the id map and the layout fingerprint are stable for the context's whole
    /// lifetime — no re-bake, and no previously matching save falling into the interpreter because a
    /// second root shape was used.
    /// </summary>
    public class StructuralRootManifestTests : TestBase
    {
        const string Ctx = "StructuralRoots";

        // The allowed root shapes, in one place. The order they are declared in must not matter.
        static readonly Type[] AllowedRoots =
        {
            typeof(List<int>),
            typeof(int[]),
            typeof(Dictionary<string, int>),
        };

        static CerasRobustSerializer New() => New(AllowedRoots);

        static CerasRobustSerializer New(params Type[] roots) => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Ctx);
            c.DeclareRoots(roots);
        });

        static SrHolder H(int id) => new SrHolder { Id = id, Name = "n" + id };

        [Fact]
        public void Declaration_Order_Does_Not_Change_The_Ids_Or_The_Fingerprint()
        {
            var forward = New(typeof(List<int>), typeof(int[]), typeof(Dictionary<string, int>));
            var reversed = New(typeof(Dictionary<string, int>), typeof(int[]), typeof(List<int>));

            // The manifest is folded in at the first seal, so give both a save first.
            forward.Save(H(1), 1);
            reversed.Save(H(1), 1);

            // One deterministic id map and one fingerprint per context, whatever the declaration order.
            Assert.Equal(forward.Config.SchemaRegistry.ComputeFingerprint(), reversed.Config.SchemaRegistry.ComputeFingerprint());

            foreach (var type in AllowedRoots)
            {
                Assert.True(forward.Config.SchemaRegistry.TryGetId(type, out int forwardId));
                Assert.True(reversed.Config.SchemaRegistry.TryGetId(type, out int reversedId));
                Assert.Equal(forwardId, reversedId);
            }
        }

        [Fact]
        public void Declaration_Order_Does_Not_Change_The_Bytes()
        {
            var forward = New(typeof(List<int>), typeof(int[]), typeof(Dictionary<string, int>));
            var reversed = New(typeof(Dictionary<string, int>), typeof(int[]), typeof(List<int>));

            Assert.Equal(
                forward.Save(H(1), 1),
                reversed.Save(H(1), 1));
            Assert.Equal(
                forward.Save(new List<int> { 1, 2, 3 }, 1),
                reversed.Save(new List<int> { 1, 2, 3 }, 1));
        }

        [Fact]
        public void Each_Instance_Loads_The_Others_Files_On_The_Matching_Layout_Path()
        {
            var forward = New(typeof(List<int>), typeof(int[]), typeof(Dictionary<string, int>));
            var reversed = New(typeof(Dictionary<string, int>), typeof(int[]), typeof(List<int>));

            // The fingerprints match, so these are the generated path, not the interpreter.
            var list = forward.Save(new List<int> { 4, 5, 6 }, 1);
            Assert.Equal(new[] { 4, 5, 6 }, reversed.Load<List<int>>(list, 1));

            var array = forward.Save(new[] { 7, 8 }, 1);
            Assert.Equal(new[] { 7, 8 }, reversed.Load<int[]>(array, 1));

            var dict = forward.Save(new Dictionary<string, int> { ["k"] = 9 }, 1);
            Assert.Equal(9, reversed.Load<Dictionary<string, int>>(dict, 1)["k"]);

            var holder = forward.Save(H(3), 1);
            Assert.Equal(3, reversed.Load<SrHolder>(holder, 1).Id);
        }

        [Fact]
        public void Every_Allowed_Root_RoundTrips_On_Both_Paths_After_The_Manifest_Is_Sealed()
        {
            var s = New();
            var holder = s.Save(H(1), 1); // seals the manifest
            var list = s.Save(new List<int> { 1, 2, 3 }, 1);
            var array = s.Save(new[] { 4, 5 }, 1);
            var dict = s.Save(new Dictionary<string, int> { ["k"] = 6 }, 1);

            // Every save still takes the matching-layout path: the fingerprint never moves.
            var fingerprint = s.Config.SchemaRegistry.ComputeFingerprint();
            Assert.Equal(1, s.Load<SrHolder>(holder, 1).Id);
            Assert.Equal(new[] { 1, 2, 3 }, s.Load<List<int>>(list, 1));
            Assert.Equal(new[] { 4, 5 }, s.Load<int[]>(array, 1));
            Assert.Equal(6, s.Load<Dictionary<string, int>>(dict, 1)["k"]);
            Assert.Equal(fingerprint, s.Config.SchemaRegistry.ComputeFingerprint());

            Assert.Equal(1, s.Load<SrHolder>(SlowPath(holder), 1).Id);
            Assert.Equal(new[] { 1, 2, 3 }, s.Load<List<int>>(SlowPath(list), 1));
            Assert.Equal(new[] { 4, 5 }, s.Load<int[]>(SlowPath(array), 1));
            Assert.Equal(6, s.Load<Dictionary<string, int>>(SlowPath(dict), 1)["k"]);
        }

        [Fact]
        public void An_Undeclared_Structural_Root_Is_Rejected_With_The_Declaration_Message()
        {
            var s = New();   // only List<int>, int[] and Dictionary<string,int> are allowed

            var ex = Assert.Throws<CerasSchemaException>(() => s.Save(new HashSet<int> { 1 }, 1));
            Assert.Contains("not part of the context's manifest", ex.Message, StringComparison.Ordinal);
            Assert.Contains("DeclareRoot", ex.Message, StringComparison.Ordinal);
            Assert.Contains("CerasKnownType", ex.Message, StringComparison.Ordinal);

            // The same rejection covers a load: the shape is not part of the manifest either way.
            var allowed = New();
            allowed.Save(new List<int> { 1 }, 1);
            var listBytes = allowed.Save(new List<int> { 1 }, 1);
            Assert.Throws<CerasSchemaException>(() => allowed.Load<HashSet<int>>(listBytes, 1));
        }

        [Fact]
        public void Declaring_A_Root_After_The_Seal_Is_Rejected_And_Leaves_The_Bake_Alone()
        {
            var s = New();
            s.Save(H(1), 1);
            var registry = s.Config.SchemaRegistry;
            Assert.True(registry.TryGetBakedSchema(out _, out _, out _, out _, out ulong baked));

            var ex = Assert.Throws<CerasSchemaException>(() => registry.DeclareRoot(typeof(HashSet<int>)));
            Assert.Contains("sealed", ex.Message, StringComparison.OrdinalIgnoreCase);

            // The refused declaration changes nothing.
            Assert.True(registry.TryGetBakedSchema(out _, out _, out _, out _, out ulong after));
            Assert.Equal(baked, after);
            Assert.Equal(after, registry.ComputeFingerprint());
        }

        [Fact]
        public void Registering_After_The_Seal_Is_Rejected_For_Every_Shape()
        {
            var s = New();
            s.Save(H(1), 1);
            var registry = s.Config.SchemaRegistry;

            // A structural shape is no longer accepted after the seal either: it has to be declared
            // before the first Save/Load. (One that is already in the manifest is simply a no-op
            // re-registration, as Register has always been idempotent.)
            var structural = Assert.Throws<CerasSchemaException>(() => registry.Register(typeof(HashSet<int>), null));
            Assert.Contains("sealed", structural.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DeclareRoot", structural.Message, StringComparison.Ordinal);

            var undeclared = Assert.Throws<CerasSchemaException>(() =>
                registry.Register(typeof(SrUndeclared), new SchemaTypeInfo(
                    "Ceras.Test.StructuralRoots.SrUndeclared", Array.Empty<string>(),
                    SchemaTypeKind.Class, Array.Empty<SchemaFieldInfo>(), 0)));
            Assert.Contains("sealed", undeclared.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[CerasContext", undeclared.Message, StringComparison.Ordinal);

            // The rejected registrations leave the bake untouched, as before.
            Assert.True(registry.TryGetBakedSchema(out _, out _, out _, out _, out ulong stillBaked));
            Assert.NotEqual(0UL, stillBaked);
        }

        [Fact]
        public void Declaring_Something_That_Is_Not_A_Root_Shape_Is_Rejected()
        {
            // A user type cannot be declared as a root shape: it has to be part of the manifest.
            var registry = new SchemaRegistry();
            var ex = Assert.Throws<CerasSchemaException>(() => registry.DeclareRoot(typeof(SrUndeclared)));
            Assert.Contains("only one-dimensional arrays", ex.Message, StringComparison.Ordinal);

            // A multidimensional array is not describable by the structural schema either.
            Assert.Throws<CerasSchemaException>(() => registry.DeclareRoot(typeof(int[,])));

            // The well-known roots need no declaration at all: they are accepted and stay out of the
            // user id space, so nothing is registered for them.
            registry.DeclareRoot(typeof(int));
            registry.DeclareRoot(typeof(string));
            registry.DeclareRoot(typeof(List<>));
            registry.Register(typeof(SrHolder), new SchemaTypeInfo(
                "Ceras.Test.StructuralRoots.SrHolder", Array.Empty<string>(),
                SchemaTypeKind.Class, Array.Empty<SchemaFieldInfo>(), 0));
            Assert.False(registry.TryGetId(typeof(int), out _));
            Assert.False(registry.TryGetId(typeof(string), out _));
            Assert.False(registry.TryGetId(typeof(List<>), out _));
            Assert.True(registry.TryGetId(typeof(SrHolder), out int holderId));
            Assert.Equal(WellKnownTypes.UserTypeBase, holderId);
        }
    }
}
