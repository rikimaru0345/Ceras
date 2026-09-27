// Cross-version tests for the *construction* contract: the bytes come from
// fixtures/Ceras.HistoricalWriter (V1PooledModel.cs, context "HistoricalV1Pooled"), which this assembly
// does not reference, so the v1 CLR types are absent and this is a real migration — the file's own schema
// is decoded and mapped onto the current model below, not this version's schema read by this version.
//
// The same-layout cross-check (both readers over identical bytes, forced by a fingerprint flip) lives in
// MigrationConstructionTests; these tests cover what only a changed schema can show: that a configured
// factory supplies the instance while the file is being interpreted, and that the untouched defaults still
// behave exactly as they do on the matching-layout path.
using System;
using System.IO;
using Xunit;
using Ceras;
using Ceras.Generated;

namespace Ceras.Test.HistoricalConstruction
{
    /// <summary>
    /// v1's <c>Relic</c>: an open class, so the current reader reads it through
    /// <c>ReferenceFormatter&lt;T&gt;</c>. v1's <c>AncientName</c> is gone, <c>Charge</c> became a
    /// <c>long</c>, and <c>Restored</c> is new.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Pooled.Relic")]
    [CerasContext("HistoricalConstruction")]
    [CerasSerializable]
    public class Relic
    {
        public int Id;
        public long Charge;                 // v1: int Charge
        public object Owner;                // v1 held a Charm
        public Relic Twin;

        /// <summary>Added in the current model: what it holds shows which construction ran.</summary>
        public string Restored = "default";

        /// <summary>Not serialized; only a configured factory sets it, so it identifies the instance.</summary>
        [Exclude]
        public string FromFactory;

        /// <summary>A pool handing out a *used* object, so the payload has to win over what it carried.</summary>
        public static Relic FromPool() => new Relic { Id = 999, Charge = -1, FromFactory = "pool" };
    }

    /// <summary>
    /// v1's sealed <c>Charm</c>, reachable both as a declared member of the vault and through
    /// <c>Relic.Owner</c> — the same object, so it must be built once.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Pooled.Charm")]
    [CerasContext("HistoricalConstruction")]
    [CerasSerializable]
    public sealed class Charm
    {
        public int Id;

        /// <summary>Added in the current model.</summary>
        public string Restored = "default";

        [Exclude]
        public string FromFactory;

        public static Charm FromPool() => new Charm { Id = 999, FromFactory = "pool" };
    }

    [PreviousTypeName("Ceras.HistoricalWriter.V1Pooled.RelicVault")]
    [CerasContext("HistoricalConstruction")]
    [CerasSerializable]
    public class RelicVault
    {
        public int Version;
        public Relic Kept;
        public Charm Token;
    }

    /// <summary>
    /// A subtype of the current <see cref="Relic"/>, so a <c>Relic</c>-typed member can hold an instance
    /// whose runtime type is not the one the file describes — the mismatch that must not be reused.
    /// </summary>
    [CerasContext("HistoricalConstruction")]
    [CerasSerializable]
    public class SpecialRelic : Relic
    {
        public string Trait;
    }

    public class HistoricalConstructionFixtureTests : TestBase
    {
        const string Ctx = "HistoricalConstruction";

