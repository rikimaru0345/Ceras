using System;
using System.Collections.Generic;
using System.Linq;
using Ceras;
using Ceras.Generated;
using Ceras.Format;
using Xunit;

namespace Ceras.Test
{
    // ── Additional test models ────────────────────────────────────────────────

    [CerasSerializable]
    [PreviousTypeName("Ceras.Test.OldPotion")]
        public class Potion
    {
        [CerasStableName("Heal")]
        public int HealAmount;

        [PreviousName("OldLabel")]
        public string Label;
    }

    [CerasSerializable]
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
            var config = new CerasRobustConfig();
            RobustFormatters.UseRobustFormatters(config);

			// Manual registration for local types to ensure they have the stable names the tests expect
			var potionInfo = new SchemaTypeInfo("Ceras.Test.Potion", new[] { "Ceras.Test.OldPotion" }, SchemaTypeKind.Class, new[] {
                new SchemaFieldInfo("Heal", Array.Empty<string>(), "System.Int32", true),
                new SchemaFieldInfo("Label", Array.Empty<string>(), "System.String", true)
            }, 0);
            ((CerasRobustConfig)config).SchemaRegistry.Register(typeof(Potion), potionInfo);

            var weaponInfo = new SchemaTypeInfo("Ceras.Test.Weapon", Array.Empty<string>(), SchemaTypeKind.Class, new[] {
                new SchemaFieldInfo("Damage", Array.Empty<string>(), "System.Int32", true),
                new SchemaFieldInfo("Name", new[] { "OldName" }, "System.String", true),
                new SchemaFieldInfo("Weight", Array.Empty<string>(), "System.Single", true)
            }, 0);
            ((CerasRobustConfig)config).SchemaRegistry.Register(typeof(Weapon), weaponInfo);

            var point2Info = new SchemaTypeInfo("Ceras.Test.Point2", Array.Empty<string>(), SchemaTypeKind.Struct, new[] {
                new SchemaFieldInfo("X", Array.Empty<string>(), "System.Single", true),
                new SchemaFieldInfo("Y", Array.Empty<string>(), "System.Single", true)
            }, 8);
            ((CerasRobustConfig)config).SchemaRegistry.Register(typeof(Point2), point2Info);

            var vec2Info = new SchemaTypeInfo("Ceras.Test.Vec2Struct", Array.Empty<string>(), SchemaTypeKind.Struct, new[] {
                new SchemaFieldInfo("X", Array.Empty<string>(), "System.Single", true),
                new SchemaFieldInfo("Y", Array.Empty<string>(), "System.Single", true)
            }, 8);
            ((CerasRobustConfig)config).SchemaRegistry.Register(typeof(Vec2Struct), vec2Info);

