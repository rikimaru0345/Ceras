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
// closure of one definition; a name the bake *derives* from a CLR type may only be claimed by the type it
// spells, in either registration order, while that type's own claim on it is always free; a reserved
// well-known name is a different id space, and only the *reserved* spelling is taken; a rejected seal
// bakes nothing; and InternalClear releases every claim, because the claims are the manifest's.
//
// The derived-name rows are the only ones decided from the *form* of a string rather than from two CLR
// types, and that is the point of them: a name the bake will derive for a type nobody has registered yet
// can only be recognized as a name, and the way to recognize one without user code is
// `WellKnownTypes.IsDerivedCanonicalName`. The first row of the pair therefore carries the *runtime's*
// spelling (`TypeIdentity.GetCanonicalName`) rather than a transcribed one, so the test cannot drift from
// the names the bake actually derives. What that form cannot decide - a *plain* canonical name, which an
// enum and a non-generic collection subclass are derived under - is a documented gap, not an omission
// here; SchemaRegistry.RequireOwnDerivedName states it, and the shape of it is pinned where those two
// kinds of name are actually derived (EnumWireNameMigrationTests, StructuralRootManifestTests).
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Xunit;
using Ceras.Exceptions;
using Ceras.Format;

namespace Ceras.Test.NameUniqueness
{
    // Four unrelated types, so a name can be claimed by two of them, or by one closure of a family and by
    // another. None is a serializable type of any context: every descriptor below is hand-written.
    sealed class Left { }
    sealed class Right { }
    sealed class Box<T> { public sealed class Cell<U> { } }
    sealed class Other<T> { }

    // Declared as a root shape by the seal test below and in no context, so nothing generates a schema
    // for it and the bake has to build one from CLR metadata. Two members under one wire name is a fact
    // about the enum rather than about the context, which is what the test is about: the shape is refused
    // while the seal folds the declared roots in, before the bake itself starts.
    enum ClashingWireNames
    {
        [CerasStableName("SameStep")] Up = 1,
        [CerasStableName("SameStep")] Down = 2,
    }

    public class TypeNameUniquenessTests
    {
        const string Shared = "Ceras.Test.NameUniqueness.Shared";
        const string ClosedList = "System.Collections.Generic.List`1<System.Int32>";

        static SchemaTypeInfo Info(string stableName, params string[] aliases)
            => new SchemaTypeInfo(stableName, aliases, SchemaTypeKind.Class, Array.Empty<SchemaFieldInfo>(), 0);

        /// <summary>
        /// Every shape the bake derives a name for, given as the type so the name is the runtime's own
        /// spelling rather than a transcription of it: the collections, the structural value types, arrays
        /// of each rank, a closed user generic (nested and not), and a collection subclass — a user type
        /// the bake describes as a <c>List&lt;&gt;</c> payload, and therefore names from the CLR type just as
        /// it does for <see cref="List{T}"/> itself.
        /// </summary>
        public static TheoryData<Type> DerivedCanonicalNames => new()
        {
            { typeof(List<int>) },
            { typeof(Dictionary<string, int>) },
            { typeof(HashSet<int>) },
            { typeof(Queue<int>) },
            { typeof(Stack<int>) },
            { typeof(int?) },
            { typeof(KeyValuePair<string, int>) },
            { typeof(Tuple<string, int>) },
            { typeof((string, int)) },
            { typeof(int[]) },
            { typeof(int[,]) },                                  // a rank the format refuses, and a derived name all the same
            { typeof(List<int>[]) },
            { typeof(Box<int>) },
            { typeof(Box<int>.Cell<string>) },
            { typeof(Box<Box<int>>) },
            { typeof(Collection<int>) },                         // a collection subclass, not a List<> one
        };

