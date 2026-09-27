// Enum values in the schema are raw 64-bit bits; the underlying type id carries the signedness. These
// cover both declared forms (a generator-emitted schema and a structurally derived one), the whole
// unsigned range, negative signed values, and a schema change that has to be migrated.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.EnumBits
{
    /// <summary>Declared in the context, so the generator emits its schema: an enum above
    /// <c>long.MaxValue</c> used to be unprintable into the long-valued schema entry.</summary>
    public enum ULongEnum : ulong
    {
        Zero = 0,
        AboveLongMax = (ulong)long.MaxValue + 1,

        // The historical writer called this member OldMax; the alias is what lets the migration map it,
        // and the value has to survive as raw bits.
        [PreviousName("OldMax")]
        Max = ulong.MaxValue,
    }

    [CerasContext("EnumBits")]
    [CerasSerializable]
    public class ULongEnumHolder
    {
        [PreviousName("OldName")]
        public ULongEnum Value;
        public int After;
    }

    /// <summary>Not marked <c>[CerasSerializable]</c>, so the registry derives its schema from CLR
    /// metadata: the structural path, which used to overflow through <c>Convert.ToInt64</c>.</summary>
    public enum UShortEnum : ushort
    {
        Low = 1,
        High = ushort.MaxValue,
    }

    public enum SignedEnum : long
    {
        Neg = -5,
        Zero = 0,
        Pos = long.MaxValue,
    }

    [CerasContext("EnumBits")]
    [CerasSerializable]
    public class StructuralEnumHolder
    {
        public UShortEnum Small;
        public SignedEnum Signed;
        public int After;
    }

    public class EnumBitsTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("EnumBits"));

        [Fact]
        public void Generated_Ulong_Enum_RoundTrips_Every_Value()
        {
            var serializer = Generated();
            foreach (var value in new[] { ULongEnum.Zero, ULongEnum.AboveLongMax, ULongEnum.Max })
            {
                var bytes = serializer.Save(new ULongEnumHolder { Value = value, After = 9 }, 1);

                foreach (var clone in new[] { serializer.Load<ULongEnumHolder>(bytes, 1), serializer.Load<ULongEnumHolder>(SlowPath(bytes), 1) })
                {
                    Assert.Equal(value, clone.Value);
                    Assert.Equal(9, clone.After);
                }
            }
        }

        [Fact]
        public void Generated_Ulong_Enum_Schema_Carries_The_Raw_Bits()
        {
            var bytes = Generated().Save(new ULongEnumHolder { Value = ULongEnum.Max, After = 0 }, 1);
            var dump = CerasRobustSerializer.DumpSchema(bytes);

            // The dump must show the magnitude (the underlying type is unsigned), not a wrapped negative.
            Assert.Contains("Max = 18446744073709551615", dump);
            Assert.Contains("AboveLongMax = 9223372036854775808", dump);
        }

        [Fact]
        public void Structural_Enums_RoundTrip_Unsigned_And_Signed()
        {
            var serializer = Generated();
            var value = new StructuralEnumHolder { Small = UShortEnum.High, Signed = SignedEnum.Neg, After = 3 };

            var bytes = serializer.Save(value, 1);
            foreach (var clone in new[] { serializer.Load<StructuralEnumHolder>(bytes, 1), serializer.Load<StructuralEnumHolder>(SlowPath(bytes), 1) })
            {
                Assert.Equal(UShortEnum.High, clone.Small);
                Assert.Equal(SignedEnum.Neg, clone.Signed);
                Assert.Equal(3, clone.After);
            }
        }

        [Fact]
        public void Dump_Shows_A_Signed_Enum_Negative_And_An_Unsigned_One_As_Magnitude()
        {
            var bytes = Generated().Save(new StructuralEnumHolder { Small = UShortEnum.High, Signed = SignedEnum.Neg }, 1);
            var dump = CerasRobustSerializer.DumpSchema(bytes);

            Assert.Contains("Neg = -5", dump);      // signed underlying type -> signed display
            Assert.Contains("High = 65535", dump);  // unsigned underlying type -> magnitude
        }

        /// <summary>
        /// A renamed member has to migrate through the enum value map, and the value travels as raw
        /// bits, so an unsigned member above <c>long.MaxValue</c> survives the mapping.
        /// </summary>
        [Fact]
        public void Renamed_Ulong_Member_Is_Mapped_Through_The_Changed_Schema()
        {
            // Build a file whose schema gives the member the OLD name; the current CLR type has the
            // renamed member. Both carry the same raw bits.
            var oldRegistry = new CerasRobustConfig().SchemaRegistry;
            oldRegistry.Register(typeof(ULongEnumHolder), new SchemaTypeInfo(
                "Ceras.Test.EnumBits.ULongEnumHolder", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("OldName", Array.Empty<string>(), "Ceras.Test.EnumBits.ULongEnum", true) },
                0, new[]
                {
                    new SchemaEnumValueInfo("OldMax", ulong.MaxValue),
                    new SchemaEnumValueInfo("OldAbove", (ulong)long.MaxValue + 1),
                }, underlyingTypeName: "System.UInt64"));
            oldRegistry.Register(typeof(ULongEnum), new SchemaTypeInfo(
                "Ceras.Test.EnumBits.ULongEnum", Array.Empty<string>(), SchemaTypeKind.Enum, null, 0,
                new[]
                {
                    new SchemaEnumValueInfo("Max", ulong.MaxValue),
                    new SchemaEnumValueInfo("AboveLongMax", (ulong)long.MaxValue + 1),
                }, underlyingTypeName: "System.UInt64"));
            oldRegistry.TryGetId(typeof(ULongEnumHolder), out int oldId);

            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);
            var intern = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, oldRegistry, intern);
            int schemaStringOffset = offset;
            intern.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)oldId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteUInt64Fixed(payload, ulong.MaxValue);   // the old "OldMax" value

            var data = payload.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;
            int stringTableStart = offset;
            new StringInternTable().Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);

            // The current member is named "Value" and declared as ULongEnum, so the migration maps the old
            // "OldName" field onto it, and the renamed enum member OldMax -> Max keeps its raw bits.
            var loaded = Generated().Load<ULongEnumHolder>(buffer[..offset], 1);
            Assert.Equal(ULongEnum.Max, loaded.Value);
        }
    }
}
