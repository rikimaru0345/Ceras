using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Ceras;
using Ceras.Generated;
using Ceras.Savegame;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// Phase 5: migration engine tests. Each test builds a savegame binary that
    /// represents an "old" schema, then loads it with the current schema to verify
    /// migration behaviour.
    /// </summary>
    public class MigrationTests : TestBase
    {
        static readonly CerasSerializer _ceras;

        static MigrationTests()
        {
            var config = new SerializerConfig();
            GeneratedFormatters.UseFormatters(config);
            CerasSave.ConfigureSavegame(config);
            _ceras = new CerasSerializer(config);
        }

        // ── Schema migration scenario: field removed ──────────────────────────

        [Fact]
        public void Migration_RemovedField_OldDataSkipped()
        {
            var oldHeroFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Hp",     Array.Empty<string>(), WellKnownTypes.Single, true),
                ("Level",  Array.Empty<string>(), WellKnownTypes.Int32,  true),
                ("Name",   Array.Empty<string>(), WellKnownTypes.String, true),
                ("OldBuff",Array.Empty<string>(), WellKnownTypes.Int32,  true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[256];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02; // NewValueSameType
            SerializerBinary.WriteFloat32Fixed(ref dataBuf, ref dataOff, 5.0f);
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 7);
            strings.WriteIndex(ref dataBuf, ref dataOff, "Knight");
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 999);
            byte[] dataBytes = dataBuf[..dataOff];

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[] { OldSaveBuilder.ClassType("Hero", Array.Empty<string>(), oldHeroFields) },
                currentRegisteredTypes: new[] { typeof(Hero) },
                dataBytes: dataBytes,
                strings: strings);

            Hero hero = CerasSave.Load<Hero>(save, currentGameVersion: 1, _ceras);

            Assert.Equal(5.0f,    hero.Hp);
            Assert.Equal(7,       hero.Level);
            Assert.Equal("Knight", hero.Name);
        }

        // ── Schema migration scenario: field added ────────────────────────────

        [Fact]
        public void Migration_AddedField_DefaultsToZero()
        {
            var oldHeroFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Level", Array.Empty<string>(), WellKnownTypes.Int32,  true),
                ("Name",  Array.Empty<string>(), WellKnownTypes.String, true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[256];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02;
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 12);
            strings.WriteIndex(ref dataBuf, ref dataOff, "Aria");
            byte[] dataBytes = dataBuf[..dataOff];

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[] { OldSaveBuilder.ClassType("Hero", Array.Empty<string>(), oldHeroFields) },
                currentRegisteredTypes: new[] { typeof(Hero) },
                dataBytes: dataBytes,
                strings: strings);

            Hero hero = CerasSave.Load<Hero>(save, currentGameVersion: 1, _ceras);

            Assert.Equal(12,    hero.Level);
            Assert.Equal("Aria", hero.Name);
            Assert.Equal(0f,    hero.Hp);
        }

        // ── Schema migration scenario: field renamed via previous names ────────

        [Fact]
        public void Migration_RenamedField_PreviousName_MapsCorrectly()
        {
            var oldHeroFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Hp",  Array.Empty<string>(), WellKnownTypes.Single, true),
                ("Lvl", Array.Empty<string>(), WellKnownTypes.Int32,  true),
                ("Name",Array.Empty<string>(), WellKnownTypes.String, true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[256];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02;
            SerializerBinary.WriteFloat32Fixed(ref dataBuf, ref dataOff, 3.0f);
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 8);
            strings.WriteIndex(ref dataBuf, ref dataOff, "Rex");
            byte[] dataBytes = dataBuf[..dataOff];

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[]
                {
                    // Old field "Lvl" has previousNames=["Level"] so the migration
                    // engine can map it to current Hero.Level
                    OldSaveBuilder.ClassType("Hero", Array.Empty<string>(), oldHeroFields,
                        fieldPreviousNames: new Dictionary<string, string[]>
                        {
                            ["Lvl"] = new[] { "Level" }
                        })
                },
                currentRegisteredTypes: new[] { typeof(Hero) },
                dataBytes: dataBytes,
                strings: strings);

            Hero hero = CerasSave.Load<Hero>(save, currentGameVersion: 1, _ceras);

            Assert.Equal(3.0f,  hero.Hp);
            Assert.Equal(8,     hero.Level);
            Assert.Equal("Rex", hero.Name);
        }

        // ── Numeric coercion: int → long ──────────────────────────────────────

        [Fact]
        public void Migration_NumericCoercion_Int32ToInt64()
        {
            var oldFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Score", Array.Empty<string>(), WellKnownTypes.Int32, true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[64];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02;
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 42);
            byte[] dataBytes = dataBuf[..dataOff];

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[] { OldSaveBuilder.ClassType("HeroLong", Array.Empty<string>(), oldFields) },
                currentRegisteredTypes: new[] { typeof(HeroLong) },
                dataBytes: dataBytes,
                strings: strings);

            HeroLong h = CerasSave.Load<HeroLong>(save, currentGameVersion: 1, _ceras);
            Assert.Equal(42L, h.Score);
        }

        // ── OnAfterDeserialize with MigrationContext ──────────────────────────

        [Fact]
        public void Migration_OnAfterDeserialize_ReceivesMigrationContext()
        {
            var oldFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Level", Array.Empty<string>(), WellKnownTypes.Int32, true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[64];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02;
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 5);
            byte[] dataBytes = dataBuf[..dataOff];

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[] { OldSaveBuilder.ClassType("MigratableHero", Array.Empty<string>(), oldFields) },
                currentRegisteredTypes: new[] { typeof(MigratableHero) },
                dataBytes: dataBytes,
                strings: strings);

            MigratableHero.LastContext = null;
            MigratableHero h = CerasSave.Load<MigratableHero>(save, currentGameVersion: 2, _ceras);

            Assert.Equal(5, h.Level);
            Assert.NotNull(MigratableHero.LastContext);
            Assert.True(MigratableHero.LastContext.WasMigrated);
            Assert.Equal(1u, MigratableHero.LastContext.OldGameVersion);
            Assert.Contains("Hp", MigratableHero.LastContext.AddedFields);
        }

        // ── Null root object ──────────────────────────────────────────────────

        [Fact]
        public void Migration_NullRoot_ReturnsNull()
        {
            var oldHeroFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Level", Array.Empty<string>(), WellKnownTypes.Int32, true),
            };

            var dataBuf = new byte[] { 0x04 }; // Null ref marker

            var strings = new StringInternTable();
            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[] { OldSaveBuilder.ClassType("Hero", Array.Empty<string>(), oldHeroFields) },
                currentRegisteredTypes: new[] { typeof(Hero) },
                dataBytes: dataBuf,
                strings: strings);

            Hero hero = CerasSave.Load<Hero>(save, currentGameVersion: 1, _ceras);
            Assert.Null(hero);
        }
    }

    // ── Test-only model types ─────────────────────────────────────────────────

    [GenerateFormatter("HeroLong", VersionTolerant = true)]
    [MemberConfig(TargetMember.PublicFields)]
    public class HeroLong
    {
        [SerializedAs("Score")]
        public long Score;
    }

    [GenerateFormatter("MigratableHero", VersionTolerant = true)]
    [MemberConfig(TargetMember.PublicFields)]
    public class MigratableHero
    {
        [SerializedAs("Level")]
        public int Level;

        [SerializedAs("Hp")]
        public float Hp;

        [ThreadStatic]
        public static MigrationContext LastContext;

        [OnAfterDeserialize]
        public void AfterDeserialize(MigrationContext ctx)
        {
            LastContext = ctx;
        }
    }

    // ── OldSaveBuilder ────────────────────────────────────────────────────────

    static class OldSaveBuilder
    {
        const int RefBias = 5;

        public static OldTypeSchema ClassType(
            string                                                             stableName,
            string[]                                                           prevTypeNames,
            List<(string name, string[] prevNames, int typeId, bool mono)>     fields,
            Dictionary<string, string[]>                                       fieldPreviousNames = null)
        {
            var fieldSchemas = new OldFieldSchema[fields.Count];
            for (int i = 0; i < fields.Count; i++)
            {
                var (fn, fps, tid, mono) = fields[i];
                string[] effectivePrevs = fps;
                if (fieldPreviousNames != null && fieldPreviousNames.TryGetValue(fn, out var extra))
                {
                    var all = new string[fps.Length + extra.Length];
                    fps.CopyTo(all, 0);
                    extra.CopyTo(all, fps.Length);
                    effectivePrevs = all;
                }
                fieldSchemas[i] = new OldFieldSchema(fn, effectivePrevs, tid, mono);
            }
            return new OldTypeSchema(200, stableName, prevTypeNames ?? Array.Empty<string>(),
                SchemaTypeKind.Class, fieldSchemas, 0);
        }

        public static byte[] Build(
            uint              gameVersion,
            OldTypeSchema[]   types,
            Type[]            currentRegisteredTypes,
            byte[]            dataBytes,
            StringInternTable strings)
        {
            var buf = new byte[4096];
            int offset = 0;

            WriteHeaderPlaceholder(ref buf, ref offset, gameVersion);

            WriteOldSchemaSection(ref buf, ref offset, types);

            long dataSectionOffset = offset;
            SavegameSchemaRegistry.TryGetId(currentRegisteredTypes[0], out int rootTypeId);
            SchemaSection.WriteLeb128(ref buf, ref offset, (uint)rootTypeId);

            int needed = offset + dataBytes.Length;
            if (needed > buf.Length) Array.Resize(ref buf, needed * 2);
            dataBytes.CopyTo(buf.AsSpan(offset));
            offset += dataBytes.Length;

            long stringTableOffset = strings.Flush(ref buf, ref offset);

            PatchHeader(buf, gameVersion, dataSectionOffset, stringTableOffset);

            return buf[..offset];
        }

        static void WriteOldSchemaSection(ref byte[] buf, ref int offset, OldTypeSchema[] types)
        {
            SchemaSection.WriteLeb128(ref buf, ref offset, (uint)types.Length);
            foreach (var t in types)
            {
                SchemaSection.WriteLeb128(ref buf, ref offset, (uint)t.TypeId);
                WriteInlineString(ref buf, ref offset, t.StableName);

                SchemaSection.WriteLeb128(ref buf, ref offset, (uint)t.PreviousNames.Length);
                foreach (var pn in t.PreviousNames)
                    WriteInlineString(ref buf, ref offset, pn);

                EnsureCapacity(ref buf, offset + 1);
                buf[offset++] = (byte)t.Kind;

                // Struct/Class
                SchemaSection.WriteLeb128(ref buf, ref offset, (uint)t.Fields.Length);
                foreach (var f in t.Fields)
                {
                    WriteInlineString(ref buf, ref offset, f.StableName);

                    SchemaSection.WriteLeb128(ref buf, ref offset, (uint)f.PreviousNames.Length);
                    foreach (var pn in f.PreviousNames)
                        WriteInlineString(ref buf, ref offset, pn);

                    SchemaSection.WriteLeb128(ref buf, ref offset, (uint)f.TypeId);
                    EnsureCapacity(ref buf, offset + 1);
                    buf[offset++] = (byte)(f.IsMonomorphic ? 1 : 0);
                }
                SchemaSection.WriteLeb128(ref buf, ref offset, 0u); // fixedByteSize
            }
        }

        static void WriteHeaderPlaceholder(ref byte[] buf, ref int offset, uint gameVersion)
        {
            EnsureCapacity(ref buf, offset + CerasSave.HeaderSize);
            buf[offset++] = (byte)'C';
            buf[offset++] = (byte)'E';
            buf[offset++] = (byte)'R';
            buf[offset++] = (byte)'A';
            buf[offset++] = CerasSave.FormatVersion;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), gameVersion);
            offset += 4;
            offset += 8;  // fingerprint placeholder
            buf[offset++] = 0; // flags
            offset += 8;  // data section placeholder
            offset += 8;  // string table placeholder
            offset += 4;  // checksum placeholder
        }

        static void PatchHeader(byte[] buf, uint gameVersion, long dataSectionOffset, long stringTableOffset)
        {
            // Use a fingerprint that differs from the current one to force slow path
            ulong fakeFp = SavegameSchemaRegistry.ComputeFingerprint() ^ 0xDEAD_BEEF_CAFE_BABEul;
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(CerasSave.OffsetFingerprint), fakeFp);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(CerasSave.OffsetDataSection), dataSectionOffset);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(CerasSave.OffsetStringTable), stringTableOffset);
        }

        static void WriteInlineString(ref byte[] buf, ref int offset, string s)
        {
            if (s == null) s = string.Empty;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
            SchemaSection.WriteLeb128(ref buf, ref offset, (uint)bytes.Length);
            EnsureCapacity(ref buf, offset + bytes.Length);
            bytes.CopyTo(buf.AsSpan(offset));
            offset += bytes.Length;
        }

        static void EnsureCapacity(ref byte[] buf, int required)
        {
            if (required <= buf.Length) return;
            Array.Resize(ref buf, Math.Max(buf.Length * 2, required + 64));
        }
    }
}
