using System;
using System.Collections.Generic;

namespace Ceras.Test
{
    using System.Linq;
    using Formatters;
    using Resolvers;
    using System.Runtime.InteropServices;
    using Xunit;
    using Ceras.Helpers;
    using System.Runtime.CompilerServices;

    public class Blitting : TestBase
    {
        // Types that the reinterpret formatter should be able to handle
        // Arrays are handled by "ReinterpretArrayFormatter" of course
        static Type[] _blittableTypes =
        {
            typeof(int),
            typeof(bool),
            typeof(byte),
            typeof(sbyte),
            typeof(ulong),

            typeof(float),
            typeof(double),

            typeof(int[]),
            typeof(bool[]),
            typeof(float[]),
            typeof(double[]),

            typeof(Vector3),
            typeof(Half2),

            typeof(Vector3[]),

            typeof(DayOfWeek), // The base type "Enum" can't be blitted, but an actual enum can!
        };

        // Types where neither ReinterpretFormatter nor ReinterpretArrayFormatter
        // should be used!
        static Type[] _nonBlittableTypes =
        {
            // Pointers and arrays
            typeof(IntPtr),
            typeof(UIntPtr),
            typeof(IntPtr[]),
            typeof(UIntPtr[]),

            // A struct declaring a C# `fixed` buffer. It USED to be in _blittableTypes, on the argument
            // that nothing else can encode it — but something else could, and what it wrote was 29 bytes
            // for a 64-byte value, with the save succeeding and `Third[1..2]` and all 24 bytes of
            // `Sixth` loading back as zeros. The blit was the only lossless encoding only because the
            // other path was broken, so the shape is refused on every path instead; see
            // FixedBufferStructTests and Formatters/FixedBufferGuard.
            typeof(BigStruct),
            typeof(BigStruct[]),

            // decimal.IsPrimitive is true, but its CLR memory order is not the documented payload order
            // (decimal.GetBits: lo, mid, hi, flags), so it must never be reinterpreted — neither alone
            // nor inside a decimal[], a KeyValuePair<decimal,_> or any struct that contains one.
            typeof(decimal),
            typeof(decimal[]),
            typeof(KeyValuePair<decimal, int>),

            // Reference types
            typeof(TestBase),
            typeof(List<int>),
            typeof(List<object>),
            typeof(object),
            typeof(Type),
            typeof(Type).GetType(),
            typeof(string),
            
            // generics can never be blitted, they are ".IsAutoLayout" by definition
            typeof(ValueTuple<byte, uint>),
            typeof(ValueTuple<int, float, double, decimal>),
            typeof(ValueTuple<BigStruct, Vector3, byte>),

            typeof(ValueTuple<byte, uint>[]),
            typeof(ValueTuple<int, float, double, decimal>[]),
            typeof(ValueTuple<BigStruct, Vector3, byte>[]),

            typeof(ValueTuple<byte, string>),
            typeof(ValueTuple<byte, Type>),

            typeof(ValueTuple<object>),
            typeof(ValueTuple<string>[]),

            typeof(ValueTuple<int[]>),
            typeof(ValueTuple<byte[], uint>),
            typeof(ValueTuple<byte, uint>[]),
            typeof(ValueTuple<BigStruct, Vector3[], byte>),

            
            // Multi dimensional arrays can not be blitted because it would make the code extremely brittle (offset of the first element depends on the array-rank)
            typeof(string[]),
            typeof(string[,]),
            typeof(string[,,]),
            typeof(string[,,,]),
            typeof(int[,]),
            typeof(int[,,]),
            typeof(Vector3[,,,,]),
            typeof(byte[,,]),
        };

        // Formatters Ceras is allowed to use to handle "blittable types"
        static Type[] _blitFormattersOpenTypes =
        {
            typeof(ReinterpretFormatter<>),
            typeof(ReinterpretArrayFormatter<>),

            typeof(BoolFormatter),
            typeof(ByteFormatter),
            typeof(SByteFormatter),

            typeof(CharFormatter),
            typeof(Int16Formatter),
            typeof(UInt16Formatter),

            typeof(Int32Formatter),
            typeof(UInt32Formatter),
            typeof(Int64Formatter),
            typeof(UInt64Formatter),

            typeof(FloatFormatter),
            typeof(DoubleFormatter),
            typeof(DecimalFormatter),
        };


