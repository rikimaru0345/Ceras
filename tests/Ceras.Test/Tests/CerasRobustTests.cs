using System;
using System.Buffers.Binary;
using System.Linq;
using Ceras;
using Ceras.Generated;
using Ceras.Format;
using Xunit;

namespace Ceras.Test
{
    // ── Additional save-test models ───────────────────────────────────────────

    [CerasSerializable]
    [MemberConfig(TargetMember.PublicFields)]
    public class SaveHero
    {
        [CerasStableName("Name")]
        public string Name;

        [CerasStableName("Level")]
        public int Level;

        [CerasStableName("Hp")]
        public float Hp;

        [Exclude]
        public float CachedDps;
    }

    [CerasSerializable]
    [MemberConfig(TargetMember.PublicFields)]
    public class RobustInventory
    {
        [CerasStableName("Gold")]
        public int Gold;

        // Two string fields — tests that deduplication works across a single object
        [CerasStableName("Tag")]
        public string Tag;

        [CerasStableName("Note")]
        public string Note;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class CerasRobustTests : TestBase
    {
        CerasSerializer CreateSerializer()
        {
            var config = new SerializerConfig();
            RobustFormatters.UseRobustFormatters(config);
            CerasRobust.Configure(config);
            return new CerasSerializer(config);
        }

        // ── Round-trip ────────────────────────────────────────────────────────

        [Fact]
        public void Save_ThenLoad_RoundTrips_SimpleObject()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "Aria", Level = 12, Hp = 98.5f };
            byte[] data  = CerasRobust.Save(hero, robustVersion: 1, ceras);
            SaveHero clone = CerasRobust.Load<SaveHero>(data, currentRobustVersion: 1, ceras);

            Assert.Equal(hero.Name,  clone.Name);
            Assert.Equal(hero.Level, clone.Level);
            Assert.Equal(hero.Hp,    clone.Hp);
        }

