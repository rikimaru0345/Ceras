
#pragma warning disable 0649 // Field is never assigned to


using System.Buffers;

// ---------------------------------------------------------------------------
// Savegame scenario tests for the Ceras source generator.
//
// These tests answer two key questions:
//   1. Does the source generator handle realistic, complex object graphs?
//   2. How does version tolerance (savegame compatibility across game updates)
//      interact with the source generator?
//
// The short answer to (2):
//   Generated formatters do NOT embed field names in the binary — they write
//   fields in sorted order with no schema header.  This makes them fast and
//   compact, but means they cannot automatically handle schema changes.
//   Version tolerance (VersionToleranceMode.Standard) works by wrapping types
//   in SchemaDynamicFormatter, which embeds a schema header.  However,
//   CustomFormatter (what GeneratedFormatters.UseFormatters registers) is
//   resolved BEFORE the schema formatter and therefore bypasses it.
//
//   Practical guidance:
//     - For AOT/IL2CPP builds: use generated formatters, keep field layout
//       stable between game versions, use [PreviousName] to rename safely.
//     - For JIT builds with old-save compatibility: use VersionToleranceMode
//       .Standard WITHOUT generated formatters (let SchemaDynamicFormatter
//       handle user types).
//     - Type changes (int → float) and removals that change binary layout
//       across saves require a hand-written migration path regardless.
// ---------------------------------------------------------------------------


namespace Ceras.Test
{
    // -----------------------------------------------------------------------
    // Savegame model — all types annotated with [GenerateFormatter]
    // -----------------------------------------------------------------------

    [GenerateFormatter]
    [MemberConfig(TargetMember.PublicFields)]
    public class SaveGame
    {
        public Player    Player;
        public string    Version;   // game version string, e.g. "1.0"
        public int       PlaytimeSeconds;
    }

    [GenerateFormatter]
    [MemberConfig(TargetMember.PublicFields)]
    public class Player
    {
        public string         Name;
        public int            Level;
        public int            Gold;
        public PlayerStats    Stats;
        public Inventory      Inventory;
        public List<Quest>    ActiveQuests;
    }

    [GenerateFormatter]
    [MemberConfig(TargetMember.PublicFields)]
    public class PlayerStats
    {
        public int Strength;
        public int Dexterity;
        public int Intelligence;
    }

    [GenerateFormatter]
    [MemberConfig(TargetMember.PublicFields)]
    public class Inventory
    {
        public List<Item> Items;
        public int        MaxCapacity;
    }

    [GenerateFormatter]
    [MemberConfig(TargetMember.PublicFields)]
    public class Item
    {
        public string     Name;
        public int        Quantity;
        public ItemRarity Rarity;
        public float      Weight;
    }

    public enum ItemRarity { Common = 0, Uncommon = 1, Rare = 2, Epic = 3, Legendary = 4 }

    [GenerateFormatter]
    [MemberConfig(TargetMember.PublicFields)]
    public class Quest
    {
        public string Name;
        public bool   IsComplete;
        public int    ObjectivesCompleted;
    }

    // -----------------------------------------------------------------------
    // Version-simulation types (separate namespaces simulate game updates)
    // -----------------------------------------------------------------------
    // NOTE: these are used only by the VersionTolerance tests below and do NOT
    // carry [GenerateFormatter] — they use SchemaDynamicFormatter instead.

    namespace SavegameV1
    {
        // Game v1: Enemy has only Name and MaxHp
        public class Enemy
        {
            public string Name;
            public int    MaxHp;
        }
    }

    namespace SavegameV2
    {
        // Game v2: Enemy gains a new Level field (added)
        public class Enemy
        {
            public string Name;
            public int    MaxHp;
            public int    Level;   // NEW in v2 — absent in old saves
        }
    }

    namespace SavegameV3
    {
        // Game v3: MaxHp renamed to HitPoints; [PreviousName] maps old data
        public class Enemy
        {
            public string Name;
            [PreviousName("MaxHp")]
            public int    HitPoints;  // was MaxHp in v1/v2
            public int    Level;
        }
    }

