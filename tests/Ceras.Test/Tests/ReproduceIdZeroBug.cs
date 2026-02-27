using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Helpers;

namespace Ceras.Test
{
    public enum MyTestEnum { A, B, C }

    public class MyTestClass
    {
        public MyTestEnum SomeEnum;
        public int SomeInt;
    }

    [CerasSerializable]
    public class MyGeneratedClass
    {
        public MyTestEnum SomeEnum;
    }

    public class ReproduceIdZeroBug : TestBase
    {
        [Fact]
        public void Test_Enum_Field_Schema_Id_Dynamic()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            config.Advanced.AllowReflectionFallback = true;
            var ceras = new CerasSerializer(config);
            var registry = config.Robust.SchemaRegistry;

            // We simulate what happens when a class is registered but its enum fields are NOT.
            var classInfo = new SchemaTypeInfo(
                "MyTestClass", 
                Array.Empty<string>(), 
                SchemaTypeKind.Class,
                new[] { 
                    new SchemaFieldInfo("SomeEnum", Array.Empty<string>(), typeof(MyTestEnum).FullName, true),
                    new SchemaFieldInfo("SomeInt", Array.Empty<string>(), typeof(int).FullName, true)
                }, 
                0);

            registry.Register(typeof(MyTestClass), classInfo);

            var obj = new MyTestClass { SomeEnum = MyTestEnum.B, SomeInt = 42 };
            byte[] data = CerasRobust.Save(obj, 1, ceras);

            string schema = CerasRobust.DumpSchema(data);

            // If the bug is present, SomeEnum will have [ID:0] because MyTestEnum was not registered.
            bool hasBug = schema.Contains("[ID:0]  Mono  SomeEnum");
            if (hasBug)
            {
                throw new Exception("Schema dump:\n" + schema);
            }
            
            Assert.Contains("ENUM", schema);
            Assert.Contains("MyTestEnum", schema);
        }

        [Fact]
        public void Test_Enum_Field_Schema_Id_Generated()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            // Specifically disable fallback to ensure we're testing the generator's registration
            config.Advanced.AllowReflectionFallback = false;
            
            // This pulls in all generated formatters/registrations
            Ceras.Generated.GeneratedFormatters.UseFormatters(config);
            
            var ceras = new CerasSerializer(config);

            var obj = new MyGeneratedClass { SomeEnum = MyTestEnum.B };
            byte[] data = CerasRobust.Save(obj, 1, ceras);

            string schema = CerasRobust.DumpSchema(data);

            // If the generator didn't register MyTestEnum, we expect [ID:0]
            Assert.DoesNotContain("[ID:0]  Mono  SomeEnum", schema);
            
            // Also, we expect it to be an ENUM, not a CLASS/STRUCT (if it were to manage to get registered)
            Assert.Contains("ENUM  ", schema);
            Assert.Contains("MyTestEnum", schema);
        }
    }
}
