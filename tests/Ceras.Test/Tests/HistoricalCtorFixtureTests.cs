// Cross-version tests for the **constructor-argument** construction contract: a type that has gained a
// [CerasConstructor] since the file was written, whose parameters come out of the payload, and whose
// bodies do not simply keep what they were handed.
//
// The bytes come from fixtures/Ceras.HistoricalWriter (V1CtorModel.cs, context "HistoricalV1Ctor"), which
// this assembly does not reference, so the v1 CLR types are absent and this is a real migration: the
// file's own schema section is decoded and mapped onto the model below.
//
// Why a checked-in file rather than the same-layout cross-check in ConstructorRewriteMemberTests: a
// fingerprint flip can only show that two readers disagree about *this* schema, and the whole contract
// here is about what happens when the schema moved. The file also carries the two things a same-layout
// file cannot — a member the current model removed (its bytes are skipped between two that are kept) and
// a member the current model added (nothing assigns it, so it keeps whatever the construction gave it).
//
// The rule under test, which the generated reader has always implemented by writing every local it read
// back after the constructor has run:
//
//   a [CerasConstructor] is a *construction* input, not an assignment. The value the payload carries is
//   the value the object ends up with, for every member, the ones a parameter was fed from included. A
//   constructor that normalizes, clamps, drops or reorders what it is handed cannot change the result —
//   on either reader.
using System;
using Xunit;
using Ceras;
using Ceras.Generated;
using static Ceras.Test.HistoricalFixtureFiles;

namespace Ceras.Test.HistoricalCtorFixtures
{
    /// <summary>
    /// v1's <c>Ledger</c> row, in the current model. It has gained a <c>[CerasConstructor]</c> that takes
    /// <c>Amount</c> and <b>clamps it</b>: a negative stored value becomes zero in the constructor's own
    /// result, and the file's value is what the object has to end up with.
    /// <para>
    /// <c>Note</c> became <c>Memo</c>, <c>Retired</c> is gone (so its four bytes are skipped, and
    /// <c>Memo</c> behind it is the witness that they were), and <c>Label</c> is new: nothing in the file
    /// describes it, so no assignment can reach it and it has to keep what the construction gave it.
    /// </para>
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Ctor.Ledger")]
    [CerasContext("HistoricalCtorCurrent")]
    [CerasSerializable]
    public class Ledger
    {
        public int Amount;

        [PreviousName("Note")]
        public string Memo;

        public Ledger Twin;

        /// <summary>Added in the current model, so the file describes no member by this name.</summary>
        public string Label = "current";

        /// <summary>What the constructor produced, so a test can tell the constructor's result from the
        /// stored one without inferring it from a member it also writes.</summary>
        [Exclude]
        public int Normalized;

        [CerasConstructor]
        public Ledger(int amount)
        {
            Normalized = amount < 0 ? 0 : amount;
            Amount = Normalized;
        }
    }

    /// <summary>
    /// v1's <c>Cover</c>. Its one constructor parameter is a <b>reference</b> — the member the file shares
    /// with <see cref="LedgerBook.Main"/> — and the body <b>drops</b> it. There is no number to compare
    /// here: the only evidence that the stored value was put back is that the shared instance came back
    /// as the shared instance, with the cycle through it intact.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Ctor.Cover")]
    [CerasContext("HistoricalCtorCurrent")]
    [CerasSerializable]
    public class Cover
    {
        public Ledger Inner;

        /// <summary>Not fed to any constructor, and rewritten by it: the file's value still has to win.</summary>
        public int Count;

        [CerasConstructor]
        public Cover(Ledger inner)
        {
            Inner = null;          // a real constructor body would look at the argument here
            Count = -1;
        }
    }

    [PreviousTypeName("Ceras.HistoricalWriter.V1Ctor.LedgerBook")]
    [CerasContext("HistoricalCtorCurrent")]
    [CerasSerializable]
    public class LedgerBook
    {
        public Ledger Main;
        public Cover Wrap;
        public uint Marker;
    }

    public class HistoricalCtorFixtureTests : TestBase
    {
        const string Ctx = "HistoricalCtorCurrent";

        /// <summary>The amount v1 stored, and the value its constructor would have produced for it.</summary>
        const int StoredAmount = -5;
        const int NormalizedAmount = 0;

        /// <summary>The trailing sentinel the crate ends with, after every member it is pinning.</summary>
        const uint Sentinel = 0xC70C0C70u;

        static byte[] Book() => Fixture("CtorLedgerBook.bin");
        static byte[] Row() => Fixture("CtorLedgerRow.bin");

        static CerasRobustSerializer Reader() => MigrationReader(Ctx);

        /// <summary>A serializer over the *current* model, for the matching-layout control below.</summary>
        static CerasRobustSerializer Control() => CurrentWriter(Ctx);

        [Fact]
        public void The_Fixtures_Really_Are_Old_Files()
        {
            AssertEveryFixture(
                new[] { "CtorLedgerBook.bin", "CtorLedgerRow.bin" },
                "Ceras.HistoricalWriter.V1Ctor", "Ceras.Test.HistoricalCtorFixtures", Ctx);
        }

