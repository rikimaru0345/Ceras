using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Generated;

// Phase 2.1: explicitly declare a closed generic (and its transitive closure) that is hidden
// behind object/interface members and therefore not reachable by walking member types.
[assembly: CerasKnownType<List<Ceras.Test.Known.KnownHidden>>]

namespace Ceras.Test.Known
{
    /// <summary>
    /// Not [CerasSerializable] and not referenced by any serializable member; only reachable
    /// because of the assembly-level [CerasKnownType&lt;List&lt;KnownHidden&gt;&gt;] declaration.
    /// </summary>
    public class KnownHidden
    {
        public int Value;
        public string Text;
    }

    public class KnownTypeTests
    {
        static CerasRobustSerializer Create() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        [Fact]
        public void CerasKnownType_MakesHiddenClosedGenericReachable()
        {
            var list = new List<KnownHidden>
            {
                new KnownHidden { Value = 1, Text = "a" },
                new KnownHidden { Value = 2, Text = "b" }
            };

            var bytes = Create().Save(list, 1);
            var clone = Create().Load<List<KnownHidden>>(bytes, 1);

            Assert.Equal(2, clone.Count);
            Assert.Equal(1, clone[0].Value);
            Assert.Equal("a", clone[0].Text);
            Assert.Equal("b", clone[1].Text);
        }
    }
}
