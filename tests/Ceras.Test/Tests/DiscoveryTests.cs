
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
    [CerasSerializable]
    [MemberConfig(TargetMember.PublicFields)]
    public class DiscoveryRoot
    {
        public DiscoveryChild Child;
        public List<DiscoveryNestedChild> NestedChildren;
        public IPolymorphicBase Polymorphic;
    }

    public class DiscoveryChild
    {
        public string Name;
    }

    public class DiscoveryNestedChild
    {
        public int id;
    }

    public interface IPolymorphicBase {}
    public class PolymorphicImpl : IPolymorphicBase { public int X; }

    public class DiscoveryTests : TestBase
    {
        [Fact]
        public void RecursiveDiscoveryWorks()
        {
            var c = new SerializerConfig();
            GeneratedFormatters.UseFormatters(c);

            var childType = typeof(DiscoveryChild);
            var nestedType = typeof(DiscoveryNestedChild);
            var implType = typeof(PolymorphicImpl);

            var childFormatter = c.ConfigType(childType).CustomFormatter;
            var nestedFormatter = c.ConfigType(nestedType).CustomFormatter;
            var implFormatter = c.ConfigType(implType).CustomFormatter;

            Assert.NotNull(childFormatter);
            Assert.NotNull(nestedFormatter);
            Assert.NotNull(implFormatter);
        }
    }
}
