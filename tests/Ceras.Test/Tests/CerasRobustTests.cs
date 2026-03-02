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
        CerasRobustSerializer CreateSerializer()
        {
            return new CerasRobustSerializer(config => {
                RobustFormatters.UseRobustFormatters(config);
            });
        }

        // ── Round-trip ────────────────────────────────────────────────────────

        [Fact]
        public void Save_ThenLoad_RoundTrips_SimpleObject()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "Aria", Level = 12, Hp = 98.5f };
            byte[] data  = ceras.Save(hero, robustVersion: 1);
            SaveHero clone = ceras.Load<SaveHero>(data, currentRobustVersion: 1);

            Assert.Equal(hero.Name,  clone.Name);
            Assert.Equal(hero.Level, clone.Level);
            Assert.Equal(hero.Hp,    clone.Hp);
        }

        [Fact]
        public void Save_ThenLoad_NullString_RoundTrips()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = null, Level = 5, Hp = 10f };
            byte[] data  = ceras.Save(hero, robustVersion: 1);
            SaveHero clone = ceras.Load<SaveHero>(data, currentRobustVersion: 1);

            Assert.Null(clone.Name);
        }

        [Fact]
        public void Save_ThenLoad_ExcludedField_IsDefault()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "X", Level = 1, Hp = 1f, CachedDps = 999f };
            byte[] data  = ceras.Save(hero, robustVersion: 1);
            SaveHero clone = ceras.Load<SaveHero>(data, currentRobustVersion: 1);

            Assert.Equal(0f, clone.CachedDps);
        }

        [Fact]
        public void Save_ThenLoad_DuplicateStrings_RoundTrip()
        {
            var ceras = CreateSerializer();
            var inv = new RobustInventory { Gold = 100, Tag = "rare", Note = "rare" };
            byte[] data  = ceras.Save(inv, robustVersion: 1);
            RobustInventory cl = ceras.Load<RobustInventory>(data, currentRobustVersion: 1);

            Assert.Equal(inv.Gold, cl.Gold);
            Assert.Equal(inv.Tag, cl.Tag);
            Assert.Equal(inv.Note, cl.Note);

            // Verify they refer to the same string object after load
            Assert.Same(cl.Tag, cl.Note);
        }

        [Fact]
        public void Load_Calculates_Checksum_Properly()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "Hero", Level = 1, Hp = 100f };
            byte[] data = ceras.Save(hero, robustVersion: 1);

            // Data should load fine
            var clone = ceras.Load<SaveHero>(data, 1);
            Assert.NotNull(clone);

            // Corrupt the data (somewhere in the middle of the payload)
            data[data.Length / 2] ^= 0xFF;

            // Load should now throw due to CRC mismatch
            Assert.ThrowsAny<Exception>(() => ceras.Load<SaveHero>(data, 1));
        }

        [Fact]
        public void Load_Throws_On_Magic_Mismatch()
        {
            var ceras = CreateSerializer();
            byte[] data = new byte[CerasRobustSerializer.HeaderSize];
            data[0] = (byte)'X'; // invalid magic

            Assert.ThrowsAny<Exception>(() => ceras.Load<SaveHero>(data, 1));
        }

        [Fact]
        public void Load_Throws_On_Version_Mismatch()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "Hero", Level = 1 };
            byte[] data = ceras.Save(hero, 1);

            // Manually set format version to something impossible
            data[CerasRobustSerializer.OffsetFormatVersion] = 99;

            Assert.ThrowsAny<Exception>(() => ceras.Load<SaveHero>(data, 1));
        }

        [Fact]
        public void Load_Throws_On_Newer_GameVersion()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "Hero", Level = 1 };
            byte[] data = ceras.Save(hero, robustVersion: 5);

            // Trying to load a version 5 save with version 1 library
            Assert.ThrowsAny<Exception>(() => ceras.Load<SaveHero>(data, currentRobustVersion: 1));
        }

        [Fact]
        public void DumpSchema_Returns_Valid_String()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "Hero" };
            ceras.Save(hero, 1);

            string schema = ceras.DumpSchema();
            Assert.Contains("SaveHero", schema);
            Assert.Contains("Name", schema);
            Assert.Contains("Level", schema);
            Assert.Contains("Hp", schema);
        }

        [Fact]
        public void DumpSchema_Static_Works_From_Data()
        {
            var ceras = CreateSerializer();
            var hero = new SaveHero { Name = "Hero" };
            byte[] data = ceras.Save(hero, 1);

            string schema = CerasRobustSerializer.DumpSchema(data);
            Assert.Contains("SaveHero", schema);
            Assert.Contains("Name", schema);
        }

        [Fact]
        public void ReferenceTrace_Works_Through_API()
        {
            var ceras = CreateSerializer();
            CerasRobustSerializer.DebugTraceEnabled = true;

            var hero = new SaveHero { Name = "Hero" };
            byte[] data = ceras.Save(hero, 1);
            ceras.Load<SaveHero>(data, 1);

            CerasRobustSerializer.DebugTraceEnabled = false;

            // This just confirms the methods don't throw; full trace validation is in other tests
            string path = "test_trace.txt";
            CerasRobustSerializer.DumpRefTraceComparison(path);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }
}
