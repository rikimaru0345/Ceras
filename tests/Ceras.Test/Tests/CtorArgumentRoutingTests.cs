// How a value reaches a type's `[CerasConstructor]`, and how every member the payload carried reaches the
// instance around it.
//
// The routing is `CtorArgumentValues`' whole job, and it makes two decisions in this order: everything the
// constructor takes is collected first, the constructor runs, and only then is every member assigned — so a
// constructor that rewrote any value, one it was handed or not, cannot change the result. That is why the
// members are held rather than written straight through, and it is the generated reader's order as well:
// it reads every member into a local, runs the constructor, and writes *all* of the locals back.
// `RoutingModel` below makes the distinction observable by having its constructor clobber a value it *was*
// handed (`Fed`) and one it was not (`Unfed`, `UnfedToo`, `Sentinel`).
//
// What the file pins, and why it is worth pinning separately from `CtorNullabilityMigrationTests` and
// `TransientCtorParamTests`:
//
//   * the *unfed* member case was uncovered on the migration path. Every existing `[CerasConstructor]`
//     model maps each of its members to a parameter, so the pending-assignment branch never ran; and
//     `TransientCtorParamTests` does cover an unfed *parameter*, but on a same-layout roundtrip that
//     takes the generated reader, so `CtorArgumentValues.Arguments` never ran either.
//   * the *fed* member case is the end-to-end half of the constructor contract and lives in
//     `ConstructorRewriteMemberTests` and `HistoricalCtorFixtureTests`, which is where the consequence of
//     this file's unit-level decision is observable as a value and as an identity.
//
// One thing deliberately **not** pinned here: the parameter-versus-pending decision itself. No end-to-end
// assertion can distinguish it any more, because both routes end with the save's value in the member, and
// that is the point — the constructor is construction input, not assignment. What the unit tests below can
// still distinguish is the *other* half: that a member a parameter fed is handed to the constructor
// (`Arguments`) and that no member is skipped on the way back out (`ApplyPendingAssignments`).

