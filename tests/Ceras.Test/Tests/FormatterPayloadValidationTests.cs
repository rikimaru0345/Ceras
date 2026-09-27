// The nullable contract of the payload-decoding formatters.
//
// Every case here is a hand-built payload, because the subject is the *decoder's* reaction to a slot the
// writers cannot fill: a null type name, a null base type, a null member name, a null enum name. Those
// slots used to escape as raw BCL failures - a NullReferenceException from dereferencing the missing
// type, an ArgumentNullException naming "value" or "name" - which says nothing about the file that was
// read, and is exactly the outcome the corruption contract (CorruptionContractTests) rules out. Each
// rejection case is paired with the payload it must not reject, so none of them can be satisfied by
// refusing everything.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Formatters;
using Ceras.Resolvers;

namespace Ceras.Test.FormatterPayloads
{
    public class FormatterPayloadValidationTests : TestBase
    {
        /// <summary>
        /// <c>TypeFormatter</c>'s marker vocabulary: one mode written with a +3 bias, so a null type is -1,
        /// a generic type is -2 (followed by the type definition, then the argument count and arguments)
        /// and a plain type is -3 (followed by the name). The constants are private to the formatter, so
        /// they are restated here to keep a failure naming the exact marker under test.
        /// </summary>
        const int TypeNull = -1;
        const int TypeNewGeneric = -2;
        const int TypeNewSingle = -3;
        const int TypeBias = 3;

        public enum PayloadEnum { Zero, One, Two }

        public class MemberHost
        {
            public int Field;
            public int Property { get; set; }
        }

        // ── TypeFormatter: a type reference has to name a type ────────────────────────────────

        [Fact]
        public void A_Type_Reference_Without_A_Name_Is_Corruption()
        {
            var ceras = new CerasFastSerializer();
            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(payload, TypeNewSingle, TypeBias);
            SerializerBinary.WriteString(payload, null);

            var ex = Capture(() => ReadType(ceras, payload.WrittenSpan.ToArray()));
            Assert.IsType<CerasDataCorruptionException>(ex);
        }

        [Fact]
        public void A_Generic_Type_Reference_Without_A_Base_Type_Is_Corruption()
        {
            var ceras = new CerasFastSerializer();
            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(payload, TypeNewGeneric, TypeBias);
            SerializerBinary.WriteUInt32Bias(payload, TypeNull, TypeBias);   // the base type, as a null marker
            SerializerBinary.WriteByte(payload, 0);                           // argument count, and no arguments

            // Everything the grammar asks for is present, so the missing base type is what gets rejected -
            // a truncated payload would be reported as a truncation instead and prove nothing.
            var ex = Capture(() => ReadType(ceras, payload.WrittenSpan.ToArray()));
            Assert.IsType<CerasDataCorruptionException>(ex);
        }

        [Fact]
        public void A_Real_Type_Reference_Still_Resolves()
        {
            // The counterpart for both cases above, through the very branch they guard: a closed generic
            // is written as its type definition plus its arguments, and has to come back as itself.
            var ceras = new CerasFastSerializer();
            var payload = new ArrayBufferWriter<byte>();
            TypeFormatter(ceras).Serialize(payload, typeof(List<int>));
            Assert.Equal(typeof(List<int>), ReadType(ceras, payload.WrittenSpan.ToArray()));

            var plain = new ArrayBufferWriter<byte>();
            TypeFormatter(ceras).Serialize(plain, typeof(string));
            Assert.Equal(typeof(string), ReadType(ceras, plain.WrittenSpan.ToArray()));
        }

        // ── MemberInfoFormatter: a member reference has to name a member ───────────────────────

        [Fact]
        public void A_Member_Reference_Without_A_Name_Is_Corruption()
        {
            var ceras = new CerasFastSerializer();

            var payload = new ArrayBufferWriter<byte>();
            ceras.GetFormatter<Type>().Serialize(payload, typeof(MemberHost));
            SerializerBinary.WriteByte(payload, (byte)MemberTypes.Field);
            SerializerBinary.WriteString(payload, null);   // a name no writer can produce

            var ex = Capture(() => ReadMember(ceras, FieldFormatter(ceras), payload.WrittenSpan.ToArray()));
            Assert.IsType<CerasDataCorruptionException>(ex);
        }