        /// <summary>
        /// The load these tests are about: the stored value of the member the constructor was fed, not the
        /// one the constructor produced for it.
        /// </summary>
        [Fact]
        public void A_Constructor_That_Rewrites_Its_Argument_Does_Not_Change_The_Stored_Value()
        {
            var loaded = Reader().Load<LedgerBook>(Book(), 1);

            Assert.Equal(StoredAmount, loaded.Main.Amount);
            Assert.Equal(NormalizedAmount, loaded.Main.Normalized);   // the constructor really did run
            Assert.Equal("alpha", loaded.Main.Memo);                  // the renamed member, after the removed one
            Assert.Equal("current", loaded.Main.Label);                // added: no assignment reached it
            Assert.Equal(3, loaded.Wrap.Count);                        // rewritten by the constructor too
            Assert.Equal(Sentinel, loaded.Marker);                     // the stream stayed aligned
        }

        /// <summary>
        /// The same, with the row as the root: a load of the row does not depend on the crate around it,
        /// and the file's own cycle is the only reference the assertion needs.
        /// </summary>
        [Fact]
        public void A_Constructor_That_Rewrites_Its_Argument_Does_Not_Change_The_Stored_Value_As_A_Root()
        {
            var loaded = Reader().Load<Ledger>(Row(), 1);

            Assert.Equal(StoredAmount, loaded.Amount);
            Assert.Equal(NormalizedAmount, loaded.Normalized);
            Assert.Equal("alpha", loaded.Memo);
            Assert.Equal("current", loaded.Label);
            Assert.Same(loaded, loaded.Twin);
        }

        /// <summary>
        /// A constructor parameter that is a <b>reference</b> it drops: the only thing that can restore the
        /// shared instance is the assignment, so this is an identity assertion and not a value one. v1
        /// wrote one ledger row into three places — <c>Main</c>, <c>Wrap.Inner</c> and its own
        /// <c>Twin</c> — and all three have to be that one object again.
        /// </summary>
        [Fact]
        public void A_Constructor_That_Drops_Its_Reference_Argument_Still_Gets_The_Shared_Instance_Back()
        {
            var loaded = Reader().Load<LedgerBook>(Book(), 1);

            Assert.NotNull(loaded.Wrap.Inner);
            Assert.Same(loaded.Main, loaded.Wrap.Inner);
            Assert.Same(loaded.Main, loaded.Main.Twin);
            Assert.Equal(StoredAmount, loaded.Wrap.Inner.Amount);
        }

        /// <summary>
        /// Both readers produce the same object from the current schema's own bytes: the value a
        /// constructor argument carried, and — the part the fixture above cannot reach — the *identity* a
        /// constructor argument that is a reference carried, when the constructor's body dropped it and the
        /// graph shares one instance through it and through a cycle.
        /// <para>
        /// This is a same-layout cross-check and nothing more. It cannot show that a constructor's rewrite
        /// loses to the stored value, because the stored value here is whatever this build's own
        /// constructor produced for the argument — so that is what the three tests above use a checked-in
        /// v1 file for. The graph is built and then wired up by hand (a builder pattern), so the file
        /// really does hold one ledger row in three places even though the constructor dropped the
        /// argument on the way in.
        /// </para>
        /// </summary>
        [Fact]
        public void Both_Readers_Agree_On_The_Current_Model_S_Own_Bytes()
        {
            var main = new Ledger(6);
            var book = new LedgerBook { Main = main, Wrap = new Cover(main), Marker = Sentinel };
            main.Twin = main;              // the cycle, wired after the constructor dropped its argument
            book.Wrap.Inner = main;        // the shared instance, likewise

            // The premise, stated here so a change to the model that invalidates the comparison is visible.
            Assert.Equal(6, main.Amount);
            Assert.Null(new Cover(main).Inner);   // the constructor really does drop its reference argument

            var serializer = Control();
            var bytes = serializer.Save(book, 1);

            AssertSameBook(serializer.Load<LedgerBook>(bytes, 1), serializer.Load<LedgerBook>(SlowPath(bytes), 1));
        }

        /// <summary>One object on each reader, checked member by member. The identities are the point: a
        /// reader that built a second ledger row would report success with the right numbers.</summary>
        static void AssertSameBook(LedgerBook loaded, LedgerBook other)
        {
            Assert.Equal(6, loaded.Main.Amount);
            Assert.Equal(6, loaded.Main.Normalized);
            Assert.Null(loaded.Main.Memo);
            Assert.Equal("current", loaded.Main.Label);
            Assert.Same(loaded.Main, loaded.Main.Twin);
            Assert.Equal(-1, loaded.Wrap.Count);
            Assert.Same(loaded.Main, loaded.Wrap.Inner);
            Assert.Equal(Sentinel, loaded.Marker);

            Assert.Equal(loaded.Main.Amount, other.Main.Amount);
            Assert.Equal(loaded.Main.Normalized, other.Main.Normalized);
            Assert.Equal(loaded.Main.Memo, other.Main.Memo);
            Assert.Equal(loaded.Main.Label, other.Main.Label);
            Assert.Equal(loaded.Wrap.Count, other.Wrap.Count);
            Assert.Equal(loaded.Marker, other.Marker);
            Assert.Same(other.Main, other.Main.Twin);
            Assert.Same(other.Main, other.Wrap.Inner);
        }
    }
}
