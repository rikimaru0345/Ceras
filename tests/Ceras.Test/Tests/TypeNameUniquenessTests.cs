// One name, one entry: the manifest's identity invariant, stated on SchemaRegistry.ClaimNames and pinned
// here against a bare `new SchemaRegistry()` and hand-written descriptors, so every row is about the
// claim itself rather than about a generated manifest.
//
// A name in a manifest is how a save file is matched onto this build, so a name two entries answer for
// binds that file to whichever was registered first - a silent mis-binding on an otherwise valid save,
// visible only as a wrong value long after the rename. Before this the stable name was written with
// `_byStableName[name] = entry` (last registration silently wins) and an alias with TryAdd (the conflict
// surfaced only at the seal, and only for two *different generic definitions*, so two unrelated types
// claiming one name was never reported at all).
//
// Pinned, in the order the invariant is stated: two entries may not claim one name in any combination; the
// one legal overlap is an *open* alias, which names a whole family and is therefore shared by every
// closure of one definition; a structural shape derives its own name from the CLR type, so a declared
// entry cannot take one; a reserved well-known name is a different id space, and only the *reserved*
// spelling is taken, since a closed canonical name is an ordinary entry of its own; a rejected seal bakes
// nothing; and InternalClear releases every claim, because the claims are the manifest's.
using System;
using System.Collections.Generic;
using Xunit;
using Ceras.Exceptions;
using Ceras.Format;

namespace Ceras.Test.NameUniqueness
{
    // Four unrelated types, so a name can be claimed by two of them, or by one closure of a family and by
    // another. None is a serializable type of any context: every descriptor below is hand-written.
    sealed class Left { }
    sealed class Right { }
    sealed class Box<T> { }
    sealed class Other<T> { }

    public class TypeNameUniquenessTests
    {
        const string Shared = "Ceras.Test.NameUniqueness.Shared";
        const string ClosedList = "System.Collections.Generic.List`1<System.Int32>";

        static SchemaTypeInfo Info(string stableName, params string[] aliases)
            => new SchemaTypeInfo(stableName, aliases, SchemaTypeKind.Class, Array.Empty<SchemaFieldInfo>(), 0);

        [Fact]
        public void Two_Types_Cannot_Claim_One_Stable_Name()
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(Left), Info(Shared));

            var ex = Assert.Throws<CerasSchemaException>(() => registry.Register(typeof(Right), Info(Shared)));

            // The name, both claimants under their own canonical identity, the role, and the fix.
            Assert.Contains(Shared, ex.Message);
            Assert.Contains("Ceras.Test.NameUniqueness.Left", ex.Message);
            Assert.Contains("Ceras.Test.NameUniqueness.Right", ex.Message);
            Assert.Contains("stable name", ex.Message);
            Assert.Contains("Rename one of the two types", ex.Message);