        [Fact]
        public void BlittableTypesUseCorrectFormatter()
        {
            var config = new CerasRawConfig();
            
            var ceras = new CerasFastSerializer(config);

            foreach (var t in _blittableTypes)
            {
                var formatter = ceras.GetSpecificFormatter(t);

                var ft = formatter.GetType();
                if (ft.IsGenericType)
                    ft = ft.GetGenericTypeDefinition();

                Assert.Contains(ft, _blitFormattersOpenTypes);
            }

            foreach (var t in _nonBlittableTypes)
            {
                var ft = FormatterTypeOrRefusal(ceras, t);

                // A type Ceras refuses outright has no formatter to be the wrong one. That is a stronger
                // statement than "not blitted", and it is what a fixed-buffer struct now gets.
                if (ft == null) continue;

                if (ft.IsGenericType)
                    if (ft.GetGenericTypeDefinition() == typeof(ReinterpretFormatter<>) ||
                        ft.GetGenericTypeDefinition() == typeof(ReinterpretArrayFormatter<>))
                        throw new Exception("Computed formatter for type " + t.Name + " is ReinterpretFormatter, that's wrong!");
            }
        }

        /// <summary>
        /// The formatter Ceras picks for <paramref name="t"/>, or null when it refuses the type outright.
        /// The refusal is looked for in the whole chain, because the reflection fallback is constructed
        /// through reflection, which wraps what its constructor threw.
        /// </summary>
        static Type FormatterTypeOrRefusal(CerasSerializer ceras, Type t)
        {
            try
            {
                return ceras.GetSpecificFormatter(t).GetType();
            }
            catch (Exception e) when (Innermost<Ceras.Exceptions.CerasSchemaException>(e) != null)
            {
                return null;
            }
        }

