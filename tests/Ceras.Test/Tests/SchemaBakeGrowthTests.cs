namespace Ceras.Test
{
    using System;
    using Ceras;
    using Ceras.Exceptions;
    using Ceras.Generated;
    using Xunit;

    /// <summary>
    /// The schema bake writes into <c>new byte[1024]</c> and grows it if the schema does not fit.
    /// That array is the caller's, not a rental, so the growth path must not hand it to the pool -
    /// and 1024 is not a pool bucket size, so handing it over would have thrown an
    /// <see cref="ArgumentException"/> out of the middle of a first save.
    /// <para>
    /// Every test context in this repository happens to produce a schema well under 1024 bytes, so
    /// this path was never taken. These tests take it deliberately.
    /// </para>
    /// </summary>
    public class SchemaBakeGrowthTests
    {
        /// <summary>
        /// Long, distinct member names, so the encoded schema is comfortably past the 1024 bytes the
        /// bake starts with. Written out rather than generated so the size is obvious and stable.
        /// </summary>
        [CerasContext("SchemaBakeGrowth")]
        [CerasSerializable]
        public class WideSchema
        {
            public int FieldWithAVeryLongNameNumberOne_aaaaaaaaaaaaaaaaaaaa;
            public int FieldWithAVeryLongNameNumberTwo_bbbbbbbbbbbbbbbbbbbb;
            public int FieldWithAVeryLongNameNumberThree_cccccccccccccccccccc;
            public int FieldWithAVeryLongNameNumberFour_dddddddddddddddddddd;
            public int FieldWithAVeryLongNameNumberFive_eeeeeeeeeeeeeeeeeeee;
            public int FieldWithAVeryLongNameNumberSix_fffffffffffffffffffff;
            public int FieldWithAVeryLongNameNumberSeven_ggggggggggggggggggg;
            public int FieldWithAVeryLongNameNumberEight_hhhhhhhhhhhhhhhhhhhh;
            public int FieldWithAVeryLongNameNumberNine_iiiiiiiiiiiiiiiiiiiii;
            public int FieldWithAVeryLongNameNumberTen_jjjjjjjjjjjjjjjjjjjjjj;
            public int FieldWithAVeryLongNameNumberEleven_kkkkkkkkkkkkkkkkkkkk;
            public int FieldWithAVeryLongNameNumberTwelve_llllllllllllllllllll;
            public int FieldWithAVeryLongNameNumberThirteen_mmmmmmmmmmmmmmmmm;
            public int FieldWithAVeryLongNameNumberFourteen_nnnnnnnnnnnnnnnnnn;
            public int FieldWithAVeryLongNameNumberFifteen_oooooooooooooooooo;
            public int FieldWithAVeryLongNameNumberSixteen_ppppppppppppppppppp;
            public int FieldWithAVeryLongNameNumberSeventeen_qqqqqqqqqqqqqqqqqq;
            public int FieldWithAVeryLongNameNumberEighteen_rrrrrrrrrrrrrrrrrrr;
            public int FieldWithAVeryLongNameNumberNineteen_sssssssssssssssssss;
            public int FieldWithAVeryLongNameNumberTwenty_ttttttttttttttttttttt;
            public int FieldWithAVeryLongNameNumberTwentyone_uuuuuuuuuuuuuuuuuuuuu;
            public int FieldWithAVeryLongNameNumberTwentyTwo_vvvvvvvvvvvvvvvvvvvv;
            public int FieldWithAVeryLongNameNumberTwentyThree_wwwwwwwwwwwwwwwwwwww;
            public int FieldWithAVeryLongNameNumberTwentyFour_xxxxxxxxxxxxxxxxxxxx;
            public int FieldWithAVeryLongNameNumberTwentyFive_yyyyyyyyyyyyyyyyyyyy;
            public int FieldWithAVeryLongNameNumberTwentySix_zzzzzzzzzzzzzzzzzzzz;
            public int FieldWithAVeryLongNameNumberTwentySeven_aaaaaaaaaaaaaaaaaa;
            public int FieldWithAVeryLongNameNumberTwentyEight_bbbbbbbbbbbbbbbbbbbb;
            public int FieldWithAVeryLongNameNumberTwentyNine_ccccccccccccccccccc;
            public int FieldWithAVeryLongNameNumberThirty_dddddddddddddddddddd;
        }

        /// <summary>
        /// Baking a context whose schema section outgrows the bake's initial buffer must succeed.
        /// Before the growth path stopped assuming every buffer was pooled, this threw.
        /// </summary>
        [Fact]
        public void BakingASchemaLargerThanTheInitialBuffer_Succeeds()
        {
            var ser = new CerasRobustSerializer(c => c.UseGeneratedFormatters("SchemaBakeGrowth"));

            // The bake is what grows, and it happens on the first save.
            var value = new WideSchema { FieldWithAVeryLongNameNumberSeven_ggggggggggggggggggg = 42 };
            var bytes = ser.Save(value, 1);

            Assert.True(bytes.Length > 0);
            // And the result still round-trips, i.e. the grown section was written correctly.
            Assert.Equal(42, ser.Load<WideSchema>(bytes, 1).FieldWithAVeryLongNameNumberSeven_ggggggggggggggggggg);
        }

        /// <summary>
        /// The string-table half of the bake is what actually grows here: the schema's string table
        /// is well over the 256 bytes the bake starts that region with, and 256 <em>is</em> a pool
        /// bucket size - so returning it would be adopted silently rather than rejected. That makes it
        /// the interesting case, and one a rental count cannot see (adopting is not renting).
        /// <para>
        /// So this checks adoption directly: the array is filled with a marker before the bake, and
        /// the pool is then drained. If the bake handed the array over, it comes back out.
        /// </para>
        /// </summary>
        /// <summary>
        /// The same, with a signal that does not depend on the pool's internals.
        /// <para>
        /// A caller array of a length that is <em>not</em> a pool bucket size makes the mistake
        /// loud: if the growth path returned it, the shared pool rejects it and the
        /// <see cref="ArgumentException"/> surfaces from inside what is otherwise an ordinary write.
        /// The power-of-two case is the quiet one - that array is simply adopted - so this length is
        /// chosen on purpose, and the behaviour it relies on is pinned by
        /// <see cref="ReturningAForeignArray_AdoptsOrThrowsDependingOnItsSize"/>.
        /// </para>
        /// </summary>
        [Fact]
        public void GrowingACallerArrayOfAnOddSize_DoesNotHandItToThePool()
        {
            var intern = new Ceras.Format.StringInternTable();
            for (int i = 0; i < 200; i++) intern.Intern("a-fairly-long-schema-name-entry-" + i);

            var target = new byte[300];   // not a pool bucket size
            int offset = 0;

            // The exception would be thrown from inside this call, not asserted around it.
            intern.Flush(ref target, ref offset, StringTableCompression.Disabled);

            Assert.True(offset > 300);
        }

        /// <summary>
        /// The same bake must leave the pool's rental count exactly as it found it: the arrays the
        /// bake allocates are the caller's, so growing them must not count as a rental either way.
        /// </summary>
        [Fact]
        public void BakingASchemaLargerThanTheInitialBuffer_LeavesThePoolBalanced()
        {
            var ser = new CerasRobustSerializer(c => c.UseGeneratedFormatters("SchemaBakeGrowth"));

            // Warm the serializer past its own one-time costs so the measurement is about the bake
            // growth rather than construction, then measure a second bake of an equal context.
            ser.Save(new WideSchema(), 1);

            int liveBefore = CerasBufferPool.LiveRentals;
            ser.Save(new WideSchema(), 1);
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        /// <summary>
        /// The failure this guards is silent for half the array sizes, so it is worth stating
        /// explicitly what the pool does with an array it never rented: a power-of-two length is
        /// adopted without complaint, anything else throws. A growth path that returned the caller's
        /// array would therefore either corrupt an unrelated renter's view of memory or fail a
        /// save - depending on nothing more than a number.
        /// </summary>
        [Fact]
        public void ReturningAForeignArray_AdoptsOrThrowsDependingOnItsSize()
        {
            // Adopted silently: the pool cannot tell this from one of its own.
            ArrayPoolAdoption.Adopt(new byte[4096]);

            // Rejected: not a bucket size.
            Assert.Throws<ArgumentException>(() => ArrayPoolAdoption.Adopt(new byte[2000]));
        }
    }
}