        static byte[] Fixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1", name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'. " +
                "Run fixtures/Ceras.HistoricalWriter to regenerate it (see its README.md).");
            return File.ReadAllBytes(path);
        }

        static byte[] Vault() => Fixture("RelicVault.bin");

        /// <summary>The relic as a root, so its polymorphic slot is where the Charm is first met.</summary>
        static byte[] RelicRoot() => Fixture("Relic.bin");

        /// <summary>No construction configured: Ceras' own defaults decide, which is the shipped behavior.</summary>
        static CerasRobustSerializer DefaultConstruction() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        /// <summary>Both types are obtained from a pool, including the one behind the polymorphic slot.</summary>
        static CerasRobustSerializer Pooled() => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Ctx);
            c.ConfigType<Relic>().ConstructBy(() => Relic.FromPool());
            c.ConfigType<Charm>().ConstructBy(() => Charm.FromPool());
        });

        [Fact]
        public void The_Fixture_Really_Is_An_Old_File()
        {
            var bytes = Vault();
            var dump = CerasRobustSerializer.DumpSchema(bytes);
            Assert.Contains("Ceras.HistoricalWriter.V1Pooled", dump);   // the old stable names
            Assert.DoesNotContain("Ceras.Test.HistoricalConstruction", dump);

            // A load seals the context, so the fingerprint is final and comparable with the file's.
            DefaultConstruction().Load<RelicVault>(bytes, 1);
            Assert.NotEqual(DefaultConstruction().Config.SchemaRegistry.ComputeFingerprint(),
                BitConverter.ToUInt64(bytes, Ceras.RobustHeader.FingerprintOffset));
        }

        [Fact]
        public void ConfiguredFactories_Supply_The_Instances_While_The_File_Is_Interpreted()
        {
            var vault = Pooled().Load<RelicVault>(Vault(), 1);

            // The migrated values, with the pool's stale state overwritten by the payload.
            Assert.Equal(4, vault.Version);
            Assert.Equal(1, vault.Kept.Id);
            Assert.Equal(7L, vault.Kept.Charge);   // v1 wrote int 7; v1's AncientName was skipped
            Assert.Equal("pool", vault.Kept.FromFactory);
            Assert.Equal("pool", vault.Token.FromFactory);
            Assert.Equal(2, vault.Token.Id);

            // The factory's instance is filled in, so a member the file does not describe keeps what the
            // pool gave it (the pool's own object initializer ran, unlike an uninitialized one).
            Assert.Equal("default", vault.Kept.Restored);
            Assert.Equal("default", vault.Token.Restored);

            // Identity: the self-cycle and the shared reference the file described are both restored, and
            // the shared object is built once.
            Assert.Same(vault.Kept, vault.Kept.Twin);
            var owner = Assert.IsType<Charm>(vault.Kept.Owner);
            Assert.Same(owner, vault.Token);
        }

        [Fact]
        public void Without_A_Configured_Construction_The_Defaults_Are_Unchanged()
        {
            var vault = DefaultConstruction().Load<RelicVault>(Vault(), 1);

            Assert.Equal(4, vault.Version);
            Assert.Equal(1, vault.Kept.Id);
            Assert.Equal(7L, vault.Kept.Charge);
            Assert.Null(vault.Kept.FromFactory);
            Assert.Null(vault.Token.FromFactory);

            // Which construction runs without a factory is still per shape: an open declared type is built
            // by its own formatter (so the field initializer runs), a sealed declared type through Ceras'
            // "uninitialized" default (so it does not). That is the shipped behavior, unchanged.
            Assert.Equal("default", vault.Kept.Restored);
            Assert.Null(vault.Token.Restored);

            // Identity is independent of the construction.
            Assert.Same(vault.Kept, vault.Kept.Twin);
            Assert.Same(vault.Kept.Owner, vault.Token);
        }

        [Fact]
        public void A_Polymorphic_Slot_Also_Honors_A_Configured_Factory()
        {
            // Read as a root, the relic's Owner slot is the first place the sealed Charm is met, so the
            // reader has to build a type it only knows through System.Object — and honor its factory.
            var charm = Assert.IsType<Charm>(Pooled().Load<Relic>(RelicRoot(), 1).Owner);
            Assert.Equal("pool", charm.FromFactory);
            Assert.Equal(2, charm.Id);

            // Without a configured construction the same slot is built by the type's own formatter, so the
            // field initializer runs — the reader does not apply the sealed-declared-type rule to a
            // polymorphic slot.
            var plain = Assert.IsType<Charm>(DefaultConstruction().Load<Relic>(RelicRoot(), 1).Owner);
            Assert.Null(plain.FromFactory);
            Assert.Equal("default", plain.Restored);
        }

        [Fact]
        public void An_Exact_Type_Reuse_Is_Never_Pulled_From_The_Pool()
        {
            var serializer = Pooled();
            var bytes = Vault();

            var existingKept = new Relic { Id = -1, FromFactory = "mine" };
            var existing = new RelicVault { Kept = existingKept, Version = 0 };
            serializer.Load(ref existing, bytes, 1);

            Assert.Same(existingKept, existing.Kept);
            Assert.Equal("mine", existing.Kept.FromFactory);   // the pool was not asked
            Assert.Equal(1, existing.Kept.Id);
            Assert.Equal(7L, existing.Kept.Charge);
            Assert.Same(existing.Kept, existing.Kept.Twin);
            Assert.Equal(4, existing.Version);

            // The nested Charm was new, so it did come from the pool.
            Assert.Equal("pool", existing.Token.FromFactory);
            Assert.Same(existing.Kept.Owner, existing.Token);
        }

        [Fact]
        public void A_Mismatched_Existing_Member_Is_Replaced_By_A_Pooled_Instance()
        {
            // A Relic-typed member holding a subtype is not reusable (the file describes the base), so the
            // replacement has to be the pool's instance rather than the stale object left in the member.
            var serializer = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.ConfigType<Relic>().ConstructBy(() => Relic.FromPool());
            });

            var stale = new SpecialRelic { Id = 42, Trait = "stale" };
            var existing = new RelicVault { Kept = stale, Version = 0 };
            serializer.Load(ref existing, Vault(), 1);

            var kept = Assert.IsType<Relic>(existing.Kept);
            Assert.NotSame(stale, existing.Kept);
            Assert.Equal("pool", kept.FromFactory);
            Assert.Equal(1, kept.Id);
            Assert.Equal(7L, kept.Charge);
            Assert.Same(kept, kept.Twin);
        }
    }
}
