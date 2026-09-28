// What a SchemaRegistry holds after its seal was refused.
//
// A seal does two things, and only one of them is undone when the seal fails:
//
//   - the frozen region (the schema bytes, the schema string table, the type->id and id->type maps) and
//     the seal watermark, which are published only once a bake has all four parts. That is atomic, so a
//     caller can never see half a bake, and RequireBakedManifest keeps refusing to resolve a file's ids
//     until a bake has really succeeded. TypeNameUniquenessTests pins that half.
//   - the registrations the seal made along the way: the declared roots folded in above the bake, and
//     the structural shapes the bake discovered through SchemaSection.ResolveId -> Register before it
//     reached the member it could not describe. Those are NOT taken back. The entries keep their id,
//     their SchemaTypeInfo and the names they claimed; _nextId stays advanced past them.
//
// A comment in SchemaRegistry.Seal used to claim a refused seal left the registry "exactly as if it had
// never been asked to seal", which is what these tests contradict. The state is harmless through
// Save/Load - a load seals again, and nothing resolves a file's ids before a bake has succeeded - so it
// is pinned here rather than rolled back; rolling it back would mean letting a bake retract an entry
// something else may already have observed.
using System;
using System.Collections.Generic;
using System.Linq;
using Ceras.Exceptions;
using Ceras.Format;
using Xunit;

namespace Ceras.Test
{
    public class RefusedSealStateTests
    {
        /// <summary>
        /// The member whose type name resolves to nothing. Nothing declares it, so the bake refuses on it
        /// by name - which is the whole point of the fixture: the refusal has to come *after* another
        /// member has been resolved, or there is no partial state to observe.
        /// </summary>
        const string MissingTypeName = "Some.Assembly.TypeThatIsNotHere";

        /// <summary>
        /// A member of a shape the manifest does not hold and that the bake registers for itself. It is
        /// listed first because <c>SchemaSection.Write</c> walks the descriptor's fields in order, and the
        /// registration happens on the way.
        /// </summary>
        const string StructuralTypeName = "System.Int32[]";

        static SchemaTypeInfo InfoWithTheSecondMemberUnresolvable(string stableName) => new SchemaTypeInfo(
            stableName,
            previousTypeNames: Array.Empty<string>(),
            kind: SchemaTypeKind.Class,
            fields: new[]
            {
                new SchemaFieldInfo("Numbers", Array.Empty<string>(), StructuralTypeName, isMonomorphic: false),
                new SchemaFieldInfo("Missing", Array.Empty<string>(), MissingTypeName, isMonomorphic: false),
            },
            fixedByteSize: 0);

        /// <summary>
        /// A registry whose seal was refused half way: the declared root is registered, then the bake
        /// registers the structural member, then it hits the member it cannot describe. What is left
        /// behind is pinned in the three tests below, one fact each, so a change to any of them names
        /// which half of the seal moved.
        /// </summary>
        static SchemaRegistry RefusedBake()
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(Holder), InfoWithTheSecondMemberUnresolvable("NameResolution.RefusedSeal.Holder"));

            // The premise: the structural member is not in the manifest yet, so the bake has work to do.
            Assert.False(registry.TryGetId(typeof(int[]), out _));
            Assert.False(registry.IsSealed);
            Assert.False(registry.HasBakedSchema);

            var ex = Assert.Throws<CerasSchemaException>(() => registry.Seal());
            Assert.Contains(MissingTypeName, ex.Message, StringComparison.Ordinal);
            Assert.Contains("field 'Missing'", ex.Message, StringComparison.Ordinal);

