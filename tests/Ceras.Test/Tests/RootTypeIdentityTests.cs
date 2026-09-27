using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Generated;

namespace Ceras.Test.RootIdentity
{
    // Two declared roots with deliberately different layouts. Both are part of the same generated
    // manifest, so they share one fingerprint/context; only the stored root type id distinguishes them.
    [CerasContext("Tests")]
    [CerasSerializable]
    public class RootLayoutA
    {
        public int Number;
        public string Text;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class RootLayoutB
    {
        public long Big;
        public double D;
        public List<int> Items = new();
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class PolyRootBase
    {
        public int BaseValue;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class PolyRootDerived : PolyRootBase
    {
        public int DerivedValue;
    }

    // Two single-root contexts: each hands its only root the same numeric type id, which is what made a
    // wrong-root load pass the id check and return null.
    [CerasContext("RootIdA")]
    [CerasSerializable]
    public class RootIdA_Root { public int Value; public string Text; }

    [CerasContext("RootIdB")]
    [CerasSerializable]
    public class RootIdB_Root { public int Value; public string Text; }

    /// <summary>The legitimate rename case: the file's schema carries the old name.</summary>
    [CerasContext("RootIdRename")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.Test.RootIdentity.RootIdRenamedRoot_OldName")]
    public class RootIdRenamedRoot { public int Value; public string Text; }

    /// <summary>
    /// Defect: <c>Load&lt;T&gt;</c> used to read and discard the stored root type id, silently
    /// interpreting the payload with whatever <c>T</c> the caller asked for. The root id must be
    /// honored with strict identity: only the declared root type (or its rename alias) may be loaded.
    /// </summary>
    public class RootTypeIdentityTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        [Fact]
        public void SameDeclaredRoot_RoundTrips()
        {
            var bytes = Generated().Save(new RootLayoutA { Number = 42, Text = "hello" }, 1);
            var clone = Generated().Load<RootLayoutA>(bytes, 1);

            Assert.Equal(42, clone.Number);
            Assert.Equal("hello", clone.Text);
        }

        [Fact]
        public void DifferentDeclaredRoot_WithDifferentLayout_Throws()
        {
            var bytes = Generated().Save(new RootLayoutA { Number = 42, Text = "hello" }, 1);

            var ex = Assert.Throws<CerasSchemaException>(() => Generated().Load<RootLayoutB>(bytes, 1));

            Assert.Contains("RootLayoutA", ex.Message);
            Assert.Contains("RootLayoutB", ex.Message);
        }

        [Fact]
        public void PolymorphicRoot_SavedAsBase_LoadedAsDerived_Throws()
        {
            // Saved with the base as the declared root (even though the instance is derived). Loading
            // as the derived type is not safe from the id alone: the payload may carry no type tag
            // (RefNewValueSameType) and the derived reader would consume base bytes. Reject strictly.
            var bytes = Generated().Save<PolyRootBase>(new PolyRootDerived { BaseValue = 1, DerivedValue = 2 }, 1);

            var ex = Assert.Throws<CerasSchemaException>(() => Generated().Load<PolyRootDerived>(bytes, 1));
            Assert.Contains("PolyRootBase", ex.Message);
            Assert.Contains("PolyRootDerived", ex.Message);
        }

        [Fact]
        public void ExactBaseInstance_SavedAsBase_LoadedAsDerived_Throws()
        {
            // The specific silent-misparse case: the saved instance's runtime type equals the declared
            // root, so the payload begins with RefNewValueSameType and no type tag. Loading as the
            // derived type must be rejected, not parsed from base bytes.
            var bytes = Generated().Save<PolyRootBase>(new PolyRootBase { BaseValue = 5 }, 1);

            var ex = Assert.Throws<CerasSchemaException>(() => Generated().Load<PolyRootDerived>(bytes, 1));
            Assert.Contains("PolyRootBase", ex.Message);
            Assert.Contains("PolyRootDerived", ex.Message);
        }

        [Fact]
        public void PolymorphicRoot_SavedAndLoadedAsBase_PreservesDerivedRuntimeType()
        {
            var bytes = Generated().Save<PolyRootBase>(new PolyRootDerived { BaseValue = 7, DerivedValue = 8 }, 1);
            var clone = Generated().Load<PolyRootBase>(bytes, 1);

            var derived = Assert.IsType<PolyRootDerived>(clone);
            Assert.Equal(7, derived.BaseValue);
            Assert.Equal(8, derived.DerivedValue);
        }

        // ── 0.0a: two contexts, one numeric id, different stable names ──────────────────────

        [Fact]
        public void WrongRoot_With_A_Colliding_TypeId_Is_Rejected()
        {
            var a = new CerasRobustSerializer(c => c.UseGeneratedFormatters("RootIdA"));
            var b = new CerasRobustSerializer(c => c.UseGeneratedFormatters("RootIdB"));

            var bytes = a.Save(new RootIdA_Root { Value = 7, Text = "seven" }, 1);

            // The id spaces really do collide, which is what made the old check pass.
            Assert.NotEqual(a.Config.SchemaRegistry.ComputeFingerprint(), b.Config.SchemaRegistry.ComputeFingerprint());
            Assert.Equal(7, a.Load<RootIdA_Root>(bytes, 1).Value);

            // The generated path.
            var fast = Assert.Throws<CerasSchemaException>(() => b.Load<RootIdB_Root>(bytes, 1));
            Assert.Contains("RootIdA_Root", fast.Message, StringComparison.Ordinal);
            Assert.Contains("RootIdB_Root", fast.Message, StringComparison.Ordinal);

            // The interpreter path: the same rejection, so a save never silently loads as null.
            var slow = Assert.Throws<CerasSchemaException>(() => b.Load<RootIdB_Root>(SlowPath(bytes), 1));
            Assert.Contains("RootIdA_Root", slow.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RenamedRoot_Still_Loads_Through_Its_Alias()
        {
            // The legitimate case the identity check must keep accepting: the file's schema carries the
            // old name, which resolves to the current type.
            var c = new CerasRobustSerializer(config => config.UseGeneratedFormatters("RootIdRename"));
            var bytes = c.Save(new RootIdRenamedRoot { Value = 3, Text = "three" }, 1);

            Assert.Equal(3, c.Load<RootIdRenamedRoot>(bytes, 1).Value);
            Assert.Equal(3, c.Load<RootIdRenamedRoot>(SlowPath(bytes), 1).Value);
        }
    }
}
