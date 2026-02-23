using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using Xunit;
using Ceras;
using Ceras.Savegame;
using Ceras.Helpers;
using Ceras.Versioning;
using Ceras.Generated;

namespace Ceras.Test.Migration
{
    /// <summary>
    /// Phase 5: migration engine tests. Each test builds a savegame binary that
    /// represents an "old" schema, then loads it with the current schema to verify
    /// migration behaviour.
    /// </summary>
    public class MigrationTests : TestBase
    {
        CerasSerializer CreateSerializer()
        {
            var config = new SerializerConfig();
            GeneratedFormatters.UseFormatters(config);
            CerasSave.ConfigureSavegame(config);
            return new CerasSerializer(config);
        }

        // ── Schema migration scenario: field removed ──────────────────────────

        [Fact]
        public void Migration_RemovedField_OldDataSkipped()
        {
            var ceras = CreateSerializer();
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
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            Hero hero = CerasSave.Load<Hero>(save, currentGameVersion: 1, ceras);

            Assert.Equal(5.0f, hero.Hp);
            Assert.Equal(7, hero.Level);
            Assert.Equal("Knight", hero.Name);
        }

        // ── Schema migration scenario: field added ────────────────────────────

        [Fact]
        public void Migration_AddedField_DefaultsToZero()
        {
            var ceras = CreateSerializer();
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
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            Hero hero = CerasSave.Load<Hero>(save, currentGameVersion: 1, ceras);

            Assert.Equal(12, hero.Level);
            Assert.Equal("Aria", hero.Name);
            Assert.Equal(0f, hero.Hp);
        }

        // ── Schema migration scenario: field renamed via previous names ────────

        [Fact]
        public void Migration_RenamedField_PreviousName_MapsCorrectly()
        {
            var ceras = CreateSerializer();
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
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            Hero hero = CerasSave.Load<Hero>(save, currentGameVersion: 1, ceras);

            Assert.Equal(3.0f, hero.Hp);
            Assert.Equal(8, hero.Level);
            Assert.Equal("Rex", hero.Name);
        }

        // ── Numeric coercion: int → long ──────────────────────────────────────

        [Fact]
        public void Migration_NumericCoercion_Int32ToInt64()
        {
            var ceras = CreateSerializer();
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
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            HeroLong h = CerasSave.Load<HeroLong>(save, currentGameVersion: 1, ceras);
            Assert.Equal(42L, h.Score);
        }

        // ── OnAfterDeserialize with MigrationContext ──────────────────────────

        [Fact]
        public void Migration_OnAfterDeserialize_ReceivesMigrationContext()
        {
            var ceras = CreateSerializer();
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
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            MigratableHero.LastContext = null;
            MigratableHero h = CerasSave.Load<MigratableHero>(save, currentGameVersion: 2, ceras);

            Assert.Equal(5, h.Level);
            Assert.NotNull(MigratableHero.LastContext);
            Assert.True(MigratableHero.LastContext.WasMigrated);
            Assert.Equal(1u, MigratableHero.LastContext.OldGameVersion);
            Assert.Contains("Hp", MigratableHero.LastContext.AddedFields);
        }

        // ── Renamed type: [PreviousTypeName] resolves old stable name ────────
        // Save was written with type stableName "HeroOldName". The current type
        // RenamedHero carries [PreviousTypeName("HeroOldName")] so the registry
        // maps "HeroOldName" → typeof(RenamedHero).

        [Fact]
        public void Migration_RenamedType_PreviousTypeName_Resolves()
        {
            var ceras = CreateSerializer();
            var oldFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Level", Array.Empty<string>(), WellKnownTypes.Int32, true),
                ("Tag",   Array.Empty<string>(), WellKnownTypes.String, true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[256];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02; // NewValueSameType
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 9);
            strings.WriteIndex(ref dataBuf, ref dataOff, "legacy");
            byte[] dataBytes = dataBuf[..dataOff];

            // The old schema uses the OLD stable name; the current type has
            // PreviousTypeName pointing back to that name.
            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[]
                {
                    OldSaveBuilder.ClassType("HeroOldName", Array.Empty<string>(), oldFields)
                },
                currentRegisteredTypes: new[] { typeof(RenamedHero) },
                dataBytes: dataBytes,
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            RenamedHero hero = CerasSave.Load<RenamedHero>(save, currentGameVersion: 1, ceras);

            Assert.Equal(9, hero.Level);
            Assert.Equal("legacy", hero.Tag);
        }