        /// <summary>
        /// The structural half of that list: the shapes Ceras describes from the CLR type alone, so the
        /// entry carrying the derived name is created by <c>Register(type, null)</c> — what
        /// <c>DeclareRoot</c> does for a declared root, and what the bake's own walk does for every shape a
        /// member reaches. The user generics are missing here because a generated manifest owns them, and
        /// the rank-2 array because the format refuses to describe it at all
        /// (<c>TryBuildStructuralSchema</c> → <c>UnsupportedMultidimensionalArray</c>), so no entry is ever
        /// derived for it; its <em>name</em> is still a derived name, which is why it is in the list above.
        /// </summary>
        public static TheoryData<Type> StructuralShapes => new()
        {
            { typeof(List<int>) },
            { typeof(Dictionary<string, int>) },
            { typeof(HashSet<int>) },
            { typeof(Queue<int>) },
            { typeof(Stack<int>) },
            { typeof(int?) },
            { typeof(KeyValuePair<string, int>) },
            { typeof(Tuple<string, int>) },
            { typeof((string, int)) },
            { typeof(int[]) },
            { typeof(List<int>[]) },
            { typeof(Collection<int>) },
        };

        /// <summary>
        /// The other half: a closed <em>user</em> generic, which is a manifest entry rather than a derived
        /// one — its name is the canonical name the generator writes for that closure, which is the same
        /// string. So this half of the rule is the generated one, asked here through a hand-written
        /// descriptor because that is the only way to make one without a generated context.
        /// </summary>
        public static TheoryData<Type> ClosedUserGenerics => new()
        {
            { typeof(Box<int>) },
            { typeof(Box<int>.Cell<string>) },
            { typeof(Box<Box<int>>) },
        };

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
        /// <para>
        /// The <b>open</b> half of that sentence is load-bearing, and this is where it is pinned: a name
        /// carrying no arguments names a family, while the closed name names one closure, so the two never
        /// meet. A claim on the closed names is a different claim, refused by
        /// <c>RequireOwnDerivedName</c>, and it does not touch this one.
        /// </para>
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

            // … and the closures the family is closed with are still the closures each entry holds, so
            // sharing the open name did not merge them: the closed names are what reach one closure.
            Assert.True(registry.TryResolveType("Ceras.Test.NameUniqueness.Box`1<System.Int32>", out var first));
            Assert.Equal(typeof(Box<int>), first);
            Assert.True(registry.TryResolveType("Ceras.Test.NameUniqueness.Box`1<System.String>", out var second));
            Assert.Equal(typeof(Box<string>), second);
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

        /// <summary>
        /// The half of the invariant that registration order cannot settle, in both orders. A structural
        /// entry's name is not chosen: it is <c>TypeIdentity.GetCanonicalName(type)</c>, read off the CLR
        /// type by the bake. So a declared entry cannot take that name whichever of the two arrives
        /// first — the derived entry holds it because the name is its own, and a claim from anywhere else
        /// is refused where it is written.
        /// </summary>
        [Fact]
        public void A_Derived_Entry_Cannot_Be_Shadowed_By_A_Declared_Name()
        {
            // Derived first: it holds the name, and the claim on it is refused.
            var derivedFirst = new SchemaRegistry();
            derivedFirst.Register(typeof(List<int>), null);   // structural: ClosedList
            var ex = Assert.Throws<CerasSchemaException>(() => derivedFirst.Register(typeof(Left), Info(ClosedList)));
            Assert.Contains(ClosedList, ex.Message);
            Assert.Contains("Ceras.Test.NameUniqueness.Left", ex.Message);

            // Declared first: the same claim is refused there instead, and the structural entry is free to
            // register afterwards. The order the two arrive in cannot decide it, because the second of
            // them is where the name belongs to somebody else.
            var declaredFirst = new SchemaRegistry();
            var early = Assert.Throws<CerasSchemaException>(() => declaredFirst.Register(typeof(Left), Info(ClosedList)));
            Assert.Contains(ClosedList, early.Message);
            Assert.False(declaredFirst.IsDeclared(typeof(Left)));

            declaredFirst.Register(typeof(List<int>), null);
            declaredFirst.Seal();
            Assert.True(declaredFirst.TryResolveType(ClosedList, out var resolved));
            Assert.Equal(typeof(List<int>), resolved);
        }

