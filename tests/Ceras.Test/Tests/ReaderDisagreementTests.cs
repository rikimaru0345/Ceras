using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.ReaderDisagreement
{
    public enum RdEnum : long { Zero = 0, One = 1, Neg = -5 }

    [CerasContext("ReaderDisagreement")]
    [CerasSerializable]
    public class RdAllKindsHolder
    {
        public bool B;
        public byte By;
        public sbyte Sb;
        public char C;
        public short S;
        public ushort Us;
        public int I;
        public uint Ui;
        public long L;
        public ulong Ul;
        public float F;
        public double D;
        public string Str;
        public RdEnum E;
        public int? Maybe;
        public DateTime When;
        public TimeSpan Span;
        public Guid G;
        public decimal Dec;
        public DateTimeOffset Dto;
    }

    [CerasContext("ReaderDisagreement")]
    [CerasSerializable]
    public class RdTimeHolder { public DateTime When; }

    [CerasContext("ReaderDisagreement")]
    [CerasSerializable]
    public class RdClass { public int X; public string S; }

    [CerasContext("ReaderDisagreement")]
    [CerasSerializable]
    public class RdPolyBase { public int B; }

    [CerasContext("ReaderDisagreement")]
    [CerasSerializable]
    public class RdPolyDerived : RdPolyBase { public string D; }

    [CerasContext("ReaderDisagreement")]
    [CerasSerializable]
    public class RdTypeHolder { public Type T; public int After; }

    /// <summary>
    /// Regression coverage: the migration interpreter (slow path, forced by a
    /// fingerprint mismatch) must agree with the generated/formatter readers for ordinary values.
    /// Each case asserts the value on the matching-layout fast path and on the interpreter path,
    /// and the scalar-layout tests pin the exact payload bytes so a shared writer/reader mistake
    /// cannot hide behind a passing roundtrip.
    /// </summary>

    // ── The shapes the 0.5 coverage never reached ─────────────────────────────────────────
    //
    // decimal and DateTimeOffset are admitted fixed-width scalars, so every place another admitted
    // scalar is exercised must work for them too: the structural Nullable path, collection element-id
    // plumbing, a user struct that contains one, a polymorphic slot, a *removed* field (which exercises
    // SkipValue against the 16- and 10-byte claims), and a payload too short to hold the value.

    [CerasSerializable]
    public struct RdMoney
    {
        public decimal Amount;
        public DateTimeOffset When;
        public int Tail;
    }

    [CerasContext("ReaderDisagreement")]
    [CerasSerializable]
    public class RdScalarShapes
    {
        public decimal? MaybeDec;
        public decimal? NullDec;
        public DateTimeOffset? MaybeDto;
        public DateTimeOffset? NullDto;
        public decimal[] DecArray;
        public List<decimal> DecList;
        public Dictionary<decimal, int> DecDict;
        public RdMoney Money;
        public object Poly;
        public int After;
    }

    [CerasSerializable]
    public class RdOnlyAfter
    {
        public int After;
    }

    [CerasContext("ReaderDisagreement")]
    [CerasSerializable]
    public class RdOnlyMaybeDec { public decimal? Maybe; }

    [CerasContext("ReaderDisagreement")]
    [CerasSerializable]
    public class RdOnlyMaybeDto { public DateTimeOffset? Maybe; }

    [CerasContext("ReaderDisagreement")]
    [CerasSerializable]
    public class RdRemovedFieldRoot
    {
        public RdOnlyAfter Kept;
    }

    public class ReaderDisagreementTests : TestBase
    {
        const string Ctx = "ReaderDisagreement";

        // The allowed structural root shapes, declared up front for the finite manifest.
        static CerasRobustSerializer New() => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Ctx);
            foreach (var root in StructuralRoots)
                c.DeclareRoot(root);
        });

        /// <summary>Every root shape this context may serialize, in one place.</summary>
        static readonly Type[] StructuralRoots =
        {
            typeof(int?), typeof(int[]), typeof(string), typeof(List<string>), typeof(List<int>),
            typeof(Guid), typeof(bool), typeof(byte), typeof(sbyte), typeof(char), typeof(short),
            typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float),
            typeof(double), typeof(decimal), typeof(string), typeof(TimeSpan), typeof(DateTime),
            typeof(DateTimeOffset), typeof(RdEnum), typeof(decimal?), typeof(DateTimeOffset?),
            typeof(decimal[]), typeof(decimal?[]), typeof(List<decimal>),
            typeof(Dictionary<decimal, int>), typeof(Type),
        };

        static void AssertRoot<T>(T value)
        {
            var s = New();
            var bytes = s.Save(value, 1);
            T fast = s.Load<T>(bytes, 1);
            T slow = s.Load<T>(SlowPath(bytes), 1);
            Assert.Equal(value, fast);
            Assert.Equal(value, slow);
        }

        static byte[] Payload<T>(T value)
        {
            var s = New();
            var bytes = s.Save(value, 1);
            RobustHeader.Read(bytes, out _, out _, out _, out long dataOffset, out long stringOffset, out _);
            int off = (int)dataOffset;
            SchemaSection.ReadLeb128(bytes, ref off); // skip the root type id
            return bytes[off..(int)stringOffset];
        }

        // ── Roots: every shape that reaches the interpreter ─────────────────────────

        [Fact] public void Root_Int()    => AssertRoot(42);
        [Fact] public void Root_Int_Minus() => AssertRoot(-1);
        [Fact] public void Root_Bool()   => AssertRoot(true);
        [Fact] public void Root_Byte()   => AssertRoot((byte)200);
        [Fact] public void Root_SByte()  => AssertRoot((sbyte)-2);
        [Fact] public void Root_Char()   => AssertRoot('A');
        [Fact] public void Root_Short()  => AssertRoot((short)-2);
        [Fact] public void Root_UShort() => AssertRoot((ushort)0x1234);
        [Fact] public void Root_UInt()   => AssertRoot(300u);
        [Fact] public void Root_Long()   => AssertRoot(-1L);
        [Fact] public void Root_ULong()  => AssertRoot(1UL);
        [Fact] public void Root_Float()  => AssertRoot(1.5f);
        [Fact] public void Root_Double() => AssertRoot(-2.25);
        [Fact] public void Root_String() => AssertRoot("hello");
        [Fact] public void Root_String_Null() => AssertRoot<string>(null);
        [Fact] public void Root_TimeSpan() => AssertRoot(TimeSpan.FromTicks(-123456789));
        [Fact] public void Root_Guid()   => AssertRoot(new Guid("00112233-4455-6677-8899-aabbccddeeff"));
        [Fact] public void Root_Enum()   => AssertRoot(RdEnum.Neg);
        [Fact] public void Root_Nullable_Value() => AssertRoot<int?>(99);
        [Fact] public void Root_Nullable_Null()  => AssertRoot<int?>(null);
        [Fact]
        public void Root_Class()
        {
            var s = New();
            var bytes = s.Save(new RdClass { X = 7, S = "x" }, 1);
            var fast = s.Load<RdClass>(bytes, 1);
            var slow = s.Load<RdClass>(SlowPath(bytes), 1);
            Assert.Equal(7, fast.X);
            Assert.NotNull(slow);
            Assert.Equal(7, slow.X);
            Assert.Equal("x", slow.S);
        }

        [Fact]
        public void Root_Array()
        {
            var s = New();
            var bytes = s.Save(new[] { 1, 2, 3 }, 1);
            Assert.Equal(new[] { 1, 2, 3 }, s.Load<int[]>(bytes, 1));
            Assert.Equal(new[] { 1, 2, 3 }, s.Load<int[]>(SlowPath(bytes), 1));
        }

        [Fact]
        public void Root_List()
        {
            var s = New();
            var bytes = s.Save(new List<int> { 4, 5 }, 1);
            Assert.Equal(new[] { 4, 5 }, s.Load<List<int>>(bytes, 1));
            Assert.Equal(new[] { 4, 5 }, s.Load<List<int>>(SlowPath(bytes), 1));
        }

        [Fact]
        public void Root_Object_Polymorphic()
        {
            object value = new RdPolyDerived { B = 1, D = "derived" };
            var s = New();
            var bytes = s.Save(value, 1);
            var fast = s.Load<object>(bytes, 1);
            var slow = s.Load<object>(SlowPath(bytes), 1);
            var f = Assert.IsType<RdPolyDerived>(fast);
            var sl = Assert.IsType<RdPolyDerived>(slow);
            Assert.Equal(1, f.B);
            Assert.Equal("derived", sl.D);
        }

        // ── System.Type values: the interpreter must reconstruct closed generics/arrays ──

        [Fact]
        public void Root_Type_ClosedGeneric_And_Array()
        {
            foreach (Type value in new[] { typeof(int), typeof(List<int>), typeof(int[]), typeof(Dictionary<string, int>) })
            {
                var s = New();
                var bytes = s.Save(value, 1);
                Assert.Equal(value, s.Load<Type>(bytes, 1));
                Assert.Equal(value, s.Load<Type>(SlowPath(bytes), 1));
            }
        }

        [Fact]
        public void Type_As_Member_RoundTrips_And_Keeps_Alignment()
        {
            var s = New();
            var bytes = s.Save(new RdTypeHolder { T = typeof(List<string>), After = 77 }, 1);
            var fast = s.Load<RdTypeHolder>(bytes, 1);
            var slow = s.Load<RdTypeHolder>(SlowPath(bytes), 1);
            foreach (var clone in new[] { fast, slow })
            {
                Assert.NotNull(clone);
                Assert.Equal(typeof(List<string>), clone.T);
                Assert.Equal(77, clone.After);
            }
        }

        [Fact]
        public void Type_Member_Null_RoundTrips()
        {
            var s = New();
            var bytes = s.Save(new RdTypeHolder { T = null, After = 3 }, 1);
            var slow = s.Load<RdTypeHolder>(SlowPath(bytes), 1);
            Assert.Null(slow.T);
            Assert.Equal(3, slow.After);
        }

        [Fact]
        public void Object_Holding_Type_Is_Reconstructed_And_Keeps_Alignment()
        {
            object value = typeof(List<string>);
            var s = New();
            var bytes = s.Save(value, 1);
            var fast = s.Load<object>(bytes, 1);
            var slow = s.Load<object>(SlowPath(bytes), 1);
            Assert.Equal(typeof(List<string>), fast);
            Assert.Equal(typeof(List<string>), slow);
        }

        // ── DateTime as root and as member: UTC / local / unspecified / boundaries ───

        static readonly DateTime[] DateTimeCases =
        {
            new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Local),
            new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
            DateTime.MinValue,
            DateTime.MaxValue,
            new DateTime(1, DateTimeKind.Utc),
            new DateTime(DateTime.MaxValue.Ticks, DateTimeKind.Local),
        };

        [Fact]
        public void DateTime_As_Root_Preserves_Value_And_Kind_On_Both_Paths()
        {
            foreach (var value in DateTimeCases)
            {
                var s = New();
                var bytes = s.Save(value, 1);
                var fast = s.Load<DateTime>(bytes, 1);
                var slow = s.Load<DateTime>(SlowPath(bytes), 1);
                Assert.Equal(value, fast);
                Assert.Equal(value, slow);
                Assert.Equal(value.Kind, fast.Kind);
                Assert.Equal(value.Kind, slow.Kind);
                Assert.Equal(value.Ticks, slow.Ticks);
            }
        }

        [Fact]
        public void DateTime_As_Member_Preserves_Value_And_Kind_On_Both_Paths()
        {
            foreach (var value in DateTimeCases)
            {
                var s = New();
                var bytes = s.Save(new RdTimeHolder { When = value }, 1);
                var fast = s.Load<RdTimeHolder>(bytes, 1);
                var slow = s.Load<RdTimeHolder>(SlowPath(bytes), 1);
                Assert.Equal(value, fast.When);
                Assert.Equal(value, slow.When);
                Assert.Equal(value.Kind, slow.When.Kind);
                Assert.Equal(value.Ticks, slow.When.Ticks);
            }
        }

        [Fact]
        public void All_Scalar_Members_RoundTrip_On_Both_Paths()
        {
            var g = new Guid("00112233-4455-6677-8899-aabbccddeeff");
            var when = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            var dto = new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.FromMinutes(330));
            var value = new RdAllKindsHolder
            {
                B = true,
                By = 0xAB,
                Sb = -2,
                C = 'A',
                S = -2,
                Us = 0x1234,
                I = -123456,
                Ui = 300,
                L = -1L,
                Ul = 1UL,
                F = 1.5f,
                D = -2.25,
                Str = "member",
                E = RdEnum.Neg,
                Maybe = 99,
                When = when,
                Span = TimeSpan.FromTicks(-123456789),
                G = g,
                Dec = 42.5m,
                Dto = dto,
            };

            var s = New();
            var bytes = s.Save(value, 1);
            var fast = s.Load<RdAllKindsHolder>(bytes, 1);
            var slow = s.Load<RdAllKindsHolder>(SlowPath(bytes), 1);

            foreach (var clone in new[] { fast, slow })
            {
                Assert.NotNull(clone);
                Assert.True(clone.B);
                Assert.Equal((byte)0xAB, clone.By);
                Assert.Equal((sbyte)-2, clone.Sb);
                Assert.Equal('A', clone.C);
                Assert.Equal((short)-2, clone.S);
                Assert.Equal((ushort)0x1234, clone.Us);
                Assert.Equal(-123456, clone.I);
                Assert.Equal(300u, clone.Ui);
                Assert.Equal(-1L, clone.L);
                Assert.Equal(1UL, clone.Ul);
                Assert.Equal(1.5f, clone.F);
                Assert.Equal(-2.25, clone.D);
                Assert.Equal("member", clone.Str);
                Assert.Equal(RdEnum.Neg, clone.E);
                Assert.Equal(99, clone.Maybe);
                Assert.Equal(when, clone.When);
                Assert.Equal(DateTimeKind.Utc, clone.When.Kind);
                Assert.Equal(TimeSpan.FromTicks(-123456789), clone.Span);
                Assert.Equal(42.5m, clone.Dec);
                Assert.Equal(dto, clone.Dto);
                Assert.Equal(TimeSpan.FromMinutes(330), clone.Dto.Offset);
                Assert.Equal(g, clone.G);
            }
        }

        // ── Expected payload bytes (little-endian, every primitive fixed-width) ──

        [Fact]
        public void Scalar_Root_Payload_Bytes_Are_Pinned()
        {
            Assert.Equal(new byte[] { 0x2A, 0, 0, 0 }, Payload(42));                    // fixed 4-byte LE
            Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, Payload(-1));            // fixed 4-byte LE
            Assert.Equal(new byte[] { 0x01 }, Payload(true));
            Assert.Equal(new byte[] { 0x00 }, Payload(false));
            Assert.Equal(new byte[] { 0xAB }, Payload((byte)0xAB));
            Assert.Equal(new byte[] { 0xFE }, Payload((sbyte)-2));
            Assert.Equal(new byte[] { 0x41, 0x00 }, Payload('A'));
            Assert.Equal(new byte[] { 0xFE, 0xFF }, Payload((short)-2));
            Assert.Equal(new byte[] { 0x34, 0x12 }, Payload((ushort)0x1234));
            Assert.Equal(new byte[] { 0x2C, 0x01, 0, 0 }, Payload(300u));               // fixed 4-byte LE
            Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, Payload(-1L));
            Assert.Equal(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0 }, Payload(1UL));
            Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, Payload(1.0f));         // IEEE-754 1.0f
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F }, Payload(1.0d));
            Assert.Equal(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0 }, Payload(TimeSpan.FromTicks(1)));
            Assert.Equal(new byte[16], Payload(Guid.Empty));
            Assert.Equal(new byte[] { 0x01, 0, 0, 0 }, Payload("first"));               // string-table index 1, fixed 4-byte
            Assert.Equal(new byte[] { 0x00, 0, 0, 0 }, Payload<string>(null));          // index 0 marks null

            // decimal: the four decimal.GetBits words (lo, mid, hi, flags), each little-endian —
            // deliberately NOT the CLR memory order (_flags, _hi, _lo, _mid).
            Assert.Equal(new byte[]
            {
                0xA9, 0x01, 0x00, 0x00,   // lo    = 425
                0x00, 0x00, 0x00, 0x00,   // mid   = 0
                0x00, 0x00, 0x00, 0x00,   // hi    = 0
                0x00, 0x00, 0x01, 0x00,   // flags = 0x00010000 (scale 1)
            }, Payload(42.5m));

            // DateTimeOffset: 8-byte wall-clock ticks, then the offset in minutes.
            var dto = new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.FromMinutes(330));
            var expectedDto = new byte[10];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(expectedDto, dto.Ticks);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(expectedDto.AsSpan(8), 330);
            Assert.Equal(expectedDto, Payload(dto));
        }

        [Fact]
        public void DateTime_Payload_Bits_Are_ToBinary()
        {
            // Unspecified: ToBinary == ticks.
            Assert.Equal(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0 }, Payload(new DateTime(1, DateTimeKind.Unspecified)));

            // UTC: ToBinary sets the Utc kind flag (bit 62 => 0x40 in the top byte).
            Assert.Equal(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0x40 }, Payload(new DateTime(1, DateTimeKind.Utc)));

            // Local: ToBinary stores UTC-adjusted ticks plus the Local kind flag; the exact bits
            // depend on the machine time zone, so pin them to ToBinary rather than a fixed literal.
            var local = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Local);
            Assert.Equal(BitConverter.GetBytes(local.ToBinary()), Payload(local));
        }

        // ── Malformed / truncated payloads must be rejected, not defaulted ────────────

        /// <summary>
        /// Rebuilds a save with its data section shortened to <paramref name="newLength"/> bytes,
        /// keeping every header offset and the CRC consistent, so the decoder reaches the payload.
        /// </summary>
        static byte[] TruncateDataSection(byte[] bytes, int newLength)
        {
            RobustHeader.Read(bytes, out _, out _, out long schemaStringOffset, out long dataOffset, out long stringOffset, out _);
            int oldLength = (int)(stringOffset - dataOffset);
            Assert.True(newLength <= oldLength);
            int remove = oldLength - newLength;

            var result = new byte[bytes.Length - remove];
            Array.Copy(bytes, 0, result, 0, (int)dataOffset + newLength);
            Array.Copy(bytes, (int)stringOffset, result, (int)dataOffset + newLength, bytes.Length - (int)stringOffset);
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(RobustHeader.StringTableOffsetOffset), stringOffset - remove);
            FixRobustChecksum(result);
            return result;
        }

        [Fact]
        public void Truncated_Int_Root_Is_Rejected()
        {
            var s = New();
            var bytes = s.Save(42, 1);
            var truncated = TruncateDataSection(bytes, 1); // keep the root id, drop the value

            Assert.ThrowsAny<Exception>(() => s.Load<int>(truncated, 1));
            Assert.ThrowsAny<Exception>(() => s.Load<int>(SlowPath(truncated), 1));
        }

        [Fact]
        public void Truncated_String_Root_Is_Rejected()
        {
            var s = New();
            var bytes = s.Save("hello", 1);
            var truncated = TruncateDataSection(bytes, 1);

            Assert.ThrowsAny<Exception>(() => s.Load<string>(truncated, 1));
            Assert.ThrowsAny<Exception>(() => s.Load<string>(SlowPath(truncated), 1));
        }

        [Fact]
        public void Truncated_Class_Root_Is_Rejected()
        {
            var s = New();
            var bytes = s.Save(new RdClass { X = 7, S = "hello" }, 1);
            var truncated = TruncateDataSection(bytes, 2); // reference marker only, no fields

            Assert.ThrowsAny<Exception>(() => s.Load<RdClass>(truncated, 1));
            Assert.ThrowsAny<Exception>(() => s.Load<RdClass>(SlowPath(truncated), 1));
        }

        [Fact]
        public void OutOfRange_String_Table_Index_Is_Rejected()
        {
            var s = New();
            var bytes = s.Save("hello", 1);
            RobustHeader.Read(bytes, out _, out _, out _, out long dataOffset, out long stringOffset, out _);
            int off = (int)dataOffset;
            SchemaSection.ReadLeb128(bytes, ref off); // root id
            bytes[off] = 0x7F;                        // string index far beyond the table
            FixRobustChecksum(bytes);

            Assert.ThrowsAny<Exception>(() => s.Load<string>(bytes, 1));
            Assert.ThrowsAny<Exception>(() => s.Load<string>(SlowPath(bytes), 1));
        }

        // ── decimal / DateTimeOffset: the shapes the admitted-scalar coverage never reached ───

        [Fact]
        public void Scalar_Shapes_RoundTrip_On_Both_Paths()
        {
            var dto = new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.FromMinutes(330));
            var value = new RdScalarShapes
            {
                MaybeDec = 42.5m,
                NullDec = null,
                MaybeDto = dto,
                NullDto = null,
                DecArray = new[] { 1.25m, -3m },
                DecList = new List<decimal> { 7m, 8m },
                DecDict = new Dictionary<decimal, int> { { 9m, 1 } },
                Money = new RdMoney { Amount = 11.5m, When = dto, Tail = 3 },
                Poly = 13.75m,
                After = 77,
            };

            var s = New();
            var bytes = s.Save(value, 1);

            foreach (var clone in new[] { s.Load<RdScalarShapes>(bytes, 1), s.Load<RdScalarShapes>(SlowPath(bytes), 1) })
            {
                Assert.Equal(42.5m, clone.MaybeDec);          // the structural Nullable path, present
                Assert.Null(clone.NullDec);
                Assert.Equal(dto, clone.MaybeDto);
                Assert.Null(clone.NullDto);
                Assert.Equal(new[] { 1.25m, -3m }, clone.DecArray);
                Assert.Equal(new List<decimal> { 7m, 8m }, clone.DecList);
                Assert.Equal(1, clone.DecDict[9m]);
                Assert.Equal(11.5m, clone.Money.Amount);       // a user struct containing both
                Assert.Equal(dto, clone.Money.When);
                Assert.Equal(3, clone.Money.Tail);
                Assert.Equal(13.75m, clone.Poly);              // a polymorphic slot
                Assert.Equal(77, clone.After);                // the stream stayed aligned
            }
        }

        [Fact]
        public void Scalar_Shape_Payload_Bytes_Are_Pinned()
        {
            var s = New();
            var dto = new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.FromMinutes(330));

            // A nullable decimal: one presence byte, then the 16 GetBits bytes.
            var maybePayload = PayloadOf(s.Save(new RdOnlyMaybeDec { Maybe = 42.5m }, 1));
            // A monomorphic value-type member is written with no reference marker: the payload is the
            // root id, the root's own marker, the presence byte, then the 16 GetBits bytes.
            int offset = SkipRootIdAndMarker(maybePayload);
            Assert.Equal(1, maybePayload[offset++]);           // presence
            Assert.Equal(new byte[] { 0xA9, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0 },
                maybePayload.Skip(offset).Take(16).ToArray());
            Assert.Equal(20, maybePayload.Length);

            // A nullable DateTimeOffset: presence byte, then 8 ticks + 2 offset minutes.
            var dtoPayload = PayloadOf(s.Save(new RdOnlyMaybeDto { Maybe = dto }, 1));
            offset = SkipRootIdAndMarker(dtoPayload);
            Assert.Equal(1, dtoPayload[offset++]);
            var expectedDto = new byte[10];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(expectedDto, dto.Ticks);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(expectedDto.AsSpan(8), 330);
            Assert.Equal(expectedDto, dtoPayload.Skip(offset).Take(10).ToArray());
        }

        /// <summary>
        /// A removed field is skipped by its declared fixed size, so a removed <c>decimal</c> (16 bytes)
        /// and a removed <c>DateTimeOffset</c> (10 bytes) must be consumed exactly — the following
        /// member is the only thing that can prove it.
        /// </summary>
        [Fact]
        public void Removed_Decimal_And_DateTimeOffset_Fields_Are_Skipped_By_Their_Fixed_Sizes()
        {
            var oldRegistry = new CerasRobustConfig().SchemaRegistry;
            oldRegistry.Register(typeof(RdRemovedFieldRoot), new SchemaTypeInfo(
                "Ceras.Test.ReaderDisagreement.RdRemovedFieldRoot", Array.Empty<string>(), SchemaTypeKind.Class,
                new[]
                {
                    new SchemaFieldInfo("GoneDec", Array.Empty<string>(), "System.Decimal", true),
                    new SchemaFieldInfo("GoneDto", Array.Empty<string>(), "System.DateTimeOffset", true),
                    new SchemaFieldInfo("Kept", Array.Empty<string>(), "Ceras.Test.ReaderDisagreement.RdOnlyAfter", true),
                }, 0));
            oldRegistry.Register(typeof(RdOnlyAfter), new SchemaTypeInfo(
                "Ceras.Test.ReaderDisagreement.RdOnlyAfter", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("After", Array.Empty<string>(), "System.Int32", true) }, 0));
            oldRegistry.TryGetId(typeof(RdRemovedFieldRoot), out int rootId);

            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);
            var intern = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, oldRegistry, intern);
            int schemaStringOffset = offset;
            intern.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)rootId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteDecimal(payload, 42.5m);                                    // 16 bytes
            SerializerBinary.WriteDateTimeOffset(payload, new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.Zero)); // 10
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteInt32Fixed(payload, 5);                                   // RdOnlyAfter.After

            var data = payload.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;
            int stringTableStart = offset;
            new StringInternTable().Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);

            var loaded = New().Load<RdRemovedFieldRoot>(buffer[..offset], 1);
            Assert.Equal(5, loaded.Kept.After);
        }

        /// <summary>
        /// A payload that ends before a fixed-width scalar holds is corruption, not a default value.
        /// The header offsets stay consistent here, so only the reader can notice.
        /// </summary>
        [Fact]
        public void Scalar_That_Does_Not_Fit_The_Payload_Is_Rejected()
        {
            var s = New();
            var bytes = s.Save(42.5m, 1);
            RobustHeader.Read(bytes, out _, out _, out _, out long dataStart, out long stringTable, out _);

            foreach (var keep in new[] { 2, 8 })
            {
                var truncated = (byte[])bytes.Clone();
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                    truncated.AsSpan(RobustHeader.StringTableOffsetOffset), (int)dataStart + keep);
                FixRobustChecksum(truncated);

                var ex = Record.Exception(() => s.Load<decimal>(truncated, 1));
                Assert.IsAssignableFrom<Ceras.Exceptions.CerasDataCorruptionException>(ex);
            }
        }

        static byte[] PayloadOf(byte[] bytes)
        {
            RobustHeader.Read(bytes, out _, out _, out _, out long dataStart, out long stringTable, out _);
            return bytes.Skip((int)dataStart).Take((int)(stringTable - dataStart)).ToArray();
        }

        static int SkipRootIdAndMarker(byte[] payload)
        {
            int offset = 0;
            SchemaSection.ReadLeb128(payload, ref offset);
            return offset + 1;
        }

    }
}
