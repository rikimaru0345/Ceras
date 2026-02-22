using System;
using System.Buffers.Binary;
using System.Linq;
using Ceras;
using Ceras.Generated;
using Ceras.Savegame;
using Xunit;

namespace Ceras.Test
{
    // ── Additional save-test models ───────────────────────────────────────────

    [GenerateFormatter("SaveHero", VersionTolerant = true)]
    [MemberConfig(TargetMember.PublicFields)]
    public class SaveHero
    {
        [SerializedAs("Name")]
        public string Name;

        [SerializedAs("Level")]
        public int Level;

        [SerializedAs("Hp")]
        public float Hp;

        [Exclude]
        public float CachedDps;
    }

    [GenerateFormatter("SavegameInventory", VersionTolerant = true)]
    [MemberConfig(TargetMember.PublicFields)]
    public class SavegameInventory
    {
        [SerializedAs("Gold")]
        public int Gold;

        // Two string fields — tests that deduplication works across a single object
        [SerializedAs("Tag")]
        public string Tag;

        [SerializedAs("Note")]
        public string Note;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class CerasSaveTests : TestBase
    {
        CerasSerializer CreateSerializer()
        {
            var config = new SerializerConfig();
            GeneratedFormatters.UseFormatters(config);
            CerasSave.ConfigureSavegame(config);
            return new CerasSerializer(config);
        }

        // ── Round-trip ────────────────────────────────────────────────────────

        [Fact]
        public void Save_ThenLoad_RoundTrips_SimpleObject()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "Aria", Level = 12, Hp = 98.5f };
            byte[] data  = CerasSave.Save(hero, gameVersion: 1, ceras);
            SaveHero clone = CerasSave.Load<SaveHero>(data, currentGameVersion: 1, ceras);

            Assert.Equal(hero.Name,  clone.Name);
            Assert.Equal(hero.Level, clone.Level);
            Assert.Equal(hero.Hp,    clone.Hp);
        }

