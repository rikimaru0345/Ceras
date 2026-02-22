using System;
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;
using Ceras.Savegame;
using Ceras.Test.Migration;
using Xunit;

namespace Ceras.Test
{
    public class SavegameFormatTests : TestBase
    {
        static readonly CerasSerializer _ceras;

        static SavegameFormatTests()
        {
            SavegameSchemaRegistry.Clear();
            var config = new SerializerConfig();
            GeneratedFormatters.UseFormatters(config);
            CerasSave.ConfigureSavegame(config);
            _ceras = new CerasSerializer(config);
        }

        [Fact]
        public void Savegame_Enum_RoundTrip()
        {
            SavegameSchemaRegistry.Clear();
            var hero = new HeroWithEnum { State = StateEnum.On };
            
            // Manual registration for test-only type
            SavegameSchemaRegistry.Register(typeof(StateEnum), new SchemaTypeInfo(
                "StateEnum", Array.Empty<string>(), SchemaTypeKind.Enum, Array.Empty<SchemaFieldInfo>(), 4,
                new[] { new SchemaEnumValueInfo("Off", 0), new SchemaEnumValueInfo("On", 1) }, "System.Int32"));
            SavegameSchemaRegistry.Register(typeof(HeroWithEnum), new SchemaTypeInfo(
                "HeroWithEnum", Array.Empty<string>(), SchemaTypeKind.Class, 
                new[] { new SchemaFieldInfo("State", Array.Empty<string>(), "Ceras.Test.StateEnum", true) }, 0));

            byte[] data = CerasSave.Save(hero, 1, _ceras);
            var clone = CerasSave.Load<HeroWithEnum>(data, 1, _ceras);

            Assert.Equal(StateEnum.On, clone.State);
        }

        [Fact]
        public void Savegame_List_RoundTrip()
        {
            SavegameSchemaRegistry.Clear();
            var hero = new HeroWithList { Scores = new List<int> { 1, 2, 3 } };

            // Manual registration
            SavegameSchemaRegistry.Register(typeof(HeroWithList), new SchemaTypeInfo(
                "HeroWithList", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("Scores", Array.Empty<string>(), "System.Collections.Generic.List`1[[System.Int32]]", true) }, 0));

            byte[] data = CerasSave.Save(hero, 1, _ceras);
            var clone = CerasSave.Load<HeroWithList>(data, 1, _ceras);

            Assert.NotNull(clone.Scores);
            Assert.Equal(3, clone.Scores.Count);
            Assert.Equal(2, clone.Scores[1]);
        }

        [Fact]
        public void Savegame_Polymorphic_RoundTrip()
        {
            SavegameSchemaRegistry.Clear();
            var hero = new HeroWithPolymorphic { Extra = new ExtraInfo { Note = "Special" } };

            // Manual registration
            SavegameSchemaRegistry.Register(typeof(ExtraInfo), new SchemaTypeInfo(
                "ExtraInfo", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("Note", Array.Empty<string>(), "System.String", true) }, 0));
            SavegameSchemaRegistry.Register(typeof(HeroWithPolymorphic), new SchemaTypeInfo(
                "HeroWithPolymorphic", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("Extra", Array.Empty<string>(), "System.Object", false) }, 0));

            byte[] data = CerasSave.Save(hero, 1, _ceras);
            var clone = CerasSave.Load<HeroWithPolymorphic>(data, 1, _ceras);

            Assert.IsType<ExtraInfo>(clone.Extra);
            Assert.Equal("Special", ((ExtraInfo)clone.Extra).Note);
        }
    }

    [GenerateFormatter(VersionTolerant = true)]
    public class HeroWithPolymorphic
    {
        public object Extra;
    }

    [GenerateFormatter(VersionTolerant = true)]
    public class ExtraInfo
    {
        public string Note;
    }
}
