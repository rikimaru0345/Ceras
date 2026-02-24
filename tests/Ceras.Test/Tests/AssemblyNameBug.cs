
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
    public class AssemblyNameBug : TestBase
    {
        public class Container
        {
            public object Any;
        }

        // A type that is NOT registered in SchemaRegistry but used in a polymorphic field
        public class UnregisteredType
        {
            public int X;
        }

        [Fact]
        public void PolymorphicUnregisteredType_TriggersFullNameFallback()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            config.Advanced.AllowReflectionFallback = true;
            
            var ceras = new CerasSerializer(config);

            var original = new Container
            {
                Any = new UnregisteredType { X = 42 }
            };

            // This should trigger RobustTypeFormatter fallback to writing FullName
            var data = CerasRobust.Save(original, 1, ceras);
            
            // This should trigger RobustTypeFormatter fallback to reading FullName 
            // and calling SimpleTypeBinder.GetTypeFromBase
            var clone = CerasRobust.Load<Container>(data, 1, ceras);

            Assert.IsType<UnregisteredType>(clone.Any);
            Assert.Equal(42, ((UnregisteredType)clone.Any).X);
        }

        CerasSerializer CreateSerializer()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            config.Advanced.AllowReflectionFallback = true;
            return new CerasSerializer(config);
        }

        [Fact]
        public void GenericUnregisteredType_TriggersError()
        {
            var cerasSave = CreateSerializer();

            var original = new Container
            {
                Any = new List<UnregisteredType> { new UnregisteredType { X = 100 } }
            };

            var data = CerasRobust.Save(original, 1, cerasSave);
            
            var cerasLoad = CreateSerializer();
            var cloneRobust = CerasRobust.Load<Container>(data, 1, cerasLoad);

            Assert.NotNull(cloneRobust.Any);
            Assert.IsType<List<UnregisteredType>>(cloneRobust.Any);
        }

        [Fact]
        public void SerializingFunc_FieldIsSkipped()
        {
            var ceras = CreateSerializer();

            var original = new Container
            {
                Any = (Func<int, int>)(x => x * 2)
            };

            // This should NOT throw anymore, but skip the field during schema creation
            var data = CerasRobust.Save(original, 1, ceras);
            
            var clone = CerasRobust.Load<Container>(data, 1, ceras);
            Assert.Null(clone.Any);
        }
    }
}
