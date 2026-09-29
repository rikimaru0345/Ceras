// A closed canonical name of arity three or more, and what the lookup does with one.
//
// Type.GetType does not answer "no such type" for every name it will not read: it *throws* —
// FileLoadException("The given assembly name was invalid") — for the canonical spelling of a closed
// generic whose arity is three or more. Arity one and two answer null, which is why the shape was
// invisible until a model declared a three-element tuple; the boundary is the runtime's, not Ceras'.
//
// The name that fails is not author-written text. It is what TypeIdentity.GetCanonicalName derives and
// what the source generator writes into a member's type name, so the first Save of a model holding an
// (int, int, int) died inside the bake with a reflection exception rather than describing the member.
// A refusal to parse is a miss, and the identity that gets asked next is the canonical parser's: it
// asks for the *definition* name (System.ValueTuple`3, which Type.GetType does read) and closes the
// generic itself. So a throw at that step never hid a type — it only hid the answer to a spelling the
// step cannot handle.
//
// What is pinned here: every reserved tuple arity resolves, a nested generic and an array inside such a
// name resolve, a name whose type is genuinely absent is a miss rather than a throw, the bake describes
// such a member and refuses a missing one with the actionable error, and both robust readers round-trip
// a graph built from them. The arities that already worked are asserted beside the broken one, so the
// fix cannot quietly re-answer a name that resolved before.
using System;
using System.Collections.Generic;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.ClosedGenericLookup
{
    /// <summary>
    /// A self-contained context, so adding tuple members to this model cannot renumber another
    /// context's type ids.
    /// </summary>
    [CerasContext(Name)]
    [CerasSerializable]
    public class TupleArityModel
    {
        /// <summary>Named once, because a context the model and the test disagree about is a silent no-op.</summary>
        public const string Name = "ClosedGenericLookup";

        public (int, int) Two;
        public (int, int, int) Three;
        public (int, int, int, int) Four;
        public (int, int, int, int, int) Five;
        public (int, int, int, int, int, int) Six;
        public (int, int, int, int, int, int, int) Seven;

        // A nested generic and an array over the arity that throws, because the runtime refuses the
        // whole name, not just the definition it is spelled with.
        public Dictionary<string, (int, int, int)> ByName;
        public (int, int, int)[] Points;
    }

    public class ClosedGenericNameLookupTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(TupleArityModel.Name));

        /// <summary>A registry with no binder — the state every hand-built one starts in.</summary>
        static SchemaRegistry Bare() => new SchemaRegistry();

        [Fact]
        public void Every_Reserved_Tuple_Arity_Resolves_Through_The_Canonical_Parser()
        {
            var registry = Bare();

            // Arity one and two answer null from Type.GetType and always did; three and above throw.
            // Every arity is asserted together, because "the broken ones now work" is only a real claim
            // if the working ones still work the same way.
            AssertResolves(registry, typeof(ValueTuple<int>), "System.ValueTuple`1<System.Int32>");
            AssertResolves(registry, typeof((int, int)), "System.ValueTuple`2<System.Int32,System.Int32>");
            AssertResolves(registry, typeof((int, int, int)), "System.ValueTuple`3<System.Int32,System.Int32,System.Int32>");
            AssertResolves(registry, typeof((int, int, int, int)), "System.ValueTuple`4<System.Int32,System.Int32,System.Int32,System.Int32>");
            AssertResolves(registry, typeof((int, int, int, int, int)), "System.ValueTuple`5<System.Int32,System.Int32,System.Int32,System.Int32,System.Int32>");
            AssertResolves(registry, typeof((int, int, int, int, int, int)), "System.ValueTuple`6<System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32>");
            AssertResolves(registry, typeof((int, int, int, int, int, int, int)), "System.ValueTuple`7<System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32>");

            // The reference tuples are the same shape under another definition, and the parser is not
            // told which family it is looking at.
            AssertResolves(registry, typeof(Tuple<int, int, int>), "System.Tuple`3<System.Int32,System.Int32,System.Int32>");
            AssertResolves(registry, typeof(Tuple<int, int, int, int, int, int, int>), "System.Tuple`7<System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32>");

            // The open definition name is the family rather than one of its closures, and it still
            // answers — the definition is what a closed name is built from, and what the same lookup
            // step has always returned for an open alias. An arity no definition has is a miss.
            Assert.True(registry.TryResolveType("System.ValueTuple`3", out var openDef));
            Assert.Equal(typeof(ValueTuple<,,>), openDef);
            Assert.False(registry.TryResolveType("System.ValueTuple`9", out _));
        }

        [Fact]
        public void A_High_Arity_Name_Resolves_Whatever_It_Nests()
        {
            var registry = Bare();

            // A collection and a lower-arity tuple as arguments of the throwing arity.
            AssertResolves(registry,
                typeof((List<int>, (int, string), int)),
                "System.ValueTuple`3<System.Collections.Generic.List`1<System.Int32>,System.ValueTuple`2<System.Int32,System.String>,System.Int32>");

            // The reverse nesting: an arity that resolves on its own, carrying one that does not.
            AssertResolves(registry,
                typeof(Dictionary<string, (int, int, int)>),
                "System.Collections.Generic.Dictionary`2<System.String,System.ValueTuple`3<System.Int32,System.Int32,System.Int32>>");

            // Arrays over it, at both ranks. The element is parsed by the same recursion, so the
            // suffix is not what decides whether a name resolves.
            AssertResolves(registry, typeof((int, int, int)[]), "System.ValueTuple`3<System.Int32,System.Int32,System.Int32>[]");
            AssertResolves(registry, typeof((int, int, int)[,]), "System.ValueTuple`3<System.Int32,System.Int32,System.Int32>[,]");

            // A mix of element types, so the answer is not accidentally the all-int one.
            AssertResolves(registry,
                typeof((int, string, double)),
                "System.ValueTuple`3<System.Int32,System.String,System.Double>");
        }

        [Fact]
        public void A_High_Arity_Name_For_A_Type_This_Build_Does_Not_Have_Is_A_Miss()
        {
            var registry = Bare();

            // The definition is absent, so there is nothing to close: a miss, the same answer a name
            // of any other arity gets. It is stated here because "the runtime refused to parse it" and
            // "this build has no such type" are different facts that must not be reported the same way.
            Assert.False(registry.TryResolveType("Ceras.Test.ClosedGenericLookup.Absent`3<System.Int32,System.Int32,System.Int32>", out var absent));
            Assert.Null(absent);

            // Absent at the outer level, present at the inner one: the name still names nothing.
            Assert.False(registry.TryResolveType("Ceras.Test.ClosedGenericLookup.Absent`3<System.Collections.Generic.List`1<System.Int32>,System.Int32,System.Int32>", out _));

            // Present as a definition, wrong argument count: the arity in the name is checked against
            // the definition, so this is a miss too rather than a closure of something else.
            Assert.False(registry.TryResolveType("System.ValueTuple`3<System.Int32,System.Int32>", out _));

            // And the assembly-qualified spelling, which is the legacy shape step 2 exists for, is
            // unaffected by any of this.
            Assert.False(registry.TryResolveType("System.Int32, Totally.Made.Up.Assembly, Version=1.0.0.0", out _));

            // A configured binder is still the last door, and it does not make a throw fatal either.
            registry.SetTypeBinder(new SimpleTypeBinder());
            Assert.False(registry.TryResolveType("Ceras.Test.ClosedGenericLookup.Absent`3<System.Int32,System.Int32,System.Int32>", out var stillAbsent));
            Assert.Null(stillAbsent);
        }

        [Fact]
        public void A_Declared_High_Arity_Member_Bakes_And_Names_Its_Own_Type()
        {
            var registry = Bare();
            const string memberType = "System.ValueTuple`3<System.Int32,System.Int32,System.Int32>";

            registry.Register(typeof(TupleArityModel), new SchemaTypeInfo(
                stableName: "Ceras.Test.ClosedGenericLookup.TupleArityModel",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Class,
                fields: new[] { new SchemaFieldInfo("Three", Array.Empty<string>(), memberType, true) },
                fixedByteSize: 0));

            // The bake is where a declared member's name is turned back into a type, so this is the
            // call the defect broke: it has to complete, and the member's type has to be describable.
            registry.Seal();

            Assert.True(registry.TryResolveType(memberType, out var member));
            Assert.Equal(typeof((int, int, int)), member);
            Assert.True(registry.TryGetId(typeof((int, int, int)), out _));
        }

        [Fact]
        public void A_Declared_Member_Of_A_Missing_High_Arity_Type_Is_Refused_Actionably()
        {
            var registry = Bare();
            const string missing = "Ceras.Test.ClosedGenericLookup.Absent`3<System.Int32,System.Int32,System.Int32>";

            registry.Register(typeof(TupleArityModel), new SchemaTypeInfo(
                stableName: "Ceras.Test.ClosedGenericLookup.TupleArityModel",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Class,
                fields: new[] { new SchemaFieldInfo("Gone", Array.Empty<string>(), missing, true) },
                fixedByteSize: 0));

            // The refusal is the declaration error it was always meant to be — naming the member and
            // the unresolvable identity — and not a reflection exception about an assembly name. The
            // bake consults the registry and nothing else, so a miss here has to be the whole answer.
            var ex = Assert.Throws<CerasSchemaException>(() => registry.Seal());
            Assert.Contains("field 'Gone'", ex.Message, StringComparison.Ordinal);
            Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
            Assert.Contains("does not resolve", ex.Message, StringComparison.Ordinal);

            // Nothing half-baked was published.
            Assert.False(registry.IsSealed);
            Assert.False(registry.HasBakedSchema);
        }

        [Fact]
        public void Tuple_Members_Of_Every_Arity_Round_Trip_Through_Both_Robust_Readers()
        {
            var model = new TupleArityModel
            {
                Two = (1, 2),
                Three = (3, 4, 5),
                Four = (6, 7, 8, 9),
                Five = (10, 11, 12, 13, 14),
                Six = (15, 16, 17, 18, 19, 20),
                Seven = (21, 22, 23, 24, 25, 26, 27),
                ByName = new Dictionary<string, (int, int, int)> { ["a"] = (28, 29, 30) },
                Points = new[] { (31, 32, 33), (34, 35, 36) },
            };

            // The generated fast path, which needs the bake to have described every tuple member.
            byte[] bytes = Generated().Save(model, 1);
            var fast = Generated().Load<TupleArityModel>(bytes, 1);
            Assert.Equal(model.Two, fast.Two);
            Assert.Equal(model.Three, fast.Three);
            Assert.Equal(model.Four, fast.Four);
            Assert.Equal(model.Five, fast.Five);
            Assert.Equal(model.Six, fast.Six);
            Assert.Equal(model.Seven, fast.Seven);
            Assert.Equal(model.ByName["a"], fast.ByName["a"]);
            Assert.Equal(model.Points, fast.Points);

            // The interpreter, which reads the same names back out of the file's schema section.
            var slow = Generated().Load<TupleArityModel>(SlowPath(bytes), 1);
            Assert.Equal(model.Three, slow.Three);
            Assert.Equal(model.ByName["a"], slow.ByName["a"]);
            Assert.Equal(model.Points, slow.Points);
        }

        /// <summary>
        /// A name resolves to exactly the type it is the canonical name of. The name is spelled out
        /// rather than derived, because the spelling is what the runtime refuses — and then compared
        /// against the identity helper, so a change to the canonical syntax cannot leave the literals
        /// quietly describing a name nothing writes any more.
        /// </summary>
        static void AssertResolves(SchemaRegistry registry, Type expected, string canonicalName)
        {
            Assert.Equal(canonicalName, TypeIdentity.GetCanonicalName(expected));

            Assert.True(registry.TryResolveType(canonicalName, out var resolved));
            Assert.Equal(expected, resolved);
        }
    }
}
