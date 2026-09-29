// The widening half of the old-to-current plan: `MigrationPlanBuilder.DetectCoercion` decides which
// conversion a member's two types need, and `MigrationPlanBuilder.Coerce` performs it.
//
// These are unit tests of a two-line switch, and that is the point: the conversion is a closed set of
// five widenings, and a member added to `CoercionKind` without an arm in `Coerce` is a *silent* no-op —
// a migration the plan accepts and the reader then performs as a copy. Nothing else in the suite can
// see that, because every reader only ever asks for the kinds the enum already has. So the first test
// below pins every member of the enum against the conversion it is supposed to perform, and the table
// it uses is checked against the enum's own size.
//
// The end-to-end coverage is deliberately separate and deliberately smaller than this file. A real
// v5 fixture only exercises one of the five: `SaveGame.bin` and `RelicVault.bin` widen an `int`
// member to a `long`, and no checked-in historical fixture retypes a member `short -> int`,
// `int -> float`, `int -> double` or `float -> double`. That gap is a property of the fixture
// corpus, not of this code, and it is why the four remaining widenings are pinned here rather than
// end to end.

using System;
using Xunit;
using Ceras.Format;
using Ceras.Generated;
using Ceras.Test.HistoricalConstruction;
using Ceras.Test.HistoricalFixtures;

namespace Ceras.Test
{
    public class MigrationCoercionTests
    {
        /// <summary>
        /// The one closed widening set, as (kind, the file's type id, the source CLR type, the target
        /// CLR type, a value of the source type, and what widening that value must produce). Kept as a
        /// table rather than as a theory so the size check below can compare it with the enum.
        /// <para>
        /// The two rows carrying 16 777 217 are why the table is spelled out rather than generated: it
        /// is the first integer a <c>float</c> cannot represent and a <c>double</c> represents exactly,
        /// so the same input under two conversions that look alike is what tells "widened" apart from
        /// "narrowed on the way".
        /// </para>
        /// </summary>
        static readonly (CoercionKind Kind, int OldTypeId, Type Source, Type Target, object Value, object Widened)[] DefinedWidenings =
        {
            (CoercionKind.Int32ToInt64, WellKnownTypes.Int32, typeof(int),    typeof(long),   1234,           1234L),
            (CoercionKind.Int16ToInt32, WellKnownTypes.Int16, typeof(short),  typeof(int),    (short)-300,    -300),
            (CoercionKind.Int32ToSingle, WellKnownTypes.Int32, typeof(int),   typeof(float),  16_777_217,     16_777_216f),
            (CoercionKind.Int32ToDouble, WellKnownTypes.Int32, typeof(int),   typeof(double), 16_777_217,     16_777_217d),
            (CoercionKind.SingleToDouble, WellKnownTypes.Single, typeof(float), typeof(double), 0.1f,          0.10000000149011612d),
        };

        // ── The set is closed, and the two halves agree ──────────────────────────────────────────

        [Fact]
        public void Every_CoercionKind_The_Enum_Declares_Is_Performed_By_Exactly_One_Widening()
        {
            // The guard that matters most in this file. `Coerce` is a switch with a `_ => value` arm, so
            // a kind added to the enum without one is not a compile error and not a test failure
            // anywhere else in the suite: every caller asks for the five that exist. This fails if the
            // table and the enum stop describing the same set, whichever side moved.
            Assert.Equal(Enum.GetValues<CoercionKind>().Length, DefinedWidenings.Length + 1 /* None */);

            foreach (var (kind, oldTypeId, source, target, value, widened) in DefinedWidenings)
            {
                // The decision half: these two types need this conversion.
                Assert.Equal(kind, MigrationPlanBuilder.DetectCoercion(oldTypeId, target));

                // The applying half: the value really becomes the target type, with the expected value.
                var coerced = MigrationPlanBuilder.Coerce(value, kind, target);
                Assert.IsType(target, coerced);
                Assert.Equal(widened, coerced);
                Assert.NotEqual(value.GetType(), coerced!.GetType());
                Assert.IsType(source, value);
            }
        }