            return registry;
        }

        /// <summary>
        /// The half that *is* rolled back, and the reason the rest is harmless: nothing about the frozen
        /// region survived, so the two flags a caller can ask about are both false and the manifest
        /// cannot be used to resolve a file's ids.
        /// </summary>
        [Fact]
        public void A_Refused_Seal_Leaves_The_Registry_Unsealed_And_Unbaked()
        {
            var registry = RefusedBake();

            Assert.False(registry.IsSealed);
            Assert.False(registry.HasBakedSchema);
            Assert.False(registry.TryGetBakedSchema(out _, out _, out _, out _, out _));

            // The guard, stated as RequireBakedManifest puts it: "before the bake none of those is in the
            // manifest". The entries below are in the registry, and this still refuses.
            var ex = Assert.Throws<CerasSchemaException>(() => registry.RequireBakedManifest());
            Assert.Contains("manifest is not baked", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The half that is *not* rolled back: the entry the bake had already made keeps its id and its
        /// descriptor, and the name it derived from the CLR type is now a name this registry answers to.
        /// This is the fact the old comment denied.
        /// </summary>
        [Fact]
        public void A_Refused_Seal_Keeps_The_Entry_The_Aborted_Bake_Had_Already_Registered()
        {
            var registry = RefusedBake();

            Assert.True(registry.TryGetId(typeof(int[]), out int id));
            Assert.True(id >= WellKnownTypes.UserTypeBase);

            // The descriptor is there too, not just the id: the shape is a first-class manifest entry.
            Assert.True(registry.TryGetInfo(typeof(int[]), out var info));
            Assert.Equal(StructuralTypeName, info.StableName);
            Assert.Equal(SchemaTypeKind.GenericInstantiation, info.Kind);

            // And the name it claimed answers, which is the part that outlives the refusal: a save file
            // saying "System.Int32[]" now resolves against a registry whose bake never finished.
            Assert.True(registry.TryResolveType(StructuralTypeName, out var resolved));
            Assert.Equal(typeof(int[]), resolved);
        }

        /// <summary>
        /// The id space was consumed too, and observably so: a type registered *after* the refusal is
        // handed an id above the leaked one rather than reusing it. So the leaked entry is not a harmless
        /// duplicate of something a later registration could overwrite - it holds its slot for good.
        /// </summary>
        [Fact]
        public void A_Type_Registered_After_The_Refusal_Does_Not_Reuse_The_Leaked_Id()
        {
            var registry = RefusedBake();
            Assert.True(registry.TryGetId(typeof(int[]), out int leakedId));

            registry.Register(typeof(Late), new SchemaTypeInfo(
                "NameResolution.RefusedSeal.Late", Array.Empty<string>(),
                SchemaTypeKind.Class, Array.Empty<SchemaFieldInfo>(), 0));

            Assert.True(registry.TryGetId(typeof(Late), out int lateId));
            Assert.True(lateId > leakedId, $"expected a fresh id above {leakedId}, got {lateId}");
        }

        /// <summary>
        /// The control: a registry with nothing wrong with it reaches exactly the state the three facts
        /// above say a refused one does not. Without it, "nothing is published and nothing is registered"
        /// would satisfy all three.
        /// </summary>
        [Fact]
        public void A_Seal_That_Succeeds_Registers_And_Publishes_Them_Together()
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(Holder), new SchemaTypeInfo(
                "NameResolution.RefusedSeal.Holder", Array.Empty<string>(),
                SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("Numbers", Array.Empty<string>(), StructuralTypeName, isMonomorphic: false) },
                0));

            registry.Seal();

            Assert.True(registry.IsSealed);
            Assert.True(registry.HasBakedSchema);
            Assert.True(registry.TryGetBakedSchema(out _, out _, out var typeToId, out var idToType, out var fingerprint));
            Assert.NotEqual(0UL, fingerprint);
            Assert.Equal(2, typeToId.Count);
            Assert.Equal(typeof(int[]), idToType[typeToId[typeof(int[])]]);
        }

        /// <summary>
        /// The other door, so the comment's wording is not read as covering only the bake. A declared-root
        /// shape refused inside the loop above the bake leaves the same partial state - the roots already
        /// folded in are registered - and leaves the pending declarations pending, because
        /// <c>_declaredRoots.Clear()</c> is only reached once the whole loop has run. So the next seal
        /// refuses on the same declaration, which is the point: the same actionable message, not a silent
        /// skip of a root the author declared.
        /// <para>
        /// The refusal is a real declaration error rather than a forgery, because the forgery this test used
        /// to stand on no longer exists: claiming a name the bake derives from a CLR type is refused where
        /// the claim is made, in <c>Register</c> through <c>RequireOwnDerivedName</c>, so such a root can
        /// never reach the loop at all. What is left is a declaration no manifest can express - an enum with
        /// two members under one wire name - which <c>BuildDeclaredSchema</c> refuses while the loop builds
        /// the root's descriptor. No earlier door rejects it: <c>DeclareRoot</c> only asks whether the shape
        /// is one it can declare, and both roots here are legal declarations, so the failure has to happen
        /// inside <c>Seal</c>, above the bake.
        /// </para>
        /// </summary>
        [Fact]
        public void A_Refused_Declared_Root_Claim_Also_Keeps_What_The_Loop_Had_Registered()
        {
            var registry = new SchemaRegistry();
            registry.DeclareRoot(typeof(FoldedInFirst));
            registry.DeclareRoot(typeof(RefusedSecond));

            // The premise, in the order the loop folds declared roots in: canonical-name order, ordinal.
            // Both roots are enums declared here for a naming reason: a structural root like List<int> is a
            // System.* name, and every Ceras.Test name sorts before every System.* one, so the refused root
            // would then be folded in first and there would be no partial state to observe at all. 'F' before
            // 'R' is what makes the premise true, and it is asserted rather than assumed, so renaming either
            // root fails the test instead of quietly emptying it.
            Assert.True(StringComparer.Ordinal.Compare(
                TypeIdentity.GetCanonicalName(typeof(FoldedInFirst)),
                TypeIdentity.GetCanonicalName(typeof(RefusedSecond))) < 0,
                "the good root has to be folded in before the refusal, or this test observes nothing");
            Assert.False(registry.TryGetId(typeof(FoldedInFirst), out _));

            var ex = Assert.Throws<CerasSchemaException>(() => registry.Seal());

            // The refusal is the enum-name check on the declared root, and not some earlier door: it names
            // the wire name the two members of RefusedSecond would share.
            Assert.Contains("SameStep", ex.Message, StringComparison.Ordinal);

            Assert.False(registry.IsSealed);
            Assert.False(registry.HasBakedSchema);

            // The partial registration is kept, exactly as in the bake case, while the root the loop could
            // not describe never became an entry at all.
            Assert.True(registry.TryGetId(typeof(FoldedInFirst), out _));
            Assert.False(registry.TryGetId(typeof(RefusedSecond), out _));

            // And the declaration that could not be honoured is still pending, so sealing again reports
            // the same problem rather than quietly dropping the root.
            var again = Assert.Throws<CerasSchemaException>(() => registry.Seal());
            Assert.Equal(ex.Message, again.Message);
        }

        /// <summary>
        /// A root shape the loop can describe: an ordinary enum, so folding it in registers an entry and
        /// gives the seal something to be half-finished about. Its canonical name is
        /// <c>Ceras.Test.RefusedSealStateTests+FoldedInFirst</c>, and that string is what the ordinal
        /// comparison in the test above actually orders the two roots by.
        /// </summary>
        enum FoldedInFirst { One, Two }

        /// <summary>
        /// A root shape no manifest can express: its two members would both be recorded under the wire name
        /// <c>SameStep</c>, and an enum member is identified by that name, so <c>BuildDeclaredSchema</c>
        /// refuses the whole root. A fact about the enum rather than about the context, which is the point -
        /// nothing here is forged, and the generated path reports the same declaration at compile time.
        /// </summary>
        enum RefusedSecond
        {
            [CerasStableName("SameStep")] Up = 1,
            [CerasStableName("SameStep")] Down = 2,
        }

        sealed class Holder { }
        sealed class Late { }
    }
}