        // ── Deleted-type field: schema traversal skips without Type.GetType ──
        // Old Hero had a field "Buff" of type GhostBuff (a struct that no longer
        // exists in the current codebase). The engine must skip GhostBuff's bytes
        // using only the old schema — no reflection on any current CLR type.

        [Fact]
        public void Migration_DeletedFieldType_SkippedViaOldSchema()
        {
            var ceras = CreateSerializer();
            // Old schema for the deleted nested struct
            var ghostFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Power", Array.Empty<string>(), WellKnownTypes.Int32, true),
            };

            // Old Hero: fields [Level:int, Buff:GhostBuff(201), Name:string]
            var oldHeroFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Level", Array.Empty<string>(), WellKnownTypes.Int32,   true),
                ("Buff",  Array.Empty<string>(), 201 /* GhostBuff id */, true),
                ("Name",  Array.Empty<string>(), WellKnownTypes.String,  true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[256];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02; // NewValueSameType for Hero
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 3);       // Level
                                                                            // GhostBuff is a struct — no ref marker, just inline fields
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 99);      // GhostBuff.Power
            strings.WriteIndex(ref dataBuf, ref dataOff, "Ghost");          // Name
            byte[] dataBytes = dataBuf[..dataOff];

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[]
                {
                    OldSaveBuilder.ClassType("Hero",      Array.Empty<string>(), oldHeroFields, typeId: 200),
                    OldSaveBuilder.StructType("GhostBuff",Array.Empty<string>(), ghostFields,   typeId: 201),
                },
                currentRegisteredTypes: new[] { typeof(Hero) },
                dataBytes: dataBytes,
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            Hero hero = CerasSave.Load<Hero>(save, currentGameVersion: 1, ceras);

