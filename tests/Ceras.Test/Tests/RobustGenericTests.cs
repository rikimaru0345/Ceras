using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;

namespace Ceras.Test.RobustGenerics
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
    public class GenericListModel
    {
        public List<ICombatEntity> Entities { get; set; }
    }

    // Source generator can't handle open generics in the test project easily
    // but CerasRobust can handle them via reflection registration.
    public class GenericPair<T1, T2>
    {
        public T1 Item1 { get; set; }
        public T2 Item2 { get; set; }
    }

    [CerasSerializable]
    public class NullableModel
    {
        public int? MaybeInt { get; set; }
        public float? MaybeFloat { get; set; }
    }

    public class RobustGenericTests
    {
        [Fact]
        public void GenericList_Roundtrip()
        {
            var config = new SerializerConfig();
            var ceras = new CerasSerializer(config);

            var model = new GenericListModel
            {
                Entities = new List<ICombatEntity>
                {
                    new CombatEntity { Id = 1, Name = "Orc" },
                    new CombatEntity { Id = 2, Name = "Goblin" }
                }
            };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, ceras);
            var loaded = CerasRobust.Load<GenericListModel>(bytes, version, ceras);

            Assert.NotNull(loaded.Entities);
            Assert.Equal(2, loaded.Entities.Count);
            Assert.Equal("Orc", ((CombatEntity)loaded.Entities[0]).Name);
            Assert.Equal("Goblin", ((CombatEntity)loaded.Entities[1]).Name);
        }

        [Fact]
        public void GenericList_Traversal()
        {
            var config = new SerializerConfig();
            var ceras = new CerasSerializer(config);

            var model = new GenericListModel
            {
                Entities = new List<ICombatEntity> { new CombatEntity { Id = 1, Name = "Orc" } }
            };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, ceras);

            // Force traversal by clearing registry
            config.Robust.SchemaRegistry.InternalClear();
            
            var loaded = CerasRobust.Load<GenericListModel>(bytes, version, ceras);

            Assert.Single(loaded.Entities);
            Assert.Equal("Orc", ((CombatEntity)loaded.Entities[0]).Name);
        }

        [Fact]
        public void GenericPair_Roundtrip_And_Traversal()
        {
            var config = new SerializerConfig();
            var ceras = new CerasSerializer(config);

            var model = new GenericPair<int, string> { Item1 = 42, Item2 = "Hello" };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, ceras);

            // 1. Normal
            var loaded = CerasRobust.Load<GenericPair<int, string>>(bytes, version, ceras);
            Assert.Equal(42, loaded.Item1);
            Assert.Equal("Hello", loaded.Item2);

            // 2. Traversal
            config.Robust.SchemaRegistry.InternalClear();
            var loaded2 = CerasRobust.Load<GenericPair<int, string>>(bytes, version, ceras);
            Assert.Equal(42, loaded2.Item1);
            Assert.Equal("Hello", loaded2.Item2);
        }

        [Fact]
        public void Nullable_Roundtrip_And_Traversal()
        {
            var config = new SerializerConfig();
            var ceras = new CerasSerializer(config);

            var model = new NullableModel { MaybeInt = 123, MaybeFloat = null };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, ceras);

            // 1. Normal
            var loaded = CerasRobust.Load<NullableModel>(bytes, version, ceras);
            Assert.Equal(123, loaded.MaybeInt);
            Assert.Null(loaded.MaybeFloat);

            // 2. Traversal
            config.Robust.SchemaRegistry.InternalClear();
            var loaded2 = CerasRobust.Load<NullableModel>(bytes, version, ceras);
            Assert.Equal(123, loaded2.MaybeInt);
            Assert.Null(loaded2.MaybeFloat);
        }

        [Fact]
        public void NestedGeneric_Traversal()
        {
            var config = new SerializerConfig();
            var ceras = new CerasSerializer(config);

            var model = new GenericPair<int, List<string>>
            {
                Item1 = 1,
                Item2 = new List<string> { "A", "B" }
            };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, ceras);

            config.Robust.SchemaRegistry.InternalClear();
            var loaded = CerasRobust.Load<GenericPair<int, List<string>>>(bytes, version, ceras);

            Assert.Equal(1, loaded.Item1);
            Assert.Equal(2, loaded.Item2.Count);
            Assert.Equal("A", loaded.Item2[0]);
            Assert.Equal("B", loaded.Item2[1]);
        }
    }
}