        [Fact]
        public void A_Target_Of_The_Same_Type_As_The_File_Recorded_Needs_No_Conversion()
        {
            // The ordinary case, and the one every same-layout cross-check exercises: the file's own
            // section already declares the current type, so nothing is converted and the value reaches
            // the member exactly as it was decoded.
            foreach (var (_, oldTypeId, source, _, value, _) in DefinedWidenings)
            {
                Assert.Equal(CoercionKind.None, MigrationPlanBuilder.DetectCoercion(oldTypeId, source));
                Assert.IsType(source, MigrationPlanBuilder.Coerce(value, CoercionKind.None, source));
            }
        }

        // ── What is deliberately not converted ─────────────────────────────────────────────────

        [Fact]
        public void Narrowing_Is_Never_A_Detected_Conversion_So_It_Is_Refused_Rather_Than_Truncated()
        {
            // The asymmetry the plan builder's refusal depends on. `long -> int` loses the high half of
            // a value and `int -> short` loses three quarters of one, so neither may ever be detected:
            // `BuildOne` turns a member whose two sides disagree into a `CerasSchemaException` naming
            // both types, and that refusal is only honest while the widening set stays one-directional.
            Assert.Equal(CoercionKind.None, MigrationPlanBuilder.DetectCoercion(WellKnownTypes.Int64, typeof(int)));
            Assert.Equal(CoercionKind.None, MigrationPlanBuilder.DetectCoercion(WellKnownTypes.Int32, typeof(short)));
            Assert.Equal(CoercionKind.None, MigrationPlanBuilder.DetectCoercion(WellKnownTypes.Double, typeof(float)));
            Assert.Equal(CoercionKind.None, MigrationPlanBuilder.DetectCoercion(WellKnownTypes.UInt64, typeof(long)));

            // And with no conversion detected there is nothing for the applying half to do, so the value
            // is handed on with its own type rather than narrowed behind the plan's back.
            var wide = 4_000_000_000L;
            var handedOn = MigrationPlanBuilder.Coerce(wide, CoercionKind.None, typeof(int));
            Assert.IsType<long>(handedOn);
            Assert.Equal(wide, handedOn);
        }

        [Fact]
        public void A_Value_That_Is_Not_The_Shape_Its_Kind_Names_Is_Handed_On_Unconverted()
        {
            // Each arm of the switch is a type test with a fall-through, and the fall-through is the
            // safe answer: the value is passed on for the assignment to reject rather than converted
            // into something plausible. `IsType` is the assertion that matters — a fall-through that
            // re-boxed into the target's shape would still compare equal.
            var notAnInt = 7L;
            var stillALong = MigrationPlanBuilder.Coerce(notAnInt, CoercionKind.Int32ToInt64, typeof(long));
            Assert.IsType<long>(stillALong);
            Assert.Equal(notAnInt, stillALong);

            var notAShort = 7;
            var stillAnInt = MigrationPlanBuilder.Coerce(notAShort, CoercionKind.Int16ToInt32, typeof(int));
            Assert.IsType<int>(stillAnInt);
            Assert.Equal(notAShort, stillAnInt);

            var notAFloat = 7d;
            var stillADouble = MigrationPlanBuilder.Coerce(notAFloat, CoercionKind.SingleToDouble, typeof(double));
            Assert.IsType<double>(stillADouble);
            Assert.Equal(notAFloat, stillADouble);

            // A reference is the one case where identity is the sharpest available claim: a
            // fall-through returns the very instance rather than an equal one.
            var reference = new object();
            Assert.Same(reference, MigrationPlanBuilder.Coerce(reference, CoercionKind.Int32ToInt64, typeof(long)));
        }