            return ((CerasRobustConfig)config).SchemaRegistry;
        }

        // ── Round-trip helpers ────────────────────────────────────────────────

        static Dictionary<string, OldTypeSchema> RoundTrip(SchemaRegistry registry)
        {
            var buffer = new byte[1024];
            int offset = 0;
            var intern = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, registry, intern);

            // Flush intern table so we can load it
            int tableOffset = offset;
            int start = tableOffset;
            intern.Flush(ref buffer, ref tableOffset);
            var stringTable = StringTable.Load(buffer, start, tableOffset - start);

            int readOffset = 0;
            return SchemaSection.Read(buffer, ref readOffset, stringTable);
        }

        // ── Basic presence ────────────────────────────────────────────────────

        [Fact] public void Write_ThenRead_ContainsAllRegisteredTypes()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);

            // Every VT type registered via RobustFormatters should appear
            foreach (var (_, _, info) in registry.All())
                Assert.True(map.ContainsKey(info.StableName),
                    $"Missing stable name '{info.StableName}' after round-trip.");
        }


        // ── Type-ID round-trip ────────────────────────────────────────────────

        [Fact] public void TypeId_RoundTrips()
        {
            var registry = CreateRegistry();
            registry.TryGetId(typeof(Weapon), out int expectedId);
			var map = RoundTrip(registry);
			Assert.Equal(expectedId, map["Ceras.Test.Weapon"].TypeId);
        }

        // ── StableName / PreviousNames ────────────────────────────────────────

        [Fact] public void StableName_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            Assert.True(map.ContainsKey("Ceras.Test.Potion"));
            Assert.Equal("Ceras.Test.Potion", map["Ceras.Test.Potion"].StableName);
        }

        [Fact] public void PreviousTypeName_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            // "Ceras.Test.OldPotion" is a previous name for Potion
            Assert.True(map.ContainsKey("Ceras.Test.OldPotion"),
                "Previous type name should be an alias key in the map.");
            Assert.Equal("Ceras.Test.Potion", map["Ceras.Test.OldPotion"].StableName);
        }

        // ── Kind ──────────────────────────────────────────────────────────────

        [Fact] public void ClassKind_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            Assert.Equal(SchemaTypeKind.Class, map["Ceras.Test.Weapon"].Kind);
        }

        [Fact] public void StructKind_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            Assert.Equal(SchemaTypeKind.Struct, map["Ceras.Test.Point2"].Kind);
        }

        // ── Fields ────────────────────────────────────────────────────────────

        [Fact] public void FieldCount_RoundTrips()
        {
            var registry = CreateRegistry();
            registry.TryGetInfo(typeof(Weapon), out var info);
            var map = RoundTrip(registry);
            Assert.Equal(info.Fields.Length, map["Ceras.Test.Weapon"].Fields.Length);
        }

        [Fact] public void FieldStableName_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            Assert.Contains(map["Ceras.Test.Weapon"].Fields, f => f.StableName == "Damage");
        }

        [Fact] public void FieldPreviousName_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            var nameField = map["Ceras.Test.Weapon"].Fields.FirstOrDefault(f => f.StableName == "Name");
            Assert.NotNull(nameField);
            Assert.Contains("OldName", nameField.PreviousNames);
        }

        [Fact] public void FieldIsMonomorphic_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            var weight = map["Ceras.Test.Weapon"].Fields.FirstOrDefault(f => f.StableName == "Weight");
            Assert.NotNull(weight);
            Assert.True(weight.IsMonomorphic);
        }

        [Fact] public void FieldTypeId_WellKnown_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            var dmg = map["Ceras.Test.Weapon"].Fields.First(f => f.StableName == "Damage");
            Assert.Equal(WellKnownTypes.Int32, dmg.TypeId);
        }

        [Fact] public void FieldTypeId_String_RoundTrips()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            var label = map["Ceras.Test.Potion"].Fields.First(f => f.StableName == "Label");
            Assert.Equal(WellKnownTypes.String, label.TypeId);
        }

        // ── Fixed byte size ───────────────────────────────────────────────────

        [Fact] public void FixedByteSize_Struct_RoundTrips()
        {
            var registry = CreateRegistry();
            registry.TryGetInfo(typeof(Vec2Struct), out var info);
            var map = RoundTrip(registry);
            Assert.Equal(info.FixedByteSize, map["Ceras.Test.Vec2Struct"].FixedByteSize);
        }

        [Fact] public void FixedByteSize_Class_IsZero()
        {
            var registry = CreateRegistry();
            var map = RoundTrip(registry);
            Assert.Equal(0, map["Ceras.Test.Weapon"].FixedByteSize);
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

        [Fact] public void Leb128_SingleByte_Values_WriteOneByte()
        {
            var buf  = new byte[8];
            int off  = 0;
            SchemaSection.WriteLeb128(ref buf, ref off, 63u);
            Assert.Equal(1, off);
        }

        [Fact] public void Leb128_128_WritesTwoBytes()
        {
            var buf = new byte[8];
            int off = 0;
            SchemaSection.WriteLeb128(ref buf, ref off, 128u);
            Assert.Equal(2, off);
        }

        // ── Buffer growth ─────────────────────────────────────────────────────

        [Fact] public void Write_WithTinyInitialBuffer_GrowsAndSucceeds()
        {
            var registry = CreateRegistry();
            var buffer = new byte[1]; // far too small
            int offset = 0;
            var intern = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, registry, intern); // must not throw
            Assert.True(offset > 0);
        }

        // ── Idempotency ───────────────────────────────────────────────────────

        [Fact] public void Write_WriteReadWrite_ProducesIdenticalBytes()
        {
            var registry = CreateRegistry();
            
            // Prime the registry by doing one round-trip.
            // This ensures all types reachable via fields are already registered.
            RoundTrip(registry);

            var buf1 = new byte[1024];
            int off1 = 0;
            var intern1 = new StringInternTable();
            SchemaSection.Write(ref buf1, ref off1, registry, intern1);

            // Read back, then write again
            int rOff = 0;
            int tOff = off1;
            int start1 = tOff;
            intern1.Flush(ref buf1, ref tOff);
            var table1 = StringTable.Load(buf1, start1, tOff - start1);
            SchemaSection.Read(buf1, ref rOff, table1); // consume exactly off1 bytes

            var buf2 = new byte[1024];
            int off2 = 0;
            var intern2 = new StringInternTable();
            SchemaSection.Write(ref buf2, ref off2, registry, intern2);

            Assert.Equal(off1, off2);
            Assert.Equal(buf1[..off1], buf2[..off2]);
        }
    }
}
