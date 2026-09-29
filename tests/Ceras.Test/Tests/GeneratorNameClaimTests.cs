// CERAS019, as a compile-time question: within one serialization context, a name belongs to exactly one
// manifest entry — the rule `Ceras.Format.SchemaRegistry.ClaimNames` enforces when the context is
// registered. It used to be asked at the first Save/Load, on a manifest that had already built, and only
// about the stable names of the discovered types; the generator now asks it while the code is being
// written, over the names the generated manifest actually hands the registry (see ManifestNames).
//
// What the check is, is what `ClaimNames` is: two entries of *one* context claiming one name, in any
// combination of stable name and `[PreviousTypeName]` alias, in either order. So these rows are in the
// order the invariant is stated:
//   * the same claim twice, in every combination, and in both orders — a duplicate clause is a
//     direction-symmetric rule, and one that only held in one order would leave a rename to break it;
//   * per context, not across contexts: two contexts are two registries, so one name in both is legal;
//   * every registration path the generated manifest has, not only the types with a generated formatter —
//     the placeholder schema, a declared minimal type and a declared structural root claim names too;
//   * the one legal overlap, an open alias shared by several closures of ONE generic family, which is
//     what makes a renamed generic readable at all (RenamedGenericAliasTests), and its boundary: a second
//     family claiming that name is a conflict;
//   * a type claiming its OWN derived canonical name is not a conflict with itself, which is what every
//     closure and every structural root in a manifest does.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Ceras.SourceGenerator;
using Xunit;

namespace Ceras.Test.Generator
{
    public class GeneratorNameClaimTests
    {
        static IReadOnlyList<string> DiagnosticIds(string source)
            => GeneratorTestHarness.Diagnostics(source).Select(d => d.Id).ToList();

        static Diagnostic Single019(string source)
        {
            var found = GeneratorTestHarness.Diagnostics(source).Where(d => d.Id == "CERAS019").ToList();
            return Assert.Single(found);
        }

        // ── The same claim twice, in every combination ────────────────────────────────────────────
        //
        // Four rows for what is one rule, because there are two kinds of name and two roles each can
        // take, and the runtime asks the same question for all four. A check that only covered the
        // stable/stable pair (what the previous version of this diagnostic did) would let three of them
        // through to a Save that silently binds to the first registration.

        [Fact]
        public void Two_Stable_Names_Are_A_Compile_Error()
        {
            var d = Single019(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable(""Dup"")]
    public class A { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable(""Dup"")]
    public class B { public int Y; }
}");
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        }

