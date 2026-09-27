using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Generated;

namespace Ceras.Test.Layout
{
    [CerasContext("Tests")]
    [CerasSerializable]
    public class CollectionFieldModel
    {
        public Dictionary<string, List<int>> Map = new();
        public List<int> Items = new();
    }

    /// <summary>
    /// Phase 3.1: built-in collections must be described by their explicit shape (generic definition
    /// + argument type names), not by reflecting their CLR internals. This keeps BCL implementation
    /// details (e.g. Dictionary`2+Entry) out of the schema and out of save-time registry growth.
    /// </summary>
    public class LayoutEncodingTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        [Fact]
        public void WellKnownCollections_DoNotRegisterReflectedClrInternals()
        {
            var robust = Generated();
            var model = new CollectionFieldModel
            {
                Map = { ["a"] = new List<int> { 1, 2 } },
                Items = { 3, 4 }
            };

            var bytes = robust.Save(model, 1); // triggers schema section write

            var stableNames = robust.Config.SchemaRegistry.All()
                .Select(e => e.info.StableName)
                .ToList();

            Assert.DoesNotContain(stableNames, n => n.Contains("Entry"));
            Assert.DoesNotContain(stableNames, n => n.Contains("_items"));
            Assert.DoesNotContain(stableNames, n => n.Contains("_size"));
            Assert.DoesNotContain(stableNames, n => n.Contains("_version"));

            var clone = Generated().Load<CollectionFieldModel>(bytes, 1);
            Assert.Equal(4, clone.Items[1]);
            Assert.Equal(2, clone.Map["a"][1]);
        }

        [Fact]
        public void WellKnownCollections_RoundTrip_ThroughInterpreter()
        {
            var model = new CollectionFieldModel
            {
                Map = { ["k"] = new List<int> { 7, 8 } },
                Items = { 9 }
            };
            var bytes = Generated().Save(model, 1);

            // Genuinely force the interpreter by invalidating the stored fingerprint. Re-registering
            // the identical deterministic manifest would hit the fast path instead.
            bytes[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(bytes);

            var clone = Generated().Load<CollectionFieldModel>(bytes, 1);

            Assert.Equal(9, clone.Items[0]);
            Assert.Equal(8, clone.Map["k"][1]);
        }
    }
}