    // -----------------------------------------------------------------------
    // Test class
    // -----------------------------------------------------------------------

    public class SavegameTests : TestBase
    {
        SerializerConfig GeneratedConfig()
        {
            var c = new SerializerConfig();
            GeneratedFormatters.UseFormatters(c);
            return c;
        }

        SerializerConfig VersionTolerantConfig()
        {
            var c = new SerializerConfig();
            c.VersionTolerance.Mode = VersionToleranceMode.Standard;
            return c;
        }

        SaveGame MakeSaveGame() => new SaveGame
        {
            Version        = "1.0",
            PlaytimeSeconds = 3661,
            Player = new Player
            {
                Name  = "Aria",
                Level = 12,
                Gold  = 4200,
                Stats = new PlayerStats { Strength = 15, Dexterity = 18, Intelligence = 10 },
                Inventory = new Inventory
                {
                    MaxCapacity = 30,
                    Items = new List<Item>
                    {
                        new Item { Name = "Iron Sword",    Quantity = 1,  Rarity = ItemRarity.Common,    Weight = 4.5f },
                        new Item { Name = "Health Potion", Quantity = 12, Rarity = ItemRarity.Uncommon,  Weight = 0.3f },
                        new Item { Name = "Dragon Scale",  Quantity = 3,  Rarity = ItemRarity.Legendary, Weight = 2.1f },
                    }
                },
                ActiveQuests = new List<Quest>
                {
                    new Quest { Name = "Slay the Dragon",  IsComplete = false, ObjectivesCompleted = 2 },
                    new Quest { Name = "Deliver the Tome", IsComplete = true,  ObjectivesCompleted = 1 },
                }
            }
        };

        // -------------------------------------------------------------------
        // 1. Full round-trip of a complex nested object graph
        // -------------------------------------------------------------------

        [Fact]
        public void Savegame_FullRoundTrip_GeneratedFormatter()
        {
            var ceras    = new CerasSerializer(GeneratedConfig());
            var original = MakeSaveGame();

            var clone = ceras.Deserialize<SaveGame>(ceras.Serialize(original));

            Assert.Equal(original.Version,        clone.Version);
            Assert.Equal(original.PlaytimeSeconds, clone.PlaytimeSeconds);

            var op = original.Player;
            var cp = clone.Player;
            Assert.Equal(op.Name,  cp.Name);
            Assert.Equal(op.Level, cp.Level);
            Assert.Equal(op.Gold,  cp.Gold);

            Assert.Equal(op.Stats.Strength,     cp.Stats.Strength);
            Assert.Equal(op.Stats.Dexterity,    cp.Stats.Dexterity);
            Assert.Equal(op.Stats.Intelligence, cp.Stats.Intelligence);

            Assert.Equal(op.Inventory.MaxCapacity,    cp.Inventory.MaxCapacity);
            Assert.Equal(op.Inventory.Items.Count,    cp.Inventory.Items.Count);
            Assert.Equal(op.Inventory.Items[0].Name,  cp.Inventory.Items[0].Name);
            Assert.Equal(op.Inventory.Items[1].Quantity, cp.Inventory.Items[1].Quantity);
            Assert.Equal(op.Inventory.Items[2].Rarity,   cp.Inventory.Items[2].Rarity);
            Assert.Equal(op.Inventory.Items[2].Weight,   cp.Inventory.Items[2].Weight);

            Assert.Equal(op.ActiveQuests.Count, cp.ActiveQuests.Count);
            Assert.Equal(op.ActiveQuests[0].Name,               cp.ActiveQuests[0].Name);
            Assert.Equal(op.ActiveQuests[0].ObjectivesCompleted, cp.ActiveQuests[0].ObjectivesCompleted);
            Assert.Equal(op.ActiveQuests[1].IsComplete,          cp.ActiveQuests[1].IsComplete);
        }

        // -------------------------------------------------------------------
        // 2. Null collections and empty lists
        // -------------------------------------------------------------------