        /// <summary>
        /// This used to be <c>BigStructBlittedCorrectly</c>: a full round trip of a 64-byte struct whose
        /// two fixed buffers (<c>Third[3]</c> and <c>Sixth[24]</c>, the latter holding two Vector3s
        /// written through a pointer) came back byte for byte, because <see cref="ReinterpretFormatter{T}"/>
        /// copied the whole image.
        /// <para>
        /// It is now a refusal, deliberately, and this is the test that records why. The same type through
        /// the member-by-member path wrote 29 bytes of that image: one byte for each of the two buffers, so
        /// <c>Third[1..2]</c> and 23 of the 24 bytes of <c>Sixth</c> — including almost all of both
        /// Vector3s — came back as zeros, with the save succeeding and the stream still aligned. So the
        /// blit was lossless only because the competing encoding was broken, and a shape that is
        /// describable in one mode and lossy in another is not a state worth keeping: the fast path
        /// <c>UseReinterpretFormatter</c> is exactly the one-line config change that swapped a correct save
        /// for a corrupted one.
        /// </para>
        /// <para>
        /// The value is still built in full below, so what is now unreachable is visible: everything the
        /// old round trip proved — the byte-for-byte equality and the exact 64-byte payload — is data this
        /// test can no longer write, which is the point of refusing it.
        /// </para>
        /// </summary>
        [Fact]
        public unsafe void BigStructIsRefused_Because_Only_Blitting_It_Was_Lossless()
        {
            BigStruct bigStruct = FullBigStruct();
            var bigStructSize = Marshal.SizeOf(typeof(BigStruct));
            Assert.Equal(64, bigStructSize);

            bigStruct.Fourth = new Half4(rngShort, rngShort, rngShort, rngShort);

            // 27 of the 64 bytes the old round trip carried live in a fixed buffer, and no member-by-member
            // encoding can see how many bytes that is.
            var exception = Assert.ThrowsAny<Exception>(() => new CerasFastSerializer().Serialize(FullBigStruct()));
            var refusal = Innermost<Ceras.Exceptions.CerasSchemaException>(exception);
            Assert.True(refusal != null, $"Expected a CerasSchemaException, but got: {exception}");

            // Both buffers are named, with the byte counts, so the user can see exactly what is at stake.
            Assert.Contains("Third", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("Sixth", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("3 bytes", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("24 bytes", refusal.Message, StringComparison.Ordinal);
        }

        static T Innermost<T>(Exception e) where T : Exception
        {
            for (var current = e; current != null; current = current.InnerException)
                if (current is T match) return match;
            return null;
        }

        /// <summary>
        /// The value the old round trip carried, in a method of its own: a struct with a fixed buffer
        /// cannot have its address taken inside a lambda, and the assertion has to be one.
        /// </summary>
        static unsafe BigStruct FullBigStruct()
        {
            BigStruct bigStruct = new BigStruct();

            bigStruct.First = 0xff;
            bigStruct.Second = 0.123456789;

            bigStruct.Third[0] = 0x5E;
            bigStruct.Third[1] = 0xE5;
            bigStruct.Third[2] = 0x99;

            bigStruct.Fifth = '@';

            var v3Size = Marshal.SizeOf(typeof(Vector3));
            *((Vector3*)(bigStruct.Sixth + v3Size * 0)) = new Vector3(1, 2, 3);
            *((Vector3*)(bigStruct.Sixth + v3Size * 1)) = new Vector3(4, 5, 6);

            bigStruct.Eighth = 0x11_22_33_44___55_66_77_88;
            return bigStruct;
        }

        [Fact]
        public void BlittingEnums()
        {
            // Usually we don't use the reinterpret-formatter for enum members so we can minimize the size (varint encoding)

            var stressTestValues = new[]
            {
                long.MinValue, long.MaxValue, -1, 0, 1, 5, 1000, 255, 256,
                rngByte, rngByte, rngByte,
                rngLong, rngLong, rngLong, rngLong, rngLong, rngLong,
            };


            var config = new CerasRawConfig();
            config.OnConfigNewType = t => t.CustomResolver = (c, t2) => c.GetFormatterResolver<ReinterpretFormatterResolver>().GetFormatter(t2);
            //config.OnConfigNewType = t => t.CustomFormatter = (IFormatter)Activator.CreateInstance(typeof(EnumFormatterUnsafe<>).MakeGenericType(t.Type));
            var ceras = new CerasFastSerializer(config);

            var typesToTest = new[]
            {
                typeof(TestEnumInt8),
                typeof(TestEnumUInt8),
                typeof(TestEnumInt16),
                typeof(TestEnumUInt16),
                typeof(TestEnumInt64),
                typeof(TestEnumUInt64),
            };
            
            var serializeMethod = typeof(CerasFastSerializer).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).First(m => m.Name == nameof(CerasFastSerializer.Serialize) && m.GetParameters().Length == 1);
            var deserializeMethod = typeof(CerasFastSerializer).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).First(m => m.Name == nameof(CerasFastSerializer.Deserialize) && m.GetParameters().Length == 1);
            

            foreach (var t in typesToTest)
            {
                Type baseType = t.GetEnumUnderlyingType();
                int expectedSize = Marshal.SizeOf(baseType);
                
                var values = Enum.GetValues(t).Cast<object>().Concat(stressTestValues.Cast<object>());
                
                foreach (var v in values)
                {
                    var obj = Enum.ToObject(t, v);

                    // We must call Serialize<T>, and we can't use <object> because that would embed the type information
                    var data = (byte[])serializeMethod.MakeGenericMethod(t).Invoke(ceras, new object[] { obj });
                    
                    Assert.True(data.Length == expectedSize);

                    var cloneObj = deserializeMethod.MakeGenericMethod(t).Invoke(ceras, new object[] { data });

                    Assert.True(obj.Equals(cloneObj));
                }
            }

            
            Assert.True(ceras.Serialize(TestEnumInt8.a).Length == 1);
            Assert.True(ceras.Serialize(TestEnumUInt8.a).Length == 1);
            
            Assert.True(ceras.Serialize(TestEnumInt16.a).Length == 2);
            Assert.True(ceras.Serialize(TestEnumUInt16.a).Length == 2);

            Assert.True(ceras.Serialize(TestEnumInt64.a).Length == 8);
            Assert.True(ceras.Serialize(TestEnumUInt64.a).Length == 8);
        }

        [Fact]
        public unsafe void CouldCopyValueTupleDirectly()
        {
            (int, decimal, Vector3, bool, long) t = ValueTuple.Create(5, 2.441M, new Vector3(1, 2, 3), false, (long)69419572);
            var s1 = ReflectionHelper.GetSize(t.GetType());
            var s2 = ReflectionHelper.UnsafeGetSize(t.GetType());

            var target = new byte[s2];
            Unsafe.Copy(Unsafe.AsPointer(ref target[0]), ref t);

            (int, decimal, Vector3, bool, long) clone = default;
            Unsafe.Copy(ref clone, Unsafe.AsPointer(ref target[0]));

            Assert.True(clone.Item3.Y == 2);
            Assert.Equal(t, clone);
        }

        enum TestEnumInt8 : sbyte { a = 123, b, c }
        enum TestEnumUInt8 : byte { a = 123, b, c }

        enum TestEnumInt16 : short { a = 123, b, c }
        enum TestEnumUInt16 : ushort { a = 123, b, c }

        enum TestEnumInt64 : long { a = 123, b, c }
        enum TestEnumUInt64 : ulong { a = 123, b, c }

    }
}
