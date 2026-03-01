using System;
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;
using Ceras.Format;
using Xunit;

namespace Ceras.Test
{
    public enum StateEnum { Off, On }

    [CerasSerializable]
    public class HeroWithEnum
    {
        public StateEnum State;
    }

    [CerasSerializable]
    public class HeroWithList
    {
        public List<int> Scores;
    }

    public class RobustFormatTests : TestBase
    {
        CerasSerializer CreateSerializer()
        {
            var config = new CerasRobustConfig();
            RobustFormatters.UseRobustFormatters(config);
            CerasRobust.Configure(config);
            return new CerasSerializer(config);
        }

        [Fact]
        public void Robust_Enum_RoundTrip()
        {
            var ceras = CreateSerializer();

            var hero = new HeroWithEnum { State = StateEnum.On };
            
            byte[] data = CerasRobust.Save(hero, 1, ceras);
            var clone = CerasRobust.Load<HeroWithEnum>(data, 1, ceras);

            Assert.Equal(StateEnum.On, clone.State);
        }

        [Fact]
        public void Robust_List_RoundTrip()
        {
            var ceras = CreateSerializer();

            var hero = new HeroWithList { Scores = new List<int> { 1, 2, 3 } };

            byte[] data = CerasRobust.Save(hero, 1, ceras);
            var clone = CerasRobust.Load<HeroWithList>(data, 1, ceras);

            Assert.NotNull(clone.Scores);
            Assert.Equal(3, clone.Scores.Count);
            Assert.Equal(2, clone.Scores[1]);
        }

        [Fact]
        public void Robust_Polymorphic_RoundTrip()
        {
            var ceras = CreateSerializer();
            var registry = ((CerasRobustConfig)ceras.Config).SchemaRegistry;

            var hero = new HeroWithPolymorphic { Extra = new ExtraInfo { Note = "Special" } };

            // Manual registration
            registry.Register(typeof(ExtraInfo), new SchemaTypeInfo(
                "ExtraInfo", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("Note", Array.Empty<string>(), "System.String", true) }, 0));
            registry.Register(typeof(HeroWithPolymorphic), new SchemaTypeInfo(
                "HeroWithPolymorphic", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("Extra", Array.Empty<string>(), "System.Object", false) }, 0));

            byte[] data = CerasRobust.Save(hero, 1, ceras);
            var clone = CerasRobust.Load<HeroWithPolymorphic>(data, 1, ceras);

            Assert.IsType<ExtraInfo>(clone.Extra);
            Assert.Equal("Special", ((ExtraInfo)clone.Extra).Note);
        }
    }

    [CerasSerializable]
    public class HeroWithPolymorphic
    {
        public object Extra;
    }

    [CerasSerializable]
    public class ExtraInfo
    {
        public string Note;
    }
}