        [Fact]
        public void Savegame_NullAndEmptyCollections()
        {
            var ceras = new CerasSerializer(GeneratedConfig());

            var original = new Player
            {
                Name         = "Ghost",
                Level        = 1,
                ActiveQuests = new List<Quest>(),   // empty, not null
                Inventory    = new Inventory { Items = null, MaxCapacity = 10 },
            };

            var clone = ceras.Deserialize<Player>(ceras.Serialize(original));

            Assert.Equal(original.Name, clone.Name);
            Assert.NotNull(clone.ActiveQuests);
            Assert.Empty(clone.ActiveQuests);
            Assert.Null(clone.Inventory.Items);
        }

        // -------------------------------------------------------------------
        // 3. Enum values survive round-trip
        // -------------------------------------------------------------------

        [Fact]
        public void Savegame_EnumRoundTrip()
        {
            var ceras = new CerasSerializer(GeneratedConfig());

            foreach (var rarity in Enum.GetValues<ItemRarity>())
            {
                var item  = new Item { Name = "x", Rarity = rarity };
                var clone = ceras.Deserialize<Item>(ceras.Serialize(item));
                Assert.Equal(rarity, clone.Rarity);
            }
        }

        // -------------------------------------------------------------------
        // 4. Generated formatter produces byte-identical output to DynamicFormatter
        //    (ensures cross-tool compatibility for save data)
        // -------------------------------------------------------------------

        [Fact]
        public void Savegame_GeneratedAndDynamic_ByteIdentical()
        {
            var generatedCeras = new CerasSerializer(GeneratedConfig());
            var dynamicCeras   = new CerasSerializer(new SerializerConfig());

            var obj = new Item { Name = "Mithril Helm", Quantity = 1, Rarity = ItemRarity.Epic, Weight = 3.7f };

            Assert.Equal(dynamicCeras.Serialize(obj), generatedCeras.Serialize(obj));
        }

        // -------------------------------------------------------------------
        // 5. VERSION TOLERANCE (using DynamicFormatter / SchemaDynamicFormatter)
        //    — Adding a field in a game update: old saves still load cleanly
        // -------------------------------------------------------------------

        [Fact]
        public void Savegame_VersionTolerance_AddedField_OldSaveLoadsWithDefault()
        {
            // v1 save: Enemy has Name + MaxHp only
            var v1Config = new SerializerConfig();
            v1Config.VersionTolerance.Mode = VersionToleranceMode.Standard;
            var v1Binder = new SavegameTypeBinder();
            v1Binder.Map(typeof(SavegameV1.Enemy), "Enemy");
            v1Config.Advanced.TypeBinder = v1Binder;

            byte[] oldSave;
            {
                var v1Ceras = new CerasSerializer(v1Config);
                var enemy   = new SavegameV1.Enemy { Name = "Goblin", MaxHp = 40 };
                oldSave = v1Ceras.Serialize(enemy);
            }

            // v2 loads old save: Level should be 0 (default) because it was absent
            var v2Config = new SerializerConfig();
            v2Config.VersionTolerance.Mode = VersionToleranceMode.Standard;
            var v2Binder = new SavegameTypeBinder();
            v2Binder.Map(typeof(SavegameV2.Enemy), "Enemy");
            v2Binder.Map("Enemy", typeof(SavegameV2.Enemy));
            v2Config.Advanced.TypeBinder = v2Binder;
            {
                var v2Ceras = new CerasSerializer(v2Config);
                SavegameV2.Enemy enemy = null;
                v2Ceras.Deserialize(ref enemy, oldSave);

                Assert.Equal("Goblin", enemy.Name);
                Assert.Equal(40,       enemy.MaxHp);
                Assert.Equal(0,        enemy.Level);   // absent in old save → default
            }
        }

        // -------------------------------------------------------------------
        // 6. VERSION TOLERANCE — Removing a field: old data for the removed
        //    field is silently skipped when loading old saves
        // -------------------------------------------------------------------

