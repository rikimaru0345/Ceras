using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.Generator
{
    [CerasContext("MemberPolicy")]
    [CerasSerializable]
    public class MemberPolicyModel
    {
        int _privateValue;             // non-public, and a member all the same
        [Exclude] int _excluded = 99;  // the one documented way to leave a field out
        public int Tail;                // alignment witness

        public void Set(int privateValue, int excluded) { _privateValue = privateValue; _excluded = excluded; }
        public int GetPrivate() => _privateValue;
        public int GetExcluded() => _excluded;
    }

    /// <summary>
    /// The default member-selection policy the generator's member walk implements: accessibility does not
    /// decide membership — a private field is serialized like any other — and <c>[Exclude]</c> is the
    /// only thing that takes a field out.
    /// <para>
    /// These two are regressions for the walk itself rather than for the attributes: the member list
    /// used to be filtered by a guard that could never reject anything, which is invisible when it is
    /// inert (the manifest looks right either way) and would only show up as a silently dropped member
    /// if the policy ever changed shape.
    /// </para>
    /// <para>
    /// The raw/transient side of the same policy is already pinned by
    /// <see cref="Ceras.Test.Tests.SourceGeneratorAdvancedTests"/>, and the <c>[Exclude]</c>-comes-back-
    /// as-its-initializer contract on the migration path by
    /// <see cref="Ceras.Test.ConstructorReuse.ConstructorReuseTests"/>; what is pinned here is the
    /// generated manifest and the two robust readers.
    /// </para>
    /// </summary>
    public class GeneratedMemberDiscoveryTests : TestBase
    {
        const string Ctx = "MemberPolicy";

        [Fact]
        public void Private_Field_Is_Discovered_And_Excluded_One_Is_Not()
        {
            var config = new CerasRobustConfig();
            RobustFormatters.UseRobustFormatters(config, Ctx);

            Assert.True(((CerasRobustConfig)config).SchemaRegistry.TryGetInfo(typeof(MemberPolicyModel), out var schema));
            var names = new System.Collections.Generic.List<string>();
            foreach (var field in schema.Fields) names.Add(field.StableName);

            Assert.Contains("_privateValue", names);
            Assert.DoesNotContain("_excluded", names);
        }

        [Fact]
        public void Private_Field_Survives_Both_Readers_And_Excluded_One_Does_Not()
        {
            var original = new MemberPolicyModel { Tail = 33 };
            original.Set(11, 777);

            var bytes = Generated().Save(original, 1);

            var fast = Generated().Load<MemberPolicyModel>(bytes, 1);
            var interpreted = Generated().Load<MemberPolicyModel>(SlowPath(bytes), 1);

            Assert.Equal(11, fast.GetPrivate());
            Assert.Equal(11, interpreted.GetPrivate());
            Assert.Equal(99, fast.GetExcluded());      // the field initializer, not a stored 777
            Assert.Equal(99, interpreted.GetExcluded());
            Assert.Equal(33, fast.Tail);              // the stream stayed aligned
            Assert.Equal(33, interpreted.Tail);
        }

        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));
    }
}
