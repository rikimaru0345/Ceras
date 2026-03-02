
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

        CerasRobustSerializer CreateSerializer() => new CerasRobustSerializer(config =>
        {
            config.UseGeneratedFormatters();
            config.AllowReflectionFallback = true;
        });

        [Fact]
        public void SerializingFunc_FieldIsSkipped()
        {
            var ceras = CreateSerializer();

            var original = new Container
            {
                Any = (Func<int, int>)(x => x * 2)
            };

            // This should NOT throw anymore, but skip the field during schema creation
            var data = ceras.Save(original, 1);

            var clone = ceras.Load<Container>(data, 1);
            Assert.Null(clone.Any);
        }
    }
}