        /// <summary>
        /// Every derived name, refused, as a stable name and as an alias. The names are the runtime's own
        /// spelling of the types the bake derives entries for, so this covers the collections, the
        /// structural value types, arrays, a closed user generic and a collection subclass at once — and
        /// says nothing about which of them is "structural", because the rule is about the name.
        /// </summary>
        [Theory]
        [MemberData(nameof(DerivedCanonicalNames))]
        public void A_Derived_Canonical_Name_Cannot_Be_Claimed_By_Another_Type(Type derivedType)
        {
            string derivedName = TypeIdentity.GetCanonicalName(derivedType);

            // Precondition of the whole row: this is a name the bake derives, and the type it names is not
            // registered yet — which is the state the exploit is written against.
            Assert.True(WellKnownTypes.IsDerivedCanonicalName(derivedName), derivedName);
            var bare = new SchemaRegistry();
            Assert.False(bare.IsDeclared(derivedType));

            var asStableName = new SchemaRegistry();
            var ex = Assert.Throws<CerasSchemaException>(() => asStableName.Register(typeof(Left), Info(derivedName)));

            // The name, the registration that tried to take it, the fact the name already has an owner,
            // and the way out of it.
            Assert.Contains(derivedName, ex.Message);
            Assert.Contains("Ceras.Test.NameUniqueness.Left", ex.Message);
            Assert.Contains("belongs to the type it spells", ex.Message);
            Assert.Contains("TypeIdentity.GetCanonicalName(type)", ex.Message);
            Assert.Contains("[PreviousTypeName] alias of its definition", ex.Message);

            // An alias is refused on the very same terms, where it is written.
            var asAlias = new SchemaRegistry();
            var aliased = Assert.Throws<CerasSchemaException>(() =>
                asAlias.Register(typeof(Left), Info("Ceras.Test.NameUniqueness.Left", derivedName)));
            Assert.Contains(derivedName, aliased.Message);
            Assert.Contains("[PreviousTypeName] alias", aliased.Message);

            // Neither registry kept anything, so the name is still the type's to take: the refused entry
            // joined no manifest, and where the name resolves at all (a framework shape resolves through
            // Type.GetType, with nothing of it in this registry) it is the type it spells.
            Assert.False(asStableName.IsDeclared(typeof(Left)));
            Assert.False(asAlias.IsDeclared(typeof(Left)));
            Assert.False(asStableName.TryGetId(typeof(Left), out _));
            if (asStableName.TryResolveType(derivedName, out var resolved))
                Assert.NotEqual(typeof(Left), resolved);
        }

        /// <summary>
        /// The other side of the same rule, and the reason no manifest is refused by it: the type a
        /// derived name names may claim it, which is what every entry the bake creates does — through
        /// <c>Register(type, null)</c> from <c>DeclareRoot</c> and from the walk that discovers a shape
        /// while baking. Each one is asked to produce that name from the CLR type, so this is not a
        /// transcription of it either.
        /// </summary>
        [Theory]
        [MemberData(nameof(StructuralShapes))]
        public void A_Structural_Shape_May_Claim_The_Canonical_Name_It_Is_Derived_Under(Type shape)
        {
            string derivedName = TypeIdentity.GetCanonicalName(shape);

            // The derived descriptor's own name, before the registry is asked anything about it.
            Assert.True(SchemaRegistry.TryBuildStructuralSchema(shape, out var derived), derivedName);
            Assert.Equal(derivedName, derived.StableName);

            var registry = new SchemaRegistry();
            registry.Register(shape, null);
            registry.Seal();

            Assert.True(registry.IsDeclared(shape));
            Assert.Equal(derivedName, registry.All().Single(e => e.type == shape).info.StableName);
            Assert.True(registry.TryResolveType(derivedName, out var resolved));
            Assert.Equal(shape, resolved);
        }

