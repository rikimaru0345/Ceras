using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.BuiltinShapes
{
    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsDtoHolder
    {
        public DateTimeOffset D;
        public int After;
    }

    /// <summary>
    /// TODO 0.5 (folded "Review later"): DateTimeOffset is consistently supported end-to-end as a
    /// fixed-width scalar (8-byte ticks + 2-byte offset minutes), following the DateTime/TimeSpan/Guid
    /// pattern. Covered as root and member on the fast path and the interpreter, with pinned bytes.
    /// </summary>
    public class DateTimeOffsetShapeTests : TestBase
    {
        const string Ctx = "BuiltinShapes";

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        static byte[] SlowPath(byte[] bytes)
        {
            var copy = (byte[])bytes.Clone();
            copy[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(copy);
            return copy;
        }

        static byte[] Payload<T>(T value)
        {
            var s = New();
            var bytes = s.Save(value, 1);
            RobustHeader.Read(bytes, out _, out _, out _, out long dataOffset, out long stringOffset, out _);
            int off = (int)dataOffset;
            SchemaSection.ReadLeb128(bytes, ref off);
            return bytes[off..(int)stringOffset];
        }

        static readonly DateTimeOffset[] Cases =
        {
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)),
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(-7)),
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(330)),   // +05:30
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(-210)),  // -03:30
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            new DateTimeOffset(DateTime.MinValue.AddDays(1), TimeSpan.FromHours(14)),
            new DateTimeOffset(DateTime.MaxValue.AddDays(-1), TimeSpan.FromHours(-14)),
            new DateTimeOffset(2020, 6, 15, 12, 30, 45, 123, TimeSpan.FromHours(9)),
        };

        [Fact]
        public void DateTimeOffset_Root_RoundTrips_Both_Paths()
        {
            var s = New();
            foreach (var value in Cases)
            {
                var bytes = s.Save(value, 1);
                Assert.Equal(value, s.Load<DateTimeOffset>(bytes, 1));
                Assert.Equal(value, s.Load<DateTimeOffset>(SlowPath(bytes), 1));
            }
        }

        [Fact]
        public void DateTimeOffset_Member_RoundTrips_Both_Paths_With_Following_Field()
        {
            var s = New();
            foreach (var value in Cases)
            {
                var bytes = s.Save(new BsDtoHolder { D = value, After = 12345 }, 1);
                var fast = s.Load<BsDtoHolder>(bytes, 1);
                var slow = s.Load<BsDtoHolder>(SlowPath(bytes), 1);
                Assert.Equal(value, fast.D);
                Assert.Equal(12345, fast.After);
                Assert.Equal(value, slow.D);
                Assert.Equal(12345, slow.After);
            }
        }

        [Fact]
        public void DateTimeOffset_Payload_Bytes_Are_Placed_Ticks_Then_OffsetMinutes()
        {
            Assert.Equal(10, Payload(default(DateTimeOffset)).Length);

            var value = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
            var expected = new byte[10];
            BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(0), value.Ticks);
            BinaryPrimitives.WriteInt16LittleEndian(expected.AsSpan(8), (short)value.Offset.TotalMinutes);
            Assert.Equal(expected, Payload(value));

            var negative = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(-210));
            var expectedNeg = new byte[10];
            BinaryPrimitives.WriteInt64LittleEndian(expectedNeg.AsSpan(0), negative.Ticks);
            BinaryPrimitives.WriteInt16LittleEndian(expectedNeg.AsSpan(8), (short)-210);
            Assert.Equal(expectedNeg, Payload(negative));
        }

        [Fact]
        public void DateTimeOffset_Registered_As_WellKnown_Scalar()
        {
            Assert.True(WellKnownTypes.TryGetId(typeof(DateTimeOffset), out int id));
            Assert.Equal(WellKnownTypes.DateTimeOffset, id);
            Assert.Equal(10, WellKnownTypes.GetFixedSize(WellKnownTypes.DateTimeOffset));
        }

        [Fact]
        public void DateTimeOffset_Raw_Serializer_RoundTrips()
        {
            var s = new CerasFastSerializer();
            foreach (var value in Cases)
            {
                var bytes = s.Serialize(value);
                Assert.Equal(value, s.Deserialize<DateTimeOffset>(bytes));
            }
        }
    }
}
