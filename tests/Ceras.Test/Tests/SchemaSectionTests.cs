using System;
using System.Collections.Generic;
using System.Linq;
using Ceras;
using Ceras.Generated;
using Ceras.Savegame;
using Xunit;

namespace Ceras.Test
{
    // ── Additional test models ────────────────────────────────────────────────

    [GenerateFormatter("Potion", VersionTolerant = true)]
    [PreviousTypeName("Ceras.Test.OldPotion")]
    [MemberConfig(TargetMember.PublicFields)]
    public class Potion
    {
        [SerializedAs("Heal")]
        public int HealAmount;

        [PreviousName("OldLabel")]
        public string Label;
    }

    [GenerateFormatter("Point2", VersionTolerant = true)]
    public struct Point2
    {
        public float X;
        public float Y;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class SchemaSectionTests : TestBase
    {
        SchemaRegistry CreateRegistry()
        {
            var config = new SerializerConfig();
            GeneratedFormatters.UseFormatters(config);
            return config.Savegame.SchemaRegistry;
        }

        // ── Round-trip helpers ────────────────────────────────────────────────

        static Dictionary<string, OldTypeSchema> RoundTrip(SchemaRegistry registry)
        {
            var buffer = new byte[1024];
            int offset = 0;
            SchemaSection.Write(ref buffer, ref offset, registry);

            int readOffset = 0;
            return SchemaSection.Read(buffer, ref readOffset);
        }

        // ── Basic presence ────────────────────────────────────────────────────

        [Fact]
        public void Write_ThenRead_ContainsAllRegisteredTypes()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);

            // Every VT type registered via GeneratedFormatters should appear
            foreach (var (_, _, info) in registry.All())
                Assert.True(map.ContainsKey(info.StableName),
                    $"Missing stable name '{info.StableName}' after round-trip.");
        }

        [Fact]
        public void NonVersionTolerantType_NotInSectionMap()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            // NoSchemaType (from SchemaRegistryTests.cs) has no stable name in the registry
            Assert.False(map.ContainsKey(nameof(NoSchemaType)));
        }

        // ── Type-ID round-trip ────────────────────────────────────────────────

        [Fact]
        public void TypeId_RoundTrips()
        {
            var registry = CreateRegistry();
            registry.TryGetId(typeof(Weapon), out int expectedId);
            var map = RoundTrip(registry);
            Assert.Equal(expectedId, map["Weapon"].TypeId);
        }

        // ── StableName / PreviousNames ────────────────────────────────────────

        [Fact]
        public void StableName_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            Assert.True(map.ContainsKey("Potion"));
            Assert.Equal("Potion", map["Potion"].StableName);
        }

        [Fact]
        public void PreviousTypeName_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            // "Ceras.Test.OldPotion" is a previous name for Potion
            Assert.True(map.ContainsKey("Ceras.Test.OldPotion"),
                "Previous type name should be an alias key in the map.");
            Assert.Equal("Potion", map["Ceras.Test.OldPotion"].StableName);
        }

        // ── Kind ──────────────────────────────────────────────────────────────

        [Fact]
        public void ClassKind_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            Assert.Equal(SchemaTypeKind.Class, map["Weapon"].Kind);
        }

        [Fact]
        public void StructKind_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            Assert.Equal(SchemaTypeKind.Struct, map["Point2"].Kind);
        }

        // ── Fields ────────────────────────────────────────────────────────────

        [Fact]
        public void FieldCount_RoundTrips()
        {
            var registry = CreateRegistry();
            registry.TryGetInfo(typeof(Weapon), out var info);
            var map = RoundTrip(registry);
            Assert.Equal(info.Fields.Length, map["Weapon"].Fields.Length);
        }

        [Fact]
        public void FieldStableName_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            Assert.Contains(map["Weapon"].Fields, f => f.StableName == "Damage");
        }

        [Fact]
        public void FieldPreviousName_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            var nameField = map["Weapon"].Fields.FirstOrDefault(f => f.StableName == "Name");
            Assert.NotNull(nameField);
            Assert.Contains("OldName", nameField.PreviousNames);
        }

        [Fact]
        public void FieldIsMonomorphic_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            var weight = map["Weapon"].Fields.FirstOrDefault(f => f.StableName == "Weight");
            Assert.NotNull(weight);
            Assert.True(weight.IsMonomorphic);
        }

        [Fact]
        public void FieldTypeId_WellKnown_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            var dmg = map["Weapon"].Fields.First(f => f.StableName == "Damage");
            Assert.Equal(WellKnownTypes.Int32, dmg.TypeId);
        }

        [Fact]
        public void FieldTypeId_String_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            var label = map["Potion"].Fields.First(f => f.StableName == "Label");
            Assert.Equal(WellKnownTypes.String, label.TypeId);
        }

        // ── Fixed byte size ───────────────────────────────────────────────────

        [Fact]
        public void FixedByteSize_Struct_RoundTrips()
        {
            var registry = CreateRegistry();
            registry.TryGetInfo(typeof(Vec2Struct), out var info);
            var map = RoundTrip(registry);
            Assert.Equal(info.FixedByteSize, map["Vec2Struct"].FixedByteSize);
        }

        [Fact]
        public void FixedByteSize_Class_IsZero()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            Assert.Equal(0, map["Weapon"].FixedByteSize);
        }

        // ── LEB128 encoding ───────────────────────────────────────────────────

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(127u)]
        [InlineData(128u)]
        [InlineData(16383u)]
        [InlineData(16384u)]
        [InlineData(uint.MaxValue)]
        public void Leb128_RoundTrips(uint value)
        {
            var buf    = new byte[8];
            int wOff   = 0;
            SchemaSection.WriteLeb128(ref buf, ref wOff, value);

            int rOff   = 0;
            uint read  = SchemaSection.ReadLeb128(buf, ref rOff);
            Assert.Equal(value, read);
            Assert.Equal(wOff, rOff); // same number of bytes consumed
        }

        [Fact]
        public void Leb128_SingleByte_Values_WriteOneByte()
        {
            var buf  = new byte[8];
            int off  = 0;
            SchemaSection.WriteLeb128(ref buf, ref off, 63u);
            Assert.Equal(1, off);
        }

        [Fact]
        public void Leb128_128_WritesTwoBytes()
        {
            var buf = new byte[8];
            int off = 0;
            SchemaSection.WriteLeb128(ref buf, ref off, 128u);
            Assert.Equal(2, off);
        }

        // ── Buffer growth ─────────────────────────────────────────────────────

        [Fact]
        public void Write_WithTinyInitialBuffer_GrowsAndSucceeds()
        {
            var registry = CreateRegistry();
            var buffer = new byte[1]; // far too small
            int offset = 0;
            SchemaSection.Write(ref buffer, ref offset, registry); // must not throw
            Assert.True(offset > 0);
        }

        // ── Idempotency ───────────────────────────────────────────────────────

        [Fact]
        public void WriteReadWrite_ProducesIdenticalBytes()
        {
            var registry = CreateRegistry();
            var buf1 = new byte[1024];
            int off1 = 0;
            SchemaSection.Write(ref buf1, ref off1, registry);

            // Read back, then write again
            int rOff = 0;
            SchemaSection.Read(buf1, ref rOff); // consume exactly off1 bytes

            var buf2 = new byte[1024];
            int off2 = 0;
            SchemaSection.Write(ref buf2, ref off2, registry);

            Assert.Equal(off1, off2);
            Assert.Equal(buf1[..off1], buf2[..off2]);
        }
    }
}
