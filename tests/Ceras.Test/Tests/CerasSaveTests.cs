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

    [GenerateFormatter("Hero", VersionTolerant = true)]
    [MemberConfig(TargetMember.PublicFields)]
    public class Hero
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
        static readonly CerasSerializer _ceras;

        static CerasSaveTests()
        {
            var config = new SerializerConfig();
            GeneratedFormatters.UseFormatters(config);
            CerasSave.ConfigureSavegame(config);
            _ceras = new CerasSerializer(config);
        }

        // ── Round-trip ────────────────────────────────────────────────────────

        [Fact]
        public void Save_ThenLoad_RoundTrips_SimpleObject()
        {
            var hero = new Hero { Name = "Aria", Level = 12, Hp = 98.5f };
            byte[] data  = CerasSave.Save(hero, gameVersion: 1, _ceras);
            Hero   clone = CerasSave.Load<Hero>(data, currentGameVersion: 1, _ceras);

            Assert.Equal(hero.Name,  clone.Name);
            Assert.Equal(hero.Level, clone.Level);
            Assert.Equal(hero.Hp,    clone.Hp);
        }

        [Fact]
        public void Save_ThenLoad_NullString_RoundTrips()
        {
            var hero = new Hero { Name = null, Level = 5, Hp = 10f };
            byte[] data  = CerasSave.Save(hero, gameVersion: 1, _ceras);
            Hero   clone = CerasSave.Load<Hero>(data, currentGameVersion: 1, _ceras);

            Assert.Null(clone.Name);
        }

        [Fact]
        public void Save_ThenLoad_ExcludedField_IsDefault()
        {
            var hero = new Hero { Name = "X", Level = 1, Hp = 1f, CachedDps = 999f };
            byte[] data  = CerasSave.Save(hero, gameVersion: 1, _ceras);
            Hero   clone = CerasSave.Load<Hero>(data, currentGameVersion: 1, _ceras);

            Assert.Equal(0f, clone.CachedDps);
        }

        [Fact]
        public void Save_ThenLoad_DuplicateStrings_RoundTrip()
        {
            var inv = new SavegameInventory { Gold = 100, Tag = "rare", Note = "rare" };
            byte[] data  = CerasSave.Save(inv, gameVersion: 1, _ceras);
            SavegameInventory cl = CerasSave.Load<SavegameInventory>(data, currentGameVersion: 1, _ceras);

            Assert.Equal("rare", cl.Tag);
            Assert.Equal("rare", cl.Note);
        }

        // ── Header validation ─────────────────────────────────────────────────

        [Fact]
        public void Header_MagicBytes_Are_CERA()
        {
            byte[] data = CerasSave.Save(new Hero(), gameVersion: 1, _ceras);
            Assert.Equal((byte)'C', data[0]);
            Assert.Equal((byte)'E', data[1]);
            Assert.Equal((byte)'R', data[2]);
            Assert.Equal((byte)'A', data[3]);
        }

        [Fact]
        public void Header_FormatVersion_IsOne()
        {
            byte[] data = CerasSave.Save(new Hero(), gameVersion: 1, _ceras);
            Assert.Equal(CerasSave.FormatVersion, data[CerasSave.OffsetFormatVersion]);
        }

        [Fact]
        public void Header_GameVersion_RoundTrips()
        {
            byte[] data = CerasSave.Save(new Hero(), gameVersion: 42u, _ceras);
            uint gv = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(CerasSave.OffsetGameVersion));
            Assert.Equal(42u, gv);
        }

        [Fact]
        public void Header_Fingerprint_MatchesRegistry()
        {
            byte[]  data = CerasSave.Save(new Hero(), gameVersion: 1, _ceras);
            ulong   fp   = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(CerasSave.OffsetFingerprint));
            Assert.Equal(SavegameSchemaRegistry.ComputeFingerprint(), fp);
        }

        [Fact]
        public void Header_DataOffset_IsAfterSchemaSection()
        {
            byte[] data = CerasSave.Save(new Hero(), gameVersion: 1, _ceras);
            long dataOff = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasSave.OffsetDataSection));
            Assert.True(dataOff > CerasSave.HeaderSize,
                $"Data section offset ({dataOff}) should be after the header ({CerasSave.HeaderSize}).");
        }

        [Fact]
        public void Header_StringTableOffset_IsAfterDataSection()
        {
            byte[] data = CerasSave.Save(new Hero(), gameVersion: 1, _ceras);
            long dataOff   = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasSave.OffsetDataSection));
            long tableOff  = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(CerasSave.OffsetStringTable));
            Assert.True(tableOff > dataOff,
                $"String table offset ({tableOff}) should be after data section ({dataOff}).");
        }

        [Fact]
        public void TotalLength_IsAtLeastHeaderSize()
        {
            byte[] data = CerasSave.Save(new Hero(), gameVersion: 1, _ceras);
            Assert.True(data.Length >= CerasSave.HeaderSize);
        }

        // ── String table deduplication ────────────────────────────────────────

             [Fact]
        public void DuplicateStrings_WriteOnlyOnce_InStringTable()
        {
            // Tag == Note == "duplicate" → should be interned once
            var inv = new SavegameInventory { Gold = 0, Tag = "duplicate", Note = "duplicate" };
            byte[] data = CerasSave.Save(inv, gameVersion: 1, _ceras);
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
            byte[] data = CerasSave.Save(new Hero(), gameVersion: 1, _ceras);
            data[0] = (byte)'X'; // corrupt magic
            Assert.Throws<InvalidOperationException>(
                () => CerasSave.Load<Hero>(data, currentGameVersion: 1, _ceras));
        }

        [Fact]
        public void Load_SaveVersionNewerThanCurrent_Throws()
        {
            byte[] data = CerasSave.Save(new Hero(), gameVersion: 10, _ceras);
            Assert.Throws<InvalidOperationException>(
                () => CerasSave.Load<Hero>(data, currentGameVersion: 5, _ceras));
        }

        [Fact]
        public void Load_SaveVersionEqualsCurrentVersion_Succeeds()
        {
            byte[] data  = CerasSave.Save(new Hero { Name = "V", Level = 1, Hp = 1 }, gameVersion: 7, _ceras);
            Hero   clone = CerasSave.Load<Hero>(data, currentGameVersion: 7, _ceras);
            Assert.Equal("V", clone.Name);
        }

        [Fact]
        public void Load_SaveVersionOlderThanCurrent_Succeeds()
        {
            // Same schema — no migration needed, just an older game version
            byte[] data  = CerasSave.Save(new Hero { Name = "Old", Level = 3, Hp = 5 }, gameVersion: 1, _ceras);
            Hero   clone = CerasSave.Load<Hero>(data, currentGameVersion: 99, _ceras);
            Assert.Equal("Old", clone.Name);
        }

        [Fact]
        public void Load_TooShort_Throws()
        {
            Assert.Throws<InvalidOperationException>(
                () => CerasSave.Load<Hero>(new byte[4], currentGameVersion: 1, _ceras));
        }

        [Fact]
        public void Load_FingerprintMismatch_ThrowsNotSupported()
        {
            byte[] data = CerasSave.Save(new Hero(), gameVersion: 1, _ceras);

            // Corrupt the fingerprint in the header
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(CerasSave.OffsetFingerprint), 0xDEADBEEFCAFEBABEul);

            Assert.Throws<NotSupportedException>(
                () => CerasSave.Load<Hero>(data, currentGameVersion: 1, _ceras));
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
