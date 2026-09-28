// Compatibility between the array-shape refusals and everything else that landed in the integration tree:
// the unsupported-collection policy (CERAS033/CERAS034 and its runtime half), the generator's own view of
// an array-valued member, and the migration reader's handling of an array-valued or renamed argument.
//
// The rank-1 array parse fix sits directly underneath the collection policy: a canonical name is how a
// `List<int[]>` is resolved, and if it resolved to a *different* `Type` from the one declared, every
// collection-shaped position built on it would break. So the point of each test is that the fix is invisible
// to the policy — the supported shapes are still supported, the refusals still fire only where they did, and
// nothing is reported that was not reported before.
//
// No model is declared here. The current models these cases load as already exist in
// `ArrayShapeNestedArgumentMigrationTests.cs` and `ArrayShapeChangeMigrationTests.cs`, and a second copy
// under a different name would be a *conflict* rather than a test: a `[PreviousTypeName]` alias stands for
// exactly one type, so claiming one twice is refused at bake time (the registry's own
// `ValidateRenameAliasesLocked`). Reusing them is also the stronger check, because it means these assertions
// run against the same declarations the rest of the suite pins.
using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Test.Generator;
using Ceras.Test.HistoricalArrayShapeFixtures;
using Ceras.Test.HistoricalNestedArrayShapeFixtures;
using static Ceras.Test.HistoricalFixtureFiles;

namespace Ceras.Test.HistoricalArrayShapeCompatibility
{
    public class ArrayShapeCollectionPolicyCompatibilityTests : TestBase
    {
        const string NestedCtx = "HistoricalNestedShapeCurrent";
        const string ShapeCtx = "HistoricalArrayShapeCurrent";

        // The two fixture families were written by different v1 models, so they carry different sentinels; the
        // nested one is 0x3C3C3C3C and the array-shape one 0xC3C3C3C. A single value here would silently
        // assert against the wrong fixture.
        const uint NestedSentinel = 0x3C3C3C3Cu;