        /// <summary>
        /// The generated half of the same rule: a closed user generic is not derived from the CLR type but
        /// declared, and what a manifest entry claims is the canonical name the generator wrote for that
        /// closure — the very string a bake-discovered shape would have derived. So the claim is legal,
        /// and a name that is a <em>different</em> closure's is not.
        /// </summary>
        [Theory]
        [MemberData(nameof(ClosedUserGenerics))]
        public void A_Closed_User_Generic_May_Claim_Its_Own_Canonical_Name(Type closure)
        {
            string ownName = TypeIdentity.GetCanonicalName(closure);

            var registry = new SchemaRegistry();
            registry.Register(closure, Info(ownName));
            registry.Seal();

            Assert.Equal(ownName, registry.All().Single(e => e.type == closure).info.StableName);
            Assert.True(registry.TryResolveType(ownName, out var resolved));
            Assert.Equal(closure, resolved);

            // … while another closure of the same family is a different name, and claiming it is the same
            // claim as any other: the name belongs to the type it spells, not to the family.
            var other = closure == typeof(Box<Box<int>>) ? typeof(Box<string>) : typeof(Box<Box<int>>);
            string otherName = TypeIdentity.GetCanonicalName(other);
            var impostor = new SchemaRegistry();
            Assert.Throws<CerasSchemaException>(() => impostor.Register(typeof(Left), Info(otherName)));
        }

        /// <summary>
        /// The rule is a recognizer over the name's <em>form</em>, so the two implementations of that form
        /// — the runtime's <see cref="TypeIdentity.GetCanonicalName"/> and
        /// <see cref="WellKnownTypes.IsDerivedCanonicalName"/> — have to agree about which types carry
        /// their shape in their name. Swept over every type this assembly declares rather than a list, so
        /// a nested generic, an enum, a plain class and a generated closure all go through it, and a name
        /// one of them spells that the other does not recognize fails the row that names it.
        /// </summary>
        [Fact]
        public void The_Recognizer_Agrees_With_The_Canonical_Name_Of_Every_Type_This_Assembly_Declares()
        {
            int swept = 0;
            foreach (var type in typeof(TypeNameUniquenessTests).Assembly.GetTypes())
            {
                // An open type has no canonical name of its own: it is a family, and its name carries an
                // arity marker instead of arguments (which is the one spelling the recognizer refuses).
                if (type.ContainsGenericParameters) continue;

                string name = TypeIdentity.GetCanonicalName(type);
                bool carriesShape = type.IsArray || (type.IsGenericType && !type.IsGenericTypeDefinition);

                Assert.True(carriesShape == WellKnownTypes.IsDerivedCanonicalName(name),
                    $"'{name}' ({type}) is {(carriesShape ? "a closed" : "a plain")} canonical name, and the recognizer disagrees.");
                swept++;
            }

            Assert.True(swept > 100, $"the sweep only looked at {swept} types, so it guards nothing.");
        }

        /// <summary>
        /// The other direction of the same recognizer, and the spellings it must <em>not</em> claim: a
        /// name is only taken when it is a closed canonical name of a type. Everything here is a name an
        /// author may legitimately write — an open alias, a plain stable name, the legacy
        /// <c>Type.FullName</c> spelling of a closed generic, a pointer, a generic parameter — plus the
        /// malformed forms of the canonical grammar itself.
        /// </summary>
        [Fact]
        public void Nothing_But_A_Closed_Canonical_Name_Is_A_Derived_Name()
        {
            string[] notDerived =
            {
                null, "", " ",
                "System.Int32",                                    // a scalar
                "System.Array",                                    // the reserved definition itself
                "System.Collections.Generic.List`1",              // an open definition
                "Ceras.Test.NameUniqueness.Box`1",
                "Legacy.Box`1",                                    // an open alias, shared by a family
                "System.Int32*",                                   // a pointer: no writer describes one
                "!0",                                              // a generic parameter
                "Ceras.Test.NameUniqueness.Left",                  // an ordinary stable name
                "System.Collections.Generic.List`1[System.Int32]", // the legacy Type.FullName spelling
                "System.Int32, System.Version",
                "Ceras.Test.NameUniqueness.Box`1<>",               // an empty argument list
                "Ceras.Test.NameUniqueness.Box`1<System.Int32",   // an unterminated one
                "Ceras.Test.NameUniqueness.Box`1<System.Int32> ",  // … with a stray character
                "Ceras.Test..Box",                                 // an empty segment
                "Ceras.Test.Box.",                                 // a trailing separator
                "Ceras.Test.Box`",                                 // an arity marker with no arity
                "Some Name<System.Int32>",                         // a space is not part of a type name
            };

            foreach (string name in notDerived)
                Assert.False(WellKnownTypes.IsDerivedCanonicalName(name), $"'{name ?? "<null>"}' is not a closed canonical name.");

            // … and the rank an array carries is part of the name, so a rank-2 spelling is as much a
            // derived name as a rank-1 one (the format refuses to *describe* the shape, which is a
            // different refusal, made where the schema is built).
            Assert.True(WellKnownTypes.IsDerivedCanonicalName("System.Int32[]"));
            Assert.True(WellKnownTypes.IsDerivedCanonicalName("System.Int32[,]"));
            Assert.True(WellKnownTypes.IsDerivedCanonicalName("System.Collections.Generic.List`1<System.Int32>[]"));
        }

