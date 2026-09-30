// The old-data graph's binding pass: the file's own members, projected onto this build's model.
//
// `GraphMigrationBinder` reads a graph the file-faithful decoder produced and the plans
// `MigrationPlanBuilder` already built, and answers one question per member: does this build have
// somewhere to put this member's value? A member that does is *named* (OldMember.CurrentFieldName); a
// member that does not is dropped from the projection (OldObjectNode.Members) while its record stays in
// RecordedMembers and its value stays in the arena. Nothing is constructed, no field is touched, and no
// production load path reaches any of this yet — the point of the stage is that the binding can be proved
// on its own, before anything is allowed to build an object out of it.
//
// ── Why genuine historical files, and which of them can get here ────────────────────────────────────────
//
// Every fixture below was written by `fixtures/Ceras.HistoricalWriter` in a compilation this assembly
// does not reference, so the v1 CLR model is absent at load time and the binder's input is genuinely a
// *file* plus a *plan*, never a live v1 object. Two of them are the opposite case and are here as such:
// a file the current plan builder **refuses** must be asserted as refused, not quietly skipped, because
// "the binder was quiet" and "the binder was never reached" look identical from the outside and mean
// opposite things. A fixture that cannot produce a plan has not been migrated, and this file says so by
// name rather than by omission.
//
// The two opaque fixtures need the reading context that installs v1's formatter under v1's own name —
// which is the point of that declaration: a build that kept the class name keeps the payload readable.
// Without the formatter the plan builder refuses by name, and that refusal is a fact about the *config*
// rather than about the binder, so it is not what this file is testing here.
//
// ── What is pinned, in the order it would cost if it were wrong ─────────────────────────────────────────
//
//  1. **Nothing moves.** Binding edits two bindings and nothing else: no value changes owner, no arena
//     index changes, no back-reference is renumbered. A binder that moved a value would make every
//     later stage's identity reasoning unsound, and it is the cheapest possible thing to get wrong.
//  2. **The file's record survives binding.** A removed member leaves `Members` and stays in
//     `RecordedMembers` with its recorded name and type identity — a migration report is printed from
//     there, and a binder that edited it would delete the evidence.
//  3. **A removed first occurrence is still a node.** The value a removed member held stays in the
//     arena, so a retained member pointing at it still resolves. This is the case the streaming reader
//     refuses today; here the node exists independently of any current field.
//  4. **The correspondence is checked, not assumed.** A plan and a graph built from one file agree by
//     construction, so a disagreement is an internal mistake — and the interesting half is that it is
//     *silent* when it happens (a plausible field of the wrong type), which is why the hand-built cases
//     at the end exist at all.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test
{
    public class GraphMigrationBinderTests
    {
        /// <summary>
        /// The current model the bulk of the fixtures are read against. It is a superset context: it holds
        /// this build's counterparts of the v1 types, so a file that names a type nothing here has still
        /// produces a plan — one whose <c>CurrentType</c> is null and whose mappings are all "removed",
        /// which is a real answer rather than a failure.
        /// </summary>
        const string Ctx = "HistoricalCurrent";

        // ── 1. Every fixture the current plan builder can legitimately build ───────────────────────

        /// <summary>
        /// Every checked-in golden, with the reading context each one needs. The two opaque fixtures get
        /// the context that installs v1's formatter, because without it the *plan builder* refuses them
        /// for a reason that has nothing to do with binding.
        /// </summary>
        public static IEnumerable<object[]> BindableFixtures()
        {
            foreach (var name in Directory.GetFiles(HistoricalFixtureFiles.Directory, "*.bin")
                                          .Select(Path.GetFileName)
                                          .OrderBy(n => n, StringComparer.Ordinal))
                yield return new object[] { name, name.StartsWith("Opaque", StringComparison.Ordinal) };

            foreach (var name in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GenericAliasV1"), "*.bin")
                                          .Select(Path.GetFileName)
                                          .OrderBy(n => n, StringComparer.Ordinal))
                yield return new object[] { name, false };
        }

        /// <summary>
        /// A golden by name, from whichever of the two checked-in directories holds it. Both are read the
        /// same way and both are real files written by the historical writer; only the directory differs.
        /// </summary>
        static byte[] Fixture(string name)
        {
            var inHistory = Path.Combine(HistoricalFixtureFiles.Directory, name);
            if (File.Exists(inHistory)) return File.ReadAllBytes(inHistory);
            return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GenericAliasV1", name));
        }

        [Theory]
        [MemberData(nameof(BindableFixtures))]
        public void Every_Fixture_Binds_And_Preserves_The_Whole_Graph(string name, bool opaque)
        {
            var bytes = Fixture(name);
            HistoricalFixtureFiles.AssertIntactChecksum(bytes);

            var graph = OldFileGraph.Read(bytes);
            var before = Snapshot(graph);

            new GraphMigrationBinder(BuildPlans(bytes, opaque)).Bind(graph);

            var after = Snapshot(graph);

            // ── 1. Nothing moved. ── Every node, every index, every reference target: identical.
            Assert.Equal(before.Indices.Count, after.Indices.Count);
            Assert.Equal(before.Indices, after.Indices);
            Assert.Equal(before.BackReferences, after.BackReferences);

            // ── 2. The file's record is untouched, member for member. ──
            Assert.Equal(before.Recorded, after.Recorded);

            // ── 3. Every surviving member is bound, and nothing else is. ──
            foreach (var entry in after.Projection)
                Assert.All(entry.Value, name => Assert.False(string.IsNullOrEmpty(name),
                    $"{name}: a member left in the projection with no current field"));

            // A projection is a subsequence of the recorded list, in the same order: nothing was
            // reordered, added, or invented.
            foreach (var entry in after.Projection)
            {
                var recorded = after.Recorded.Where(r => r.StartsWith(entry.Key + ".", StringComparison.Ordinal)).ToArray();
                int cursor = 0;
                foreach (var bound in entry.Value)
                {
                    int at = Array.FindIndex(recorded, cursor, r => r.StartsWith(entry.Key + "." + bound + ":", StringComparison.Ordinal));
                    Assert.True(at >= 0, $"{entry.Key}: '{bound}' is not in the recorded member list in order");
                    cursor = at + 1;
                }
            }
        }

        // ── 2. A removed member leaves the projection and stays in the audit trail ────────────────

        /// <summary>
        /// `SaveGame` is the broadest evolution in the set: v1's `RetiredText`, `Home` and `Retired` have
        /// no counterpart here, and the graph holds all three before binding. After binding the projection
        /// has lost them and the recorded list has not — which is the whole difference between a report
        /// that can say what a save contained and one that cannot.
        /// </summary>
        [Fact]
        public void A_Removed_Member_Leaves_The_Projection_And_Stays_In_The_Recorded_List()
        {
            var bytes = Fixture("SaveGame.bin");
            var graph = OldFileGraph.Read(bytes);
            new GraphMigrationBinder(BuildPlans(bytes, opaque: false)).Bind(graph);

            var root = Assert.IsType<OldObjectNode>(graph.Root);

            var recorded = root.RecordedMembers.Select(m => m.Recorded!.StableName).ToArray();
            Assert.Contains("RetiredText", recorded);
            Assert.Contains("Home", recorded);
            Assert.Contains("Retired", recorded);

            // v1's `Health`, removed from `Player` specifically so the claim is about a *member* removal
            // and not about a type the model has lost.
            var player = (OldObjectNode)root.Members.Single(m => m.Recorded!.StableName == "Main").Value;
            Assert.Contains("Health", player.RecordedMembers.Select(m => m.Recorded!.StableName));

            // Every remaining projection member is bound to a name the current model actually uses, which
            // is checked against the current type rather than against the plan that produced the binding.
            AssertBoundAgainstCurrentType(root, typeof(HistoricalFixtures.SaveGame));
            AssertBoundAgainstCurrentType(player, typeof(HistoricalFixtures.Actor));

            // The mapped ones are the ones v1 and this build both have, found through the renames. On the
            // root, v1's `Stats` is what this build calls `Options`; on the player, v1's `Mana` is the
            // member this build widened from `int` to `long`, and a widened member is still the same
            // member rather than a removed one.
            Assert.Contains("Options", root.Members.Select(m => m.CurrentFieldName));
            Assert.Contains("Main", root.Members.Select(m => m.CurrentFieldName));
            Assert.Contains("Mana", player.Members.Select(m => m.CurrentFieldName));
            Assert.Contains("Name", player.Members.Select(m => m.CurrentFieldName));
        }

        /// <summary>
        /// The removal is the *only* thing that changes a member's count. A member that maps keeps its
        /// position relative to the others, so a reader walking the projection is walking a subsequence of
        /// the file's own order — which is what lets a materializer apply values in the order the file
        /// wrote them without re-deriving that order from the current model.
        /// </summary>
        [Fact]
        public void Removal_Takes_The_Unmapped_Members_And_Leaves_The_Rest_In_File_Order()
        {
            var bytes = Fixture("SaveGame.bin");
            var graph = OldFileGraph.Read(bytes);
            var before = Assert.IsType<OldObjectNode>(graph.Root);
            var recordedOrder = before.RecordedMembers.Select(m => m.Recorded!.StableName).ToArray();

            new GraphMigrationBinder(BuildPlans(bytes, opaque: false)).Bind(graph);

            var projection = ProjectedNames(before);
            Assert.NotEmpty(projection);
            Assert.True(projection.Length < recordedOrder.Length, "this fixture is only interesting if something was removed");

            // Every projected member is a recorded member, and the recorded order is preserved.
            int cursor = 0;
            foreach (var bound in projection)
            {
                int at = Array.IndexOf(recordedOrder, bound, cursor);
                Assert.True(at >= 0, $"'{bound}' was never recorded");
                cursor = at + 1;
            }
        }

        // ── 3. Identity, and the first occurrence under a removed member ───────────────────────────

        /// <summary>
        /// The streaming reader's standing refusal, in a form this stage can answer.
        /// <para>
        /// v1 wrote `Main` as the first occurrence of one player, and `Players[0]` as a back-reference to
        /// it. Binding does not touch values, so both the node and the reference survive it, and the
        /// reference still resolves to the node it always named. `IdentityCache.Resolve` refuses exactly
        /// this shape on the streaming path because a skip there builds nothing to point at; here the node
        /// was never skipped, it was decoded.
        /// </para>
        /// </summary>
        [Fact]
        public void A_Back_Reference_Still_Resolves_After_Binding()
        {
            var bytes = Fixture("SaveGame.bin");
            var graph = OldFileGraph.Read(bytes);
            new GraphMigrationBinder(BuildPlans(bytes, opaque: false)).Bind(graph);

            var root = Assert.IsType<OldObjectNode>(graph.Root);
            var main = (OldObjectNode)root.Members.Single(m => m.Recorded!.StableName == "Main").Value;
            var players = (OldCollectionNode)root.Members.Single(m => m.Recorded!.StableName == "Players").Value;

            // `Players[0]` is the same instance v1 wrote as `Main`, and the file recorded it as a
            // back-reference rather than a second copy.
            var first = Assert.IsType<OldReferenceNode>(players.Elements[0]);
            Assert.Equal(graph.IndexOf(main), first.TargetIndex);
            Assert.Same(main, graph.Node(first.TargetIndex));

            // And the cycle through `Main.Friend` is closed the same way, so binding did not flatten a
            // graph the decoder had already resolved.
            var friend = (OldObjectNode)main.Members.Single(m => m.Recorded!.StableName == "Friend").Value;
            var backToMain = Assert.IsType<OldReferenceNode>(friend.Members.Single(m => m.Recorded!.StableName == "Friend").Value);
            Assert.Same(main, graph.Node(backToMain.TargetIndex));
        }

        /// <summary>
        /// The precondition the streaming reader's standing refusal would need, and the reason the graph can
        /// meet it: a value whose <em>only</em> first occurrence is under a member this build no longer has
        /// is still a node, because the arena numbers every value and binding moves none of them.
        /// <para>
        /// `SaveGame` supplies the shape on its own: v1's player had a `Health` member that this build
        /// dropped, and the value behind it stays indexed after binding. So a retained member pointing at
        /// that value would resolve — the refusal in <c>SkippedReferenceRefusalTests</c> has nothing left
        /// to refuse, because it exists there only because a <em>skip</em> builds nothing to point at.
        /// </para>
        /// <para>
        /// <b>What is deliberately not claimed.</b> No checked-in fixture has a retained member whose
        /// back-reference targets a value first occurring under a removed member — the shape the refusal
        /// test hand-builds, and the one a genuine writer would produce. This test therefore pins the
        /// <em>precondition</em> (the value is there, indexed, and findable) and not the resolution, which
        /// belongs to a materializer and needs a fixture that does not exist yet.
        /// </para>
        /// </summary>
        [Fact]
        public void A_Removed_Members_Value_Stays_In_The_Arena_And_Keeps_Its_Index()
        {
            var bytes = Fixture("SaveGame.bin");
            var graph = OldFileGraph.Read(bytes);

            var root = Assert.IsType<OldObjectNode>(graph.Root);
            var player = (OldObjectNode)root.Members.Single(m => m.Recorded!.StableName == "Main").Value;
            Assert.Contains("Health", player.RecordedMembers.Select(m => m.Recorded!.StableName));

            // The index the removed member's value occupies, taken before binding so the claim is about
            // the value and not about a lookup that happens to work after.
            var health = player.RecordedMembers.Single(m => m.Recorded!.StableName == "Health");
            int indexBefore = graph.IndexOf(health.Value);
            Assert.True(indexBefore >= 0);

            new GraphMigrationBinder(BuildPlans(bytes, opaque: false)).Bind(graph);

            // Gone from the projection, present in the audit trail, and still the very same node.
            Assert.DoesNotContain("Health", player.Members.Select(m => m.Recorded!.StableName));
            Assert.Contains("Health", player.RecordedMembers.Select(m => m.Recorded!.StableName));
            Assert.Equal(indexBefore, graph.IndexOf(health.Value));
            Assert.Same(health.Value, graph.Node(indexBefore));
        }

        // ── 4. The bare object, and a type this build has no type for ────────────────────────────

        /// <summary>
        /// `new object()` in a polymorphic slot is a marker and nothing else — the writer's formatter for
        /// `object` has no members, so the file records an object with an empty member list. There is
        /// nothing to bind, no plan to look up, and nothing to refuse; `PolyObjectKept` and
        /// `PolyRemovedObjectMid` are the two fixtures that hold one, and the whole arena is walked, so
        /// this shape is met in the middle of a real save rather than in isolation.
        /// </summary>
        [Fact]
        public void A_Bare_System_Object_Is_Left_Alone()
        {
            var bytes = Fixture("PolyObjectKept.bin");
            var graph = OldFileGraph.Read(bytes);

            var bare = new List<OldObjectNode>();
            for (int i = 0; i < graph.NodeCount; i++)
                if (graph.Node(i) is OldObjectNode o && o.Type.TypeId == WellKnownTypes.Object)
                    bare.Add(o);

            Assert.NotEmpty(bare);
            Assert.All(bare, o => Assert.Empty(o.Members));
            Assert.All(bare, o => Assert.Equal("System.Object", o.Type.StableName));

            new GraphMigrationBinder(BuildPlans(bytes, opaque: false)).Bind(graph);

            // Unchanged, and still holding nothing: the binder had no plan for this identity and had
            // nothing to say, which is the correct outcome rather than a skipped one.
            Assert.All(bare, o => Assert.Empty(o.Members));
            Assert.All(bare, o => Assert.Equal(0, o.RecordedMembers.Count));
        }

        /// <summary>
        /// A type the file describes and this build does not have. The plan says so honestly —
        /// <c>CurrentType</c> null, every mapping "removed" — so binding empties that object's projection
        /// and says nothing about it. This is the shape the removed-only missing-type policy is written
        /// for: a value whose type is gone is skipped only where nothing retained depends on it, and
        /// whether a retained member depends on it is a question for the materializer, not for a pass that
        /// only decides destinations.
        /// </summary>
        [Fact]
        public void A_Type_This_Build_Does_Not_Have_Binds_To_No_Member_At_All()
        {
            var bytes = Fixture("PolyUserType.bin");
            var graph = OldFileGraph.Read(bytes);
            var plans = BuildPlans(bytes, opaque: false);

            // A removed type, with the plan saying so rather than the binder discovering it.
            Assert.Contains(plans, kv => kv.Value.CurrentType == null && kv.Value.Mappings!.Length > 0);
            Assert.Contains(plans, kv => kv.Value.CurrentType == null);

            var removedTypeNames = plans.Where(kv => kv.Value.CurrentType == null).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            int emptied = 0;
            for (int i = 0; i < graph.NodeCount; i++)
            {
                if (graph.Node(i) is not OldObjectNode o) continue;
                if (!removedTypeNames.Contains(o.Type.StableName)) continue;
                int recorded = o.RecordedMembers.Count;
                Assert.NotEmpty(o.Members);
                emptied++;
            }

            new GraphMigrationBinder(plans).Bind(graph);

            for (int i = 0; i < graph.NodeCount; i++)
            {
                if (graph.Node(i) is not OldObjectNode o) continue;
                if (!removedTypeNames.Contains(o.Type.StableName)) continue;
                Assert.Empty(o.Members);
                Assert.NotEmpty(o.RecordedMembers);
            }
            Assert.True(emptied > 0, "this fixture is only interesting if it holds a type this build has not got");
        }

        // ── 4b. Removed-only: the projection is emptied and nothing is left holding a name ──────────

        /// <summary>
        /// A removed type, bound, leaves a state a later pass can read without re-deriving anything: the
        /// projection is empty, the audit trail is whole, and — the part worth stating — <em>no member is
        /// left holding a current field name</em>. A removed member is the same object as the recorded one,
        /// so a name left on it would say the opposite of the projection: the record would claim a
        /// destination the binding has already declared there is none.
        /// </summary>
        [Fact]
        public void A_Removed_Only_Type_Leaves_No_Member_Holding_A_Current_Field_Name()
        {
            var bytes = Fixture("PolyUserType.bin");
            var graph = OldFileGraph.Read(bytes);
            var plans = BuildPlans(bytes, opaque: false);
            var removedTypeNames = plans.Where(kv => kv.Value.CurrentType == null).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

            new GraphMigrationBinder(plans).Bind(graph);

            int checkedNodes = 0;
            for (int i = 0; i < graph.NodeCount; i++)
            {
                if (graph.Node(i) is not OldObjectNode o) continue;
                if (!removedTypeNames.Contains(o.Type.StableName)) continue;
                checkedNodes++;

                Assert.Empty(o.Members);
                Assert.NotEmpty(o.RecordedMembers);
                // The audit trail is the file's own account and carries no binding at all.
                Assert.All(o.RecordedMembers, m => Assert.Null(m.CurrentFieldName));
            }
            Assert.True(checkedNodes > 0, "this fixture is only interesting if it holds a type this build has not got");
        }

        /// <summary>
        /// The same state reached the other way: a removed type whose file happened to describe it with no
        /// members at all. An empty projection with a plan that describes no members is a pair that agrees,
        /// and it must not be mistaken for the disagreement in the next test.
        /// </summary>
        [Fact]
        public void A_Removed_Only_Type_With_No_Recorded_Members_Is_Not_A_Disagreement()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Gone");
            var obj = hand.Object(identity);          // no members at all
            var graph = hand.Build(obj);

            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                ["Old.Gone"] = PlanFor(null),
            };

            // Bound without a word: nothing to name, nothing to remove, and no current type to be
            // ambiguous about.
            new GraphMigrationBinder(plans).Bind(graph);

            Assert.Empty(obj.Members);
            Assert.Empty(obj.RecordedMembers);
        }

        // ── 5. A file the current plan builder refuses ────────────────────────────────────────────

        /// <summary>
        /// A file whose schema this build refuses never reaches the binder, and the reason is a
        /// <b>plan-stage</b> refusal, not a binding one.
        /// <para>
        /// There are two such doors and `Load` opens them in a fixed order, so this asks for them in that
        /// order too: <see cref="CerasRobustSerializer.BuildIdToType"/> first, which refuses a name the
        /// file's section carries and nothing resolves, and then <see cref="MigrationPlanBuilder.BuildAll"/>,
        /// which refuses a member or a structural argument the current model cannot be migrated to. Which
        /// door turns a given file away is not a detail — it is measured here, and reported by name, rather
        /// than assumed to be the second one. (The first draft of this test assumed the second and found
        /// that one of its rows is really the first door's.)
        /// </para>
        /// <para>
        /// What this adds to the existing refusal tests is only the position of binding in the order: no plan
        /// for these files survives, so there is nothing to bind, and the binder is not what turned them
        /// away.
        /// </para>
        /// </summary>
        [Fact]
        public void A_File_The_Plan_Builder_Refuses_Never_Reaches_The_Binder()
        {
            // The context each of these is refused under, which is the context the existing refusal test
            // for that direction already pins. A refusal is a property of a file *and a current model*: the
            // same bytes build plans under a context whose current model happens to declare the member
            // compatibly, and are refused under the one that does not.
            var refused = new (string Name, string Context)[]
            {
                // The retyped-member refusal: an element this build has no defined conversion out of.
                ("InheritedNarrowRefused.bin", "HistoricalInheritedCurrent"),
                // The array-payload refusals: the reference marker moves, so there is no common reading.
                ("ArrayShapeToObject.bin", "HistoricalArrayShapeCurrent"),
                ("ArrayShapeFromArray.bin", "HistoricalArrayShapeCurrent"),
                ("ArrayShapeFromObject.bin", "HistoricalArrayShapeCurrent"),
                ("ArrayShapeToList.bin", "HistoricalArrayShapeCurrent"),
                // The structural-argument refusals, one level down: a collection argument that no longer
                // names the same current CLR type, or a type the current model has removed.
                ("NestedRemovedArg.bin", "HistoricalNestedShapeCurrent"),
                ("NestedDictKeyChange.bin", "HistoricalNestedShapeCurrent"),
                ("NestedListFromObject.bin", "HistoricalNestedShapeCurrent"),
                ("NestedDeepArgChange.bin", "HistoricalNestedShapeCurrent"),
                ("NestedDeepToObject.bin", "HistoricalNestedShapeCurrent"),
            };

            var refusedByNameResolution = 0;
            var refusedByPlanShape = 0;

            foreach (var (name, context) in refused)
            {
                var bytes = Fixture(name);
                var schemas = ParseSchemaSection(bytes);
                var reader = HistoricalFixtureFiles.MigrationReader(context);
                var registry = reader.Config.SchemaRegistry;
                registry.Seal();

                // Door one, exactly where Load puts it, and before any plan exists.
                Exception first = null;
                try { CerasRobustSerializer.BuildIdToType(schemas, registry, reader.Config.TypeBinder ?? new SimpleTypeBinder()); }
                catch (Exception e) { first = e; }

                if (first != null)
                {
                    Assert.IsType<CerasSchemaException>(first);
                    refusedByNameResolution++;
                    continue;
                }

                // Door two.
                Exception second = null;
                try { MigrationPlanBuilder.BuildAll(schemas, registry, allowDroppingCustomPayloads: false); }
                catch (Exception e) { second = e; }

                Assert.True(second is CerasSchemaException,
                    $"{name} under context '{context}': expected a plan-stage refusal, and none was raised. " +
                    (second == null
                        ? "If this direction has become migratable, remove the row and pin what it now migrates."
                        : $"It threw {second.GetType().Name} instead: {second.Message}"));
                refusedByPlanShape++;
            }

            // Both doors are exercised, so this file is not quietly resting on one of them.
            Assert.Equal(refused.Length, refusedByNameResolution + refusedByPlanShape);
            Assert.True(refusedByPlanShape > 0, "no row was refused by the plan-shape door, so that half is uncovered");
        }

        // ── 6. The correspondence refusals, on hand-built graphs ──────────────────────────────────

        /// <summary>
        /// A plan and a graph that disagree about a member's <em>name</em> but not about its position.
        /// This is the mistake that would otherwise be invisible: the binder would map position 0 onto
        /// position 0's field, write a value of one type into a field of another, and the failure would
        /// surface much later as a cast exception with no connection to the cause. So it is refused here,
        /// where both spellings are still in hand.
        /// </summary>
        [Fact]
        public void A_Plan_And_A_Graph_That_Disagree_About_A_Member_Name_Are_Refused()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Crate");
            var obj = hand.RecordedObject(identity, new OldValue[] { hand.Int(1), hand.Int(2) },
                new[] { ("First", WellKnownTypes.Int32), ("Second", WellKnownTypes.String) });
            var graph = hand.Build(obj);

            // A plan for a type that recorded "Renamed" in the position the graph records as "Second".
            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                ["Old.Crate"] = PlanFor(null,
                    Mapping("First", WellKnownTypes.Int32, typeof(InvocableField)),
                    Mapping("Renamed", WellKnownTypes.String, typeof(InvocableField))),
            };

            var error = Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(plans).Bind(graph));
            Assert.Contains("Old.Crate", error.Message, StringComparison.Ordinal);
            Assert.Contains("Second", error.Message, StringComparison.Ordinal);
            Assert.Contains("Renamed", error.Message, StringComparison.Ordinal);
            Assert.Contains("Internal error", error.Message, StringComparison.Ordinal);

            // Refused before anything was written, so the graph is exactly as it was handed over.
            Assert.Equal(2, obj.Members.Count);
            Assert.All(obj.Members, m => Assert.Null(m.CurrentFieldName));
        }

        /// <summary>
        /// The same mistake one level harder to see: both sides agree on the member's name and disagree on
        /// what it holds. A name check alone would pass this, and the binder would then put an `int` where
        /// a `string` is declared. The type id is compared for exactly that reason, and this is the test
        /// that says the check is there on purpose.
        /// </summary>
        [Fact]
        public void A_Plan_And_A_Graph_That_Disagree_About_A_Member_Type_Are_Refused()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Crate");
            var obj = hand.RecordedObject(identity, new OldValue[] { hand.Int(1), hand.Int(2) },
                new[] { ("Value", WellKnownTypes.Int32), ("Other", WellKnownTypes.Int32) });
            var graph = hand.Build(obj);

            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                // Same name, same position, same count — and the plan believes the second member is a
                // string. Nothing but the recorded type id tells the two sides apart here.
                ["Old.Crate"] = PlanFor(null,
                    Mapping("Value", WellKnownTypes.Int32, typeof(InvocableField)),
                    Mapping("Other", WellKnownTypes.String, typeof(InvocableField))),
            };

            var error = Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(plans).Bind(graph));
            Assert.Contains("Other", error.Message, StringComparison.Ordinal);
            Assert.Contains("type id", error.Message, StringComparison.Ordinal);
            Assert.Equal(2, obj.Members.Count);
            Assert.All(obj.Members, m => Assert.Null(m.CurrentFieldName));
        }

        /// <summary>
        /// The count clause, which is the one that fires when a plan is handed to a graph built from a
        /// *different* file — the mistake that produces a member list of the wrong length entirely, and
        /// where reading past the end would be a bare <c>IndexOutOfRangeException</c> naming nothing.
        /// </summary>
        [Fact]
        public void A_Plan_And_A_Graph_Of_Different_Lengths_Are_Refused()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Crate");
            var obj = hand.RecordedObject(identity, new OldValue[] { hand.Int(1) }, new[] { ("Only", WellKnownTypes.Int32) });
            var graph = hand.Build(obj);

            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                ["Old.Crate"] = PlanFor(null,
                    Mapping("Only", WellKnownTypes.Int32, typeof(InvocableField)),
                    Mapping("Ghost", WellKnownTypes.String, typeof(InvocableField))),
            };

            var error = Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(plans).Bind(graph));
            Assert.Contains("Old.Crate", error.Message, StringComparison.Ordinal);
            Assert.Single(obj.Members);
        }

        /// <summary>
        /// A member with no recorded half is one a transformation synthesised — the inlining primitive's
        /// output. A plan is built from the file's section, which has no entry for it, so the binder
        /// refuses rather than binding by position against a member the file never wrote.
        /// </summary>
        [Fact]
        public void A_Member_With_No_Recorded_Identity_Is_Refused()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Crate");
            var obj = hand.RecordedObject(identity, new OldValue[] { hand.Int(1) }, new[] { ("Value", WellKnownTypes.Int32) });
            var graph = hand.Build(obj);

            // The inlining shape: a real member, and beside it one that was never in the file. The plan
            // still describes two positions, so the count agrees and the *recorded identity* is the clause
            // that has to fire — which is the point: a synthesized member is invisible to every other check.
            obj.Members.Add(new OldMember(recorded: null, hand.Int(99), currentFieldName: null));

            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                ["Old.Crate"] = PlanFor(null,
                    Mapping("Value", WellKnownTypes.Int32, typeof(InvocableField)),
                    Mapping("Inlined", WellKnownTypes.Int32, typeof(InvocableField))),
            };

            var error = Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(plans).Bind(graph));
            Assert.Contains("no recorded identity", error.Message, StringComparison.Ordinal);
            Assert.Contains("member 1", error.Message, StringComparison.Ordinal);
        }

        // ── 6b. An object with no plan it should have had ────────────────────────────────────────

        /// <summary>
        /// An object with members and nothing to bind them against.
        /// <para>
        /// The first draft of this pass treated "no plan" and "no members" as two shapes to skip, and the
        /// only reason that was wrong is that a third shape exists: an object with members that no plan
        /// describes. It is not reachable from a real save — a plan is built for every type the file's
        /// section carries, and the two identities that legitimately have no plan (a bare
        /// <c>System.Object</c>, and a shape decoded from a <c>Type</c> value) are a memberless object and a
        /// non-object respectively — so a hand-built graph is the only way to reach it, and reaching it
        /// silently is exactly the "the binder was quiet" failure this stage keeps having to rule out.
        /// </para>
        /// </summary>
        [Fact]
        public void An_Object_With_Members_And_No_Plan_Is_Refused()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Unplanned");
            var obj = hand.RecordedObject(identity, new OldValue[] { hand.Int(1) }, new[] { ("Value", WellKnownTypes.Int32) });
            var graph = hand.Build(obj);

            var error = Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(new Dictionary<string, TypeMigrationPlan>()).Bind(graph));

            Assert.Contains("Old.Unplanned", error.Message, StringComparison.Ordinal);
            Assert.Contains("no migration plan", error.Message, StringComparison.Ordinal);
            Assert.Single(obj.Members);
            Assert.All(obj.Members, m => Assert.Null(m.CurrentFieldName));
        }

        /// <summary>
        /// The other side of the same question: an empty projection that a plan says should have members.
        /// A plan is built from the file's section, and every member of that section is recorded, so "the
        /// object has no members" and "the plan describes some" cannot both be true of one file — and if
        /// they are, the two halves are from different files.
        /// </summary>
        [Fact]
        public void An_Empty_Object_With_A_Plan_That_Describes_Members_Is_Refused()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Crate");
            var obj = hand.Object(identity);          // no members
            var graph = hand.Build(obj);

            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                ["Old.Crate"] = PlanFor(null,
                    Mapping("Only", WellKnownTypes.Int32, typeof(InvocableField)),
                    Mapping("Other", WellKnownTypes.String, typeof(InvocableField))),
            };

            var error = Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(plans).Bind(graph));
            Assert.Contains("Old.Crate", error.Message, StringComparison.Ordinal);
            Assert.Empty(obj.Members);
        }

        // ── 6c. Ambiguity: two old members, one destination ─────────────────────────────────────

        /// <summary>
        /// Two members the file recorded, and one current field to put them in. Nothing in the save says
        /// which value belongs where, so a rule that picked one would be Ceras inventing an answer to a
        /// question the data does not answer — and the answer it produced would be right for whichever file
        /// happened to be loaded first.
        /// </summary>
        [Fact]
        public void Two_Old_Members_Mapping_Onto_One_Current_Field_Are_Refused()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Crate");
            var obj = hand.RecordedObject(identity, new OldValue[] { hand.Int(1), hand.Int(2) },
                new[] { ("Health", WellKnownTypes.Int32), ("Hp", WellKnownTypes.Int32) });
            var graph = hand.Build(obj);

            // Both names resolve to the one field: the file renamed a member and the two spellings were
            // both kept, which is a legitimate thing to write and an ambiguous thing to read.
            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                ["Old.Crate"] = PlanFor(null,
                    Mapping("Health", WellKnownTypes.Int32, typeof(InvocableField)),
                    Mapping("Hp", WellKnownTypes.Int32, typeof(InvocableField))),
            };

            var error = Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(plans).Bind(graph));
            Assert.Contains("Health", error.Message, StringComparison.Ordinal);
            Assert.Contains("Hp", error.Message, StringComparison.Ordinal);
            Assert.Contains("Value", error.Message, StringComparison.Ordinal);
            Assert.Equal(2, obj.Members.Count);
            Assert.All(obj.Members, m => Assert.Null(m.CurrentFieldName));
        }

        // ── 6d. Ambiguity: two current members, one wire name ───────────────────────────────────

        /// <summary>
        /// The failure the plan cannot see, and the reason this pass looks at the current type at all.
        /// <para>
        /// <c>MigrationPlanBuilder</c> resolves an old name by inserting into a map with first-wins, so when
        /// two members answer to one name, the second is not unmapped — it is <em>absent</em>. It appears in
        /// no mapping, in no <c>AddedFieldNames</c> entry that names it as itself, and nowhere in the graph.
        /// The file's value for it has nowhere to go, and the report says the member was removed rather
        /// than that two names are contested.
        /// </para>
        /// <para>
        /// So the check is asked of the <em>type</em>: which members does this build have, and which name
        /// does each answer to? Two distinct members answering to one name is the ambiguity. One member
        /// answering to several is not — that is what <c>[PreviousName]</c> is for, and the next test says
        /// the distinction is drawn.
        /// </para>
        /// </summary>
        [Fact]
        public void Two_Current_Members_Answering_To_One_Wire_Name_Are_Refused()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Crate");
            var obj = hand.RecordedObject(identity, new OldValue[] { hand.Int(1), hand.Int(2) },
                new[] { ("First", WellKnownTypes.Int32), ("Second", WellKnownTypes.Int32) });
            var graph = hand.Build(obj);

            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                ["Old.Crate"] = PlanFor(typeof(ContestedNames),
                    Mapping("First", WellKnownTypes.Int32, FieldOf<ContestedNames>(nameof(ContestedNames.Left))),
                    Mapping("Second", WellKnownTypes.Int32, FieldOf<ContestedNames>(nameof(ContestedNames.Right)))),
            };

            var error = Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(plans).Bind(graph));
            Assert.Contains("answering to the wire name", error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(ContestedNames.Left), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(ContestedNames.Right), error.Message, StringComparison.Ordinal);
            Assert.Equal(2, obj.Members.Count);
            Assert.All(obj.Members, m => Assert.Null(m.CurrentFieldName));
        }

        /// <summary>
        /// The same collision across the inheritance boundary, which is where it does the most damage: the
        /// map is built derived-first, so a derived member's <c>[PreviousName]</c> takes the name before
        /// the base member that owns it is ever considered, and the base member ends up with no name at all.
        /// <para>
        /// The refusal names the derived member, because that is the one that took the name and the one the
        /// author can act on. What makes this worth its own test is that the *base* member is the victim
        /// and the derived one is the one in the plan, so a check written against the plan alone would see
        /// a consistent, fully-mapped member set and find nothing.
        /// </para>
        /// </summary>
        [Fact]
        public void A_Derived_Member_Claiming_A_Base_Members_Wire_Name_Is_Refused()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Crate");
            var obj = hand.RecordedObject(identity, new OldValue[] { hand.Int(1) }, new[] { ("Shared", WellKnownTypes.Int32) });
            var graph = hand.Build(obj);

            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                // The file's "Shared" lands on the derived member — which is exactly what the plan's map
                // does, because it is built derived-first and the alias was inserted first.
                ["Old.Crate"] = PlanFor(typeof(HiddenDerivedMember),
                    Mapping("Shared", WellKnownTypes.Int32, FieldOf<HiddenDerivedMember>(nameof(HiddenDerivedMember.Derived)))),
            };

            var error = Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(plans).Bind(graph));
            Assert.Contains("answering to the wire name", error.Message, StringComparison.Ordinal);
            Assert.Contains("Shared", error.Message, StringComparison.Ordinal);
            Assert.Single(obj.Members);
            Assert.All(obj.Members, m => Assert.Null(m.CurrentFieldName));
        }

        /// <summary>
        /// The half of that distinction which must <em>not</em> be refused: one member answering to its own
        /// stable name and its own aliases. That is what <c>[PreviousName]</c> is for, and a check that
        /// compared names instead of fields would refuse it — and refuse the whole fixture corpus, since a
        /// renamed member is the common case rather than the rare one.
        /// </summary>
        [Fact]
        public void One_Member_Answering_To_Its_Own_Aliases_Is_Not_Ambiguous()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Crate");
            var obj = hand.RecordedObject(identity, new OldValue[] { hand.Int(1) }, new[] { ("WasCalledThis", WellKnownTypes.Int32) });
            var graph = hand.Build(obj);

            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                // The file's old name reaches the renamed member, and the member answers to both.
                ["Old.Crate"] = PlanFor(typeof(OwnAliases),
                    Mapping("WasCalledThis", WellKnownTypes.Int32, FieldOf<OwnAliases>(nameof(OwnAliases.Renamed)))),
            };

            new GraphMigrationBinder(plans).Bind(graph);

            Assert.Single(obj.Members);
            Assert.Equal(nameof(OwnAliases.Renamed), obj.Members[0].CurrentFieldName);
        }

        // ── 6e. A refused bind leaves the graph exactly as it was ────────────────────────────────

        /// <summary>
        /// The property every refusal above depends on and none of them can show on its own: a refused
        /// <c>Bind</c> changes nothing at all.
        /// <para>
        /// A pass that bound what it could and refused the rest would leave a graph whose set names look
        /// exactly like one that had been bound deliberately, and no later reader could tell the difference
        /// — the failure mode is silence, not a wrong value. So the two halves are checked together: this
        /// object would bind cleanly on its own, and the refusal comes from the *other* object in the same
        /// graph, later in arena order.
        /// </para>
        /// </summary>
        [Fact]
        public void A_Refused_Bind_Leaves_Every_Member_Of_Every_Object_Untouched()
        {
            var hand = new Hand();
            var clean = hand.OldType("Old.Clean");
            var broken = hand.OldType("Old.Broken");

            var cleanObj = hand.RecordedObject(clean, new OldValue[] { hand.Int(1), hand.Int(2) },
                new[] { ("First", WellKnownTypes.Int32), ("Second", WellKnownTypes.String) });
            var brokenObj = hand.RecordedObject(broken, new OldValue[] { hand.Int(2) },
                new[] { ("Only", WellKnownTypes.Int32) });
            var graph = hand.Build(cleanObj);

            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                // The first object would bind: two distinct destinations, both fields named by the file, and
                // a current type with no contested name of its own.
                ["Old.Clean"] = PlanFor(typeof(InvocableField),
                    Mapping("First", WellKnownTypes.Int32, FieldOf<InvocableField>(nameof(InvocableField.Value))),
                    Mapping("Second", WellKnownTypes.String, FieldOf<InvocableField>(nameof(InvocableField.Text)))),
                // The second cannot: the plan describes two members and the graph records one.
                ["Old.Broken"] = PlanFor(null,
                    Mapping("Only", WellKnownTypes.Int32, typeof(InvocableField)),
                    Mapping("Ghost", WellKnownTypes.String, typeof(InvocableField))),
            };

            // Sanity: the first object really is bindable on its own, so the assertion below is about the
            // refusal and not about a graph that was never going to bind. A second Hand, because an arena
            // holds each value once.
            var control = new Hand();
            var controlIdentity = control.OldType("Old.Clean");
            var controlGraph = control.Build(control.RecordedObject(controlIdentity, new OldValue[] { control.Int(9), control.Int(10) },
                new[] { ("First", WellKnownTypes.Int32), ("Second", WellKnownTypes.String) }));

            new GraphMigrationBinder(new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal) { ["Old.Clean"] = plans["Old.Clean"] })
                .Bind(controlGraph);

            Assert.All(Assert.IsType<OldObjectNode>(controlGraph.Root).Members, m => Assert.NotNull(m.CurrentFieldName));

            Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(plans).Bind(graph));

            // The object that would have bound cleanly was never touched, even though it comes first.
            Assert.Equal(2, cleanObj.Members.Count);
            Assert.All(cleanObj.Members, m => Assert.Null(m.CurrentFieldName));
            Assert.Equal(2, cleanObj.RecordedMembers.Count);

            Assert.Single(brokenObj.Members);
            Assert.All(brokenObj.Members, m => Assert.Null(m.CurrentFieldName));
        }

        /// <summary>
        /// Binding twice is refused rather than repeated. A member that already carries a name is either
        /// the output of a previous bind or an edit made since, and overwriting either would make a second
        /// opinion indistinguishable from the first — so the pass is a transition a graph makes once.
        /// </summary>
        [Fact]
        public void Binding_Twice_Is_Refused_Rather_Than_Repeated()
        {
            var hand = new Hand();
            var identity = hand.OldType("Old.Crate");
            var obj = hand.RecordedObject(identity, new OldValue[] { hand.Int(1) }, new[] { ("Only", WellKnownTypes.Int32) });
            var graph = hand.Build(obj);

            var plans = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal)
            {
                ["Old.Crate"] = PlanFor(null, Mapping("Only", WellKnownTypes.Int32, typeof(InvocableField))),
            };

            new GraphMigrationBinder(plans).Bind(graph);
            Assert.Equal(nameof(InvocableField.Value), obj.Members[0].CurrentFieldName);

            var error = Assert.Throws<CerasSchemaException>(() => new GraphMigrationBinder(plans).Bind(graph));
            Assert.Contains("already carries", error.Message, StringComparison.Ordinal);
            Assert.Equal(nameof(InvocableField.Value), obj.Members[0].CurrentFieldName);
        }

        // ── 7. Determinism, and the object-independence the stage rests on ────────────────────────

        /// <summary>
        /// The same save, bound twice from two independently decoded graphs, has to produce the same
        /// bindings. Without this, a binding difference between two readers could be a hash-order
        /// artifact, and "the two readers agree" would be a statement about one run.
        /// </summary>
        [Fact]
        public void Binding_Is_Deterministic_Across_Independent_Reads()
        {
            var bytes = Fixture("SaveGame.bin");

            var first = OldFileGraph.Read(bytes);
            new GraphMigrationBinder(BuildPlans(bytes, opaque: false)).Bind(first);

            var second = OldFileGraph.Read(bytes);
            new GraphMigrationBinder(BuildPlans(bytes, opaque: false)).Bind(second);

            Assert.Equal(BindingOf(first), BindingOf(second));
            Assert.NotEmpty(BindingOf(first));
        }

        /// <summary>
        /// Binding takes a graph and a plan map and nothing else: no serializer, no config, no registry,
        /// no formatter. That is the property the whole stage rests on, because it is what makes the
        /// binding provable on its own — and it is structural rather than a convention, so it is asserted
        /// structurally too: the type has no constructor that takes a serializer, and its only member is
        /// the plan map it was handed.
        /// </summary>
        [Fact]
        public void The_Binder_Needs_Nothing_But_A_Plan_Map()
        {
            var constructors = typeof(GraphMigrationBinder).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var binder = Assert.Single(constructors);

            var parameters = binder.GetParameters();
            var plans = Assert.Single(parameters);
            Assert.Equal(typeof(Dictionary<string, TypeMigrationPlan>), plans.ParameterType);

            // And the fields: there is nowhere for a serializer or a config to be kept.
            var fields = typeof(GraphMigrationBinder).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.Single(fields);
            Assert.Equal(typeof(Dictionary<string, TypeMigrationPlan>), fields[0].FieldType);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The recorded names still in the current projection, in projection order.</summary>
        static string[] ProjectedNames(OldObjectNode node) => node.Members.Select(m => m.Recorded!.StableName).ToArray();

        /// <summary>
        /// Every wire name a current type's real members answer to, derived independently of the plan —
        /// so "this member was bound to a name the current model uses" is checked against the model and
        /// not against the plan that produced the binding.
        /// </summary>
        static HashSet<string> RealWireNames(Type type)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                foreach (var f in current.GetFields(flags))
                {
                    if (f.DeclaringType != current) continue;
                    if (f.GetCustomAttribute<ExcludeAttribute>() != null) continue;
                    if (f.GetCustomAttribute<NonSerializedAttribute>() != null) continue;

                    string name = f.Name;
                    PropertyInfo property = null;
                    if (name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                    {
                        var propertyName = name.Substring(1, name.Length - 1 - ">k__BackingField".Length);
                        property = f.DeclaringType?.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                        name = propertyName;
                    }
                    if (property == null && f.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() != null) continue;
                    if (property?.GetCustomAttribute<ExcludeAttribute>() != null) continue;
                    if (property?.GetCustomAttribute<NonSerializedAttribute>() != null) continue;

                    var stable = f.GetCustomAttribute<CerasStableNameAttribute>() ?? property?.GetCustomAttribute<CerasStableNameAttribute>();
                    if (stable?.Name != null) name = stable.Name;
                    names.Add(name);

                    foreach (var member in new[] { (MemberInfo)f, property })
                    {
                        if (member == null) continue;
                        foreach (var previous in member.GetCustomAttributes<PreviousNameAttribute>())
                        {
                            if (previous.Name != null) names.Add(previous.Name);
                            foreach (var alternative in previous.AlternativeNames) if (alternative != null) names.Add(alternative);
                        }
                    }
                }
            return names;
        }

        /// <summary>
        /// Every bound member names a wire name the current type really uses, and every unbound one names
        /// nothing at all. The first half is what "bound" means; the second is what "removed" means, and
        /// conflating them is how a materializer would end up looking a field up that was never there.
        /// </summary>
        static void AssertBoundAgainstCurrentType(OldObjectNode node, Type currentType)
        {
            var real = RealWireNames(currentType);
            foreach (var member in node.Members)
            {
                Assert.NotNull(member.CurrentFieldName);
                Assert.Contains(member.CurrentFieldName, real);
            }
        }

        /// <summary>
        /// The whole observable state of a graph, as the comparisons above read it. Recorded member
        /// identities, the projection's bound names, the arena indices, and every back-reference target.
        /// </summary>
        static GraphSnapshot Snapshot(OldDataGraph graph)
        {
            var recorded = new List<string>();
            var projection = new List<KeyValuePair<string, List<string>>>();
            var indices = new List<string>();
            var backReferences = new List<string>();

            for (int i = 0; i < graph.NodeCount; i++)
            {
                var node = graph.Node(i);
                indices.Add($"{i}:{node!.GetType().Name}");

                switch (node)
                {
                    case OldObjectNode o:
                        recorded.AddRange(o.RecordedMembers.Select(m => $"{o.Type.StableName}.{m.Recorded?.StableName ?? "<synthesized>"}:{m.Recorded?.Type.TypeId}"));
                        projection.Add(new(o.Type.StableName, o.Members.Select(m => m.Recorded?.StableName ?? "<synthesized>").ToList()));
                        break;
                    case OldReferenceNode r:
                        backReferences.Add($"{i}->{r.TargetIndex}");
                        break;
                }
            }

            return new(recorded, projection, indices, backReferences);
        }

        /// <summary>Every bound name in the graph, in arena order: the binder's whole output.</summary>
        static string[] BindingOf(OldDataGraph graph)
        {
            var bound = new List<string>();
            for (int i = 0; i < graph.NodeCount; i++)
                if (graph.Node(i) is OldObjectNode o)
                    bound.AddRange(o.Members.Where(m => m.CurrentFieldName != null).Select(m => $"{o.Type.StableName}.{m.CurrentFieldName}"));
            return bound.ToArray();
        }

        readonly struct GraphSnapshot
        {
            public readonly List<string> Recorded;
            public readonly List<KeyValuePair<string, List<string>>> Projection;
            public readonly List<string> Indices;
            public readonly List<string> BackReferences;

            public GraphSnapshot(List<string> recorded, List<KeyValuePair<string, List<string>>> projection,
                                  List<string> indices, List<string> backReferences)
            {
                Recorded = recorded;
                Projection = projection;
                Indices = indices;
                BackReferences = backReferences;
            }
        }

        // ── the plan and the graph, built for these tests ─────────────────────────────────────────

        /// <summary>
        /// Stands in for the builder: every value is appended in the writer's first-occurrence order, which
        /// is what makes that order the arena's index space. A recorded null would be the one thing that
        /// takes no index, and these tests do not need one.
        /// </summary>
        sealed class Hand
        {
            readonly List<OldValue> _nodes = new();

            public OldTypeIdentity Int32 { get; } =
                new(WellKnownTypes.Int32, "System.Int32", null, SchemaTypeKind.Struct);

            public OldTypeIdentity Str { get; } =
                new(WellKnownTypes.String, "System.String", null, SchemaTypeKind.Struct);

            public T Append<T>(T value) where T : OldValue { _nodes.Add(value); return value; }

            public OldScalar Int(int value) => Append(new OldScalar(Int32, value));

            /// <summary>
            /// A manifest-id identity standing for a type the file's section describes. An identity is a
            /// value and not a node (rule 1), so it takes no arena index and is not appended.
            /// </summary>
            public OldTypeIdentity OldType(string name)
                => new(WellKnownTypes.UserTypeBase + 1, name, null, SchemaTypeKind.Class);

            public OldObjectNode Object(OldTypeIdentity type, params OldValue[] values)
                => Append(new OldObjectNode(type, values.Select(v => new OldMember(null, v)).ToList()));

            /// <summary>
            /// An object whose members carry a recorded identity, which is what the real builder produces
            /// and what the correspondence check reads. Named apart from <see cref="Object(OldTypeIdentity,
            /// OldValue[])"/> because a <c>params OldValue[]</c> overload and a trailing array of tuples are
            /// the same call to the compiler.
            /// </summary>
            public OldObjectNode RecordedObject(OldTypeIdentity type, OldValue[] values, (string Name, int TypeId)[] recorded)
            {
                if (values.Length != recorded.Length) throw new ArgumentException("one recorded identity per value");
                var members = recorded
                    .Select((r, i) => new OldMember(new OldFieldIdentity(r.Name, Array.Empty<string>(), IdentityOf(r.TypeId)), values[i]))
                    .ToList();
                return Append(new OldObjectNode(type, members));
            }

            OldTypeIdentity IdentityOf(int typeId)
                => typeId == WellKnownTypes.Int32 ? Int32
                 : typeId == WellKnownTypes.String ? Str
                 : new OldTypeIdentity(typeId, $"Type{typeId}", null, SchemaTypeKind.Struct);

            public OldDataGraph Build(OldValue root) => new(_nodes, root);
        }

        static FieldMapping Mapping(string name, int typeId, FieldInfo newField)
            => new()
            {
                OldField = new OldFieldSchema(name, Array.Empty<string>(), typeId, true),
                NewField = newField,
                Coercion = CoercionKind.None,
            };

        static FieldMapping Mapping(string name, int typeId, Type newFieldType)
            => Mapping(name, typeId, newFieldType.GetField(nameof(InvocableField.Value)));

        /// <summary>The field a hand-written mapping names, named by its own C# name.</summary>
        static FieldInfo FieldOf<T>(string fieldName)
            => typeof(T).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public)
               ?? throw new ArgumentException($"{typeof(T).Name} has no public field '{fieldName}'");

        /// <summary>
        /// A plan for one type, over hand-written mappings. <c>TypeMigrationPlan</c> is only ever built by
        /// <c>MigrationPlanBuilder</c>, so this is the one place a test needs a plan the builder would not
        /// produce — which is exactly what the correspondence and ambiguity refusals are for.
        /// <para>
        /// <paramref name="currentType"/> is what the plan claims this build's counterpart is, and is
        /// <em>not</em> derived from the graph's identity on purpose: a test that needs two current members
        /// claiming one name has to be able to say so about a type that is not the one the file names.
        /// </para>
        /// </summary>
        static TypeMigrationPlan PlanFor(Type currentType, params FieldMapping[] mappings)
            => new()
            {
                CurrentType = currentType,
                Mappings = mappings,
                AddedFieldNames = Array.Empty<string>(),
                HasChanges = true,
            };

        /// <summary>
        /// A destination a hand-written mapping can point at. It exists because <see cref="FieldMapping"/>
        /// carries a <see cref="FieldInfo"/> and a mapping has to name a real one, and two plainly named
        /// fields are easier to state than a private field of an unrelated model type would be.
        /// </summary>
        public sealed class InvocableField
        {
            public int Value;
            public string Text;
        }

        // ── the models the ambiguity refusals are about ──────────────────────────────────────────

        /// <summary>
        /// Two members of one type answering to the same wire name. The alias is declared on both, so the
        /// first one the map reaches keeps it and the second keeps only its own stable name — the shape
        /// that makes a name ambiguous for a *reader* even though nothing here is ill-formed.
        /// </summary>
        public sealed class ContestedNames
        {
            [PreviousName("Target")] public int Left;
            [PreviousName("Target")] public int Right;
        }

        /// <summary>
        /// The same collision across the inheritance boundary, which is the case that costs data: the map
        /// is built derived-first, so <c>Derived</c> claims the base's wire name as an alias before the base
        /// member is ever seen, and the base member is left with no name at all.
        /// </summary>
        public class HiddenBaseMember
        {
            public int Shared;
        }

        public sealed class HiddenDerivedMember : HiddenBaseMember
        {
            [PreviousName(nameof(HiddenBaseMember.Shared))] public int Derived;
        }

        /// <summary>
        /// The shape that must <em>not</em> be refused: one member answering to its own stable name and its
        /// own alias. A renamed member is the common case, and a check comparing names rather than fields
        /// would refuse it.
        /// </summary>
        public sealed class OwnAliases
        {
            [PreviousName("WasCalledThis")] public int Renamed;
        }

        // ── the plans, and the graphs they are for ────────────────────────────────────────────────

        /// <summary>
        /// The plan set for one file, built by the current <see cref="MigrationPlanBuilder"/> from the
        /// file's own schema section against a sealed registry — the same call
        /// <c>CerasRobustSerializer.Load</c> makes before it reads a single payload byte, and deliberately
        /// the only source of binding decisions here.
        /// </summary>
        static Dictionary<string, TypeMigrationPlan> BuildPlans(byte[] bytes, bool opaque)
        {
            var registry = opaque
                ? new CerasRobustSerializer(c =>
                {
                    c.UseGeneratedFormatters("OpaqueFixtureCurrent");
                    c.ConfigType<OpaqueFixtures.RecipeRef>().CustomFormatter = new Ceras.HistoricalWriter.V1Opaque.RecipeRefFormatter();
                }).Config.SchemaRegistry
                : HistoricalFixtureFiles.MigrationReader(Ctx).Config.SchemaRegistry;

            registry.Seal();
            return MigrationPlanBuilder.BuildAll(ParseSchemaSection(bytes), registry, allowDroppingCustomPayloads: false);
        }

        /// <summary>
        /// A file's schema section, decoded the way the load path decodes it: the schema region is
        /// <c>[header, dataSectionOffset)</c>, and its own string table is never compressed whatever the
        /// payload's compression flag says.
        /// </summary>
        static Dictionary<string, OldTypeSchema> ParseSchemaSection(byte[] data)
        {
            RobustHeader.Read(data, out _, out _, out long schemaStringOffset, out long dataSectionOffset, out _, out _);
            var strings = StringTable.Load(data, (int)schemaStringOffset,
                (int)dataSectionOffset - (int)schemaStringOffset, StringTableCompression.Disabled);

            var sequence = new ReadOnlySequence<byte>(data, RobustHeader.Size, (int)schemaStringOffset - RobustHeader.Size);
            var reader = new SequenceReader<byte>(sequence);
            return SchemaSection.Read(ref reader, strings);
        }
    }
}
