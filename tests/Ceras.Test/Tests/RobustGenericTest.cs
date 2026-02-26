using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;

namespace Ceras.Test
{
    [CerasSerializable]
    public interface ICombatEntity
    {
        int Id { get; set; }
    }

    [CerasSerializable]
    public class CombatEntity : ICombatEntity
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    [CerasSerializable]
    public class MapChunk
    {
        public List<ICombatEntity> Entities { get; set; }
    }

    public class Pair<T1, T2>
    {
        public T1 Item1 { get; set; }
        public T2 Item2 { get; set; }
    }

    public class RobustGenericTest
    {
        [Fact]
        public void TestRobustGenericList()
        {
            var config = new SerializerConfig();
            // config.Robust.SchemaRegistry is read-only, it has a default instance
            
            var ceras = new CerasSerializer(config);

            var chunk = new MapChunk
            {
                Entities = new List<ICombatEntity>
                {
                    new CombatEntity { Id = 1, Name = "Orc" },
                    new CombatEntity { Id = 2, Name = "Goblin" }
                }
            };

            // Roundtrip through CerasRobust
            uint version = 1;
            var bytes = CerasRobust.Save(chunk, version, ceras);
            
            // Log schema for debugging if needed
            // Console.WriteLine(CerasRobust.DumpSchema(config.Robust.SchemaRegistry));

            var loadedChunk = CerasRobust.Load<MapChunk>(bytes, version, ceras);

            Assert.NotNull(loadedChunk);
            Assert.NotNull(loadedChunk.Entities);
            Assert.Equal(2, loadedChunk.Entities.Count);
            Assert.Equal(1, loadedChunk.Entities[0].Id);
            Assert.Equal("Orc", ((CombatEntity)loadedChunk.Entities[0]).Name);
            Assert.Equal(2, loadedChunk.Entities[1].Id);
            Assert.Equal("Goblin", ((CombatEntity)loadedChunk.Entities[1]).Name);
        }

        [Fact]
        public void TestRobustGenericListTraversal()
        {
            var config = new SerializerConfig();
            var ceras = new CerasSerializer(config);

            var chunk = new MapChunk
            {
                Entities = new List<ICombatEntity>
                {
                    new CombatEntity { Id = 1, Name = "Orc" },
                }
            };

            uint version = 1;
            var bytes = CerasRobust.Save(chunk, version, ceras);

            // Force fingerprint mismatch by clearing registry
            // (In a real scenario, the fingerprint would change because types are added/removed/changed)
            config.Robust.SchemaRegistry.InternalClear();
            // Re-register root type but it will have a different fingerprint if anything else was different,
            // or just clearing it is enough to make Load think it needs to traverse.
            
            var loadedChunk = CerasRobust.Load<MapChunk>(bytes, version, ceras);

            Assert.NotNull(loadedChunk);
            Assert.Single(loadedChunk.Entities);
            Assert.Equal(1, loadedChunk.Entities[0].Id);
            Assert.Equal("Orc", ((CombatEntity)loadedChunk.Entities[0]).Name);
        }

        [Fact]
        public void TestRobustGenericPair()
        {
            var config = new SerializerConfig();
            var ceras = new CerasSerializer(config);

            var pair = new Pair<int, string> { Item1 = 42, Item2 = "Hello" };

            uint version = 1;
            var bytes = CerasRobust.Save(pair, version, ceras);

            // 1. Normal roundtrip
            var loadedPair = CerasRobust.Load<Pair<int, string>>(bytes, version, ceras);
            Assert.Equal(42, loadedPair.Item1);
            Assert.Equal("Hello", loadedPair.Item2);

            // 2. Traversal roundtrip
            config.Robust.SchemaRegistry.InternalClear();
            var loadedPair2 = CerasRobust.Load<Pair<int, string>>(bytes, version, ceras);
            Assert.Equal(42, loadedPair2.Item1);
            Assert.Equal("Hello", loadedPair2.Item2);
        }
    }
}