        [Fact]
        public void A_Null_Is_Not_Converted_And_An_Unknown_Kind_Is_Not_Either()
        {
            foreach (var (kind, _, _, target, _, _) in DefinedWidenings)
                Assert.Null(MigrationPlanBuilder.Coerce(null, kind, target));

            // A kind the switch has no arm for, which is what a future `CoercionKind` member would be
            // on the day it is added. Pinned so the value of adding one is a visible choice rather
            // than a silent no-op discovered by a save.
            Assert.Null(MigrationPlanBuilder.Coerce(null, (CoercionKind)99, typeof(long)));
            var value = 5;
            var handedOn = MigrationPlanBuilder.Coerce(value, (CoercionKind)99, typeof(long));
            Assert.IsType<int>(handedOn);
            Assert.Equal(value, handedOn);
        }

        [Fact]
        public void The_Target_Type_Argument_Cannot_Change_The_Conversion()
        {
            // `Coerce` accepts where the value is going and does not read it: the two arguments that
            // decide the answer are the value and the kind. Pinned because the argument looks load
            // bearing, and a caller that started relying on it being consulted would be relying on
            // nothing.
            foreach (var target in new[] { null, typeof(string), typeof(object) })
            {
                Assert.Equal(1234L, MigrationPlanBuilder.Coerce(1234, CoercionKind.Int32ToInt64, target));
                Assert.IsType<int>(MigrationPlanBuilder.Coerce(1234, CoercionKind.None, target));
            }
        }

        // ── The edges of the arithmetic itself ─────────────────────────────────────────────────

        [Fact]
        public void A_Negative_And_An_Extreme_Int_Widen_With_Sign_Extension()
        {
            // `-7` is the value the real fixture stores, and `int.MinValue` is the one that would
            // expose a zero-extension mistake: read as a 32-bit pattern and widened, it becomes
            // 2147483648 rather than -2147483648.
            //
            // Every expected side below is spelled with the target's cast, because the widening
            // changes the CLR type and `Assert.Equal` compares boxed values by type as well as by
            // value: an uncast `int.MinValue` against a boxed `long` reports a difference between two
            // values that print identically.
            Assert.Equal(-7L, MigrationPlanBuilder.Coerce(-7, CoercionKind.Int32ToInt64, typeof(long)));
            Assert.Equal((long)int.MinValue, MigrationPlanBuilder.Coerce(int.MinValue, CoercionKind.Int32ToInt64, typeof(long)));
            Assert.Equal((long)int.MaxValue, MigrationPlanBuilder.Coerce(int.MaxValue, CoercionKind.Int32ToInt64, typeof(long)));
            Assert.Equal((float)int.MinValue, MigrationPlanBuilder.Coerce(int.MinValue, CoercionKind.Int32ToSingle, typeof(float)));
            Assert.Equal((double)int.MinValue, MigrationPlanBuilder.Coerce(int.MinValue, CoercionKind.Int32ToDouble, typeof(double)));
            Assert.Equal((float)int.MaxValue, MigrationPlanBuilder.Coerce(int.MaxValue, CoercionKind.Int32ToSingle, typeof(float)));

            // A short that is negative, which is the same question one width down.
            Assert.Equal((int)short.MinValue, MigrationPlanBuilder.Coerce(short.MinValue, CoercionKind.Int16ToInt32, typeof(int)));
            Assert.Equal((int)short.MaxValue, MigrationPlanBuilder.Coerce(short.MaxValue, CoercionKind.Int16ToInt32, typeof(int)));
        }

