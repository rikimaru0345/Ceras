using System;
using Xunit;
using Ceras;

namespace Ceras.Test
{
    public class ImprovedErrorMessageTest : TestBase
    {
        public struct UnserializableType
        {
            public int Value;
            public string Name;
        }

        public class MyClass
        {
            public UnserializableType MyMember;
        }

        [Fact]
        public void NoFormatter_IncludesContextInException()
        {
            var config = new CerasRawConfig();
            config.AllowReflectionFallback = false;
            var ceras = new CerasSerializer(config);

            var member = typeof(MyClass).GetField(nameof(MyClass.MyMember));
            
            ceras.PushContext(typeof(MyClass), member);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => ceras.GetSpecificFormatter(typeof(UnserializableType)));
                
                Console.WriteLine("DEBUG MESSAGE START");
                Console.WriteLine(ex.Message);
                Console.WriteLine("DEBUG MESSAGE END");

                Assert.Contains("No formatter found for type", ex.Message);
                Assert.Contains("UnserializableType", ex.Message);
                Assert.Contains("encountered while processing member 'MyMember' in type 'Ceras.Test.ImprovedErrorMessageTest+MyClass'", ex.Message);
            }
            finally
            {
                ceras.PopContext();
            }
        }

        public class ParentClass
        {
            public UnserializableType Child;
        }

        class FakeGeneratedFormatter : Ceras.Formatters.IFormatter<ParentClass>
        {
            // PrepareFormatter will try to inject this
            public Ceras.Formatters.IFormatter<UnserializableType> _childFormatter;

            public void Serialize(System.Buffers.IBufferWriter<byte> writer, ParentClass value) {}
            public void Deserialize(ref System.Buffers.SequenceReader<byte> reader, ref ParentClass value) {}
            public void SerializeUntyped(System.Buffers.IBufferWriter<byte> writer, object value) {}
            public void DeserializeUntyped(ref System.Buffers.SequenceReader<byte> reader, object container, System.Reflection.MemberInfo member) {}
        }

        [Fact]
        public void NoFormatter_DI_IncludesContextInException()
        {
            var config = new CerasRawConfig();
            config.AllowReflectionFallback = false;
            var ceras = new CerasSerializer(config);

            var formatter = new FakeGeneratedFormatter();

            var ex = Assert.Throws<InvalidOperationException>(() => ((CerasSerializer)ceras).PrepareFormatter(formatter));

            Assert.Contains("No formatter found for type", ex.Message);
            Assert.Contains("UnserializableType", ex.Message);
            Assert.Contains("encountered while processing a member in type 'Ceras.Test.ImprovedErrorMessageTest+ParentClass'", ex.Message);
        }
    }
}