            // The refused entry is not in the manifest; the one that was there is untouched.
            Assert.False(registry.IsDeclared(typeof(Right)));
            Assert.True(registry.IsDeclared(typeof(Left)));
        }

        [Fact]
        public void A_Stable_Name_And_Another_Types_Alias_Conflict_In_Either_Order()
        {
            // Alias first: the stable name is the second claim, and it is refused.
            var aliasFirst = new SchemaRegistry();
            aliasFirst.Register(typeof(Left), Info("Ceras.Test.NameUniqueness.Left", Shared));
            var ex = Assert.Throws<CerasSchemaException>(() => aliasFirst.Register(typeof(Right), Info(Shared)));
            Assert.Contains("stable name", ex.Message);
            Assert.Contains("[PreviousTypeName] alias", ex.Message);

            // Stable name first: the same pair in the other order. One map, so the order the two
            // declarations arrive in cannot decide which of them is the wrong one.
            var stableFirst = new SchemaRegistry();
            stableFirst.Register(typeof(Left), Info(Shared));
            Assert.Throws<CerasSchemaException>(() =>
                stableFirst.Register(typeof(Right), Info("Ceras.Test.NameUniqueness.Right", Shared)));
        }

        [Fact]
        public void A_Stable_Name_Cannot_Claim_An_Open_Alias_Another_Family_Holds()
        {
            // An open alias is a name for a family, so claiming it *as a stable name* is still claiming a
            // name somebody else answers for - a family, rather than one of its members.
            var registry = new SchemaRegistry();
            registry.Register(typeof(Box<int>), Info("Ceras.Test.NameUniqueness.Box`1<System.Int32>", "Legacy.Box`1"));

            var ex = Assert.Throws<CerasSchemaException>(() => registry.Register(typeof(Other<int>), Info("Legacy.Box`1")));
            Assert.Contains("Legacy.Box`1", ex.Message);
            Assert.Contains("Ceras.Test.NameUniqueness.Other`1<System.Int32>", ex.Message);
            Assert.Contains("Ceras.Test.NameUniqueness.Box`1<System.Int32>", ex.Message);
        }

        /// <summary>
        /// The one legal overlap, and why the rule is about <em>families</em> rather than about types: an
        /// open alias is closed with the file's own arguments, so <c>Legacy.Box`1</c> stands for every
        /// closure of <c>Box&lt;&gt;</c>. The manifest bakes, and the name resolves to the family.
        /// </summary>
        [Fact]
        public void Several_Closures_Of_One_Family_May_Share_An_Open_Alias()
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(Box<int>), Info("Ceras.Test.NameUniqueness.Box`1<System.Int32>", "Legacy.Box`1"));
            registry.Register(typeof(Box<string>), Info("Ceras.Test.NameUniqueness.Box`1<System.String>", "Legacy.Box`1"));
            registry.Seal();

            Assert.True(registry.HasBakedSchema);
            Assert.True(registry.TryResolveType("Legacy.Box`1", out var resolved));
            Assert.Equal("Ceras.Test.NameUniqueness.Box`1", TypeIdentity.GetCanonicalName(resolved));
        }

        [Fact]
        public void An_Alias_May_Repeat_Its_Own_Stable_Name()
        {
            // A name the entry already holds is not a conflict with itself: the alias is redundant rather
            // than wrong, so an author can write it and the descriptor stays legal.
            var registry = new SchemaRegistry();
            registry.Register(typeof(Left), Info(Shared, Shared));
            registry.Seal();

            Assert.True(registry.TryResolveType(Shared, out var resolved));
            Assert.Equal(typeof(Left), resolved);
        }

        [Fact]
        public void A_Structural_Entry_Cannot_Be_Shadowed_By_A_Declared_Name()
        {
            // A structural shape derives its own name from the CLR type, so the manifest cannot hand it a
            // different one - but a declared entry can take the name one, and whichever of the two
            // registered second used to be the one the name index forgot.
            var derivedFirst = new SchemaRegistry();
            derivedFirst.Register(typeof(List<int>), null);   // structural: ClosedList
            Assert.Throws<CerasSchemaException>(() => derivedFirst.Register(typeof(Left), Info(ClosedList)));

            var declaredFirst = new SchemaRegistry();
            declaredFirst.Register(typeof(Left), Info(ClosedList));
            Assert.Throws<CerasSchemaException>(() => declaredFirst.Register(typeof(List<int>), null));
        }

        [Fact]
        public void A_Reserved_WellKnown_Name_Cannot_Be_Claimed()
        {
            var asStableName = new SchemaRegistry();
            var ex = Assert.Throws<CerasSchemaException>(() => asStableName.Register(typeof(Left), Info("System.Int32")));
            Assert.Contains("System.Int32", ex.Message);
            Assert.Contains($"well-known type id {WellKnownTypes.Int32}", ex.Message);

            // An alias is refused on the very same terms, where it is written.
            var asAlias = new SchemaRegistry();
            var aliased = Assert.Throws<CerasSchemaException>(() =>
                asAlias.Register(typeof(Left), Info("Ceras.Test.NameUniqueness.Left", "System.Collections.Generic.List`1")));
            Assert.Contains($"well-known type id {WellKnownTypes.List}", aliased.Message);

            // The control that keeps the rule narrow: a closed name is an ordinary entry that happens to
            // mention a reserved definition, and an array is its own shape.
            var closedNames = new SchemaRegistry();
            closedNames.Register(typeof(Left), Info(ClosedList));
            closedNames.Register(typeof(Right), Info("System.Int32[]"));
            closedNames.Seal();
            Assert.True(closedNames.HasBakedSchema);
        }

        /// <summary>
        /// The reserved table itself, now that one dictionary states it: the bake resolves a member's type
        /// name through it and the registry refuses an entry that claims one of these names, so an id and
        /// its name have to be the same fact.
        /// </summary>
        [Fact]
        public void Every_Reserved_Id_Is_Reachable_By_Exactly_Its_Own_Canonical_Name()
        {
            for (int id = 0; id < WellKnownTypes.UserTypeBase; id++)
            {
                if (!WellKnownTypes.TryGetType(id, out var type)) continue;
                Assert.True(WellKnownTypes.TryGetIdByCanonicalName(TypeIdentity.GetCanonicalName(type), out int back), type.ToString());
                Assert.Equal(id, back);
            }

            // The spellings a member can actually carry: the *open* definition names.
            Assert.Equal(WellKnownTypes.List, Id("System.Collections.Generic.List`1"));
            Assert.Equal(WellKnownTypes.Nullable, Id("System.Nullable`1"));
            Assert.Equal(WellKnownTypes.VTuple2, Id("System.ValueTuple`2"));
            Assert.Equal(WellKnownTypes.Array, Id("System.Array"));

            // … and a name that only resembles one resolves to nothing, so it stays a legal entry name.
            Assert.Equal(-1, Id(ClosedList));
            Assert.Equal(-1, Id("System.Int32[]"));
            Assert.Equal(-1, Id("System.Int32, System.Version"));   // the legacy Type.FullName of a closed generic
            Assert.Equal(-1, Id(""));
            Assert.Equal(-1, Id(null));

            static int Id(string name) => WellKnownTypes.TryGetIdByCanonicalName(name, out int id) ? id : -1;
        }

        [Fact]
        public void An_Empty_Stable_Name_Is_Refused_And_An_Empty_Alias_Is_Ignored()
        {
            // The stable name is the entry's identity on the wire, so it may not be empty; SchemaTypeInfo
            // rejects a null one, and this is the other half of that.
            var registry = new SchemaRegistry();
            var ex = Assert.Throws<CerasSchemaException>(() => registry.Register(typeof(Left), Info("")));
            Assert.Contains("stable name is empty", ex.Message);

            // An empty alias names nothing, exactly as the loader skips it, so it is not a claim at all -
            // otherwise every descriptor that happened to carry one would collide with the next.
            var withEmptyAliases = new SchemaRegistry();
            withEmptyAliases.Register(typeof(Left), Info("Ceras.Test.NameUniqueness.Left", ""));
            withEmptyAliases.Register(typeof(Right), Info("Ceras.Test.NameUniqueness.Right", "", ""));
            withEmptyAliases.Seal();

            Assert.False(withEmptyAliases.TryResolveType("", out _));
            Assert.True(withEmptyAliases.HasBakedSchema);
        }

        /// <summary>
        /// A refused seal leaves a registry that has not sealed and has not baked. Half a bake - the frozen
        /// region published, the seal unfinished - answers "baked" to a caller that asks
        /// <see cref="SchemaRegistry.HasBakedSchema"/> and false to the same caller's
        /// <see cref="SchemaRegistry.IsSealed"/>, the one combination nothing can reason about.
        /// </summary>
        [Fact]
        public void A_Rejected_Seal_Leaves_Nothing_Baked()
        {
            // A declared root whose derived name is already claimed: refused in the declared-root loop,
            // before the bake starts at all. …
            var claimedName = new SchemaRegistry();
            claimedName.Register(typeof(Left), Info(ClosedList));
            claimedName.DeclareRoot(typeof(List<int>));
            AssertNothingBaked(claimedName);

            // … and a bake that fails for its own reason - a member type name that resolves to nothing -
            // is refused on the same terms: the frozen region is published only once the bake has succeeded.
            var undeclaredMember = new SchemaRegistry();
            undeclaredMember.Register(typeof(Left), new SchemaTypeInfo(Shared, Array.Empty<string>(),
                SchemaTypeKind.Class,
                new[] { new SchemaFieldInfo("Member", Array.Empty<string>(), "Some.Missing.Type", false) }, 0));
            AssertNothingBaked(undeclaredMember);

            static void AssertNothingBaked(SchemaRegistry registry)
            {
                Assert.Throws<CerasSchemaException>(() => registry.Seal());
                Assert.False(registry.IsSealed);
                Assert.False(registry.HasBakedSchema);
                Assert.False(registry.TryGetBakedSchema(out _, out _, out _, out _, out _));
            }
        }

        [Fact]
        public void InternalClear_Releases_Every_Claim()
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(Left), Info(Shared));
            Assert.Throws<CerasSchemaException>(() => registry.Register(typeof(Right), Info(Shared)));

            // The claims are the manifest's, and the manifest is what went: the name the entry that is gone
            // held is free again. A claim table that outlived InternalClear would refuse this.
            registry.InternalClear();
            registry.Register(typeof(Right), Info(Shared));
            registry.Register(typeof(Left), Info("Ceras.Test.NameUniqueness.Left"));
            registry.Seal();
            Assert.True(registry.HasBakedSchema);

            // … and the rule is still in force afterwards: a conflict is decided by what is registered now.
            registry.InternalClear();
            registry.Register(typeof(Left), Info(Shared));
            Assert.Throws<CerasSchemaException>(() => registry.Register(typeof(Right), Info(Shared)));
        }
    }
}