        [Fact]
        public void An_Alias_And_A_Stable_Name_Are_A_Compile_Error_In_Either_Order()
        {
            // Alias first: the stable name is the second claim.
            var aliasFirst = Single019(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""Shared"")]
    public class A { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable(""Shared"")]
    public class B { public int Y; }
}");
            Assert.Contains("[PreviousTypeName] alias", aliasFirst.GetMessage());
            Assert.Contains("stable name", aliasFirst.GetMessage());

            // Stable name first: the same pair the other way round. One name index, so which of the two
            // is reported must not depend on the order the declarations happen to be written in.
            var stableFirst = Single019(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable(""Shared"")]
    public class A { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""Shared"")]
    public class B { public int Y; }
}");
            Assert.Contains("stable name", stableFirst.GetMessage());
            Assert.Contains("[PreviousTypeName] alias", stableFirst.GetMessage());
        }

        [Fact]
        public void Two_Aliases_Are_A_Compile_Error()
        {
            var d = Single019(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""Old.Name"")]
    public class A { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""Old.Name"")]
    public class B { public int Y; }
}");
            Assert.Contains("Old.Name", d.GetMessage());
        }

        [Fact]
        public void The_Report_Names_Both_Claimants_And_The_Context()
        {
            // A compile error is only actionable if it says which name, which two types, and which
            // context — the context especially, because the same two types may be legal in another one.
            var d = Single019(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""Old.Name"")]
    public class A { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""Old.Name"")]
    public class B { public int Y; }
}");

            string message = d.GetMessage();
            Assert.Contains("'Old.Name'", message);
            Assert.Contains("'Tests'", message);
            Assert.Contains("A", message);
            Assert.Contains("B", message);
            // … and the fix, which is about the claim rather than about a member name (CERAS009's advice).
            Assert.Contains("Rename one of the two types", message);
            Assert.Contains("[PreviousTypeName] alias on only one of them", message);

            // Reported on the second claim — the alias itself, where it was written — not on the type
            // and not on the other claimant. That is the runtime's rule too: it refuses a claim where the
            // claim is made, so the author sees it at the name rather than at a registration that will
            // happen much later.
            var span = d.Location.GetLineSpan();
            string reportedLine = d.Location.SourceTree!.ToString().Split('\n')[span.StartLinePosition.Line];
            Assert.Contains("PreviousTypeName", reportedLine, System.StringComparison.Ordinal);
            Assert.Contains("Old.Name", reportedLine, System.StringComparison.Ordinal);
        }

        // ── Per context, never across contexts ────────────────────────────────────────────────────

        [Fact]
        public void The_Same_Name_In_Two_Contexts_Is_Legal()
        {
            // Two contexts are two manifests with two registries, and the runtime never asks one about
            // the other's names. A union check would refuse this, which would make one name per
            // compilation — and the whole point of contexts is that they are independent finite graphs.
            Assert.DoesNotContain("CERAS019", DiagnosticIds(@"
using Ceras;
namespace T
{
    [CerasContext(""First"")]
    [CerasSerializable(""Shared"")]
    public class A { public int X; }

    [CerasContext(""Second"")]
    [CerasSerializable(""Shared"")]
    public class B { public int Y; }
}"));
        }

        [Fact]
        public void An_Alias_Reused_Across_Contexts_Is_Legal_Too()
        {
            Assert.DoesNotContain("CERAS019", DiagnosticIds(@"
using Ceras;
namespace T
{
    [CerasContext(""First"")]
    [CerasSerializable]
    [PreviousTypeName(""Old.Name"")]
    public class A { public int X; }

    [CerasContext(""Second"")]
    [CerasSerializable]
    [PreviousTypeName(""Old.Name"")]
    public class B { public int Y; }
}"));
        }

        [Fact]
        public void A_Third_Context_Reaching_Both_Claimants_Is_Reported_Too()
        {
            // The third context has no declaration of its own for either type, but discovery reaches them
            // through C's members, so they are in that manifest as well - and that manifest is refused at
            // registration too. A check scoped to declared roots would report only the first two contexts
            // and leave this one a save/load-time error, which is the same hole the per-path model closes.
            var diagnostics = GeneratorTestHarness.Diagnostics(@"
using Ceras;
namespace T
{
    [CerasContext(""First"")]
    [CerasSerializable(""Dup"")]
    public class A { public int X; }

    [CerasContext(""Second"")]
    [CerasSerializable(""Dup"")]
    public class B { public int Y; }

    [CerasContext(""ReachesBoth"")]
    [CerasSerializable]
    public class C { public A A; public B B; }
}").Where(d => d.Id == "CERAS019").ToList();

            // Exactly one: in "First" only A is registered and in "Second" only B, so neither of those
            // manifests contains a conflict. It is the one that reached *both* that does.
            var d0 = Assert.Single(diagnostics);
            Assert.Contains("'ReachesBoth'", d0.GetMessage());
            Assert.Contains("'Dup'", d0.GetMessage());
            Assert.Contains("A", d0.GetMessage());
            Assert.Contains("B", d0.GetMessage());
        }

        [Fact]
        public void A_Context_Reaching_One_Claimant_Alone_Is_Not_A_Conflict()
        {
            // The mirror of the row above, and the reason a per-context check is not merely a weaker
            // version of a union check: "ReachesOnlyA" holds A but not B, so its manifest is fine and
            // must not be reported, however many other contexts conflict.
            var diagnostics = GeneratorTestHarness.Diagnostics(@"
using Ceras;
namespace T
{
    [CerasContext(""First"")]
    [CerasSerializable(""Dup"")]
    public class A { public int X; }

    [CerasContext(""Second"")]
    [CerasSerializable(""Dup"")]
    public class B { public int Y; }

    [CerasContext(""ReachesBoth"")]
    [CerasSerializable]
    public class C { public A A; public B B; }

    [CerasContext(""ReachesOnlyA"")]
    [CerasSerializable]
    public class D { public A A; }
}").Where(d => d.Id == "CERAS019").ToList();

            var d0 = Assert.Single(diagnostics);
            Assert.Contains("'ReachesBoth'", d0.GetMessage());
            Assert.DoesNotContain(diagnostics, d => d.GetMessage().Contains("'ReachesOnlyA'"));
            Assert.DoesNotContain(diagnostics, d => d.GetMessage().Contains("'First'"));
        }

        // ── The one legal overlap: closures of ONE family sharing an open alias ───────────────────

        [Fact]
        public void Several_Closures_Of_One_Family_May_Share_An_Open_Alias()
        {
            // This is what makes a renamed generic readable at all, and it is the reason the runtime's
            // duplicate rule compares claim *targets* rather than types: an open name stands for the
            // family, so every closure of Box<> answers for it. Refusing it would make a migration
            // impossible to write, so the generator must not refuse it either.
            Assert.DoesNotContain("CERAS019", DiagnosticIds(@"
using Ceras;
using System.Collections.Generic;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""Legacy.Box`1"")]
    public class Box<T> { public T Payload; public int Added; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        public Box<int> Ints;
        public Box<string> Text;
        public List<Box<Box<int>>> Nested;
    }
}"));
        }

        [Fact]
        public void An_Open_Alias_Two_Different_Definitions_Claim_Is_A_Compile_Error()
        {
            // The boundary of the row above. This shape used to be declared in the test assembly itself,
            // with a test asserting the CerasSchemaException its first Save/Load raised; the generator
            // now refuses the declaration, so that context can no longer be built. The runtime half of
            // the same rule is still pinned on a hand-written descriptor in TypeNameUniquenessTests
            // (A_Stable_Name_Cannot_Claim_An_Open_Alias_Another_Family_Holds).
            var d = Single019(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""Legacy.Box`1"")]
    public class First<T> { public T Value; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""Legacy.Box`1"")]
    public class Second<T> { public T Value; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public First<int> A; public Second<string> B; }
}");

            Assert.Contains("Legacy.Box`1", d.GetMessage());
            Assert.Contains("First<int>", d.GetMessage());
            Assert.Contains("Second<string>", d.GetMessage());
        }

        [Fact]
        public void A_Stable_Name_May_Claim_An_Open_Alias_Another_Family_Holds()
        {
            // The mirror of the row above, in the other role: claiming the name *as a stable name* is
            // still claiming a name somebody else answers for — a family, not one of its members.
            Assert.Contains("CERAS019", DiagnosticIds(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""Legacy.Box`1"")]
    public class Box<T> { public T Value; }

    [CerasContext(""Tests"")]
    [CerasSerializable(""Legacy.Box`1"")]
    public class Other<T> { public T Value; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public Box<int> A; public Other<string> B; }
}"));
        }

        [Fact]
        public void A_Closed_Name_Is_Claimed_Only_By_The_Closure_It_Names()
        {
            // ... while a name that carries arguments names ONE closure, so the family exception does
            // not reach it and a different type claiming it is a conflict like any other. This is the
            // boundary of the row above, and it is decided the way the runtime decides it: the claim
            // target of a *closed* name is that closure, not the family.
            //
            // Note which rule carries this. The runtime would refuse the impostor under its derived-name
            // rule even if no closure of Box<> were in the context at all - a closed canonical name
            // belongs to the type it spells, whatever else claims it. The generator cannot make that
            // judgment for a type it has no symbol for (ManifestNames says so), so what it checks here
            // is the duplicate half, and that is enough: Box<int> is in this context, so the name does
            // have two claimants and the conflict is real either way.
            Assert.Contains("CERAS019", DiagnosticIds(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Box<T> { public T Value; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public Box<int> A; }

    [CerasContext(""Tests"")]
    [CerasSerializable(""T.Box`1<System.Int32>"")]
    public class Impostor { public int X; }
}"));
        }

        [Fact]
        public void An_Alias_May_Repeat_Its_Own_Stable_Name()
        {
            // A name the entry already holds is redundant rather than wrong, so it stays legal: the
            // runtime indexes it with TryAdd and a second closure of the same entry is the same claim.
            Assert.DoesNotContain("CERAS019", DiagnosticIds(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable(""Same"")]
    [PreviousTypeName(""Same"")]
    public class A { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public A A; }
}"));
        }

        // ── Every registration path, not only the types with a generated formatter ───────────────
        //
        // The previous version of this check iterated the discovered types and skipped anything that
        // looked like a built-in shape, so the three paths below were invisible to it. Each is a
        // registration the emitted manifest makes, and each claims the name it is given.

        [Fact]
        public void A_Declared_Structural_Root_Claims_Its_Canonical_Name()
        {
            // A context root of a shape no generated formatter can describe (here an enum) is declared
            // with DeclareRoot and registered under the canonical name the runtime derives from the CLR
            // type. Another entry claiming that name is a duplicate.
            Assert.Contains("CERAS019", DiagnosticIds(@"
using Ceras;
[assembly: CerasKnownType(typeof(T.Tier), ""Tests"")]
namespace T
{
    public enum Tier { Low = 1, High = 2 }

    [CerasContext(""Tests"")]
    [CerasSerializable(""T.Tier"")]
    public class Impostor { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public Tier Step; public int After; }
}"));
        }

        [Fact]
        public void A_Declared_Structural_Root_Also_Claims_Its_Rename_Aliases()
        {
            // The enum branch of the runtime's derived descriptor reads [PreviousTypeName] off the CLR
            // type, so an alias on a *declared root* is a claim like any other — a shape the old check
            // could not see at all, because it iterated discovered types only.
            Assert.Contains("CERAS019", DiagnosticIds(@"
using Ceras;
[assembly: CerasKnownType(typeof(T.Tier), ""Tests"")]
namespace T
{
    [PreviousTypeName(""T.OldTier"")]
    public enum Tier { Low = 1, High = 2 }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""T.OldTier"")]
    public class Impostor { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public Tier Step; public int After; }
}"));
        }

        [Fact]
        public void A_Minimal_Registration_Claims_Its_Canonical_Name()
        {
            // An interface a polymorphic member declares gets the field-less placeholder schema, under
            // the type's own canonical name. Nothing about it is a "generated" type, so the old check
            // never looked at it.
            Assert.Contains("CERAS019", DiagnosticIds(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public IShape Shape; public int After; }

    [Ceras.CerasSerializable]
    public interface IShape { int Id { get; } }

    [CerasContext(""Tests"")]
    [CerasSerializable(""T.IShape"")]
    public class Impostor { public int X; }
}"));
        }

        [Fact]
        public void A_Referenced_But_Not_Generated_Type_Claims_Its_Canonical_Name()
        {
            // A type a member references but discovery deliberately leaves out - here a blacklisted
            // type, which is what this registration path exists for - is registered with a field-less
            // placeholder schema under its canonical name. It is a claim, and one the old check could not
            // see at all: the old check iterated the *discovered* types, and this type is precisely the
            // one that is not discovered.
            const string source = @"
using Ceras;
[assembly: CerasGeneratorBlacklist(""T.External"")]
namespace T
{
    [CerasContext(""Tests"")]
    [Ceras.Serializable]
    public class Root { public External Ext; public int After; }

    public class External { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable(""T.External"")]
    public class Impostor { public int X; }
}";

            var diagnostics = GeneratorTestHarness.Diagnostics(source).Where(d => d.Id == "CERAS019").ToList();
            var d0 = Assert.Single(diagnostics);
            Assert.Contains("T.External", d0.GetMessage());

            // The claim is real, not a guess about a registration that is not written: the placeholder
            // registration for the blacklisted type is in the emitted manifest, under that same name.
            var manifest = GeneratorTestHarness.GeneratedSourceContaining(source, "RobustFormatters");
            Assert.NotNull(manifest);
            Assert.Contains("SchemaRegistry.Register(typeof(global::T.External)", manifest, StringComparison.Ordinal);
            Assert.Contains("stableName: \"T.External\"", manifest, StringComparison.Ordinal);
        }

        // ── The types the old check skipped ──────────────────────────────────────────────────────

        [Fact]
        public void A_User_Type_That_Lives_In_A_System_Namespace_Is_Checked()
        {
            // The old check skipped anything IsWellKnownType accepted, and a System.* type this
            // compilation declares is exactly that — so two such types claiming one name produced no
            // diagnostic at all. It is a user type (its own author marked it), it gets a generated
            // formatter and it claims its name like any other.
            Assert.Contains("CERAS019", DiagnosticIds(@"
using Ceras;
namespace System.Games
{
    [Ceras.CerasSerializable]
    public class Hero { public int Hp; }
}
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public System.Games.Hero Hero; public int After; }

    [CerasContext(""Tests"")]
    [CerasSerializable(""System.Games.Hero"")]
    public class Impostor { public int X; }
}"));
        }

        [Fact]
        public void A_Type_Is_Not_Reported_Against_Its_Own_Derived_Name()
        {
            // The whole of a normal manifest does this: every closure of a generic and every structural
            // root is registered under the canonical name the bake would derive for it. A rule that
            // treated a derived name as claimed-by-another would refuse every manifest there is, so the
            // check compares claim *targets* and the type's own name is its own.
            Assert.DoesNotContain("CERAS019", DiagnosticIds(@"
using Ceras;
using System.Collections.Generic;
[assembly: CerasKnownType(typeof(System.Collections.Generic.List<T.Hero>), ""Tests"")]
namespace T
{
    public class Hero { public int Hp; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        public List<Hero> Heroes;
        public Hero[] More;
        public Dictionary<string, Hero> ByName;
        public int After;
    }
}"));
        }

        [Fact]
        public void A_Closure_May_Claim_Its_Own_Canonical_Name()
        {
            // A closed user generic is not derived at the bake — a generated manifest *declares* it —
            // and what it declares is the very string a bake-discovered shape would have derived. So the
            // claim is legal, and the same name on a second closure is a different string.
            Assert.DoesNotContain("CERAS019", DiagnosticIds(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [Ceras.Serializable]
    public class Box<T> { public T Value; public int Added; }

    [CerasContext(""Tests"")]
    [Ceras.Serializable]
    public class Root { public Box<int> A; public Box<string> B; }
}"));
        }

        // ── Two claims on one type, and a claim that is not a claim ───────────────────────────────

        [Fact]
        public void One_Type_Claiming_Two_Different_Names_Is_Fine()
        {
            // The rule is about names, not about types: a stable name plus a distinct alias is the
            // ordinary renamed-type shape.
            Assert.DoesNotContain("CERAS019", DiagnosticIds(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable(""Current"")]
    [PreviousTypeName(""Old.One"")]
    [PreviousTypeName(""Older.One"")]
    public class A { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public A A; }
}"));
        }

        [Fact]
        public void An_Empty_Alias_Is_Not_A_Claim()
        {
            // An empty alias names nothing, and the runtime skips it rather than indexing it — so it
            // cannot collide with the next entry's empty alias, and the check must not invent that
            // collision.
            Assert.DoesNotContain("CERAS019", DiagnosticIds(@"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName("""")]
    public class A { public int X; }

    [CerasContext(""Tests"")]
    [Ceras.Serializable]
    [PreviousTypeName("""")]
    public class B { public int Y; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public A A; public B B; }
}"));
        }

        [Fact]
        public void Distinct_Names_Produce_No_Diagnostic()
        {
            // The control for the whole file: everything a normal manifest does — explicit stable names,
            // aliases, closures of one family, a structural root, a collection member — in one context.
            Assert.DoesNotContain("CERAS019", DiagnosticIds(@"
using Ceras;
using System.Collections.Generic;
[assembly: CerasKnownType(typeof(System.Collections.Generic.List<T.Hero>), ""Tests"")]
namespace T
{
    public class Hero { public int Hp; }

    public enum Tier { Low = 1, High = 2 }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    [PreviousTypeName(""T.OldBox`1"")]
    public class Box<T> { public T Value; }

    [CerasContext(""Tests"")]
    [CerasSerializable(""T.Plain"")]
    [PreviousTypeName(""T.Older"")]
    public class Plain { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        public List<Hero> Heroes;
        public Box<int> IntBox;
        public Box<string> TextBox;
        public Plain P;
        public Tier Step;
        public int After;
    }
}"));
        }
    }
}
