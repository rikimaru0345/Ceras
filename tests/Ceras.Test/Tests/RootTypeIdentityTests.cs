using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Generated;

namespace Ceras.Test.RootIdentity
{
    // Two declared roots with deliberately different layouts. Both are part of the same generated
    // manifest, so they share one fingerprint/context; only the stored root type id distinguishes them.
    [CerasSerializable]
    public class RootLayoutA
    {
        public int Number;
        public string Text;
    }

    [CerasSerializable]
    public class RootLayoutB
    {
        public long Big;
        public double D;
        public List<int> Items = new();
    }

    [CerasSerializable]
    public class PolyRootBase
    {
        public int BaseValue;
    }

    [CerasSerializable]
    public class PolyRootDerived : PolyRootBase
    {
        public int DerivedValue;
    }

    /// <summary>
    /// Defect: <c>Load&lt;T&gt;</c> used to read and discard the stored root type id, silently
    /// interpreting the payload with whatever <c>T</c> the caller asked for. The root id must be
    /// honored, with assignment-compatible (polymorphic) roots still accepted.
    /// </summary>
    public class RootTypeIdentityTests
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters());

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
        public void PolymorphicRoot_SavedAsBase_LoadedAsDerived_Works()
        {
            // A legitimate polymorphic root: the declared root is the base type, but the actual
            // instance is derived. Both are assignment-compatible, so the load must be allowed and
            // the runtime type is recovered from the payload's polymorphism marker.
            var bytes = Generated().Save<PolyRootBase>(new PolyRootDerived { BaseValue = 1, DerivedValue = 2 }, 1);
            var clone = Generated().Load<PolyRootDerived>(bytes, 1);

            Assert.Equal(1, clone.BaseValue);
            Assert.Equal(2, clone.DerivedValue);
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
    }
}