            Assert.Equal(3, hero.Level);
            Assert.Equal("Ghost", hero.Name);
            // Hp is not in old schema → default
            Assert.Equal(0f, hero.Hp);
        }

        // ── Null root object ──────────────────────────────────────────────────

        [Fact]
        public void Migration_NullRoot_ReturnsNull()
        {
            var ceras = CreateSerializer();
            var oldHeroFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Level", Array.Empty<string>(), WellKnownTypes.Int32, true),
            };

            var dataBuf = new byte[] { 0x00 }; // Null ref marker (fixed to 0x00 in SAVEMAKE_FORMAT.md review)

            var strings = new StringInternTable();
            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[] { OldSaveBuilder.ClassType("Hero", Array.Empty<string>(), oldHeroFields) },
                currentRegisteredTypes: new[] { typeof(Hero) },
                dataBytes: dataBuf,
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            Hero hero = CerasSave.Load<Hero>(save, currentGameVersion: 1, ceras);
            Assert.Null(hero);
        }

        // ── Enum migration ────────────────────────────────────────────────────

        [Fact]
        public void Migration_Enum_Basic()
        {
            var ceras = CreateSerializer();
            var enumVals = new List<(string name, long value)>
            {
                ("Off", 0),
                ("On", 1),
            };

            // Register StateEnum manually
            var stateEnumSchema = new SchemaTypeInfo(
                stableName: "StateEnum",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Enum,
                fields: Array.Empty<SchemaFieldInfo>(),
                fixedByteSize: 4,
                enumValues: new[]
                {
                    new SchemaEnumValueInfo("Off", 0),
                    new SchemaEnumValueInfo("On", 1),
                },
                underlyingTypeName: "System.Int32");
            ceras.Config.Savegame.SchemaRegistry.Register(typeof(StateEnum), stateEnumSchema);

            // Register HeroWithEnum manually since it's defined in the test
            var heroSchema = new SchemaTypeInfo(
                stableName: "HeroWithEnum",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Class,
                fields: new[] { new SchemaFieldInfo("State", Array.Empty<string>(), "Ceras.Test.StateEnum", true) },
                fixedByteSize: 0);
            ceras.Config.Savegame.SchemaRegistry.Register(typeof(HeroWithEnum), heroSchema);

            var oldFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("State", Array.Empty<string>(), 201 /* StateEnum id */, true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[64];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02; // NewValueSameType
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 1); // "On"
            byte[] dataBytes = dataBuf[..dataOff];

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[]
                {
                    OldSaveBuilder.ClassType("HeroWithEnum", Array.Empty<string>(), oldFields, typeId: 200),
                    OldSaveBuilder.EnumType("StateEnum", Array.Empty<string>(), enumVals, WellKnownTypes.Int32, typeId: 201),
                },
                currentRegisteredTypes: new[] { typeof(HeroWithEnum) },
                dataBytes: dataBytes,
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            HeroWithEnum h = CerasSave.Load<HeroWithEnum>(save, currentGameVersion: 1, ceras);
            Assert.Equal(StateEnum.On, h.State);
        }

        [Fact]
        public void Migration_Enum_DeletedValue()
        {
            var ceras = CreateSerializer();
            var enumVals = new List<(string name, long value)>
            {
                ("Off", 0),
                ("On",  1),
                ("Maybe", 2), // This value exists in old schema but not in current StateEnum
            };

            var oldFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("State", Array.Empty<string>(), 201 /* StateEnum id */, true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[64];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02; // NewValueSameType
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 2); // "Maybe" (2)
            byte[] dataBytes = dataBuf[..dataOff];

            // Manual registration of current types
            var stateEnumSchema = new SchemaTypeInfo(
                stableName: "StateEnum",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Enum,
                fields: Array.Empty<SchemaFieldInfo>(),
                fixedByteSize: 4,
                enumValues: new[]
                {
                    new SchemaEnumValueInfo("Off", 0),
                    new SchemaEnumValueInfo("On", 1),
                },
                underlyingTypeName: "System.Int32");
            ceras.Config.Savegame.SchemaRegistry.Register(typeof(StateEnum), stateEnumSchema);

            var heroSchema = new SchemaTypeInfo(
                stableName: "HeroWithEnum",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Class,
                fields: new[] { new SchemaFieldInfo("State", Array.Empty<string>(), "Ceras.Test.StateEnum", true) },
                fixedByteSize: 0);
            ceras.Config.Savegame.SchemaRegistry.Register(typeof(HeroWithEnum), heroSchema);

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[]
                {
                    OldSaveBuilder.ClassType("HeroWithEnum", Array.Empty<string>(), oldFields, typeId: 200),
                    OldSaveBuilder.EnumType("StateEnum", Array.Empty<string>(), enumVals, WellKnownTypes.Int32, typeId: 201),
                },
                currentRegisteredTypes: new[] { typeof(HeroWithEnum) },
                dataBytes: dataBytes,
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            // In .NET, you can set an out-of-range value to an enum field.
            // But for the Savegame format, we now validate it against the current enum names.
            HeroWithEnum h = CerasSave.Load<HeroWithEnum>(save, currentGameVersion: 1, ceras);
            Assert.Equal((StateEnum)0, h.State); // Should be Off (0) because "Maybe" was deleted
        }

        [Fact]
        public void Migration_Enum_ReorderedValues_MapsByName()
        {
            var ceras = CreateSerializer();
            var enumVals = new List<(string name, long value)>
            {
                ("Off", 1),
                ("On",  0), // Reordered: old Off=1, On=0
            };

            var oldFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("State", Array.Empty<string>(), 201, true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[64];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02;
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 0); // "On" in old schema
            byte[] dataBytes = dataBuf[..dataOff];

            // Manual registration of current types (Off=0, On=1)
            var stateEnumSchema = new SchemaTypeInfo(
                "StateEnum", Array.Empty<string>(), SchemaTypeKind.Enum, Array.Empty<SchemaFieldInfo>(), 4,
                new[] { new SchemaEnumValueInfo("Off", 0), new SchemaEnumValueInfo("On", 1) }, "System.Int32");
            ceras.Config.Savegame.SchemaRegistry.Register(typeof(StateEnum), stateEnumSchema);

            var heroSchema = new SchemaTypeInfo(
                "HeroWithEnum", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("State", Array.Empty<string>(), "Ceras.Test.StateEnum", true) }, 0);
            ceras.Config.Savegame.SchemaRegistry.Register(typeof(HeroWithEnum), heroSchema);

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[]
                {
                    OldSaveBuilder.ClassType("HeroWithEnum", Array.Empty<string>(), oldFields, typeId: 200),
                    OldSaveBuilder.EnumType("StateEnum", Array.Empty<string>(), enumVals, WellKnownTypes.Int32, typeId: 201),
                },
                currentRegisteredTypes: new[] { typeof(HeroWithEnum) },
                dataBytes: dataBytes,
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            HeroWithEnum h = CerasSave.Load<HeroWithEnum>(save, currentGameVersion: 1, ceras);
            // Even though old value for "On" was 0, and current is 1, it should map by name to 1
            Assert.Equal(StateEnum.On, h.State);
            Assert.Equal(1, (int)h.State);
        }

        [Fact]
        public void Migration_Collection_Basic()
        {
            var ceras = CreateSerializer();
            var oldFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Scores", Array.Empty<string>(), 201 /* List<int> id */, true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[256];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02; // NewValueSameType for HeroWithList
            SerializerBinary.WriteUInt32(ref dataBuf, ref dataOff, 3); // Count
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 10);
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 20);
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 30);
            byte[] dataBytes = dataBuf[..dataOff];

            // Register HeroWithList
            var heroSchema = new SchemaTypeInfo(
                stableName: "HeroWithList",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Class,
                fields: new[] { new SchemaFieldInfo("Scores", Array.Empty<string>(), "System.Collections.Generic.List`1[[System.Int32]]", true) },
                fixedByteSize: 0);
            ceras.Config.Savegame.SchemaRegistry.Register(typeof(HeroWithList), heroSchema);

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[]
				{
					OldSaveBuilder.ClassType("HeroWithList", Array.Empty<string>(), oldFields, typeId: 200),
                    OldSaveBuilder.GenericType("List<int>", Array.Empty<string>(), WellKnownTypes.List, new[] { WellKnownTypes.Int32 }, typeId: 201)
                },
                currentRegisteredTypes: new[] { typeof(HeroWithList) },
                dataBytes: dataBytes,
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            HeroWithList h = CerasSave.Load<HeroWithList>(save, currentGameVersion: 1, ceras);
            Assert.NotNull(h.Scores);
            Assert.Equal(3, h.Scores.Count);
            Assert.Equal(10, h.Scores[0]);
            Assert.Equal(20, h.Scores[1]);
            Assert.Equal(30, h.Scores[2]);
        }

        [Fact]
        public void Migration_DeletedClassField_Skipped()
        {
            var ceras = CreateSerializer();
            // Old schema for deleted class
            var deletedClassFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Id", Array.Empty<string>(), WellKnownTypes.Int32, true),
            };

            // Old Hero: fields [Level:int, Extra:DeletedClass(201), Name:string]
            var oldHeroFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Level", Array.Empty<string>(), WellKnownTypes.Int32,   true),
                ("Extra", Array.Empty<string>(), 201 /* DeletedClass id */, true),
                ("Name",  Array.Empty<string>(), WellKnownTypes.String,  true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[256];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02; // NewValueSameType for Hero
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 5); // Level
                                                                      // Extra: NewValueSameType for DeletedClass
            dataBuf[dataOff++] = 0x02; // marker
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 123); // DeletedClass.Id
            strings.WriteIndex(ref dataBuf, ref dataOff, "Test"); // Name
            byte[] dataBytes = dataBuf[..dataOff];

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[]
                {
                    OldSaveBuilder.ClassType("Hero",        Array.Empty<string>(), oldHeroFields,      typeId: 200),
                    OldSaveBuilder.ClassType("DeletedClass",Array.Empty<string>(), deletedClassFields, typeId: 201),
                },
                currentRegisteredTypes: new[] { typeof(Hero) },
                dataBytes: dataBytes,
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            Hero hero = CerasSave.Load<Hero>(save, currentGameVersion: 1, ceras);

            Assert.Equal(5, hero.Level);
            Assert.Equal("Test", hero.Name);
        }

        [Fact]
        public void Migration_Dictionary_Basic()
        {
            var ceras = CreateSerializer();
            var oldFields = new List<(string name, string[] prevNames, int typeId, bool mono)>
            {
                ("Data", Array.Empty<string>(), 201 /* Dict id */, true),
            };

            var strings = new StringInternTable();
            var dataBuf = new byte[256];
            int dataOff = 0;
            dataBuf[dataOff++] = 0x02; // HeroWithDict NewValueSameType
            SerializerBinary.WriteUInt32(ref dataBuf, ref dataOff, 2); // Count
                                                                       // Entry 1
            strings.WriteIndex(ref dataBuf, ref dataOff, "Key1");
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 100);
            // Entry 2
            strings.WriteIndex(ref dataBuf, ref dataOff, "Key2");
            SerializerBinary.WriteInt32(ref dataBuf, ref dataOff, 200);
            byte[] dataBytes = dataBuf[..dataOff];

            var heroSchema = new SchemaTypeInfo(
                "HeroWithDict", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("Data", Array.Empty<string>(), "System.Collections.Generic.Dictionary`2[[System.String],[System.Int32]]", true) }, 0);
            ceras.Config.Savegame.SchemaRegistry.Register(typeof(HeroWithDict), heroSchema);

            byte[] save = OldSaveBuilder.Build(
                gameVersion: 1,
                types: new[]
                {
                    OldSaveBuilder.ClassType("HeroWithDict", Array.Empty<string>(), oldFields, typeId: 200),
                    OldSaveBuilder.GenericType("Dict", Array.Empty<string>(), WellKnownTypes.Dictionary, new[] { WellKnownTypes.String, WellKnownTypes.Int32 }, typeId: 201)
                },
                currentRegisteredTypes: new[] { typeof(HeroWithDict) },
                dataBytes: dataBytes,
                strings: strings,
                registry: ceras.Config.Savegame.SchemaRegistry);

            HeroWithDict h = CerasSave.Load<HeroWithDict>(save, currentGameVersion: 1, ceras);
            Assert.NotNull(h.Data);
            Assert.Equal(2, h.Data.Count);
            Assert.Equal(100, h.Data["Key1"]);
            Assert.Equal(200, h.Data["Key2"]);
        }
    }

    // ── Test-only model types ─────────────────────────────────────────────────
    [GenerateFormatter("HeroWithDict", VersionTolerant = true)]
    public class HeroWithDict
    {
        public Dictionary<string, int> Data;
    }


    [GenerateFormatter("HeroWithList", VersionTolerant = true)]
    [MemberConfig(TargetMember.PublicFields)]
    public class HeroWithList
    {
        public List<int> Scores;
    }

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

    [GenerateFormatter("StateEnum", VersionTolerant = true)]
    public enum StateEnum { Off, On }

    [GenerateFormatter("HeroWithEnum", VersionTolerant = true)]
    [MemberConfig(TargetMember.PublicFields)]
    public class HeroWithEnum
    {
        [SerializedAs("State")]
        public StateEnum State;
    }

    [GenerateFormatter("RenamedHero", VersionTolerant = true)]
    [PreviousTypeName("HeroOldName")]
    [MemberConfig(TargetMember.PublicFields)]
    public class RenamedHero
    {
        [SerializedAs("Level")]
        public int Level;

        [SerializedAs("Tag")]
        public string Tag;
    }

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
            string stableName,
            string[] prevTypeNames,
            List<(string name, string[] prevNames, int typeId, bool mono)> fields,
            Dictionary<string, string[]> fieldPreviousNames = null,
            int typeId = 200)
            => BuildUserType(stableName, prevTypeNames, fields, fieldPreviousNames, typeId, SchemaTypeKind.Class);

        public static OldTypeSchema StructType(
            string stableName,
            string[] prevTypeNames,
            List<(string name, string[] prevNames, int typeId, bool mono)> fields,
            int typeId = 200)
            => BuildUserType(stableName, prevTypeNames, fields, null, typeId, SchemaTypeKind.Struct);

        public static OldTypeSchema EnumType(
            string stableName,
            string[] prevTypeNames,
            List<(string, long)> values,
            int underlyingTypeId,
            int typeId = 200)
        {
            var evs = new OldEnumValue[values.Count];
            for (int i = 0; i < values.Count; i++)
                evs[i] = new OldEnumValue(values[i].Item1, values[i].Item2);
            return new OldTypeSchema(typeId, stableName, prevTypeNames ?? Array.Empty<string>(),
                underlyingTypeId, evs);
        }

        public static OldTypeSchema GenericType(
            string stableName,
            string[] prevTypeNames,
            int defTypeId,
            int[] argTypeIds,
            int typeId = 200)
        {
            return new OldTypeSchema(typeId, stableName, prevTypeNames ?? Array.Empty<string>(),
                defTypeId, argTypeIds);
        }

        static OldTypeSchema BuildUserType(
            string stableName,
            string[] prevTypeNames,
            List<(string name, string[] prevNames, int typeId, bool mono)> fields,
            Dictionary<string, string[]> fieldPreviousNames,
            int typeId,
            SchemaTypeKind kind)
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
            return new OldTypeSchema(typeId, stableName, prevTypeNames ?? Array.Empty<string>(),
                kind, fieldSchemas, 0);
        }

        public static byte[] Build(
            uint gameVersion,
            OldTypeSchema[] types,
            Type[] currentRegisteredTypes,
            byte[] dataBytes,
            StringInternTable strings,
            SchemaRegistry registry)
        {
            var buf = new byte[4096];
            int offset = 0;

            WriteHeaderPlaceholder(ref buf, ref offset, gameVersion);

            WriteOldSchemaSection(ref buf, ref offset, types);

            long dataSectionOffset = offset;
            registry.TryGetId(currentRegisteredTypes[0], out int rootTypeId);
            SchemaSection.WriteLeb128(ref buf, ref offset, (uint)rootTypeId);

            int needed = offset + dataBytes.Length;
            if (needed > buf.Length) Array.Resize(ref buf, needed * 2);
            dataBytes.CopyTo(buf.AsSpan(offset));
            offset += dataBytes.Length;

            long stringTableOffset = strings.Flush(ref buf, ref offset);

            PatchHeader(buf, offset, gameVersion, dataSectionOffset, stringTableOffset, registry);

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

                if (t.Kind == SchemaTypeKind.Enum)
                {
                    SchemaSection.WriteLeb128(ref buf, ref offset, (uint)t.DefinitionTypeId);
                    SchemaSection.WriteLeb128(ref buf, ref offset, (uint)t.EnumValues.Length);
                    foreach (var ev in t.EnumValues)
                    {
                        WriteInlineString(ref buf, ref offset, ev.Name);
                        EnsureCapacity(ref buf, offset + 8);
                        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(offset), ev.Value);
                        offset += 8;
                    }
                }
                else if (t.Kind == SchemaTypeKind.GenericInstantiation)
                {
                    SchemaSection.WriteLeb128(ref buf, ref offset, (uint)t.DefinitionTypeId);
                    SchemaSection.WriteLeb128(ref buf, ref offset, (uint)t.ArgTypeIds.Length);
                    foreach (var argId in t.ArgTypeIds)
                        SchemaSection.WriteLeb128(ref buf, ref offset, (uint)argId);
                }
                else
                {
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
            buf[offset++] = 1; // flags (has checksum)
            offset += 8;  // data section placeholder
            offset += 8;  // string table placeholder
            offset += 4;  // checksum placeholder
        }

        static void PatchHeader(byte[] buf, int totalLength, uint gameVersion, long dataSectionOffset, long stringTableOffset, SchemaRegistry registry)
        {
            // Use a fingerprint that differs from the current one to force slow path
            ulong fakeFp = registry.ComputeFingerprint() ^ 0xDEAD_BEEF_CAFE_BABEul;
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(CerasSave.OffsetFingerprint), fakeFp);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(CerasSave.OffsetDataSection), dataSectionOffset);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(CerasSave.OffsetStringTable), stringTableOffset);

            // CRC32
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(CerasSave.OffsetChecksum), 0);
            uint crc = Helpers.HashHelpers.CalculateCrc32(buf.AsSpan(0, totalLength));
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(CerasSave.OffsetChecksum), crc);
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
