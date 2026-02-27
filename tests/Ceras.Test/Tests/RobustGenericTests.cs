using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;

namespace Ceras.Test.RobustGenerics
{
    // 1. Explicitly registered interface
    [CerasSerializable]
    public interface ICombatEntity
    {
        int Id { get; set; }
    }

    // 2. Unregistered interface (tests TypeBinder fallback in Load)
    public interface IUnregistered
    {
        string Data { get; set; }
    }

    [CerasSerializable]
    public class CombatEntity : ICombatEntity, IUnregistered
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Data { get; set; }
    }

    [CerasSerializable]
    public class GenericListModel
    {
        public List<ICombatEntity> Entities { get; set; }
    }

    [CerasSerializable]
    public class UnregisteredListModel
    {
        public List<IUnregistered> Items { get; set; }
    }

    public enum Status { Idle, Active, Dead }

    [CerasSerializable]
    public class StatusModel
    {
        public Status CurrentStatus { get; set; }
        public List<Status> History { get; set; }
    }

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

    [CerasSerializable]
    public class DictionaryModel
    {
        public Dictionary<string, ICombatEntity> Entities { get; set; }
    }

    [CerasSerializable]
    public class ArrayModel
    {
        public ICombatEntity[] EntityArray { get; set; }
        public int[] IntArray { get; set; }
    }

    public class RobustGenericTests
    {
        CerasSerializer CreateSerializer()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            return new CerasSerializer(config);
        }

        [Fact]
        public void GenericList_Roundtrip()
        {
            var model = new GenericListModel
            {
                Entities = new List<ICombatEntity>
                {
                    new CombatEntity { Id = 1, Name = "Orc" },
                    new CombatEntity { Id = 2, Name = "Goblin" }
                }
            };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, CreateSerializer());
            
            // Use a FRESH serializer for loading to simulate restart
            var loaded = CerasRobust.Load<GenericListModel>(bytes, version, CreateSerializer());

            Assert.NotNull(loaded.Entities);
            Assert.Equal(2, loaded.Entities.Count);
            Assert.Equal("Orc", ((CombatEntity)loaded.Entities[0]).Name);
        }

        [Fact]
        public void UnregisteredInterface_Roundtrip_And_Traversal()
        {
            var model = new UnregisteredListModel
            {
                Items = new List<IUnregistered>
                {
                    new CombatEntity { Id = 1, Data = "Secret" }
                }
            };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, CreateSerializer());

            // 1. Normal (Fresh)
            var loaded = CerasRobust.Load<UnregisteredListModel>(bytes, version, CreateSerializer());
            Assert.Equal("Secret", loaded.Items[0].Data);

            // 2. Traversal
            var serializer = CreateSerializer();
            // Populate registry so we know the types locally
            CerasRobust.Save(model, version, serializer);
            // Clear it to force traversal (IDs will be different from data)
            serializer.Config.Robust.SchemaRegistry.InternalClear();
            var loaded2 = CerasRobust.Load<UnregisteredListModel>(bytes, version, serializer);
            Assert.Equal("Secret", loaded2.Items[0].Data);
        }

        [Fact]
        public void Enum_Roundtrip_And_Traversal()
        {
            var model = new StatusModel
            {
                CurrentStatus = Status.Active,
                History = new List<Status> { Status.Idle, Status.Active }
            };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, CreateSerializer());

            // 1. Normal
            var loaded = CerasRobust.Load<StatusModel>(bytes, version, CreateSerializer());
            Assert.Equal(Status.Active, loaded.CurrentStatus);
            Assert.Equal(2, loaded.History.Count);

            // 2. Traversal
            var serializer = CreateSerializer();
            serializer.Config.Robust.SchemaRegistry.InternalClear();
            var loaded2 = CerasRobust.Load<StatusModel>(bytes, version, serializer);
            Assert.Equal(Status.Active, loaded2.CurrentStatus);
        }

        [Fact]
        public void GenericPair_Roundtrip_And_Traversal()
        {
            var model = new GenericPair<int, string> { Item1 = 42, Item2 = "Hello" };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, CreateSerializer());

            // 1. Normal
            var loaded = CerasRobust.Load<GenericPair<int, string>>(bytes, version, CreateSerializer());
            Assert.Equal(42, loaded.Item1);

            // 2. Traversal
            var serializer = CreateSerializer();
            serializer.Config.Robust.SchemaRegistry.InternalClear();
            var loaded2 = CerasRobust.Load<GenericPair<int, string>>(bytes, version, serializer);
            Assert.Equal(42, loaded2.Item1);
        }

        [Fact]
        public void Dictionary_Roundtrip()
        {
            var model = new DictionaryModel
            {
                Entities = new Dictionary<string, ICombatEntity>
                {
                    { "K", new CombatEntity { Id = 1, Name = "Orc" } }
                }
            };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, CreateSerializer());
            var loaded = CerasRobust.Load<DictionaryModel>(bytes, version, CreateSerializer());
            Assert.Equal("Orc", ((CombatEntity)loaded.Entities["K"]).Name);
        }

        [Fact]
        public void Array_Roundtrip()
        {
            var model = new ArrayModel
            {
                EntityArray = new ICombatEntity[] { new CombatEntity { Id = 1, Name = "Orc" } },
                IntArray = new int[] { 1, 2, 3 }
            };

            uint version = 1;
            var bytes = CerasRobust.Save(model, version, CreateSerializer());
            var loaded = CerasRobust.Load<ArrayModel>(bytes, version, CreateSerializer());
            Assert.Single(loaded.EntityArray);
            Assert.Equal(new int[] { 1, 2, 3 }, loaded.IntArray);
        }

        [Fact]
        public void Nullable_Roundtrip()
        {
            var model = new NullableModel { MaybeInt = 123, MaybeFloat = null };
            uint version = 1;
            var bytes = CerasRobust.Save(model, version, CreateSerializer());
            var loaded = CerasRobust.Load<NullableModel>(bytes, version, CreateSerializer());
            Assert.Equal(123, loaded.MaybeInt);
            Assert.Null(loaded.MaybeFloat);
        }
    }
}