        [Fact]
        public void A_Real_Member_Reference_Still_Resolves()
        {
            var ceras = new CerasFastSerializer();

            var field = typeof(MemberHost).GetField(nameof(MemberHost.Field));
            var fieldPayload = new ArrayBufferWriter<byte>();
            FieldFormatter(ceras).Serialize(fieldPayload, field);

            var readField = ReadMember(ceras, FieldFormatter(ceras), fieldPayload.WrittenSpan.ToArray());
            Assert.NotNull(readField);
            Assert.Equal(field.Name, readField.Name);
            Assert.Equal(typeof(MemberHost), readField.DeclaringType);

            // The property branch, which resolves through the same lookup with a different member type.
            var property = typeof(MemberHost).GetProperty(nameof(MemberHost.Property));
            var propertyPayload = new ArrayBufferWriter<byte>();
            PropertyFormatter(ceras).Serialize(propertyPayload, property);

            var readProperty = ReadMember(ceras, PropertyFormatter(ceras), propertyPayload.WrittenSpan.ToArray());
            Assert.NotNull(readProperty);
            Assert.Equal(property.Name, readProperty.Name);
            Assert.Equal(typeof(MemberHost), readProperty.DeclaringType);
        }

        [Fact]
        public void A_Member_That_No_Longer_Exists_Reads_Back_As_Null()
        {
            // The behaviour a lookup miss has always had, and which is kept deliberately: a member that
            // was renamed or removed between writing and reading must not cost the whole object.
            var ceras = new CerasFastSerializer();

            var payload = new ArrayBufferWriter<byte>();
            ceras.GetFormatter<Type>().Serialize(payload, typeof(MemberHost));
            SerializerBinary.WriteByte(payload, (byte)MemberTypes.Field);
            SerializerBinary.WriteString(payload, "NoSuchMember");

            Assert.Null(ReadMember(ceras, FieldFormatter(ceras), payload.WrittenSpan.ToArray()));
        }

        // ── EnumAsStringFormatter: an enum value has to be named ───────────────────────────────

        [Fact]
        public void An_Enum_Value_Without_A_Name_Is_Corruption()
        {
            var ceras = EnumSerializer();
            var formatter = EnumFormatter(ceras);

            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteString(payload, null);

            var ex = Capture(() => ReadEnum(formatter, payload.WrittenSpan.ToArray()));
            Assert.IsType<CerasDataCorruptionException>(ex);
        }

        [Fact]
        public void A_Real_Enum_Name_Still_Parses()
        {
            var ceras = EnumSerializer();
            var formatter = EnumFormatter(ceras);

            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteString(payload, nameof(PayloadEnum.Two));

            Assert.Equal(PayloadEnum.Two, ReadEnum(formatter, payload.WrittenSpan.ToArray()));
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        static CerasFastSerializer EnumSerializer()
            => new CerasFastSerializer(c => c.ConfigType<PayloadEnum>().SetFormatter(new EnumAsStringFormatter<PayloadEnum>()));

        static TypeFormatter TypeFormatter(CerasSerializer ceras) => (TypeFormatter)ceras.GetSpecificFormatter(typeof(Type));

        // Constructed rather than resolved: a serializer only ever hands this formatter out for a
        // concrete member type, and FieldInfo/PropertyInfo themselves are abstract. The formatter resolves
        // the member from the declaring type it reads, so its own T only has to be a type the member can
        // be cast to.
        static MemberInfoFormatter<FieldInfo> FieldFormatter(CerasSerializer ceras) => new MemberInfoFormatter<FieldInfo>(ceras);

        static MemberInfoFormatter<PropertyInfo> PropertyFormatter(CerasSerializer ceras) => new MemberInfoFormatter<PropertyInfo>(ceras);

        static EnumAsStringFormatter<PayloadEnum> EnumFormatter(CerasSerializer ceras)
            => (EnumAsStringFormatter<PayloadEnum>)ceras.GetSpecificFormatter(typeof(PayloadEnum));

        // A SequenceReader is a ref struct, so each read has to live in a method of its own where the
        // ref locals can be passed by reference.
        static Type ReadType(CerasSerializer ceras, byte[] payload)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
            Type value = null;
            TypeFormatter(ceras).Deserialize(ref reader, ref value);
            return value;
        }

        static TMember ReadMember<TMember>(CerasSerializer ceras, MemberInfoFormatter<TMember> formatter, byte[] payload)
            where TMember : MemberInfo
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
            TMember value = null;
            formatter.Deserialize(ref reader, ref value);
            return value;
        }

        static PayloadEnum ReadEnum(EnumAsStringFormatter<PayloadEnum> formatter, byte[] payload)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
            PayloadEnum value = PayloadEnum.Zero;
            formatter.Deserialize(ref reader, ref value);
            return value;
        }

        static Exception Capture(Func<object> read)
        {
            try
            {
                read();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
    }
}