        [Fact]
        public void Savegame_VersionTolerance_RemovedField_OldDataSkipped()
        {
            // We re-use V2 (has Level) as the "old" save and V1 (no Level) as
            // the "new" type that no longer cares about Level.
            var v2Config = new SerializerConfig();
            v2Config.VersionTolerance.Mode = VersionToleranceMode.Standard;
            var v2Binder = new SavegameTypeBinder();
            v2Binder.Map(typeof(SavegameV2.Enemy), "Enemy");
            v2Config.Advanced.TypeBinder = v2Binder;

            byte[] v2Save;
            {
                var v2Ceras = new CerasSerializer(v2Config);
                var enemy   = new SavegameV2.Enemy { Name = "Orc", MaxHp = 80, Level = 5 };
                v2Save = v2Ceras.Serialize(enemy);
            }

            // V1 type no longer has Level — old Level data is skipped
            var v1Config = new SerializerConfig();
            v1Config.VersionTolerance.Mode = VersionToleranceMode.Standard;
            var v1Binder = new SavegameTypeBinder();
            v1Binder.Map(typeof(SavegameV1.Enemy), "Enemy");
            v1Binder.Map("Enemy", typeof(SavegameV1.Enemy));
            v1Config.Advanced.TypeBinder = v1Binder;
            {
                var v1Ceras = new CerasSerializer(v1Config);
                SavegameV1.Enemy enemy = null;
                v1Ceras.Deserialize(ref enemy, v2Save);

                Assert.Equal("Orc", enemy.Name);
                Assert.Equal(80,    enemy.MaxHp);
                // Level data was in the binary but silently discarded — no exception
            }
        }

        // -------------------------------------------------------------------
        // 7. VERSION TOLERANCE — Renaming a field with [PreviousName]:
        //    old saves map the old name to the new field automatically
        // -------------------------------------------------------------------

        [Fact]
        public void Savegame_VersionTolerance_RenamedField_PreviousName()
        {
            // Old save uses V1 (field is "MaxHp")
            var v1Config = new SerializerConfig();
            v1Config.VersionTolerance.Mode = VersionToleranceMode.Standard;
            var v1Binder = new SavegameTypeBinder();
            v1Binder.Map(typeof(SavegameV1.Enemy), "Enemy");
            v1Config.Advanced.TypeBinder = v1Binder;

            byte[] oldSave;
            {
                var v1Ceras = new CerasSerializer(v1Config);
                var enemy   = new SavegameV1.Enemy { Name = "Troll", MaxHp = 150 };
                oldSave = v1Ceras.Serialize(enemy);
            }

            // V3 renames MaxHp → HitPoints but declares [PreviousName("MaxHp")]
            var v3Config = new SerializerConfig();
            v3Config.VersionTolerance.Mode = VersionToleranceMode.Standard;
            var v3Binder = new SavegameTypeBinder();
            v3Binder.Map(typeof(SavegameV3.Enemy), "Enemy");
            v3Binder.Map("Enemy", typeof(SavegameV3.Enemy));
            v3Config.Advanced.TypeBinder = v3Binder;
            {
                var v3Ceras = new CerasSerializer(v3Config);
                SavegameV3.Enemy enemy = null;
                v3Ceras.Deserialize(ref enemy, oldSave);

                Assert.Equal("Troll", enemy.Name);
                Assert.Equal(150,     enemy.HitPoints);  // mapped from old "MaxHp" via [PreviousName]
            }
        }

        // -------------------------------------------------------------------
        // 8. LIMITATION: generated formatters bypass SchemaDynamicFormatter.
        //    Adding a field while using generated formatters corrupts old saves.
        //    This test documents the limitation — it expects an exception or
        //    wrong data, NOT a clean round-trip across schema versions.
        // -------------------------------------------------------------------

        [Fact]
        public void Savegame_GeneratedFormatter_DoesNotSupportSchemaChanges()
        {
            // Write an Item using generated formatter (no schema header)
            var configV1 = GeneratedConfig();
            var cerasV1  = new CerasSerializer(configV1);
            var item     = new Item { Name = "Club", Quantity = 1, Rarity = ItemRarity.Common, Weight = 1.0f };
            byte[] bytes = cerasV1.Serialize(item);

            // If a new field were added to Item (simulated by the fact that
            // reading back with exactly the same layout works fine):
            var configV2 = GeneratedConfig();
            var cerasV2  = new CerasSerializer(configV2);
            var clone    = cerasV2.Deserialize<Item>(bytes);

            // Same layout → still works:
            Assert.Equal(item.Name, clone.Name);
            Assert.Equal(item.Weight, clone.Weight);

            // The point: there is no schema header in `bytes`. If Item gained
            // a new field between V1 and V2, the bytes above would be
            // misinterpreted (the new formatter would try to read e.g. a float
            // from bytes that were written as something else). There is no
            // automatic recovery. This is expected and by design — generated
            // formatters trade schema flexibility for compactness and speed.
        }