        [Fact]
        public void Save_ThenLoad_NullString_RoundTrips()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = null, Level = 5, Hp = 10f };
            byte[] data  = CerasSave.Save(hero, gameVersion: 1, ceras);
            SaveHero clone = CerasSave.Load<SaveHero>(data, currentGameVersion: 1, ceras);

            Assert.Null(clone.Name);
        }

        [Fact]
        public void Save_ThenLoad_ExcludedField_IsDefault()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "X", Level = 1, Hp = 1f, CachedDps = 999f };
            byte[] data  = CerasSave.Save(hero, gameVersion: 1, ceras);
            SaveHero clone = CerasSave.Load<SaveHero>(data, currentGameVersion: 1, ceras);

            Assert.Equal(0f, clone.CachedDps);
        }

        [Fact]
        public void Save_ThenLoad_DuplicateStrings_RoundTrip()
        {
            var ceras = CreateSerializer();
            var inv = new SavegameInventory { Gold = 100, Tag = "rare", Note = "rare" };
            byte[] data  = CerasSave.Save(inv, gameVersion: 1, ceras);
            SavegameInventory cl = CerasSave.Load<SavegameInventory>(data, currentGameVersion: 1, ceras);

            Assert.Equal("rare", cl.Tag);
            Assert.Equal("rare", cl.Note);
        }

        // ── Header validation ─────────────────────────────────────────────────

        [Fact]
        public void Header_MagicBytes_Are_CERA()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasSave.Save(new SaveHero(), gameVersion: 1, ceras);
            Assert.Equal((byte)'C', data[0]);
            Assert.Equal((byte)'E', data[1]);
            Assert.Equal((byte)'R', data[2]);
            Assert.Equal((byte)'A', data[3]);
        }

        [Fact]
        public void Header_FormatVersion_IsOne()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasSave.Save(new SaveHero(), gameVersion: 1, ceras);
            Assert.Equal(CerasSave.FormatVersion, data[CerasSave.OffsetFormatVersion]);
        }

        [Fact]
        public void Header_GameVersion_RoundTrips()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasSave.Save(new SaveHero(), gameVersion: 42u, ceras);
            uint gv = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(CerasSave.OffsetGameVersion));
            Assert.Equal(42u, gv);
        }

        [Fact]
        public void Header_Fingerprint_MatchesRegistry()
        {
            var ceras = CreateSerializer();
            byte[]  data = CerasSave.Save(new SaveHero(), gameVersion: 1, ceras);
            ulong   fp   = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(CerasSave.OffsetFingerprint));
            Assert.Equal(ceras.Config.Savegame.SchemaRegistry.ComputeFingerprint(), fp);
        }

        [Fact]
        public void Header_DataOffset_IsAfterSchemaSection()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasSave.Save(new SaveHero(), gameVersion: 1, ceras);
            long dataOff = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasSave.OffsetDataSection));
            Assert.True(dataOff > CerasSave.HeaderSize,
                $"Data section offset ({dataOff}) should be after the header ({CerasSave.HeaderSize}).");
        }

        [Fact]
        public void Header_StringTableOffset_IsAfterDataSection()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasSave.Save(new SaveHero(), gameVersion: 1, ceras);
            long dataOff   = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasSave.OffsetDataSection));
            long tableOff  = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasSave.OffsetStringTable));
            Assert.True(tableOff > dataOff,
                $"String table offset ({tableOff}) should be after data section ({dataOff}).");
        }

        [Fact]
        public void TotalLength_IsAtLeastHeaderSize()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasSave.Save(new SaveHero(), gameVersion: 1, ceras);
            Assert.True(data.Length >= CerasSave.HeaderSize);
        }

        // ── String table deduplication ────────────────────────────────────────

             [Fact]
        public void DuplicateStrings_WriteOnlyOnce_InStringTable()
        {
            var ceras = CreateSerializer();
            // Tag == Note == "duplicate" → should be interned once
            var inv = new SavegameInventory { Gold = 0, Tag = "duplicate", Note = "duplicate" };
            byte[] data = CerasSave.Save(inv, gameVersion: 1, ceras);
            long tableOff = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasSave.OffsetStringTable));

            // Read string table count
            int tOff  = (int)tableOff;
            uint cnt  = SchemaSection.ReadLeb128(data, ref tOff);

            // "duplicate" interned once; null is index 0 (not stored).
            // The count should include exactly one entry for "duplicate".
            // (There may be others from the schema, but let's check the table loaded correctly.)
            var table = StringTable.Load(data, (int)tableOff);
            int occurrences = table.Count(s => s == "duplicate");
            Assert.Equal(1, occurrences);
        }

        // ── Error cases ───────────────────────────────────────────────────────

        [Fact]
        public void Load_BadMagic_Throws()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasSave.Save(new SaveHero(), gameVersion: 1, ceras);
            data[0] = (byte)'X'; // corrupt magic
            Assert.Throws<InvalidOperationException>(
                () => CerasSave.Load<SaveHero>(data, currentGameVersion: 1, ceras));
        }

        [Fact]
        public void Load_SaveVersionNewerThanCurrent_Throws()
        {
            var ceras = CreateSerializer();
            byte[] data = CerasSave.Save(new SaveHero(), gameVersion: 10, ceras);
            Assert.Throws<InvalidOperationException>(
                () => CerasSave.Load<SaveHero>(data, currentGameVersion: 5, ceras));
        }

        [Fact]
        public void Load_SaveVersionEqualsCurrentVersion_Succeeds()
        {
            var ceras = CreateSerializer();
            byte[] data  = CerasSave.Save(new SaveHero { Name = "V", Level = 1, Hp = 1 }, gameVersion: 7, ceras);
            SaveHero clone = CerasSave.Load<SaveHero>(data, currentGameVersion: 7, ceras);
            Assert.Equal("V", clone.Name);
        }

        [Fact]
        public void Load_SaveVersionOlderThanCurrent_Succeeds()
        {
            var ceras = CreateSerializer();
            // Same schema — no migration needed, just an older game version
            byte[] data  = CerasSave.Save(new SaveHero { Name = "Old", Level = 3, Hp = 5 }, gameVersion: 1, ceras);
            SaveHero clone = CerasSave.Load<SaveHero>(data, currentGameVersion: 99, ceras);
            Assert.Equal("Old", clone.Name);
        }

        [Fact]
        public void Load_TooShort_Throws()
        {
            var ceras = CreateSerializer();
            Assert.Throws<InvalidOperationException>(
                () => CerasSave.Load<SaveHero>(new byte[4], currentGameVersion: 1, ceras));
        }

        [Fact]
        public void Load_FingerprintMismatch_TriggersSlowPath_AndSucceeds()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "V", Level = 1, Hp = 1f };
            byte[] data = CerasSave.Save(hero, gameVersion: 1, ceras);

            // Corrupt the fingerprint in the header to force slow path
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(CerasSave.OffsetFingerprint), 0xDEADBEEFCAFEBABEul);

            // Recalculate checksum
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(CerasSave.OffsetChecksum), 0);
            uint crc = Helpers.HashHelpers.CalculateCrc32(data);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(CerasSave.OffsetChecksum), crc);

            // Slow path: schema section in the file matches current schema exactly
            // -> migration plan has no changes -> values are read correctly
            SaveHero clone = CerasSave.Load<SaveHero>(data, currentGameVersion: 1, ceras);
            Assert.Equal(hero.Name,  clone.Name);
            Assert.Equal(hero.Level, clone.Level);
            Assert.Equal(hero.Hp,    clone.Hp);
        }

        // ── ConfigureSavegame ─────────────────────────────────────────────────

        [Fact]
        public void ConfigureSavegame_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => CerasSave.ConfigureSavegame(null));
        }

        [Fact]
        public void ConfigureSavegame_DisablesEmbedChecksum()
        {
            var config = new SerializerConfig();
            config.Advanced.EmbedChecksum = true;
            CerasSave.ConfigureSavegame(config);
            Assert.False(config.Advanced.EmbedChecksum);
        }
    }
}
