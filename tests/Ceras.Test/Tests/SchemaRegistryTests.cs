using System;
using System.Linq;
using Ceras;
using Ceras.Generated;
using Ceras.Savegame;
using Xunit;

namespace Ceras.Test
{
    // ── Test models ──────────────────────────────────────────────────────────

    [GenerateFormatter("Weapon", VersionTolerant = true)]
    [PreviousTypeName("Ceras.Test.OldWeapon")]
    [MemberConfig(TargetMember.PublicFields)]
    public class Weapon
    {
        [SerializedAs("Damage")]
        public int AttackPower;

        [PreviousName("OldName")]
        public string Name;

        [Monomorphic]
        public float Weight;

        [Exclude]
        public float CachedStat;
    }

    [GenerateFormatter("Vec2Struct", VersionTolerant = true)]
    public struct Vec2Struct
    {
        public float X;
        public float Y;
    }

    // Non-version-tolerant — should NOT appear in registry
    [GenerateFormatter]
    [MemberConfig(TargetMember.PublicFields)]
    public class NoSchemaType
    {
        public int Value;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class SchemaRegistryTests : TestBase
    {
        static SchemaRegistryTests()
        {
            // Register once for all tests in this class
            var config = new SerializerConfig();
            GeneratedFormatters.UseFormatters(config);
        }

        [Fact]
        public void VersionTolerantType_IsRegistered()
        {
            Assert.True(SavegameSchemaRegistry.TryGetInfo(typeof(Weapon), out var info));
            Assert.NotNull(info);
        }

        [Fact]
        public void NonVersionTolerantType_IsNotRegistered()
        {
            Assert.False(SavegameSchemaRegistry.TryGetInfo(typeof(NoSchemaType), out _));
        }

        [Fact]
        public void StableName_FromAttribute()
        {
            SavegameSchemaRegistry.TryGetInfo(typeof(Weapon), out var info);
            Assert.Equal("Weapon", info.StableName);
        }

        [Fact]
        public void StableName_DefaultsToClassName()
        {
            SavegameSchemaRegistry.TryGetInfo(typeof(Vec2Struct), out var info);
            Assert.Equal("Vec2Struct", info.StableName);
        }

        [Fact]
        public void PreviousTypeName_IsIncluded()
        {
            SavegameSchemaRegistry.TryGetInfo(typeof(Weapon), out var info);
            Assert.Contains("Ceras.Test.OldWeapon", info.PreviousTypeNames);
        }

        [Fact]
        public void PreviousTypeName_ResolvesToCurrentType()
        {
            Assert.True(SavegameSchemaRegistry.TryResolveType("Ceras.Test.OldWeapon", out var type));
            Assert.Equal(typeof(Weapon), type);
        }

        [Fact]
        public void StableName_ResolvesToCurrentType()
        {
            Assert.True(SavegameSchemaRegistry.TryResolveType("Weapon", out var type));
            Assert.Equal(typeof(Weapon), type);
        }

        [Fact]
        public void TypeId_IsAtLeast128()
        {
            Assert.True(SavegameSchemaRegistry.TryGetId(typeof(Weapon), out int id));
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
            SavegameSchemaRegistry.TryGetInfo(typeof(Weapon), out var info);
            var names = info.Fields.Select(f => f.TypeName + f.StableName).ToArray();
            var sorted = names.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(sorted, names);
        }

        [Fact]
        public void Field_StableName_FromSerializedAs()
        {
            SavegameSchemaRegistry.TryGetInfo(typeof(Weapon), out var info);
            var dmg = info.Fields.FirstOrDefault(f => f.StableName == "Damage");
            Assert.NotNull(dmg);
            Assert.Equal("System.Int32", dmg.TypeName);
        }

        [Fact]
        public void Field_PreviousName_IsIncluded()
        {
            SavegameSchemaRegistry.TryGetInfo(typeof(Weapon), out var info);
            var name = info.Fields.FirstOrDefault(f => f.StableName == "Name");
            Assert.NotNull(name);
            Assert.Contains("OldName", name.PreviousNames);
        }

        [Fact]
        public void Field_Monomorphic_ExplicitAttribute()
        {
            SavegameSchemaRegistry.TryGetInfo(typeof(Weapon), out var info);
            var weight = info.Fields.FirstOrDefault(f => f.StableName == "Weight");
            Assert.NotNull(weight);
            Assert.True(weight.IsMonomorphic);
        }

        [Fact]
        public void ExcludedField_NotInSchema()
        {
            SavegameSchemaRegistry.TryGetInfo(typeof(Weapon), out var info);
            Assert.DoesNotContain(info.Fields, f => f.StableName == "CachedStat");
        }

        [Fact]
        public void Struct_HasCorrectFixedByteSize()
        {
            // Vec2Struct: float X (4) + float Y (4) = 8
            SavegameSchemaRegistry.TryGetInfo(typeof(Vec2Struct), out var info);
            Assert.Equal(8, info.FixedByteSize);
        }

        [Fact]
        public void Class_HasZeroFixedByteSize()
        {
            // Classes are always variable (reference marker + fields)
            SavegameSchemaRegistry.TryGetInfo(typeof(Weapon), out var info);
            Assert.Equal(0, info.FixedByteSize);
        }

        [Fact]
        public void Fingerprint_IsStable()
        {
            ulong fp1 = SavegameSchemaRegistry.ComputeFingerprint();
            ulong fp2 = SavegameSchemaRegistry.ComputeFingerprint();
            Assert.Equal(fp1, fp2);
        }

        [Fact]
        public void Formatter_StillWorks_WithVersionTolerant()
        {
            // The formatter itself must still produce correct output after the schema changes
            var config = new SerializerConfig();
            GeneratedFormatters.UseFormatters(config);
            var ceras = new CerasSerializer(config);

            var original = new Weapon { AttackPower = 50, Name = "Excalibur", Weight = 3.2f };
            var clone    = ceras.Deserialize<Weapon>(ceras.Serialize(original));

            Assert.Equal(original.AttackPower, clone.AttackPower);
            Assert.Equal(original.Name,        clone.Name);
            Assert.Equal(original.Weight,      clone.Weight);
        }
    }
}
