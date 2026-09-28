// The one half of the manifest's name invariant that registration order cannot settle, pinned through a
// real context, a real save and a real load: a member whose type name is a *closed canonical name* — which
// is the name the bake derives from the CLR type — and a hand-written descriptor that claims that name.
//
// The bake resolves a member's recorded type name (`SchemaSection.ResolveId`) through the reserved table,
// then through the name index, and registers the real shape only if the name is not already claimed. A
// descriptor that claims the name first is therefore what that member resolves to: the real shape is never
// registered, the member is written with the impostor's id, and nothing complains. On the migration path
// the impostor is not a collection shape, so `ReadUnknownCollectionPayload` consumes the count, skips the
// elements and answers null — the member silently gone, the stream still aligned, the load reporting
// success. `MigrationPlanBuilder` does not refuse it, because the impostor records the very definition the
// real type would have recorded, so its own compatibility check agrees with itself.
//
// The corruption needs the forged descriptor in the context that *reads* the save, and that is what makes
// it so easy to miss: a reader whose manifest does not carry the impostor registers the real shape under
// that name while it bakes, so the same bytes come back whole there. A save written by a build with the
// descriptor and read by a build without it is fine; a save read back by the build that has it — the
// ordinary case for a hand-forged descriptor, which is configuration — loses the member. Observed on the
// code before the rule, on the model below: the manifest holds the impostor under the member's name and no
// `List<int>` entry at all, the generated reader still returns `[1,2,3]` (it never consults the schema for
// a member's own type), and the migration interpreter returns `null` with no exception, the array member
// and the trailing field intact.
//
// The fix is upstream of all of that: `SchemaRegistry.RequireOwnDerivedName` refuses the claim where it is
// written, so this file cannot reach the state it describes. That is the point of the shape below — it is
// the state, expressed as a model, a serializer and an assertion, so the door the refusal happens at is
// pinned along with the consequences it replaces.
//
//   * A_Descriptor_Claiming_A_Members_Closed_Canonical_Name_Is_Refused  — the door, and what it says.
//   * The_Name_The_Exploit_Claims_Is_The_One_The_Bake_Derives           — the precondition it needs.
//   * Without_The_Claim_The_Member_Is_Bound_To_Its_Own_Type_On_Both_Readers — what is left, on both
//     readers: the interpreter is the one that used to answer null, so it is the one that has to be asked.
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.DerivedNameBinding
{
    /// <summary>
    /// The context the exploit needs. Two derived names and one trailing field, so the bake has to resolve
    /// both of its own and a decode that goes wrong anywhere is visible: <see cref="Items"/> is the member
    /// whose name is claimed, <see cref="Numbers"/> is the witness beside it (its own name is never
    /// claimed, so it survives and proves the corruption is a member and not a file), and
    /// <see cref="After"/> is the alignment witness — a silent skip of the collection payload would consume
    /// exactly the right number of bytes and leave this holding, which is why nothing else can catch it.
    /// </summary>
    [CerasContext("DerivedNameBinding")]
    [CerasSerializable]
    public class ClaimDerivedRoot
    {
        public List<int> Items;
        public int[] Numbers;
        public int After;
    }

    /// <summary>
    /// The type a forged descriptor is hung on: in no context, never serialized, and with no members at
    /// all. What it <em>is</em> is irrelevant — what matters is the descriptor recorded under <em>its</em>
    /// name, which describes a collection payload, so the file describes the member correctly and binds it
    /// to the wrong CLR type. This is the documented public forge
    /// (<c>SchemaRegistry.Register</c> with an explicit descriptor) pointed at somebody else's name.
    /// </summary>
    public sealed class Impostor
    {
    }

    public class DerivedNameBindingTests : TestBase
    {
        const string ClaimContext = "DerivedNameBinding";
        const int Sentinel = 4242;

        /// <summary>The two members whose type the bake derives a name for.</summary>
        public static TheoryData<Type> DerivedMemberTypes => new()
        {
            { typeof(List<int>) },
            { typeof(int[]) },
        };

        static string Name(Type type) => TypeIdentity.GetCanonicalName(type);

        static ClaimDerivedRoot Model() => new ClaimDerivedRoot
        {
            Items = new List<int> { 1, 2, 3 },
            Numbers = new[] { 4, 5 },
            After = Sentinel,
        };

        static CerasRobustSerializer Clean()
            => new CerasRobustSerializer(c => c.UseGeneratedFormatters(ClaimContext));

        /// <summary>
        /// A context that adds one hand-written descriptor, recorded under the canonical name of
        /// <paramref name="memberType"/> and describing exactly the payload that type is written with — the
        /// element type, the definition, the arity. Nothing about the descriptor is malformed, which is why
        /// the structural invariant accepts it and only the <em>name</em> rule can refuse it: the file
        /// would describe the member's payload correctly and bind it to the wrong CLR type.
        /// </summary>
        static CerasRobustSerializer Claiming(Type memberType)
        {
            var (definition, element) = memberType.IsArray
                ? ("System.Array", TypeIdentity.GetCanonicalName(memberType.GetElementType()))
                : ("System.Collections.Generic.List`1", TypeIdentity.GetCanonicalName(memberType.GetGenericArguments()[0]));

            return new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(ClaimContext);
                c.SchemaRegistry.Register(typeof(Impostor), new SchemaTypeInfo(
                    stableName: Name(memberType),
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.GenericInstantiation,
                    fields: Array.Empty<SchemaFieldInfo>(),
                    fixedByteSize: 0,
                    underlyingTypeName: definition,
                    argTypeNames: new[] { element }));
            });
        }

        /// <summary>
        /// The door. The claim is refused before a byte is written — earlier than the bake, which would
        /// have resolved the member's name to the impostor, and earlier than any load, which would have
        /// read the member back as null. What the refusal says has to name the name, the registration that
        /// tried to take it, the fact that the name already has an owner, and the way to describe a renamed
        /// generic instead, or an author has nothing to act on.
        /// </summary>
        [Theory]
        [MemberData(nameof(DerivedMemberTypes))]
        public void A_Descriptor_Claiming_A_Members_Closed_Canonical_Name_Is_Refused(Type memberType)
        {
            string claimed = Name(memberType);

            var ex = Assert.Throws<CerasSchemaException>(() => Claiming(memberType));

            Assert.Contains(claimed, ex.Message);
            Assert.Contains(Name(typeof(Impostor)), ex.Message);
            Assert.Contains("belongs to the type it spells", ex.Message);
            Assert.Contains("a member declared as that type resolves its name through this context's name index", ex.Message);
            Assert.Contains("reads it back as null", ex.Message);   // the consequence it prevents
            Assert.Contains("OPEN [PreviousTypeName] alias", ex.Message);   // the way out
        }

        /// <summary>
        /// The precondition, asserted rather than assumed: the name the forge claims is exactly the string
        /// the bake derives, for the member's own CLR type, and the member's recorded type name is that same
        /// string — so <c>SchemaSection.ResolveId</c> is handed the impostor's name to resolve. The array
        /// member is here for the same reason, and it is the one the forge does <em>not</em> claim.
        /// </summary>
        [Fact]
        public void The_Name_The_Exploit_Claims_Is_The_One_The_Bake_Derives()
        {
            var serializer = Clean();
            serializer.Save(Model(), 1);   // seal + bake

            var registry = serializer.Config.SchemaRegistry;

            // The derived entry, under the name derived from the CLR type it describes.
            Assert.True(registry.TryGetInfo(typeof(List<int>), out var list));
            Assert.Equal("System.Collections.Generic.List`1<System.Int32>", list.StableName);
            Assert.True(registry.TryGetInfo(typeof(int[]), out var array));
            Assert.Equal("System.Int32[]", array.StableName);

            // … and the same two strings as the members' recorded type names, which is what a resolution
            // starts from.
            var root = registry.All().Single(e => e.type == typeof(ClaimDerivedRoot)).info;
            Assert.Equal(list.StableName, root.Fields.Single(f => f.StableName == nameof(ClaimDerivedRoot.Items)).TypeName);
            Assert.Equal(array.StableName, root.Fields.Single(f => f.StableName == nameof(ClaimDerivedRoot.Numbers)).TypeName);
        }

        /// <summary>
        /// What is left once the claim is refused: the member is bound to <c>List&lt;int&gt;</c> and to
        /// <c>int[]</c>, on <em>both</em> readers. The generated reader was never in doubt — it never
        /// consults the schema for a member's own type — so the interpreter is the half that matters: it
        /// resolves the file's schema section against this build's manifest, which is the step the impostor
        /// used to take over, and it is the reader that answered null. Every member is checked, so a member
        /// that came back empty could not hide behind a neighbour that did not.
        /// </summary>
        [Fact]
        public void Without_The_Claim_The_Member_Is_Bound_To_Its_Own_Type_On_Both_Readers()
        {
            var bytes = Clean().Save(Model(), 1);

            foreach (var loaded in new[]
            {
                Clean().Load<ClaimDerivedRoot>(bytes, 1),
                Clean().Load<ClaimDerivedRoot>(SlowPath(bytes), 1),   // the interpreter, forced
            })
            {
                Assert.Equal(new[] { 1, 2, 3 }, loaded.Items);
                Assert.Equal(new[] { 4, 5 }, loaded.Numbers);
                Assert.Equal(Sentinel, loaded.After);
            }
        }

        /// <summary>
        /// The manifest is the same one in both of the rows above, and it holds the names the bake derived
        /// as the entries' <em>own</em> — which is the state the claim above would have taken away. A
        /// context that cannot produce this is a context the rule would refuse, so the control is a real
        /// check on the rule rather than a restatement of it.
        /// </summary>
        [Fact]
        public void The_Contexts_Own_Manifest_Holds_Each_Derived_Name_Under_The_Type_It_Names()
        {
            var serializer = Clean();
            serializer.Save(Model(), 1);

            foreach (var (type, id, info) in serializer.Config.SchemaRegistry.All())
                Assert.Equal(Name(type), info.StableName);
        }
    }
}
