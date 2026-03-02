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
using System.Linq;

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
        CerasRawConfig GeneratedConfig()
        {
            var c = new CerasRawConfig();

            RobustFormatters.UseRobustFormatters(c);
            return c;
        }

        RobustData MakeRobustData() => new RobustData
        {
            Version        = "1.0",
            PlaytimeSeconds = 3600,
            Player = new Player
            {
                Name  = "Aria",
                Level = 12,
                Gold  = 450,
                Stats = new PlayerStats { Strength = 10, Dexterity = 15, Intelligence = 8 },
                Inventory = new Inventory
                {
                    MaxCapacity = 20,
                    Items = new List<Item>
                    {
                        new Item { Name = "Small Health Potion", Quantity = 5, Rarity = ItemRarity.Common,   Weight = 0.5f },
                        new Item { Name = "Iron Sword",         Quantity = 1, Rarity = ItemRarity.Uncommon, Weight = 4.0f },
                    }
                },
                ActiveQuests = new List<Quest>
                {
                    new Quest { Name = "First Steps", IsComplete = true,  ObjectivesCompleted = 1 },
                    new Quest { Name = "The Cave",    IsComplete = false, ObjectivesCompleted = 0 },
                }
            }
        };

        // -------------------------------------------------------------------
        // 1. Full round-trip of a complex nested object graph
        // -------------------------------------------------------------------

        [Fact]
        public void Robust_FullRoundTrip_GeneratedFormatter()
        {
            var ceras    = new CerasFastSerializer(GeneratedConfig());
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

            Assert.Equal(op.Inventory.MaxCapacity, cp.Inventory.MaxCapacity);
            Assert.Equal(op.Inventory.Items.Count, cp.Inventory.Items.Count);
            Assert.Equal(op.Inventory.Items[0].Name, cp.Inventory.Items[0].Name);
            Assert.Equal(op.Inventory.Items[1].Rarity, cp.Inventory.Items[1].Rarity);

            Assert.Equal(op.ActiveQuests.Count, cp.ActiveQuests.Count);
            Assert.Equal(op.ActiveQuests[0].Name, cp.ActiveQuests[0].Name);
            Assert.Equal(op.ActiveQuests[1].IsComplete,          cp.ActiveQuests[1].IsComplete);
        }

        // -------------------------------------------------------------------
        // 2. Null collections and empty lists
        // -------------------------------------------------------------------

        [Fact]
        public void Robust_NullAndEmptyCollections()
        {
            var ceras = new CerasFastSerializer(GeneratedConfig());

            var original = new Player
            {
                Name         = "Ghost",
                Level        = 1,
                ActiveQuests = new List<Quest>(),   // empty, not null
                Inventory    = new Inventory { Items = null, MaxCapacity = 10 },
            };

            var clone = ceras.Deserialize<Player>(ceras.Serialize(original));

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
            var ceras = new CerasFastSerializer(GeneratedConfig());

            foreach (var rarity in Enum.GetValues<ItemRarity>())
            {
                var item  = new Item { Name = "x", Rarity = rarity };
                var clone = ceras.Deserialize<Item>(ceras.Serialize(item));
                Assert.Equal(rarity, clone.Rarity);
            }
		}

        // -------------------------------------------------------------------
        // 4. Regression: Circular references in polymorphic slots
        // -------------------------------------------------------------------

        [Fact]
        public void Robust_CircularReference_Polymorphic()
        {
            var ceras = new CerasRobustSerializer(config => {
                RobustFormatters.UseRobustFormatters(config);
            });

            var container = new RobustContainer();
            var node = new RobustNode { Name = "Recursive" };
            node.Reference = node; // Circular

            container.Dict[node] = "some value";
            container.List.Add(node);

            // 1. Save
            var data = ceras.Save(container, 1);

            // 2. Load with schema change to force SchemaTraversalEngine
            data[CerasRobustSerializer.OffsetFingerprint] ^= 0xFF;
            var clone = ceras.Load<RobustContainer>(data, 1);

            Assert.NotNull(clone);
            Assert.Single(clone.List);
            var clonedNode = (RobustNode)clone.List[0];
            Assert.NotNull(clonedNode);
            Assert.Same(clonedNode, clonedNode.Reference);
            Assert.True(clone.Dict.ContainsKey(clonedNode));
        }

        // -------------------------------------------------------------------
        // 5. Regression: Generic structs in monomorphic slots
        // -------------------------------------------------------------------

        [Fact]
        public void Robust_GenericStruct_Monomorphic()
        {
            var ceras = new CerasRobustSerializer(config => {
                RobustFormatters.UseRobustFormatters(config);
            });

            var container = new GenericStructContainer { Pair = new KeyValuePair<int, int>(1, 2) };

            // 1. Save
            var data = ceras.Save(container, 1);

            // 2. Load with schema change to force SchemaTraversalEngine
            data[CerasRobustSerializer.OffsetFingerprint] ^= 0xFF;
            var clone = ceras.Load<GenericStructContainer>(data, 1);

            Assert.Equal(1, clone.Pair.Key);
            Assert.Equal(2, clone.Pair.Value);
        }
	}

    [CerasSerializable]
    public class RobustNode
    {
        public string Name;
        public object Reference;
    }

    [CerasSerializable]
    public class RobustContainer
    {
        public Dictionary<object, object> Dict = new();
        public List<object> List = new();
    }

    [CerasSerializable]
    public class GenericStructContainer
    {
        public KeyValuePair<int, int> Pair;
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
