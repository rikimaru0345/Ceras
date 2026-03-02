using System;
using System.Linq;
using Ceras;
using Ceras.Generated;
using Ceras.Format;
using Xunit;

namespace Ceras.Test
{
    // ── Test models ──────────────────────────────────────────────────────────

    [CerasSerializable]
    [PreviousTypeName("Ceras.Test.OldWeapon")]
        public class Weapon
    {
        [CerasStableName("Damage")]
        public int AttackPower;

        [PreviousName("OldName")]
        public string Name;

        public float Weight;

        [Exclude]
        public float CachedStat;
    }

    [CerasSerializable]
    public struct Vec2Struct
    {
        public float X;
        public float Y;
    }

	// ─────────────────────────────────────────────────────────────────────────

	public class SchemaRegistryTests : TestBase {
        readonly SchemaRegistry _registry;

        public SchemaRegistryTests()
        {
            var config = new CerasRobustConfig();
            RobustFormatters.UseRobustFormatters(config);
            _registry = ((CerasRobustConfig)config).SchemaRegistry;
        }

        [Fact]
        public void VersionTolerantType_IsRegistered()
        {
            Assert.True(_registry.TryGetInfo(typeof(Weapon), out var info));
            Assert.NotNull(info);
        }


        [Fact]
        public void StableName_FromAttribute()
        {
            _registry.TryGetInfo(typeof(Weapon), out var info);
            Assert.Equal("Ceras.Test.Weapon", info.StableName);
        }

        [Fact]
        public void StableName_DefaultsToClassName()
        {
            _registry.TryGetInfo(typeof(Vec2Struct), out var info);
            Assert.Equal("Ceras.Test.Vec2Struct", info.StableName);
        }

        [Fact]
        public void PreviousTypeName_IsIncluded()
        {
            _registry.TryGetInfo(typeof(Weapon), out var info);
            Assert.Contains("Ceras.Test.OldWeapon", info.PreviousTypeNames);
        }

        [Fact]
        public void PreviousTypeName_ResolvesToCurrentType()
        {
            Assert.True(_registry.TryResolveType("Ceras.Test.OldWeapon", out var type));
            Assert.Equal(typeof(Weapon), type);
        }

        [Fact]
        public void StableName_ResolvesToCurrentType()
        {
            Assert.True(_registry.TryResolveType("Ceras.Test.Weapon", out var type));
            Assert.Equal(typeof(Weapon), type);
        }

        [Fact]
        public void TypeId_IsAtLeast128()
        {
            Assert.True(_registry.TryGetId(typeof(Weapon), out int id));
            Assert.True(id >= WellKnownTypes.UserTypeBase,
                $"Expected id >= {WellKnownTypes.UserTypeBase}, got {id}");
        }

        [Fact]
        public void Fields_AreInSchemaMemberComparerOrder()
        {
            // Weapon fields (sorted by SchemaMemberComparer):
            //   int AttackPower  → "System.Int32AttackPower..."
            //   float Weight     → "System.SingleWeight..."
            //   string Name      → "System.StringName..."
            // (excluded: CachedStat)
            _registry.TryGetInfo(typeof(Weapon), out var info);
            var names = info.Fields.Select(f => f.TypeName + f.StableName).ToArray();
            var sorted = names.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(sorted, names);
        }

        [Fact]
        public void Field_StableName_FromCerasStableName()
        {
            _registry.TryGetInfo(typeof(Weapon), out var info);
            var dmg = info.Fields.FirstOrDefault(f => f.StableName == "Damage");
            Assert.NotNull(dmg);
            Assert.Equal("System.Int32", dmg.TypeName);
        }

        [Fact]
        public void Field_PreviousName_IsIncluded()
        {
            _registry.TryGetInfo(typeof(Weapon), out var info);
            var name = info.Fields.FirstOrDefault(f => f.StableName == "Name");
            Assert.NotNull(name);
            Assert.Contains("OldName", name.PreviousNames);
        }

        [Fact]
        public void Field_Monomorphic_ExplicitAttribute()
        {
            _registry.TryGetInfo(typeof(Weapon), out var info);
            var weight = info.Fields.FirstOrDefault(f => f.StableName == "Weight");
            Assert.NotNull(weight);
            Assert.True(weight.IsMonomorphic);
        }

        [Fact]
        public void ExcludedField_NotInSchema()
        {
            _registry.TryGetInfo(typeof(Weapon), out var info);
            Assert.DoesNotContain(info.Fields, f => f.StableName == "CachedStat");
        }

        [Fact]
        public void Struct_HasCorrectFixedByteSize()
        {
            // Vec2Struct: float X (4) + float Y (4) = 8
            _registry.TryGetInfo(typeof(Vec2Struct), out var info);
            Assert.Equal(8, info.FixedByteSize);
        }

        [Fact]
        public void Class_HasZeroFixedByteSize()
        {
            // Classes are always variable (reference marker + fields)
            _registry.TryGetInfo(typeof(Weapon), out var info);
            Assert.Equal(0, info.FixedByteSize);
        }

        [Fact]
        public void Fingerprint_IsStable()
        {
            ulong fp1 = _registry.ComputeFingerprint();
            ulong fp2 = _registry.ComputeFingerprint();
            Assert.Equal(fp1, fp2);
        }

        [Fact]
        public void Formatter_StillWorks_WithVersionTolerant()
        {
            // The formatter itself must still produce correct output after the schema changes
            var config = new CerasRobustConfig();
            RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasSerializer(config);

            var original = new Weapon { AttackPower = 50, Name = "Excalibur", Weight = 3.2f };
            var clone    = ceras.Deserialize<Weapon>(ceras.Serialize(original));

            Assert.Equal(original.AttackPower, clone.AttackPower);
            Assert.Equal(original.Name,        clone.Name);
            Assert.Equal(original.Weight,      clone.Weight);
        }
    }
}
