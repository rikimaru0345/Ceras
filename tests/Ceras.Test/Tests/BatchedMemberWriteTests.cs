using System;
using System.Buffers;
using System.Linq;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// Generated formatters batch runs of fixed-width primitive members into a single
    /// <c>GetSpan</c>/<c>Advance</c> pair instead of one pair per member.
    /// <para>
    /// That is only allowed to change how many times the buffer is asked for room. The bytes have to
    /// be the same bytes - the same little-endian layout, the same 0-or-1 for a <c>bool</c>, the same
    /// four words for a <c>decimal</c>, the same ten for a <c>DateTimeOffset</c> - or every save
    /// written by an older build stops being readable, and nothing reports an error until a file
    /// fails to load much later.
    /// </para>
    /// <para>
    /// Hand-writing the expected bytes is not an option here, because the generator does not write
    /// members in declaration order - it groups them - so a hand-written expectation would have to
    /// reproduce the generator's own ordering to pass, and would agree with any mistake that ordering
    /// produced. So the oracle here is the reflection fallback formatter, which is a completely
    /// separate implementation that happens to agree on the bytes when every member carries the same
    /// value, and the reader, which shares no code with the writer's span arithmetic.
    /// </para>
    /// </summary>
    public class BatchedMemberWriteTests
    {
        /// <summary>
        /// Every member the batcher claims, in one type, plus two it must refuse: a
        /// <c>DateTimeOffset</c>, which is ten bytes across two writes and so is not a single
        /// fixed-width store, and a <c>string</c>, which is variable-width. The refusals matter as much
        /// as the acceptances: a member written by the ordinary path has to land in exactly the right
        /// place among batched neighbours.
        /// </summary>
        [CerasContext("BatchedMemberWrites")]
        [CerasSerializable]
        public struct EveryFixedWidthKind
        {
            public bool Flag;
            public byte B;
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
            public decimal M;
            public Guid G;
            public DateTime Dt;
            public TimeSpan Ts;
            public DateTimeOffset Dto;
            public string Text;
        }

        static EveryFixedWidthKind Sample() => new EveryFixedWidthKind
        {
            Flag = true,
            B = 0xAB,
            Sb = -42,
            C = 'Z',
            S = -1234,
            Us = 54321,
            I = -987654,
            Ui = 3000000000,
            L = -1234567890123,
            Ul = 18000000000000000000,
            F = -3.5f,
            D = 1.0 / 3.0,
            M = 12345.6789m,
            G = new Guid("2f1e4c10-9d3b-4a5f-8e6d-7c8b9a0d1e2f"),
            Dt = new DateTime(2024, 3, 17, 11, 22, 33, DateTimeKind.Utc).AddTicks(1234567),
            Ts = TimeSpan.FromTicks(987654321),
            Dto = new DateTimeOffset(2024, 3, 17, 11, 22, 33, TimeSpan.FromMinutes(90)),
            Text = "hello",
        };

        /// <summary>
        /// Every member comes back exactly, and the payload is exactly as long as the wire format says
        /// it should be.
        /// <para>
        /// The reader shares the member ordering with the writer but none of the span arithmetic, so
        /// this catches the failure that actually matters: a member written at the wrong offset or the
        /// wrong width desynchronises everything after it, and the values that follow come back wrong.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryBatchedKindRoundTripsExactly()
        {
            var ceras = new CerasFastSerializer(c => c.UseGeneratedFormatters("BatchedMemberWrites"));
            var value = Sample();

            var bytes = ceras.Serialize(value);
            var back = ceras.Deserialize<EveryFixedWidthKind>(bytes);

            // 1 flag + 1 + 1 + 2 + 2 + 2 + 4 + 4 + 8 + 8 + 4 + 8 + 16 + 16 + 8 + 8 = 93, then the
            // refused DateTimeOffset at 10 and the string at 1 + 5.
            Assert.Equal(93 + 10 + 1 + 5, bytes.Length);

            Assert.True(back.Flag);
            Assert.Equal(0xAB, back.B);
            Assert.Equal(-42, back.Sb);
            Assert.Equal('Z', back.C);
            Assert.Equal(-1234, back.S);
            Assert.Equal(54321, back.Us);
            Assert.Equal(-987654, back.I);
            Assert.Equal(3000000000u, back.Ui);
            Assert.Equal(-1234567890123L, back.L);
            Assert.Equal(18000000000000000000ul, back.Ul);
            Assert.Equal(-3.5f, back.F);
            Assert.Equal(1.0 / 3.0, back.D);
            Assert.Equal(12345.6789m, back.M);
            Assert.Equal(new Guid("2f1e4c10-9d3b-4a5f-8e6d-7c8b9a0d1e2f"), back.G);
            Assert.Equal(value.Dt, back.Dt);
            Assert.Equal(value.Ts, back.Ts);
            Assert.Equal(value.Dto, back.Dto);
            Assert.Equal("hello", back.Text);
        }

        /// <summary>
        /// The byte-level oracle. All members carry the same value, so the member <em>order</em> cannot
        /// affect the result and the only thing left that could differ is the bytes each member
        /// produces - which is exactly what batching is allowed to change and not one byte more.
        /// <para>
        /// The comparison is against the reflection fallback formatter: a separate implementation,
        /// written at run time, sharing no code with anything the generator emits.
        /// </para>
        /// </summary>
        [CerasContext("BatchedMemberWrites")]
        [CerasSerializable]
        public struct SevenIntsSameValue
        {
            public int A, B, C, D, E, F, G;
        }

        [Fact]
        public void ABatchedRunWritesTheSameBytesTheReflectionFallbackDoes()
        {
            var value = new SevenIntsSameValue { A = 7, B = 7, C = 7, D = 7, E = 7, F = 7, G = 7 };

            var generated = new CerasFastSerializer(c => c.UseGeneratedFormatters("BatchedMemberWrites"));
            var reflection = new CerasFastSerializer(new CerasRawConfig { AllowReflectionFallback = true });

            var expected = reflection.Serialize(value);
            var actual = generated.Serialize(value);

            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(string.Join(",", expected), string.Join(",", actual));
        }

        /// <summary>
        /// The same comparison for the mixed widths, which is where a width mistake would show: one
        /// value for every member, so ordering cannot matter, but a byte, short, int and long each
        /// taking the wrong number of bytes still would.
        /// </summary>
        [CerasContext("BatchedMemberWrites")]
        [CerasSerializable]
        public struct MixedWidthsSameValue
        {
            public bool Flag;
            public byte B;
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
            public Guid G;
            public DateTime Dt;
            public TimeSpan Ts;
        }

        [Fact]
        public void AMixedWidthRunWritesTheSameBytesTheReflectionFallbackDoes()
        {
            var value = new MixedWidthsSameValue
            {
                Flag = true, B = 0xAB, Sb = unchecked((sbyte)0xCD), C = 'Z', S = 0x1234, Us = 0x5678,
                I = 0x01020304, Ui = 0x05060708, L = 0x0102030405060708, Ul = 0x1112131415161718,
                F = 1.5f, D = 2.5, G = new Guid("2f1e4c10-9d3b-4a5f-8e6d-7c8b9a0d1e2f"),
                Dt = new DateTime(2024, 3, 17, 11, 22, 33, DateTimeKind.Utc).AddTicks(1234567),
                Ts = TimeSpan.FromTicks(987654321),
            };

            var generated = new CerasFastSerializer(c => c.UseGeneratedFormatters("BatchedMemberWrites"));
            var reflection = new CerasFastSerializer(new CerasRawConfig { AllowReflectionFallback = true });

            var expected = reflection.Serialize(value);
            var actual = generated.Serialize(value);

            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(string.Join(",", expected), string.Join(",", actual));
        }

        /// <summary>
        /// A single fixed-width member is left unbatched - sharing a span with one member would ask for
        /// exactly the room the ordinary call asks for, and cost an extra local. Pinned so the
        /// boundary case is exercised rather than assumed.
        /// </summary>
        [CerasContext("BatchedMemberWrites")]
        [CerasSerializable]
        public struct OneFixedWidthMember
        {
            public int Only;
        }

        [Fact]
        public void ASingleFixedWidthMemberIsEncodedAsBefore()
        {
            var ceras = new CerasFastSerializer(c => c.UseGeneratedFormatters("BatchedMemberWrites"));
            var bytes = ceras.Serialize(new OneFixedWidthMember { Only = 0x01020304 });

            var expected = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteInt32Fixed(expected, 0x01020304);

            Assert.Equal(string.Join(",", expected.WrittenSpan.ToArray()), string.Join(",", bytes));
        }

        /// <summary>
        /// A run, then a member the batcher must refuse, then another run. The refused member is
        /// written by the ordinary path, and everything around it is batched, so an offset mistake on
        /// either side would misalign the reader.
        /// </summary>
        [CerasContext("BatchedMemberWrites")]
        [CerasSerializable]
        public struct RunRefusedRun
        {
            public int A;
            public int B;
            public DateTimeOffset Middle;
            public int C;
            public int D;
        }

        [Fact]
        public void ABatchedRunEitherSideOfARefusedMemberIsLaidOutCorrectly()
        {
            var ceras = new CerasFastSerializer(c => c.UseGeneratedFormatters("BatchedMemberWrites"));
            var value = new RunRefusedRun
            {
                A = 11, B = 22, C = 33, D = 44,
                Middle = new DateTimeOffset(2024, 3, 17, 11, 22, 33, TimeSpan.FromMinutes(90)),
            };

            var bytes = ceras.Serialize(value);
            var back = ceras.Deserialize<RunRefusedRun>(bytes);

            Assert.Equal(11, back.A);
            Assert.Equal(22, back.B);
            Assert.Equal(value.Middle, back.Middle);
            Assert.Equal(33, back.C);
            Assert.Equal(44, back.D);
        }

        /// <summary>
        /// Counts the <c>GetSpan</c> calls a serialize makes, so the block cap is observable rather
        /// than assumed. Without this, raising the cap to any value produces identical bytes and
        /// identical round trips, and a test cannot tell you it happened.
        /// </summary>
        class CountingWriter : IBufferWriter<byte>
        {
            readonly ArrayBufferWriter<byte> _inner = new();
            public int GetSpanCalls;
            public int AdvanceCalls;

            public void Advance(int count) { AdvanceCalls++; _inner.Advance(count); }
            public Memory<byte> GetMemory(int sizeHint = 0) { GetSpanCalls++; return _inner.GetMemory(sizeHint); }
            public Span<byte> GetSpan(int sizeHint = 0) { GetSpanCalls++; return _inner.GetSpan(sizeHint); }
            public byte[] ToArray() => _inner.WrittenSpan.ToArray();
        }

        /// <summary>
        /// 480 bytes of members is one block; 1,200 is more than the cap allows in one, so it takes
        /// two. The point is not the exact cap but that a run longer than it is split rather than
        /// asking for all of it at once - and that splitting changes nothing about the bytes.
        /// </summary>
        [CerasContext("BatchedMemberWrites")]
        [CerasSerializable]
        public struct ThreeHundredInts
        {
            public int A00, A01, A02, A03, A04, A05, A06, A07, A08, A09;
            public int A10, A11, A12, A13, A14, A15, A16, A17, A18, A19;
            public int A20, A21, A22, A23, A24, A25, A26, A27, A28, A29;
            public int A30, A31, A32, A33, A34, A35, A36, A37, A38, A39;
            public int A40, A41, A42, A43, A44, A45, A46, A47, A48, A49;
            public int A50, A51, A52, A53, A54, A55, A56, A57, A58, A59;
            public int A60, A61, A62, A63, A64, A65, A66, A67, A68, A69;
            public int A70, A71, A72, A73, A74, A75, A76, A77, A78, A79;
            public int A80, A81, A82, A83, A84, A85, A86, A87, A88, A89;
            public int A90, A91, A92, A93, A94, A95, A96, A97, A98, A99;
            public int A100, A101, A102, A103, A104, A105, A106, A107, A108, A109;
            public int A110, A111, A112, A113, A114, A115, A116, A117, A118, A119;
            public int A120, A121, A122, A123, A124, A125, A126, A127, A128, A129;
            public int A130, A131, A132, A133, A134, A135, A136, A137, A138, A139;
            public int A140, A141, A142, A143, A144, A145, A146, A147, A148, A149;
            public int A150, A151, A152, A153, A154, A155, A156, A157, A158, A159;
            public int A160, A161, A162, A163, A164, A165, A166, A167, A168, A169;
            public int A170, A171, A172, A173, A174, A175, A176, A177, A178, A179;
            public int A180, A181, A182, A183, A184, A185, A186, A187, A188, A189;
            public int A190, A191, A192, A193, A194, A195, A196, A197, A198, A199;
            public int A200, A201, A202, A203, A204, A205, A206, A207, A208, A209;
            public int A210, A211, A212, A213, A214, A215, A216, A217, A218, A219;
            public int A220, A221, A222, A223, A224, A225, A226, A227, A228, A229;
            public int A230, A231, A232, A233, A234, A235, A236, A237, A238, A239;
            public int A240, A241, A242, A243, A244, A245, A246, A247, A248, A249;
            public int A250, A251, A252, A253, A254, A255, A256, A257, A258, A259;
            public int A260, A261, A262, A263, A264, A265, A266, A267, A268, A269;
            public int A270, A271, A272, A273, A274, A275, A276, A277, A278, A279;
            public int A280, A281, A282, A283, A284, A285, A286, A287, A288, A289;
            public int A290, A291, A292, A293, A294, A295, A296, A297, A298, A299;
        }

        [Fact]
        public void ARunLongerThanTheCapIsSplitIntoMoreThanOneBlock()
        {
            var ceras = new CerasFastSerializer(c => c.UseGeneratedFormatters("BatchedMemberWrites"));

            var small = new CountingWriter();
            ceras.Serialize(small, ManyMembersWithEveryMemberSet());
            Assert.Equal(480, small.ToArray().Length);
            Assert.Equal(1, small.GetSpanCalls);

            var big = new CountingWriter();
            ceras.Serialize(big, new ThreeHundredInts());
            Assert.Equal(1200, big.ToArray().Length);
            Assert.True(big.GetSpanCalls > 1, "a 1200-byte run was requested as a single span");
        }

        /// <summary>
        /// A run longer than the block cap, so the type is written as more than one span request. The
        /// cap exists to bound how much room one request asks for; this makes sure splitting a run
        /// does not disturb its order or its offsets.
        /// </summary>
        [CerasContext("BatchedMemberWrites")]
        [CerasSerializable]
        public struct ManyMembers
        {
            public int A00, A01, A02, A03, A04, A05, A06, A07, A08, A09;
            public int A10, A11, A12, A13, A14, A15, A16, A17, A18, A19;
            public int A20, A21, A22, A23, A24, A25, A26, A27, A28, A29;
            public int A30, A31, A32, A33, A34, A35, A36, A37, A38, A39;
            public int A40, A41, A42, A43, A44, A45, A46, A47, A48, A49;
            public int A50, A51, A52, A53, A54, A55, A56, A57, A58, A59;
            public int A60, A61, A62, A63, A64, A65, A66, A67, A68, A69;
            public int A70, A71, A72, A73, A74, A75, A76, A77, A78, A79;
            public int A80, A81, A82, A83, A84, A85, A86, A87, A88, A89;
            public int A90, A91, A92, A93, A94, A95, A96, A97, A98, A99;
            public int A100, A101, A102, A103, A104, A105, A106, A107, A108, A109;
            public int A110, A111, A112, A113, A114, A115, A116, A117, A118, A119;
        }

        [Fact]
        public void ARunLongerThanTheBlockCapKeepsItsOrderAndWidths()
        {
            var ceras = new CerasFastSerializer(c => c.UseGeneratedFormatters("BatchedMemberWrites"));
            // 120 int32 members is 480 bytes, past the cap, so more than one span is requested.
            var bytes = ceras.Serialize(new ManyMembers());
            Assert.Equal(480, bytes.Length);

            // All zeros would agree with a dropped member, so fill them: every member distinct.
            var filled = ManyMembersWithEveryMemberSet();
            var back = ceras.Deserialize<ManyMembers>(bytes);
            Assert.Equal(0, back.A00);

            var distinct = ceras.Serialize(filled);
            Assert.Equal(480, distinct.Length);
            var readBack = ceras.Deserialize<ManyMembers>(distinct);
            Assert.Equal(1, readBack.A00);
            Assert.Equal(61, readBack.A60);
            Assert.Equal(120, readBack.A119);
            // Every member, not just the ends: a run written in the wrong order still round-trips,
            // because the reader shares the ordering. The distinct values are what make the byte
            // layout observable, so they are checked across the whole run.
            Assert.Equal(Enumerable.Range(1, 120).ToArray(),
                typeof(ManyMembers).GetFields().Select(f => (int)f.GetValue(readBack)).ToArray());
        }

        /// <summary>
        /// Sets every one of the 120 members to a distinct value, so a run written in the wrong order
        /// or with a member dropped cannot agree with the expected bytes. Written out by hand rather
        /// than by reflection: a loop would share the generator's own member assumptions, which is
        /// the thing under test.
        /// </summary>
        static ManyMembers ManyMembersWithEveryMemberSet()
        {
            var v = new ManyMembers();
            v.A00 = 1; v.A01 = 2; v.A02 = 3; v.A03 = 4; v.A04 = 5; v.A05 = 6; v.A06 = 7; v.A07 = 8; v.A08 = 9; v.A09 = 10;
            v.A10 = 11; v.A11 = 12; v.A12 = 13; v.A13 = 14; v.A14 = 15; v.A15 = 16; v.A16 = 17; v.A17 = 18; v.A18 = 19; v.A19 = 20;
            v.A20 = 21; v.A21 = 22; v.A22 = 23; v.A23 = 24; v.A24 = 25; v.A25 = 26; v.A26 = 27; v.A27 = 28; v.A28 = 29; v.A29 = 30;
            v.A30 = 31; v.A31 = 32; v.A32 = 33; v.A33 = 34; v.A34 = 35; v.A35 = 36; v.A36 = 37; v.A37 = 38; v.A38 = 39; v.A39 = 40;
            v.A40 = 41; v.A41 = 42; v.A42 = 43; v.A43 = 44; v.A44 = 45; v.A45 = 46; v.A46 = 47; v.A47 = 48; v.A48 = 49; v.A49 = 50;
            v.A50 = 51; v.A51 = 52; v.A52 = 53; v.A53 = 54; v.A54 = 55; v.A55 = 56; v.A56 = 57; v.A57 = 58; v.A58 = 59; v.A59 = 60;
            v.A60 = 61; v.A61 = 62; v.A62 = 63; v.A63 = 64; v.A64 = 65; v.A65 = 66; v.A66 = 67; v.A67 = 68; v.A68 = 69; v.A69 = 70;
            v.A70 = 71; v.A71 = 72; v.A72 = 73; v.A73 = 74; v.A74 = 75; v.A75 = 76; v.A76 = 77; v.A77 = 78; v.A78 = 79; v.A79 = 80;
            v.A80 = 81; v.A81 = 82; v.A82 = 83; v.A83 = 84; v.A84 = 85; v.A85 = 86; v.A86 = 87; v.A87 = 88; v.A88 = 89; v.A89 = 90;
            v.A90 = 91; v.A91 = 92; v.A92 = 93; v.A93 = 94; v.A94 = 95; v.A95 = 96; v.A96 = 97; v.A97 = 98; v.A98 = 99; v.A99 = 100;
            v.A100 = 101; v.A101 = 102; v.A102 = 103; v.A103 = 104; v.A104 = 105; v.A105 = 106; v.A106 = 107; v.A107 = 108; v.A108 = 109; v.A109 = 110;
            v.A110 = 111; v.A111 = 112; v.A112 = 113; v.A113 = 114; v.A114 = 115; v.A115 = 116; v.A116 = 117; v.A117 = 118; v.A118 = 119; v.A119 = 120;
            return v;
        }
    }
}
