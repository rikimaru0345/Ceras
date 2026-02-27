
#pragma warning disable 0649 // Field is never assigned to


using System.Buffers;

// ---------------------------------------------------------------------------
// Robust scenario tests for the Ceras source generator.
//
// These tests answer two key questions:
//   1. Does the source generator handle realistic, complex object graphs?
//   2. How do generated formatters interact with the 'CerasRobust' system?
//
// The short answer to (2):
//   Generated formatters do NOT embed field names in the binary — they write
//   fields in sorted order with no schema header. This makes them fast and
//   compact.
//
//   Robust versioning (adding/removing/renaming fields) is handled by the
//   'CerasRobust' system, which uses the migration engine and SchemaTypeInfo
//   emitted by the source generator when 'VersionTolerant = true' is used.
// ---------------------------------------------------------------------------


using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Formatters;
using Ceras.Helpers;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test
{
    // -----------------------------------------------------------------------
    // Robust model — all types annotated with [CerasSerializable]
    // -----------------------------------------------------------------------

    [CerasSerializable]
    [MemberConfig(TargetMember.PublicFields)]
    public class RobustData
    {
        public Player    Player;
        public string    Version;   // game version string, e.g. "1.0"
        public int       PlaytimeSeconds;
    }

    [CerasSerializable]
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

    [CerasSerializable]
    [MemberConfig(TargetMember.PublicFields)]
    public class PlayerStats
    {
        public int Strength;
        public int Dexterity;
        public int Intelligence;
    }

    [CerasSerializable]
    [MemberConfig(TargetMember.PublicFields)]
    public class Inventory
    {
        public List<Item> Items;
        public int        MaxCapacity;
    }

    [CerasSerializable]
    [MemberConfig(TargetMember.PublicFields)]
    public class Item
    {
        public string     Name;
        public int        Quantity;
        public ItemRarity Rarity;
        public float      Weight;
    }

    public enum ItemRarity { Common = 0, Uncommon = 1, Rare = 2, Epic = 3, Legendary = 4 }

    [CerasSerializable]
    [MemberConfig(TargetMember.PublicFields)]
    public class Quest
    {
        public string Name;
        public bool   IsComplete;
        public int    ObjectivesCompleted;
    }

    // -----------------------------------------------------------------------
    // Test class
    // -----------------------------------------------------------------------

    public class RobustTests : TestBase
    {
        SerializerConfig GeneratedConfig()
        {
            var c = new SerializerConfig();
            GeneratedFormatters.UseFormatters(c);
            return c;
        }

        RobustData MakeRobustData() => new RobustData
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
        public void Robust_FullRoundTrip_GeneratedFormatter()
        {
            var ceras    = new CerasSerializer(GeneratedConfig());
            var original = MakeRobustData();

            var clone = ceras.Deserialize<RobustData>(ceras.Serialize(original));

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
        public void Robust_NullAndEmptyCollections()
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
        public void Robust_EnumRoundTrip()
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
        // 4. Generated formatter produces byte-identical output to ReflectionFallbackFormatter
        //    (ensures cross-tool compatibility for save data)
        // -------------------------------------------------------------------

        [Fact]
        public void Robust_GeneratedAndDynamic_ByteIdentical()
        {
            var generatedCeras = new CerasSerializer(GeneratedConfig());
            var dynamicCeras   = new CerasSerializer(new SerializerConfig());

            var obj = new Item { Name = "Mithril Helm", Quantity = 1, Rarity = ItemRarity.Epic, Weight = 3.7f };

            Assert.Equal(dynamicCeras.Serialize(obj), generatedCeras.Serialize(obj));
        }
    }

    /// <summary>
    /// Hand-written IFormatter for Item — used in the mixed-approach test to
    /// demonstrate that you can selectively opt types into generated formatting
    /// without using the auto-generated class directly.
    /// </summary>
    internal sealed class Ceras_Test_ItemFormatter : FormatterBase<Item>
    {
        IFormatter<string>     _stringFormatter;
        IFormatter<int>        _intFormatter;
        IFormatter<ItemRarity> _rarityFormatter;
        IFormatter<float>      _floatFormatter;

        public override void Serialize(IBufferWriter<byte> writer, Item value)
        {
            _rarityFormatter.Serialize(writer, value.Rarity);
            _intFormatter.Serialize(writer, value.Quantity);
            _floatFormatter.Serialize(writer, value.Weight);
            _stringFormatter.Serialize(writer, value.Name);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Item value)
        {
            _rarityFormatter.Deserialize(ref reader, ref value.Rarity);
            _intFormatter.Deserialize(ref reader, ref value.Quantity);
            _floatFormatter.Deserialize(ref reader, ref value.Weight);
            _stringFormatter.Deserialize(ref reader, ref value.Name);
        }
    }
}
