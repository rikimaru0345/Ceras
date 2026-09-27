using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.CanonicalIdentity
{
    [CerasContext("Tests")]
    [CerasSerializable]
    public class CanonicalCollectionsModel
    {
        public Dictionary<string, List<int>> Map = new();
        public List<int> Items = new();
    }

    /// <summary>
    /// Defect: structural/declared runtime schemas used <see cref="Type.FullName"/> while the
    /// generator emitted canonical names, so closed generics got assembly-version-qualified stable
    /// names and the fingerprint depended on the BCL assembly version. Both sides now use
    /// <see cref="TypeIdentity.GetCanonicalName"/>, matching <c>TypeHelpers.GetRobustName</c>.
    /// </summary>
    public class CanonicalIdentityTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        [Fact]
        public void CanonicalName_MatchesGeneratorShape_AndHasNoRuntimeTokens()
        {
            Assert.Equal("System.Int32", TypeIdentity.GetCanonicalName(typeof(int)));
            Assert.Equal("System.String", TypeIdentity.GetCanonicalName(typeof(string)));
            Assert.Equal("System.Int32[]", TypeIdentity.GetCanonicalName(typeof(int[])));
            Assert.Equal("System.Int32[,]", TypeIdentity.GetCanonicalName(typeof(int[,])));
            Assert.Equal("System.Collections.Generic.List`1", TypeIdentity.GetCanonicalName(typeof(List<>)));
            Assert.Equal("System.Collections.Generic.List`1<System.Int32>", TypeIdentity.GetCanonicalName(typeof(List<int>)));
            Assert.Equal(
                "System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<System.Int32>>",
                TypeIdentity.GetCanonicalName(typeof(Dictionary<string, List<int>>)));

            string dict = TypeIdentity.GetCanonicalName(typeof(Dictionary<string, List<int>>));
            Assert.DoesNotContain("Version=", dict);
            Assert.DoesNotContain("PublicKeyToken", dict);
            Assert.DoesNotContain("Culture=", dict);
        }

        [Fact]
        public void Registry_StableNames_ForClosedGenerics_AreCanonical_AndResolvable()
        {
            var robust = Generated();
            robust.Save(new CanonicalCollectionsModel { Map = { ["a"] = new List<int> { 1 } } }, 1); // bake

            var registry = robust.Config.SchemaRegistry;
            var names = registry.All().Select(e => e.info.StableName).ToList();

            const string listName = "System.Collections.Generic.List`1<System.Int32>";
            const string dictName = "System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<System.Int32>>";

            Assert.Contains(listName, names);
            Assert.Contains(dictName, names);
            Assert.DoesNotContain(names, n => n.Contains("Version=") || n.Contains("PublicKeyToken") || n.Contains("Culture="));

            // _byStableName must cover the closed generics (no assembly scan needed).
            Assert.True(registry.TryResolveType(listName, out var listType));
            Assert.Equal(typeof(List<int>), listType);
            Assert.True(registry.TryResolveType(dictName, out var dictType));
            Assert.Equal(typeof(Dictionary<string, List<int>>), dictType);
        }

        [Fact]
        public void IndependentlyBuiltSerializers_ProduceIdenticalFingerprints()
        {
            var a = Generated();
            a.Save(new CanonicalCollectionsModel { Items = { 1, 2, 3 } }, 1);

            var b = Generated();
            b.Save(new CanonicalCollectionsModel { Items = { 4, 5 } }, 1);

            Assert.Equal(a.Config.SchemaRegistry.ComputeFingerprint(), b.Config.SchemaRegistry.ComputeFingerprint());
        }

        [Fact]
        public void ClosedGenericField_RoundTrips_OnFastPath()
        {
            var model = new CanonicalCollectionsModel
            {
                Map = { ["k"] = new List<int> { 7, 8 } },
                Items = { 9, 10 }
            };

            byte[] bytes = Generated().Save(model, 1);
            var clone = Generated().Load<CanonicalCollectionsModel>(bytes, 1);

            Assert.Equal(new[] { 9, 10 }, clone.Items);
            Assert.Equal(new[] { 7, 8 }, clone.Map["k"]);
        }

        [Fact]
        public void ClosedGenericField_RoundTrips_ThroughInterpreter()
        {
            var model = new CanonicalCollectionsModel
            {
                Map = { ["k"] = new List<int> { 11, 12 } },
                Items = { 13 }
            };

            byte[] bytes = Generated().Save(model, 1);

            // Genuinely force the interpreter (see RobustSlowPathTests).
            bytes[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(bytes);

            var clone = Generated().Load<CanonicalCollectionsModel>(bytes, 1);

            Assert.Equal(new[] { 13 }, clone.Items);
            Assert.Equal(new[] { 11, 12 }, clone.Map["k"]);
        }
    }
}
