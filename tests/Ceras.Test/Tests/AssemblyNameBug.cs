
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

        CerasSerializer CreateSerializer()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            Ceras.Generated.RobustFormatters.UseRobustFormatters(config);
            config.Advanced.AllowReflectionFallback = true;
            return new CerasSerializer(config);
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
