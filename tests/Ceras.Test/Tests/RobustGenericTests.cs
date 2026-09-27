using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

[assembly: Ceras.CerasKnownType<Ceras.Test.RobustGenerics.GenericPair<int, string>>]

namespace Ceras.Test.RobustGenerics
{
    // 1. Explicitly registered interface
    [CerasContext("Tests")]
    [CerasSerializable]
    public interface ICombatEntity
    {
        int Id { get; set; }
    }

    // 2. Unregistered interface (tests TypeBinder fallback in Load)
    [CerasContext("Tests")]
    [CerasSerializable]
    public interface IUnregistered
    {
        string Data { get; set; }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CombatEntity : ICombatEntity, IUnregistered
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Data { get; set; }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class GenericListModel
    {
        public List<ICombatEntity> Entities { get; set; }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class UnregisteredListModel
    {
        public List<IUnregistered> Items { get; set; }
    }

    public enum Status { Idle, Active, Dead }

    [CerasContext("Tests")]
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

    [CerasContext("Tests")]
    [CerasSerializable]
    public class NullableModel
    {
        public int? MaybeInt { get; set; }
        public float? MaybeFloat { get; set; }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class DictionaryModel
    {
        public Dictionary<string, ICombatEntity> Entities { get; set; }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ArrayModel
    {
        public ICombatEntity[] EntityArray { get; set; }
        public int[] IntArray { get; set; }
    }

    public class DummyForMismatch { }

    public class RobustGenericTests
    {
        CerasRobustSerializer CreateSerializer() => new CerasRobustSerializer(config => config.UseGeneratedFormatters("Tests"));

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
            var bytes = CreateSerializer().Save(model, version);

            // Use a FRESH serializer for loading to simulate restart
            var loaded = CreateSerializer().Load<GenericListModel>(bytes, version);

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
            // Writer serializer with DummyForMismatch (Fingerprint F1).
            var s1 = CreateSerializer();
            s1.Config.SchemaRegistry.Register(typeof(DummyForMismatch), null);
            var bytes = s1.Save(model, version);
            ulong writerFingerprint = s1.Config.SchemaRegistry.ComputeFingerprint();

            // 1. Fresh reader WITHOUT DummyForMismatch. Its sealed fingerprint (F2) genuinely differs,
            //    so this load takes the interpreter path; the dummy registration is what forces it.
            var reader1 = CreateSerializer();
            var loaded = reader1.Load<UnregisteredListModel>(bytes, version);
            Assert.NotEqual(writerFingerprint, reader1.Config.SchemaRegistry.ComputeFingerprint());
            Assert.Equal("Secret", loaded.Items[0].Data);

            // 2. Same serializer, registry cleared and repopulated without DummyForMismatch.
            var serializer = CreateSerializer();
            serializer.Config.SchemaRegistry.InternalClear();
            serializer.Config.UseGeneratedFormatters("Tests");
            // No DummyForMismatch here!
            var loaded2 = serializer.Load<UnregisteredListModel>(bytes, version);
            Assert.NotEqual(writerFingerprint, serializer.Config.SchemaRegistry.ComputeFingerprint());
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
            var s1 = CreateSerializer();
            s1.Config.SchemaRegistry.Register(typeof(DummyForMismatch), null);
            var bytes = s1.Save(model, version);
            ulong writerFingerprint = s1.Config.SchemaRegistry.ComputeFingerprint();

            // 1. Fresh reader without the dummy: fingerprint differs -> interpreter path.
            var reader1 = CreateSerializer();
            var loaded = reader1.Load<StatusModel>(bytes, version);
            Assert.NotEqual(writerFingerprint, reader1.Config.SchemaRegistry.ComputeFingerprint());
            Assert.Equal(Status.Active, loaded.CurrentStatus);
            Assert.Equal(2, loaded.History.Count);

            // 2. Same serializer, registry cleared and repopulated without the dummy.
            var serializer = CreateSerializer();
            serializer.Config.SchemaRegistry.InternalClear();
            serializer.Config.UseGeneratedFormatters("Tests");
            var loaded2 = serializer.Load<StatusModel>(bytes, version);
            Assert.NotEqual(writerFingerprint, serializer.Config.SchemaRegistry.ComputeFingerprint());
            Assert.Equal(Status.Active, loaded2.CurrentStatus);
        }

        [Fact]
        public void GenericPair_Roundtrip_And_Traversal()
        {
            var model = new GenericPair<int, string> { Item1 = 42, Item2 = "Hello" };

            uint version = 1;
            var s1 = CreateSerializer();
            s1.Config.SchemaRegistry.Register(typeof(DummyForMismatch), null);
            var bytes = s1.Save(model, version);
            ulong writerFingerprint = s1.Config.SchemaRegistry.ComputeFingerprint();

            // 1. Fresh reader without the dummy: fingerprint differs -> interpreter path.
            var reader1 = CreateSerializer();
            var loaded = reader1.Load<GenericPair<int, string>>(bytes, version);
            Assert.NotEqual(writerFingerprint, reader1.Config.SchemaRegistry.ComputeFingerprint());
            Assert.Equal(42, loaded.Item1);

            // 2. Same serializer, registry cleared and repopulated without the dummy.
            var serializer = CreateSerializer();
            serializer.Config.SchemaRegistry.InternalClear();
            serializer.Config.UseGeneratedFormatters("Tests");
            var loaded2 = serializer.Load<GenericPair<int, string>>(bytes, version);
            Assert.NotEqual(writerFingerprint, serializer.Config.SchemaRegistry.ComputeFingerprint());
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
            var bytes = CreateSerializer().Save(model, version);
            var loaded = CreateSerializer().Load<DictionaryModel>(bytes, version);
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
            var bytes = CreateSerializer().Save(model, version);
            var loaded = CreateSerializer().Load<ArrayModel>(bytes, version);
            Assert.Single(loaded.EntityArray);
            Assert.Equal(new int[] { 1, 2, 3 }, loaded.IntArray);
        }

        [Fact]
        public void Nullable_Roundtrip()
        {
            var model = new NullableModel { MaybeInt = 123, MaybeFloat = null };
            uint version = 1;
            var bytes = CreateSerializer().Save(model, version);
            var loaded = CreateSerializer().Load<NullableModel>(bytes, version);
            Assert.Equal(123, loaded.MaybeInt);
            Assert.Null(loaded.MaybeFloat);
        }
    }
}