        /// <summary>
        /// The rule cannot be a resolution, and this is why: a claim is checked inside
        /// <c>SchemaRegistry.Register</c>, which holds the registry's lock, and the only way to ask "is
        /// this string the name of a CLR type" is the configured <see cref="ITypeBinder"/> — user code,
        /// documented never to run under that lock. So the check is asked with a binder installed that
        /// counts, and not one claim — refused or accepted — may reach it.
        /// </summary>
        [Fact]
        public void A_Claim_Is_Never_Resolved_Through_The_Configured_Binder()
        {
            var registry = new SchemaRegistry();
            var binder = new CountingBinder();
            registry.SetTypeBinder(binder);

            // One refusal per rule, and two accepted claims: none of them may consult the binder, whether
            // the claim is refused or not. (The duplicate needs two claimants, so the first one claims the
            // name and the second one is the refusal.)
            Assert.Throws<CerasSchemaException>(() => registry.Register(typeof(Left), Info(ClosedList)));
            Assert.Throws<CerasSchemaException>(() => registry.Register(typeof(Left), Info("System.Int32")));
            registry.Register(typeof(Left), Info(Shared));          // an ordinary name, accepted
            Assert.Throws<CerasSchemaException>(() => registry.Register(typeof(Right), Info(Shared)));
            registry.Register(typeof(Box<int>), null);            // the derived path

            Assert.Equal(0, binder.Calls);

            // … and the manifest is intact afterwards, so the refused claims left nothing behind and the
            // registry still answers from itself, with no binder involved.
            registry.Seal();
            Assert.True(registry.TryResolveType(Shared, out var left));
            Assert.Equal(typeof(Left), left);
            Assert.True(registry.TryResolveType(TypeIdentity.GetCanonicalName(typeof(Box<int>)), out var box));
            Assert.Equal(typeof(Box<int>), box);
            Assert.Equal(0, binder.Calls);
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
            // The declared roots are folded in before the bake starts, and a shape whose own descriptor
            // cannot be built is refused right there - an enum with two members under one wire name, which
            // the schema cannot express (the generated path reports the same declaration at compile time).
            //
            // This case used to be a declared root whose *derived* name somebody had already claimed, and
            // it can no longer be written: that claim is refused where it is made, long before a seal
            // (A_Derived_Entry_Cannot_Be_Shadowed_By_A_Declared_Name), so the declared-root loop has
            // nothing left to refuse on that account.
            var refusedRootShape = new SchemaRegistry();
            refusedRootShape.DeclareRoot(typeof(ClashingWireNames));
            AssertNothingBaked(refusedRootShape);

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

        /// <summary>A binder that answers "no such type" and counts how often it was asked at all.</summary>
        sealed class CountingBinder : ITypeBinder
        {
            public int Calls;
            public string GetBaseName(Type type) => type.FullName;
            public Type GetTypeFromBase(string baseTypeName) { Calls++; return null; }
            public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments) { Calls++; return null; }
        }
    }
}