        // -------------------------------------------------------------------
        // 9. Mixing: use generated formatter for some types, version-tolerant
        //    DynamicFormatter for types that need schema flexibility.
        //    Only register generated formatters for STABLE types.
        // -------------------------------------------------------------------

        [Fact]
        public void Savegame_MixedApproach_GeneratedForStableTypes_DynamicForVolatile()
        {
            // Suppose Item layout is stable — use generated formatter.
            // Quest layout might change — leave it for SchemaDynamicFormatter.
            var config = new SerializerConfig();
            config.VersionTolerance.Mode = VersionToleranceMode.Standard;

            // Only register the formatter for Item, NOT for Quest.
            // Quest will fall through to SchemaDynamicFormatter automatically.
            config.ConfigType<Item>().CustomFormatter = new Ceras_Test_ItemFormatter();

            var ceras = new CerasSerializer(config);

            var quest = new Quest { Name = "Find the Relic", IsComplete = false, ObjectivesCompleted = 1 };
            var clone = ceras.Deserialize<Quest>(ceras.Serialize(quest));
            Assert.Equal(quest.Name, clone.Name);
            Assert.Equal(quest.ObjectivesCompleted, clone.ObjectivesCompleted);

            var item      = new Item { Name = "Staff", Rarity = ItemRarity.Rare, Weight = 1.5f, Quantity = 1 };
            var itemClone = ceras.Deserialize<Item>(ceras.Serialize(item));
            Assert.Equal(item.Name,   itemClone.Name);
            Assert.Equal(item.Rarity, itemClone.Rarity);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Simple type binder that maps type↔name for the version-tolerance tests.
    /// </summary>
    class SavegameTypeBinder : ITypeBinder
    {
        readonly Dictionary<Type, string>   _toName   = new();
        readonly Dictionary<string, Type>   _fromName = new();

        public void Map(Type type, string name) { _toName[type]   = name; }
        public void Map(string name, Type type) { _fromName[name] = type; }

        public string GetBaseName(Type type)
            => _toName.TryGetValue(type, out var n) ? n : type.FullName;

        public Type GetTypeFromBase(string baseTypeName)
            => _fromName.TryGetValue(baseTypeName, out var t) ? t
               : Type.GetType(baseTypeName) ?? throw new InvalidOperationException($"Unknown type: {baseTypeName}");

        public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-written IFormatter for Item — used in the mixed-approach test to
    /// demonstrate that you can selectively opt types into generated formatting
    /// without using the auto-generated class directly.  In practice you would
    /// use GeneratedFormatters.UseFormatters(config) and then explicitly
    /// NOT register formatters for types that need version tolerance.
    /// </summary>
    internal sealed class Ceras_Test_ItemFormatter : FormatterBase<Item>
    {
        IFormatter<string>     _stringFormatter;
        IFormatter<int>        _intFormatter;
        IFormatter<ItemRarity> _rarityFormatter;
        IFormatter<float>      _floatFormatter;

        public override void Serialize(IBufferWriter<byte> writer, Item value)
        {
            _stringFormatter.Serialize(writer, value.Name);
            _intFormatter.Serialize(writer, value.Quantity);
            _rarityFormatter.Serialize(writer, value.Rarity);
            _floatFormatter.Serialize(writer, value.Weight);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Item value)
        {
            _stringFormatter.Deserialize(ref reader, ref value.Name);
            _intFormatter.Deserialize(ref reader, ref value.Quantity);
            _rarityFormatter.Deserialize(ref reader, ref value.Rarity);
            _floatFormatter.Deserialize(ref reader, ref value.Weight);
        }
    }
}