using System;
using System.Linq;
using System.Reflection;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test
{
    [CerasContext("CtorArgumentRouting")]
    [CerasSerializable]
    public class RoutingModel
    {
        /// <summary>Fed by the <c>fed</c> parameter, so it goes through the constructor — which then keeps
        /// the value verbatim, so the end-to-end test below states the pending-assignment order without
        /// also depending on the fed member being written back. The unit tests cover that half.</summary>
        public int Fed;
        /// <summary>Fed by no parameter at all: the constructor clobbers it and the save must win.</summary>
        public int Unfed;

        /// <summary>A second unfed member, so more than one pending assignment has to be applied.</summary>
        public int UnfedToo;

        /// <summary>Unfed, and fixed-width, so a wrong width shows up as a wrong number.</summary>
        public uint Sentinel;

        public const uint ExpectedSentinel = 0xA5A5A5A5u;

        /// <summary>What the transient parameter arrived with; nothing in the save can set it.</summary>
        public static int TransientSeen;

        public static int CtorRuns;

        [CerasConstructor]
        public RoutingModel(int fed, int transient = 41)
        {
            Fed = fed;
            Unfed = -999;      // a value the save must overwrite after this constructor returns
            UnfedToo = -999;
            Sentinel = 0;
            TransientSeen = transient;
            CtorRuns++;
        }

        /// <summary>
        /// A second constructor, used only to build the object that is saved so the payload holds
        /// values the attributed constructor above would never produce. The reader still selects the
        /// attributed one: `CtorArgumentPlanBuilder.SelectConstructor` asks for an explicit
        /// `[CerasConstructor]` before it looks at anything else.
        /// </summary>
        public RoutingModel()
        {
            Fed = 111;
            Unfed = 222;
            UnfedToo = 333;
            Sentinel = ExpectedSentinel;
        }

        public static void Reset()
        {
            TransientSeen = 0;
            CtorRuns = 0;
        }
    }

    public class CtorArgumentRoutingTests : TestBase
    {
        const string Ctx = "CtorArgumentRouting";

        static CerasRobustSerializer Reader() => new(c => c.UseGeneratedFormatters(Ctx));

        // ── End to end, on both readers ───────────────────────────────────────────────────────────

        [Fact]
        public void A_Member_No_Parameter_Feeds_Keeps_The_Save_S_Value_On_Both_Readers()
        {
            // The order, stated as a value. The attributed constructor sets `Unfed`, `UnfedToo` and
            // `Sentinel` to values the save does not contain, so the save's numbers can only be there
            // if they were assigned *after* the constructor returned. Asserted on both readers,
            // because the generated reader and the migration interpreter reach the same decision by
            // different code and a save that round-trips on one path is not evidence about the other.
            var bytes = Reader().Save(new RoutingModel(), 1);

            foreach (var (name, loaded) in new[]
                     {
                         ("generated", Reader().Load<RoutingModel>(bytes, 1)),
                         ("interpreter", Reader().Load<RoutingModel>(SlowPath(bytes), 1)),
                     })
            {
                Assert.Equal(111, loaded.Fed);
                Assert.Equal(222, loaded.Unfed);
                Assert.Equal(333, loaded.UnfedToo);
                Assert.Equal(RoutingModel.ExpectedSentinel, loaded.Sentinel);
                Assert.True(RoutingModel.CtorRuns > 0, $"{name}: the constructor never ran");
            }
        }

        [Fact]
        public void A_Transient_Parameter_Gets_Its_Declared_Default_On_The_Migration_Path()
        {
            // `Arguments` fills every parameter no serialized member feeds with the parameter's own
            // declared default, which is the one half of the routing that the existing suite only
            // reached on the generated path: `TransientCtorParamTests` saves and loads the same model,
            // so its fingerprint matches and the interpreter never runs.
            var bytes = Reader().Save(new RoutingModel(), 1);

            RoutingModel.Reset();
            Reader().Load<RoutingModel>(SlowPath(bytes), 1);
            var viaInterpreter = RoutingModel.TransientSeen;

            RoutingModel.Reset();
            Reader().Load<RoutingModel>(bytes, 1);
            var viaGenerated = RoutingModel.TransientSeen;

            Assert.Equal(41, viaInterpreter);
            Assert.Equal(viaGenerated, viaInterpreter);
        }

        // ── The routing itself ────────────────────────────────────────────────────────────────────

        [Fact]
        public void Routing_Puts_A_Member_Into_Its_Parameter_Slot_When_A_Parameter_Feeds_It()
        {
            var values = NewValues();

            Set(values, nameof(RoutingModel.Fed), 42);

            // The value reaches the constructor, which is what the slot is for and the only thing this
            // can distinguish: `Arguments` returns what `ConstructorInfo.Invoke` will be handed.
            Assert.Equal(42, values.Arguments()[0]);

            // It is *also* held for afterwards, because a [CerasConstructor] is a construction input and
            // not an assignment: the generated reader writes every member back from its local once the
            // constructor has returned, so the save's value is the object's whatever the constructor did
            // with the argument. A reader that left this member to the constructor would return the
            // constructor's result here and the generated reader's result on the fast path.
            var target = new RoutingModel();
            values.ApplyPendingAssignments(target);
            Assert.Equal(42, target.Fed);

            // A member nothing was routed for is not touched at all: it keeps the constructor's value.
            Assert.Equal(222, target.Unfed);
        }

        [Fact]
        public void Routing_Holds_A_Member_For_Afterwards_When_No_Parameter_Feeds_It()
        {
            var values = NewValues();

            Set(values, nameof(RoutingModel.Unfed), 777);
            Set(values, nameof(RoutingModel.UnfedToo), 888);

            // The parameter nothing fed is still empty at this point: the value is held, not passed.
            var args = values.Arguments();
            Assert.Null(args[0]);
            Assert.Equal(41, args[1]);

            var target = new RoutingModel();
            values.ApplyPendingAssignments(target);
            Assert.Equal(111, target.Fed);        // the constructor's, untouched
            Assert.Equal(777, target.Unfed);
            Assert.Equal(888, target.UnfedToo);
        }

        [Fact]
        public void Arguments_Gives_Every_Unmapped_Parameter_Its_Declared_Default_And_Leaves_A_Fed_One_Alone()
        {
            var values = NewValues();
            Set(values, nameof(RoutingModel.Fed), 42);

            var args = values.Arguments();

            Assert.Equal(42, args[0]);   // the member fed it
            Assert.Equal(41, args[1]);   // the transient parameter's declared default
        }

        [Fact]
        public void Arguments_Called_Twice_Keeps_A_Fed_Parameter_S_Value()
        {
            // The engine calls `Arguments` once, immediately before the reflection call. Pinning that a
            // second call is harmless costs nothing and keeps a second caller — a materializer reading
            // the same values twice — from silently handing the constructor `default(T)`.
            var values = NewValues();
            Set(values, nameof(RoutingModel.Fed), 42);

            Assert.Equal(42, values.Arguments()[0]);
            Assert.Equal(42, values.Arguments()[0]);
            Assert.Equal(41, values.Arguments()[1]);
        }

        [Fact]
        public void Applying_The_Pending_Assignments_Is_Repeatable_And_Writes_Every_Routed_Member()
        {
            var values = NewValues();
            Set(values, nameof(RoutingModel.Fed), 42);

            var target = new RoutingModel();
            values.ApplyPendingAssignments(target);
            values.ApplyPendingAssignments(target);   // twice, because a list is not consumed by writing it

            Assert.Equal(42, target.Fed);        // the value it was handed, not the constructor's
            Assert.Equal(222, target.Unfed);
        }

        // ── Harness ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A <see cref="CtorArgumentValues"/> over the real plan of <see cref="RoutingModel"/>, built
        /// the way the engine builds one: the plan is the answer <c>MigrationPlanBuilder</c> gives, and
        /// the buffer is a thin thing over it. Every member is mapped, because a member no parameter
        /// feeds is a member the *constructor* did not claim — not a member the file did not record.
        /// </summary>
        static CtorArgumentValues NewValues()
        {
            var mappings = new[]
            {
                nameof(RoutingModel.Fed),
                nameof(RoutingModel.Unfed),
                nameof(RoutingModel.UnfedToo),
                nameof(RoutingModel.Sentinel),
            }.Select(name => new FieldMapping
            {
                OldField = Stored(name),
                NewField = Field(name),
                Coercion = CoercionKind.None,
            }).ToArray();

            var plan = new TypeMigrationPlan
            {
                CurrentType = typeof(RoutingModel),
                Mappings = mappings,
                AddedFieldNames = Array.Empty<string>(),
            };

            var ctorPlan = CtorArgumentPlanBuilder.Resolve(typeof(RoutingModel), plan);
            Assert.NotNull(ctorPlan);
            return new CtorArgumentValues(ctorPlan!);
        }

        static FieldInfo Field(string name)
            => typeof(RoutingModel).GetField(name, BindingFlags.Public | BindingFlags.Instance)!;

        /// <summary>Routes one member's value the way the engine does: its field, the name the file
        /// recorded it under, and the value.</summary>
        static void Set(CtorArgumentValues values, string name, object value)
            => values.Set(Field(name), Stored(name), value);

        /// <summary>
        /// The file's own record of a member. `CtorArgumentValues` only forwards it to
        /// <c>CtorArgumentPlan.AcceptMemberValue</c>, which reads its name for a refusal message, so the
        /// name is the part that has to be right.
        /// </summary>
        static OldFieldSchema Stored(string name)
            => new(name, Array.Empty<string>(), WellKnownTypes.Int32, isMonomorphic: true);
    }
}