        [Fact]
        public void Save_ThenLoad_NullString_RoundTrips()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = null, Level = 5, Hp = 10f };
            byte[] data  = CerasRobust.Save(hero, robustVersion: 1, ceras);
            SaveHero clone = CerasRobust.Load<SaveHero>(data, currentRobustVersion: 1, ceras);

            Assert.Null(clone.Name);
        }

        [Fact]
        public void Save_ThenLoad_ExcludedField_IsDefault()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "X", Level = 1, Hp = 1f, CachedDps = 999f };
            byte[] data  = CerasRobust.Save(hero, robustVersion: 1, ceras);
            SaveHero clone = CerasRobust.Load<SaveHero>(data, currentRobustVersion: 1, ceras);

            Assert.Equal(0f, clone.CachedDps);
        }

        [Fact]
        public void Save_ThenLoad_DuplicateStrings_RoundTrip()
        {
            var ceras = CreateSerializer();
            var inv = new RobustInventory { Gold = 100, Tag = "rare", Note = "rare" };
            byte[] data  = CerasRobust.Save(inv, robustVersion: 1, ceras);
            RobustInventory cl = CerasRobust.Load<RobustInventory>(data, currentRobustVersion: 1, ceras);

            Assert.Equal("rare", cl.Tag);
            Assert.Equal("rare", cl.Note);
        }

        // ── Header validation ─────────────────────────────────────────────────

        [Fact]
        public void Header_MagicBytes_Are_CERA()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasRobust.Save(new SaveHero(), robustVersion: 1, ceras);
            Assert.Equal((byte)'C', data[0]);
            Assert.Equal((byte)'E', data[1]);
            Assert.Equal((byte)'R', data[2]);
            Assert.Equal((byte)'A', data[3]);
        }

        [Fact]
        public void Header_FormatVersion_IsOne()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasRobust.Save(new SaveHero(), robustVersion: 1, ceras);
            Assert.Equal(CerasRobust.FormatVersion, data[CerasRobust.OffsetFormatVersion]);
        }

        [Fact]
        public void Header_GameVersion_RoundTrips()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasRobust.Save(new SaveHero(), robustVersion: 42u, ceras);
            uint gv = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(CerasRobust.OffsetGameVersion));
            Assert.Equal(42u, gv);
        }

        [Fact]
        public void Header_Fingerprint_MatchesRegistry()
        {
            var ceras = CreateSerializer();
            byte[]  data = CerasRobust.Save(new SaveHero(), robustVersion: 1, ceras);
            ulong   fp   = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(CerasRobust.OffsetFingerprint));
            Assert.Equal(ceras.Config.Robust.SchemaRegistry.ComputeFingerprint(), fp);
        }

        [Fact]
        public void Header_DataOffset_IsAfterSchemaSection()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasRobust.Save(new SaveHero(), robustVersion: 1, ceras);
            long dataOff = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasRobust.OffsetDataSection));
            Assert.True(dataOff > CerasRobust.HeaderSize,
                $"Data section offset ({dataOff}) should be after the header ({CerasRobust.HeaderSize}).");
        }

        [Fact]
        public void Header_StringTableOffset_IsAfterDataSection()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasRobust.Save(new SaveHero(), robustVersion: 1, ceras);
            long dataOff   = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasRobust.OffsetDataSection));
            long tableOff  = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasRobust.OffsetStringTable));
            Assert.True(tableOff > dataOff,
                $"String table offset ({tableOff}) should be after data section ({dataOff}).");
        }

        [Fact]
        public void TotalLength_IsAtLeastHeaderSize()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasRobust.Save(new SaveHero(), robustVersion: 1, ceras);
            Assert.True(data.Length >= CerasRobust.HeaderSize);
        }

        // ── String table deduplication ────────────────────────────────────────

        [Fact]
        public void DuplicateStrings_WriteOnlyOnce_InStringTable()
        {
            var config = new SerializerConfig();
            Ceras.Generated.RobustFormatters.UseRobustFormatters(config);
            CerasRobust.Configure(config);
            var ceras = new CerasSerializer(config);

            // Tag == Note == "duplicate" → should be interned once
            var inv = new RobustInventory { Gold = 0, Tag = "duplicate", Note = "duplicate" };
            byte[] data = CerasRobust.Save(inv, robustVersion: 1, ceras);
            long tableOff = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasRobust.OffsetStringTable));

            // Read string table
            var table = StringTable.Load(data, (int)tableOff);
            
            // "duplicate" interned once; null is index 0 (not stored).
            // Check if any entry in the table matches "duplicate"
            bool found = Array.Exists(table, s => s == "duplicate");
            Assert.True(found, "String 'duplicate' not found in table");
            
            int occurrences = table.Count(s => s == "duplicate");
            Assert.Equal(1, occurrences);
        }

        // ── Error cases ───────────────────────────────────────────────────────

        [Fact]
        public void Load_BadMagic_Throws()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasRobust.Save(new SaveHero(), robustVersion: 1, ceras);
            data[0] = (byte)'X'; // corrupt magic
            Assert.Throws<InvalidOperationException>(
                () => CerasRobust.Load<SaveHero>(data, currentRobustVersion: 1, ceras));
        }

        [Fact]
        public void Load_SaveVersionNewerThanCurrent_Throws()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasRobust.Save(new SaveHero(), robustVersion: 10, ceras);
            Assert.Throws<InvalidOperationException>(
                () => CerasRobust.Load<SaveHero>(data, currentRobustVersion: 5, ceras));
        }

        [Fact]
        public void Load_SaveVersionEqualsCurrentVersion_Succeeds()
        {
            var ceras = CreateSerializer();
            byte[] data  = CerasRobust.Save(new SaveHero { Name = "V", Level = 1, Hp = 1 }, robustVersion: 7, ceras);
            SaveHero clone = CerasRobust.Load<SaveHero>(data, currentRobustVersion: 7, ceras);
            Assert.Equal("V", clone.Name);
        }

        [Fact]
        public void Load_SaveVersionOlderThanCurrent_Succeeds()
        {
            var ceras = CreateSerializer();
            // Same schema — no migration needed, just an older game version
            byte[] data  = CerasRobust.Save(new SaveHero { Name = "Old", Level = 3, Hp = 5 }, robustVersion: 1, ceras);
            SaveHero clone = CerasRobust.Load<SaveHero>(data, currentRobustVersion: 99, ceras);
            Assert.Equal("Old", clone.Name);
        }

        [Fact]
        public void Load_TooShort_Throws()
        {
            var ceras = CreateSerializer();
            Assert.Throws<InvalidOperationException>(
                () => CerasRobust.Load<SaveHero>(new byte[4], currentRobustVersion: 1, ceras));
        }

        [Fact]
        public void Load_FingerprintMismatch_TriggersSlowPath_AndSucceeds()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "V", Level = 1, Hp = 1f };
            byte[] data = CerasRobust.Save(hero, robustVersion: 1, ceras);

            // Corrupt the fingerprint in the header to force slow path
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(CerasRobust.OffsetFingerprint), 0xDEADBEEFCAFEBABEul);

            // Recalculate checksum
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(CerasRobust.OffsetChecksum), 0);
            uint crc = Helpers.HashHelpers.CalculateCrc32(data);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(CerasRobust.OffsetChecksum), crc);

            // Slow path: schema section in the file matches current schema exactly
            // -> migration plan has no changes -> values are read correctly
            SaveHero clone = CerasRobust.Load<SaveHero>(data, currentRobustVersion: 1, ceras);
            Assert.Equal(hero.Name,  clone.Name);
            Assert.Equal(hero.Level, clone.Level);
            Assert.Equal(hero.Hp,    clone.Hp);
        }

        // ── Configure ─────────────────────────────────────────────────

        [Fact]
        public void Configure_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => CerasRobust.Configure(null));
        }

        [Fact]
        public void Configure_DisablesEmbedChecksum()
        {
            var config = new SerializerConfig();
            config.Advanced.EmbedChecksum = true;
            CerasRobust.Configure(config);
            Assert.False(config.Advanced.EmbedChecksum);
        }
    }
}
