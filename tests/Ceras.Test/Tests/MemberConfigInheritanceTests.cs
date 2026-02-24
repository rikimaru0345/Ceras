using System;
using System.Collections.Generic;
using Xunit;

namespace Ceras.Test.Tests
{
    public class MemberConfigInheritanceTests : TestBase
    {
        SerializerConfig CreateSgConfig()
        {
            var config = new SerializerConfig();
            Ceras.Generated.GeneratedFormatters.UseFormatters(config);
            return config;
        }

        [Fact]
        public void MemberConfig_InheritsFromInterface()
        {
            var config = CreateSgConfig();
            var obj = new DerivedFromInterface { PublicField = 1 };
            obj.SetPrivateField(2);
            
            var clone = Clone(obj, config);
            
            Assert.Equal(1, clone.PublicField);
            // PrivateField should be serialized because IHasMemberConfig has [MemberConfig(All)]
            Assert.Equal(2, clone.GetPrivateField());
        }

        [Fact]
        public void MemberConfig_DerivedOverridesInterface()
        {
            var config = CreateSgConfig();
            var ceras = new CerasSerializer(config);
            var formatter = ceras.GetSpecificFormatter(typeof(DerivedWithOverride));
            Assert.Contains("Ceras.Generated", formatter.GetType().FullName);

            var obj = new DerivedWithOverride { PublicField = 1 };
            obj.SetPrivateField(2);
            
            var clone = Clone(obj, CreateSgConfig());
            
            Assert.Equal(1, clone.PublicField);
            // PrivateField should NOT be serialized because DerivedWithOverride has [MemberConfig(AllPublic)]
            Assert.Equal(0, clone.GetPrivateField());
        }
        [Fact]
        public void MemberConfig_MultipleInterfaces_Matching_Works()
        {
            var config = CreateSgConfig();
            var obj = new MultipleInterfacesMatch { PublicField = 1 };
            obj.SetPrivateField(2);
            
            var clone = Clone(obj, config);
            
            Assert.Equal(1, clone.PublicField);
            Assert.Equal(2, clone.GetPrivateField());
        }
    }

    [MemberConfig(TargetMember.All)]
    public interface IHasMemberConfig
    {
    }

    [MemberConfig(TargetMember.All)]
    public interface IOtherHasMemberConfig
    {
    }

    [GenerateFormatter]
    public partial class MultipleInterfacesMatch : IHasMemberConfig, IOtherHasMemberConfig
    {
        public int PublicField;
        internal int PrivateField;

        public void SetPrivateField(int v) => PrivateField = v;
        public int GetPrivateField() => PrivateField;
    }

    [GenerateFormatter]
    public partial class DerivedFromInterface : IHasMemberConfig
    {
        public int PublicField;
        internal int PrivateField;

        public void SetPrivateField(int v) => PrivateField = v;
        public int GetPrivateField() => PrivateField;
    }

    [GenerateFormatter]
    [MemberConfig(TargetMember.AllPublic)]
    public partial class DerivedWithOverride : IHasMemberConfig
    {
        public int PublicField;
        internal int PrivateField;

        public void SetPrivateField(int v) => PrivateField = v;
        public int GetPrivateField() => PrivateField;
    }
}
