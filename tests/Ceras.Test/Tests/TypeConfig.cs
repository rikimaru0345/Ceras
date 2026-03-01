using System;
using System.Collections.Generic;

namespace Ceras.Test
{
    using Formatters;
    using Xunit;
    using System.Buffers;

    public class TypeConfig : TestBase
    {
        [Fact]
        public void CanGenerateDebugReports()
        {
            var ceras = new CerasSerializer();

            var reportBool = ceras.GenerateSerializationDebugReport(typeof(bool));
            var reportType = ceras.GenerateSerializationDebugReport(typeof(Type));
            var reportString = ceras.GenerateSerializationDebugReport(typeof(string));

            var reportList = ceras.GenerateSerializationDebugReport(typeof(List<int>));
            var reportTuple = ceras.GenerateSerializationDebugReport(typeof(Tuple<string, int>));
            var reportValueTuple = ceras.GenerateSerializationDebugReport(typeof(ValueTuple<string, int>));
            var reportPerson = ceras.GenerateSerializationDebugReport(typeof(Issue25.Adult));

        }

        [Fact]
        public void CanNotConfigurePrimitives()
        {
            // Changing any settings for "Serialization Primitives" should not be allowed
            // String, Type, int, ...

            CerasRawConfig config = new CerasRawConfig();

            HashSet<Type> primitiveTypes = new HashSet<Type>
            {
                typeof(Type),
                typeof(byte),
                typeof(int),
                typeof(float),
                typeof(string),
            };

            bool configGotCalled = false;

            config.OnConfigNewType = t =>
            {
                if (primitiveTypes.Contains(t.Type))
                    configGotCalled = true;
            };

            // Configuring primitives should not be possible
            foreach (var t in primitiveTypes)
                ExpectException(() => config.ConfigType(t));

            // Enum is not a real type (it's an abstract base class)
            ExpectException(() => config.ConfigType(typeof(Enum)));


            if (configGotCalled)
                throw new Exception("on config new type should not be called for 'serialization primitives'");
        }

        [Fact]
        public void CustomFormatterForEnum()
        {
            var config = new CerasRawConfig();
            config.ConfigType<DayOfWeek>().CustomFormatter = new DayOfWeekFormatter();

            var ceras = new CerasSerializer(config);
            
            Assert.True(ceras.Deserialize<DayOfWeek>(ceras.Serialize(DayOfWeek.Sunday)) == DayOfWeek.Sunday);
            Assert.True(ceras.Deserialize<DayOfWeek>(ceras.Serialize(DayOfWeek.Monday)) == DayOfWeek.Monday);
            Assert.True(ceras.Deserialize<DayOfWeek>(ceras.Serialize(DayOfWeek.Saturday)) == DayOfWeek.Saturday);
            Assert.True(ceras.Deserialize<DayOfWeek>(ceras.Serialize((DayOfWeek)591835)) == (DayOfWeek)591835);

        }

        [Fact]
        public void CtorTest()
        {
            var obj = new ConstructorTest(5);
            var ceras = new CerasSerializer();

            // Ceras is now smart enough to find the constructor automatically 
            // because 'int x' parameter matches 'public int x' field.
            var data = ceras.Serialize(obj);
            var clone = ceras.Deserialize<ConstructorTest>(data);
            
            Assert.Equal(obj.x, clone.x);
        }



        static void ExpectException(Action f)
        {
            try
            {
                f();
                throw new InvalidOperationException("The given method should have thrown an exception!");
            }
            catch
            {
            }
        }
    }

    class DayOfWeekFormatter : FormatterBase<DayOfWeek>
    {
        public override void Serialize(IBufferWriter<byte> writer, DayOfWeek value)
        {
            SerializerBinary.WriteUInt64(writer, (ulong)value);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref DayOfWeek value)
        {
            value = (DayOfWeek)SerializerBinary.ReadUInt64(ref reader);
        }
    }

    class ErrorFormatter<T> : FormatterBase<T>
    {
        public override void Serialize(IBufferWriter<byte> writer, T value) => Throw();
        public override void Deserialize(ref SequenceReader<byte> reader, ref T value) => Throw();

        void Throw() => throw new InvalidOperationException("This shouldn't happen");
    }

    class ConstructorTest
    {
        public int x;

        public ConstructorTest(int x)
        {
            this.x = x;
        }
    }

}