        [Fact]
        public void Widening_To_Single_Rounds_To_The_Nearest_Representable_Float_And_To_Double_Does_Not()
        {
            // 16 777 217 is the first integer a `float` cannot represent: its neighbours are
            // 16 777 216 and 16 777 218 and it falls exactly between them, so it rounds to the even
            // one. A `double` has no such boundary here and keeps the value exactly. Pinned as the
            // exact IEEE result, because a conversion that quietly rounded would change a save
            // without saying so.
            Assert.Equal(16_777_216f, MigrationPlanBuilder.Coerce(16_777_217, CoercionKind.Int32ToSingle, typeof(float)));
            Assert.Equal(16_777_216f, MigrationPlanBuilder.Coerce(16_777_216, CoercionKind.Int32ToSingle, typeof(float)));
            Assert.Equal(16_777_218f, MigrationPlanBuilder.Coerce(16_777_218, CoercionKind.Int32ToSingle, typeof(float)));

            Assert.Equal(16_777_217d, MigrationPlanBuilder.Coerce(16_777_217, CoercionKind.Int32ToDouble, typeof(double)));

            // 0.1f is not 0.1, and the double it becomes is the one the conversion has to produce.
            Assert.NotEqual(0.1d, MigrationPlanBuilder.Coerce(0.1f, CoercionKind.SingleToDouble, typeof(double)));
        }

        [Fact]
        public void A_Float_That_Is_Not_Finite_Survives_The_Widening_To_Double()
        {
            foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                Assert.Equal((double)value, MigrationPlanBuilder.Coerce(value, CoercionKind.SingleToDouble, typeof(double)));
        }

        // ── The one real file, pinned against its own bytes ─────────────────────────────────────

        [Fact]
        public void A_Real_V5_Fixture_Still_Reads_Its_Widened_Member_And_Consumes_Its_Data_Section_Exactly()
        {
            // The end-to-end half, and it is one fixture because that is all the corpus has:
            // `SaveGame.bin` is the only checked-in v5 file whose model widens a member, and it widens
            // `int Mana` to `long Mana`. The byte claim is the load itself: a robust save holds nothing
            // after the root's payload, so `VerifyDataSectionConsumedExactly` refuses a reader that
            // stopped even one byte early — a successful load of this file *is* the assertion that the
            // widened member consumed exactly its own four bytes and left the members after it aligned.
            var bytes = HistoricalFixtureFiles.Fixture("SaveGame.bin");
            HistoricalFixtureFiles.AssertIntactChecksum(bytes);
            HistoricalFixtureFiles.AssertDoesNotMatchCurrentLayout(bytes, "HistoricalCurrent");

            var game = HistoricalFixtureFiles.MigrationReader("HistoricalCurrent").Load<SaveGame>(bytes, 1);

            // Positive and negative, so a sign-extension mistake cannot pass on the first value alone.
            Assert.Equal(1234L, game.Players[0].Mana);
            Assert.Equal(-7L, game.Players[1].Mana);

            // The members that follow the widened one in the file's order, which is what a wrong width
            // or a wrong read position would have moved.
            Assert.Equal(Rank.Highest, game.Players[0].Rank);
            Assert.Equal("Ayla", game.Players[0].Name);
            Assert.Equal(0, game.Players[0].Level);          // added by this build: its declared default
            Assert.Same(game.Players[0], game.Players[0].Buddy.Buddy);   // the cycle still closes
            Assert.Same(game.Players[0], game.Main);                       // the shared reference is one instance
        }

        [Fact]
        public void A_Second_Real_V5_Fixture_Widens_A_Member_Through_The_Same_Path()
        {
            // `RelicVault.bin`'s `int Charge` became `long Charge`, on a file whose construction goes
            // through a configured factory, so the coercion is checked where the instance is not a
            // plain allocation. Its negative twin is `Relic.FromPool`'s `Charge = -1`, which is what a
            // pooled read of `Relic.bin` exercises.
            var bytes = HistoricalFixtureFiles.Fixture("RelicVault.bin");
            HistoricalFixtureFiles.AssertIntactChecksum(bytes);
            HistoricalFixtureFiles.AssertDoesNotMatchCurrentLayout(bytes, "HistoricalConstruction");

            var vault = HistoricalFixtureFiles.MigrationReader("HistoricalConstruction").Load<RelicVault>(bytes, 1);

            Assert.Equal(7L, vault.Kept.Charge);
            Assert.Same(vault.Kept, vault.Kept.Twin);      // the self-cycle through the pooled type
            Assert.Same(vault.Token, vault.Kept.Owner);    // one instance behind two paths
        }
    }
}
