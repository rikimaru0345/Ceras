using System;
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;
using Ceras.Format;
using Xunit;

namespace Ceras.Test
{
    public enum StateEnum { Off, On }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class HeroWithEnum
    {
        public StateEnum State;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class HeroWithList
    {
        public List<int> Scores;
    }

    public class RobustFormatTests : TestBase
    {
        CerasRobustSerializer CreateSerializer() => new CerasRobustSerializer(config => config.UseGeneratedFormatters("Tests"));

        [Fact]
        public void Robust_Enum_RoundTrip()
        {
            var ceras = CreateSerializer();

            var hero = new HeroWithEnum { State = StateEnum.On };

            byte[] data = ceras.Save(hero, 1);
            var clone = ceras.Load<HeroWithEnum>(data, 1);

            Assert.Equal(StateEnum.On, clone.State);
        }

        [Fact]
        public void Robust_List_RoundTrip()
        {
            var ceras = CreateSerializer();

            var hero = new HeroWithList { Scores = new List<int> { 1, 2, 3 } };

            byte[] data = ceras.Save(hero, 1);
            var clone = ceras.Load<HeroWithList>(data, 1);

            Assert.NotNull(clone.Scores);
            Assert.Equal(3, clone.Scores.Count);
            Assert.Equal(2, clone.Scores[1]);
        }

        [Fact]
        public void Robust_Polymorphic_RoundTrip()
        {
            var ceras = CreateSerializer();

            var hero = new HeroWithPolymorphic { Extra = new ExtraInfo { Note = "Special" } };

            // Manual registration
            ceras.Config.SchemaRegistry.Register(typeof(ExtraInfo), new SchemaTypeInfo(
                "ExtraInfo", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("Note", Array.Empty<string>(), "System.String", true) }, 0));
            ceras.Config.SchemaRegistry.Register(typeof(HeroWithPolymorphic), new SchemaTypeInfo(
                "HeroWithPolymorphic", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("Extra", Array.Empty<string>(), "System.Object", false) }, 0));

            byte[] data = ceras.Save(hero, 1);
            var clone = ceras.Load<HeroWithPolymorphic>(data, 1);

            Assert.IsType<ExtraInfo>(clone.Extra);
            Assert.Equal("Special", ((ExtraInfo)clone.Extra).Note);
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class HeroWithPolymorphic
    {
        public object Extra;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ExtraInfo
    {
        public string Note;
    }
}
