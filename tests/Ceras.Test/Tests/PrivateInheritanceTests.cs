using System;
using System.Collections.Generic;
using Xunit;

namespace Ceras.Test.Tests
{
    public class PrivateInheritanceTests : TestBase
    {
        SerializerConfig CreateSgConfig()
        {
            var config = new SerializerConfig();
            Ceras.Generated.GeneratedFormatters.UseFormatters(config);
            return config;
        }

        [Fact]
        public void PrivateField_Inherited_RoundTripsCorrectly()
        {
            var config = CreateSgConfig();
            var obj = new DerivedWithPrivateBase();
            obj.SetBaseValue(42);
            obj.DerivedValue = 100;
            
            var clone = Clone(obj, config);
            
            Assert.Equal(42, clone.GetBaseValue());
            Assert.Equal(100, clone.DerivedValue);
        }
    }

    [GenerateFormatter]
    [MemberConfig(TargetMember.All)]
    public partial class BaseWithPrivate
    {
        int _privateBaseField;
        
        public void SetBaseValue(int v) => _privateBaseField = v;
        public int GetBaseValue() => _privateBaseField;
    }

    [GenerateFormatter]
    [MemberConfig(TargetMember.All)]
    public partial class DerivedWithPrivateBase : BaseWithPrivate
    {
        public int DerivedValue;
    }
}