        /// <summary>
        /// The generator must not report an unsupported-collection diagnostic for the shapes the collection
        /// policy supports, and a canonical name containing a rank-1 array is exactly what such a member's
        /// type resolves through. <c>List&lt;int[]&gt;</c> is the case: an array argument, which is neither
        /// <c>ReadOnlyCollection&lt;T&gt;</c> nor a <c>List</c>/<c>Dictionary</c> subclass, so it is supported —
        /// and it has to stay supported now that its name parses to the vector array type.
        /// </summary>
        [Fact]
        public void An_Array_Valued_Collection_Member_Reports_No_Collection_Policy_Diagnostic()
        {
            var ids = GeneratorTestHarness.DiagnosticIds(@"
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;

[CerasContext(""Harness"")]
[CerasSerializable]
public class Holder
{
    public List<int[]> Arrays;
    public Dictionary<string, int[]> Map;
    public int[] Direct;
}");

            Assert.DoesNotContain("CERAS033", ids);
            Assert.DoesNotContain("CERAS034", ids);
        }

        /// <summary>
        /// The mirror of the above, and the reason the previous test alone is not enough: the policy must
        /// still fire for the shapes it refuses. If the array fix had made an unsupported shape look
        /// supported, this is where it would show.
        /// </summary>
        [Fact]
        public void The_Collection_Policy_Still_Reports_An_Unsupported_Member()
        {
            var ids = GeneratorTestHarness.DiagnosticIds(@"
using System.Collections.ObjectModel;
using Ceras;
using Ceras.Generated;

[CerasContext(""Harness"")]
[CerasSerializable]
public class Holder
{
    public ReadOnlyCollection<int> Refused;
}");

            Assert.Contains("CERAS033", ids);
        }

        /// <summary>
        /// The runtime half of the same policy, from the direction that can be asserted here: an
        /// array-valued collection member must still <em>bake</em> in a robust context. If the array-name fix
        /// had made a supported shape look refused, the bake — not the generator — is where it would surface,
        /// and the generator test above cannot see it.
        /// <para>
        /// The refusing half is deliberately not repeated: the generator reports it as an <em>error</em>, so a
        /// model of that shape cannot exist in this assembly at all — which is itself the proof that the policy
        /// is enforced before any of this runs. <c>UnsupportedCollectionPolicyTests</c> covers the runtime
        /// refusal on the shapes that can be declared.
        /// </para>
        /// </summary>
        [Fact]
        public void An_Array_Valued_Collection_Member_Still_Bakes_In_A_Robust_Context()
        {
            var writer = CurrentWriter(NestedCtx);
            var bytes = writer.Save(new SameListCrate
            {
                Slot = new List<int[]> { new[] { 1, 2 }, new[] { 3 } },
                Marker = NestedSentinel,
            }, 1);

            Assert.Equal(writer.Config.SchemaRegistry.ComputeFingerprint(),
                BitConverter.ToUInt64(bytes, RobustHeader.FingerprintOffset));
            Assert.Equal(NestedSentinel, writer.Load<SameListCrate>(bytes, 1).Marker);
        }

        // ── A member whose array-valued argument survives migration ─────────────────────────────────

        /// <summary>
        /// The cross-version case the rank-1 fix unblocks, on a real old-build file: v1 declared
        /// <c>List&lt;int[]&gt;</c>, this build declares the same, and the argument type id the file recorded
        /// has to resolve to the <em>same</em> <c>Type</c> the current member declares — otherwise the
        /// structural-argument refusal would fire on a shape that did not change. It must load, and the
        /// sentinel has to survive.
        /// </summary>
        [Fact]
        public void An_Array_Valued_Argument_That_Did_Not_Change_Still_Migrates()
        {
            var crate = MigrationReader(NestedCtx).Load<SameListCrate>(Fixture("NestedSameList.bin"), 1);

            Assert.Equal(NestedSentinel, crate.Marker);
            Assert.Equal(2, crate.Slot.Count);
            Assert.Equal(new[] { 1, 2 }, crate.Slot[0]);
            Assert.Equal(new[] { 3 }, crate.Slot[1]);

            // The declared type really is the one the file's name resolves to, which is the whole point: the
            // two are reference-equal, so the assignment the reader performs can succeed at all.
            var declared = typeof(SameListCrate).GetField("Slot").FieldType.GetGenericArguments()[0];
            Assert.Same(typeof(int[]), declared);
        }

        /// <summary>
        /// A renamed argument type still migrates, and still for the reason the refusal message gives: the
        /// file's name for the element resolves through <c>[PreviousTypeName]</c> to the very type the current
        /// declaration has, so both sides of the identity comparison are the same <see cref="Type"/>.
        /// </summary>
        [Fact]
        public void A_Renamed_Argument_Type_Still_Migrates()
        {
            var crate = MigrationReader(NestedCtx).Load<RenamedArgCrate>(Fixture("NestedRenamedArgList.bin"), 1);

            Assert.Equal(NestedSentinel, crate.Marker);
            Assert.Single(crate.Slot);
            Assert.Equal(33, crate.Slot[0].Beat);
        }

        /// <summary>
        /// A member whose declared type changed shape is still refused, and by the array-payload rule rather
        /// than by anything the collection policy owns. The negative control for the two above: the same
        /// fixture family, the same reader, a different declaration — and a different context, since a
        /// manifest's ids come from its own.
        /// </summary>
        [Fact]
        public void A_Changed_Declared_Array_Is_Still_Refused_By_The_Array_Rule()
        {
            var refusal = Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                MigrationReader(ShapeCtx).Load<PolymorphicCrate>(Fixture("ArrayShapeToObject.bin"), 1)));

            Assert.Contains("array payload", refusal.Message);
            Assert.Contains("System.Int32[]", refusal.Message);
        }
    }
}
