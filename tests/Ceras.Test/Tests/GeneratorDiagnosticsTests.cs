using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ceras.SourceGenerator;
using Xunit;

namespace Ceras.Test.Generator
{
    /// <summary>
    /// Pins the CERAS018 boundary: a private member with a nameable type must be accessed through a
    /// plain <c>[UnsafeAccessor]</c> and must NOT trip CERAS018; only a member whose *type* generated
    /// code cannot name should. (No reflection fallback is added for these shapes.)
    /// </summary>
    public class GeneratorDiagnosticsTests
    {
        static IReadOnlyList<string> RunGeneratorDiagnosticIds(string source)
            => GeneratorTestHarness.DiagnosticIds(source);

        static IReadOnlyList<Diagnostic> RunGeneratorDiagnostics(string source)
            => GeneratorTestHarness.Diagnostics(source);

        [Fact]
        public void PrivateMember_WithNameableType_DoesNotEmitCERAS018()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder
    {
        private int _value;
        public Holder(int value) { _value = value; }
        public int Value => _value;
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS018", ids);
        }

        [Fact]
        public void Member_WithInaccessibleType_EmitsCERAS018()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder
    {
        private Secret _secret;
        private class Secret { public int X; }
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS018", ids);
        }

        [Fact]
        public void TwoTypes_WithSameExplicitStableName_EmitCERAS019()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable(""Dup"")]
    public class A { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable(""Dup"")]
    public class B { public int Y; }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS019", ids);
        }

        [Fact]
        public void TwoEnumMembers_WithSameStableName_EmitCERAS009()
        {
            // The recorded name is how a member is identified across a migration, so two members under
            // one name would make the schema ambiguous rather than merely redundant. The runtime-declared
            // path refuses the same declaration with a CerasSchemaException instead.
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder { public Tier Steps; }

    public enum Tier
    {
        [CerasStableName(""SameStep"")]
        Up = 1,
        [CerasStableName(""SameStep"")]
        Down = 2,
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS009", ids);
        }

        [Fact]
        public void DistinctEnumMembers_DoNotEmitCERAS009()
        {
            // A wire name that merely *contains* another member's name is not a duplicate: the comparison
            // is on whole names, so the pinned prefixes below are legal and the enum is usable.
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder { public Tier Steps; }

    public enum Tier
    {
        Step = 1,
        [CerasStableName(""WireStep"")]
        Rung = 2,
        [CerasStableName(""Other"")]
        [PreviousName(""Ladder"")]
        Rung2 = 3,
    }
}";

            Assert.DoesNotContain("CERAS009", RunGeneratorDiagnosticIds(source));
        }

        // ----------------------------------------------------------------------------------------
        // CASE A — generated formatter field-name collisions.
        //
        // MakeFormatterFieldName sanitizes the fully-qualified display: 'A.B_C' and the nested
        // 'A.B.C' both become '_A_B_CFormatter'. Here the second type is a reference-type collection,
        // so it is skipped from formatter generation (no CERAS022) and no member references it, which
        // isolates the field-name collision to exactly one declaring type.
        // ----------------------------------------------------------------------------------------
        [Fact]
        public void FormatterFieldNameCollision_EmitsCERAS020()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder
    {
        public A.B_C First;
        public A.B.C Second;
    }
}

namespace A
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class B_C { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class B
    {
        [CerasContext(""Tests"")]
        [CerasSerializable]
        public class C : System.Collections.Generic.List<int> { }
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS020", ids);
            // The collection type is skipped before it can claim a generated name, and a reference-type
            // collection is deliberately excluded from the CASE B check, so this isolates CERAS020.
            Assert.DoesNotContain("CERAS021", ids);
            Assert.DoesNotContain("CERAS022", ids);
        }

        [Fact]
        public void DistinctMemberTypes_DoNotEmitCERAS020()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class First { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Second { public int Y; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder
    {
        public First A;
        public Second B;
        public int Number;
        public System.Collections.Generic.List<int> Items;
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS020", ids);
        }

        // ----------------------------------------------------------------------------------------
        // CASE A — generated class / source-hint name collisions (two distinct types sanitize to the
        // same identifier even though no single formatter declares both as members).
        // ----------------------------------------------------------------------------------------
        [Fact]
        public void GeneratedNameCollision_EmitsCERAS022()
        {
            const string source = @"
using Ceras;
namespace A
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class B_C { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class B
    {
        [CerasContext(""Tests"")]
        [CerasSerializable]
        public class C { public int Y; }
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS022", ids);
            // No single formatter declares both colliding types as members, so this is purely the
            // global generated-name collision (class name / source hint).
            Assert.DoesNotContain("CERAS020", ids);
        }

        [Fact]
        public void DistinctGeneratedNames_DoNotEmitCERAS022()
        {
            const string source = @"
using Ceras;
namespace A
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class B_C { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class B
    {
        [CerasContext(""Tests"")]
        [CerasSerializable]
        public class D { public int Y; }
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS022", ids);
        }

        // ----------------------------------------------------------------------------------------
        // CASE B — a member references a [CerasSerializable] type for which no formatter will be
        // generated because Ceras cannot construct it.
        // ----------------------------------------------------------------------------------------
        [Fact]
        public void Member_ReferencingUnconstructableSerializableType_EmitsCERAS021()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class NoCtor
    {
        public int Value;
        public NoCtor(int somethingElse) { }
    }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder
    {
        public NoCtor Member;
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS021", ids);
        }

        [Fact]
        public void Member_ReferencingConstructableSerializableType_DoesNotEmitCERAS021()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Nested { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder
    {
        public Nested Member;
        public int Number;
        public System.Collections.Generic.List<int> Items;
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS021", ids);
        }

        // A member whose type has no [CerasSerializable] attribute is treated as user-provided (a custom
        // formatter supplies it), so no generated formatter is required and CASE B must not fire.
        [Fact]
        public void Member_ReferencingCustomFormatterType_DoesNotEmitCERAS021()
        {
            const string source = @"
using Ceras;
namespace T
{
    public class External { public int X; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder
    {
        public External Member;
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS021", ids);
        }

        // ----------------------------------------------------------------------------------------
        // CERAS025 — a compilation that intends to use the generated manifest (it annotates at
        // least one type with [CerasSerializable]/[CerasContext], or has an assembly-level
        // [CerasSerializable<T>]/[CerasKnownType]) but declares no [CerasContext] context is a
        // compile-time error, because Ceras has no default context and every UseGeneratedFormatters
        // call would otherwise throw at runtime.
        // ----------------------------------------------------------------------------------------
        [Fact]
        public void NoContext_WithSerializableType_EmitsCERAS025()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasSerializable]
    public class Holder { public int Value; }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS025", ids);
        }

        [Fact]
        public void NoContext_WithOnlyAssemblyLevelKnownType_EmitsCERAS025()
        {
            const string source = @"
using Ceras;
[assembly: CerasKnownType<System.Collections.Generic.List<int>>]
namespace T
{
    public class Plain { public int Value; }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS025", ids);
        }

        // The deliberately tolerated case: a compilation that references the analyzer but shows no
        // serialization intent (no type-level annotations and no assembly-level declarations) is not
        // asked to declare a context. The generator emits an empty (compilable) manifest.
        [Fact]
        public void NoContext_WithoutSerializationIntent_DoesNotEmitCERAS025()
        {
            const string source = @"
namespace T
{
    public class Plain { public int Value; }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS025", ids);
        }

        // ----------------------------------------------------------------------------------------
        // Member diagnostics (persisted by MemberExplorer during discovery) must be reported exactly
        // once per build, not once per discovery/fail-early/emission pass (see the `## Review later`
        // entry on duplicate diagnostics). CERAS016 fires for an implicitly-declared polymorphic
        // member, and CERAS001 for a [CerasSerializable] type with no serializable members.
        // ----------------------------------------------------------------------------------------
        [Fact]
        public void MemberDiagnostic_IsReportedOnce_NotOncePerPass()
        {
            const string source = @"
using Ceras;
namespace T
{
    public interface IPolymorphic { }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        public IPolymorphic Member;
    }
}";

            var diagnostics = RunGeneratorDiagnostics(source);
            Assert.Equal(1, diagnostics.Count(d => d.Id == "CERAS016"));
        }

        [Fact]
        public void NoSerializableMembersDiagnostic_IsReportedOnce()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Empty { }
}";

            var diagnostics = RunGeneratorDiagnostics(source);
            Assert.Equal(1, diagnostics.Count(d => d.Id == "CERAS001"));
        }

        // ----------------------------------------------------------------------------------------
        // CERAS026 — unsupported constructor-dependent immutable cycle.
        //
        // Node has a readonly member that participates in a cycle (Node.Next -> Node), but the
        // selected constructor does not initialize it and there is no parameterless construction
        // path, so Ceras cannot construct the cycle.
        // ----------------------------------------------------------------------------------------
        [Fact]
        public void UnsupportedImmutableCycle_EmitsCERAS026()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Node
    {
        public readonly Node Next;
        public readonly int Value;
        public Node(int value) { Value = value; }
    }
}";

            var diagnostics = RunGeneratorDiagnostics(source);
            Assert.Contains("CERAS026", diagnostics.Select(d => d.Id));
        }

        // A parameterless construction path means Ceras can allocate+register before reading members,
        // so the same cycle is supported (no false positive).
        [Fact]
        public void ImmutableCycle_WithParameterlessCtor_DoesNotEmitCERAS026()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Node
    {
        public readonly Node Next;
        public Node() { }
        public Node(Node next) { Next = next; }
    }
}";

            var diagnostics = RunGeneratorDiagnostics(source);
            Assert.DoesNotContain("CERAS026", diagnostics.Select(d => d.Id));
        }

        // The readonly member is supplied by the chosen [CerasConstructor], so it is initialized
        // during construction and the cycle is constructible.
        [Fact]
        public void ImmutableCycle_WithConstructorParameter_DoesNotEmitCERAS026()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Node
    {
        public readonly Node Next;
        [CerasConstructor]
        public Node(Node next) { Next = next; }
    }
}";

            var diagnostics = RunGeneratorDiagnostics(source);
            Assert.DoesNotContain("CERAS026", diagnostics.Select(d => d.Id));
        }

        // A mutable cycle (the member is writable) is fully supported and must not be flagged.
        [Fact]
        public void MutableCycle_DoesNotEmitCERAS026()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Node
    {
        public Node Next;
        public Node(Node next) { Next = next; }
    }
}";

            var diagnostics = RunGeneratorDiagnostics(source);
            Assert.DoesNotContain("CERAS026", diagnostics.Select(d => d.Id));
        }

        // A recursive declared type is not itself a problem: the readonly member is writable through
        // its constructor, so an acyclic value round-trips and a cycle is constructible. The rule must
        // not fire merely because the type is self-referential (TODO 0.4).
        [Fact]
        public void RecursiveType_WithConstructorProvidedMember_DoesNotEmitCERAS026()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class LinkedListNode
    {
        public readonly LinkedListNode Next;
        public readonly int Value;
        public LinkedListNode(int value) { Value = value; }
        public LinkedListNode(int value, LinkedListNode next) { Value = value; Next = next; }
    }
}";

            var diagnostics = RunGeneratorDiagnostics(source);
            Assert.DoesNotContain("CERAS026", diagnostics.Select(d => d.Id));
        }

        // A mutual A->B->A readonly cycle where neither side can be constructed without the other:
        // both types are separately diagnosed (the member is not constructor-provided).
        [Fact]
        public void MutualImmutableCycle_EmitsCERAS026()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class A
    {
        public readonly B Other;
        public readonly int Value;
        public A(int value) { Value = value; }
    }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class B
    {
        public readonly A Other;
        public readonly int Value;
        public B(int value) { Value = value; }
    }
}";

            var diagnostics = RunGeneratorDiagnostics(source);
            Assert.Equal(2, diagnostics.Count(d => d.Id == "CERAS026"));
        }

        // A generic self-cycle with a readonly member that the constructor does not provide.
        [Fact]
        public void GenericImmutableCycle_EmitsCERAS026()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Box<T>
    {
        public readonly Box<T> Next;
        public readonly T Value;
        public Box(T value) { Value = value; }
    }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        public Box<int> IntBox;
    }
}";

            var diagnostics = RunGeneratorDiagnostics(source);
            Assert.Contains("CERAS026", diagnostics.Select(d => d.Id));
        }

        // ----------------------------------------------------------------------------------------
        // CERAS029 — a member whose type the robust format has no built-in representation for, and
        // which the generator cannot treat as a user type either: not declared in this compilation,
        // not marked [CerasSerializable] by whoever owns it, and living in a System.* namespace
        // (System.Uri, System.Numerics.BigInteger, System.Version, …).
        //
        // The generator used to classify every System.* type as "well known", record "it uses a
        // built-in formatter" and leave the type out of the manifest, so the build succeeded and the
        // FIRST robust Save failed at the bake with advice that cannot work: declare the type, add
        // [CerasKnownType], or register a custom formatter. None of those make a type the format
        // cannot describe serializable, so the report has to happen here instead.
        //
        // The rule is a namespace heuristic, and the message says so: the generator cannot ask whether
        // an assembly is a framework one. The two cases it can resolve are covered below — a type this
        // compilation declares, and a type its own author marked [CerasSerializable] — and both are
        // user types, not reportable ones.
        // ----------------------------------------------------------------------------------------
        static string RootWithMember(string member) => @"
using Ceras;
using System;
using System.Collections;
using System.Numerics;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        " + member + @"
        public int After;
    }
}";

        [Theory]
        // member declaration, the offending type as the report must name it (symbols display)
        // (IntPtr/UIntPtr display as their nint/nuint aliases, like every other Ceras diagnostic)
        // the three member types the runtime probe (scratch-probes/SystemMembers) used
        [InlineData("public System.Uri Site;", "Uri")]
        [InlineData("public BigInteger Big;", "BigInteger")]
        [InlineData("public System.Version Ver;", "Version")]
        // other types the format has no reserved id and no structural shape for
        [InlineData("public System.Text.StringBuilder Builder;", "StringBuilder")]
        [InlineData("public IntPtr Handle;", "nint")]
        [InlineData("public System.Collections.ArrayList List;", "ArrayList")]
        [InlineData("public System.Collections.ICollection Old;", "ICollection")]
        [InlineData("public System.Collections.Generic.IEnumerable<int> Items;", "IEnumerable<int>")]
        [InlineData("public System.Attribute Meta;", "Attribute")]
        // the same kind of type hidden inside a shape the format DOES expand into further type ids
        [InlineData("public System.Uri[] Sites;", "Uri")]
        [InlineData("public System.Collections.Generic.List<System.Uri> Sites;", "Uri")]
        [InlineData("public System.Collections.Generic.Dictionary<string, System.Numerics.BigInteger> Big;", "BigInteger")]
        [InlineData("public System.Collections.Generic.KeyValuePair<int, System.Version> Pair;", "Version")]
        [InlineData("public (int, System.Uri) Pair;", "Uri")]
        public void UndescribableMemberType_EmitsCERAS029(string member, string offendingType)
        {
            var diagnostics = RunGeneratorDiagnostics(RootWithMember(member));
            var d = Assert.Single(diagnostics.Where(x => x.Id == "CERAS029"));
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);

            // Actionable: it names the offending type itself (not the whole member type, so a type
            // hidden inside a supported shape is still reported precisely), and offers only fixes
            // that work.
            string message = d.GetMessage();
            Assert.Contains($"'{offendingType}' is not declared in this compilation", message, StringComparison.Ordinal);
            Assert.Contains("carries no [CerasSerializable] attribute", message, StringComparison.Ordinal);
            Assert.Contains("[Exclude]", message, StringComparison.Ordinal);
            Assert.Contains("No declaration ([CerasSerializable]/[CerasContext]/[CerasKnownType<...>])", message, StringComparison.Ordinal);

            // Reported on the member itself, not on the type.
            var span = d.Location.GetLineSpan();
            Assert.Contains(member.Split(';')[0].Trim(), RootWithMember(member).Split('\n')[span.StartLinePosition.Line],
                StringComparison.Ordinal);
        }

        [Fact]
        public void UndescribableMemberType_DoesNotOfferDeclarationOrCustomFormatterAsAFix()
        {
            // The runtime bake error suggested "[CerasSerializable]/[CerasContext]" and
            // "[assembly: CerasKnownType<...>]"; the probe showed neither can rescue a Uri, and a
            // custom IFormatter<Uri> cannot either. None of them may be offered as a fix here.
            var d = Assert.Single(RunGeneratorDiagnostics(RootWithMember("public System.Uri Site;"))
                .Where(x => x.Id == "CERAS029"));

            string message = d.GetMessage();
            Assert.Contains("No declaration ([CerasSerializable]/[CerasContext]/[CerasKnownType<...>])", message, StringComparison.Ordinal);
            Assert.Contains("no custom IFormatter<T> can make it serializable in a robust context", message, StringComparison.Ordinal);

            // The one case where more input from the user fixes it: their own type from another assembly.
            Assert.Contains("mark it [CerasSerializable]", message, StringComparison.Ordinal);
        }

        [Fact]
        public void UndescribableMemberType_IsReportedOnceAndTheMemberIsDropped()
        {
            // The member is dropped (like CERAS027), so the emitted schema no longer references the
            // type, and the diagnostic is reported exactly once even though member discovery runs in
            // several passes over a build.
            var run = GeneratorTestHarness.Run(RootWithMember("public System.Uri Site;"));
            Assert.Single(run.Diagnostics.Where(x => x.Id == "CERAS029"));

            var root = GeneratorTestHarness.GeneratedSourceContaining(RootWithMember("public System.Uri Site;"), "Root");
            Assert.NotNull(root);
            Assert.DoesNotContain("Site", root, StringComparison.Ordinal);
            Assert.Contains("After", root, StringComparison.Ordinal);
        }

        // A user type that merely lives in a System.* namespace is the user's own type, so it needs a
        // generated formatter and schema like any other — the mirror image of the case above.
        [Fact]
        public void UserTypeInSystemNamespace_IsNotAFrameworkType()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        public System.Games.Hero Hero;
        public int After;
    }
}

namespace System.Games
{
    [Ceras.CerasSerializable]
    public class Hero
    {
        public int Hp;
    }
}";

            Assert.DoesNotContain("CERAS029", RunGeneratorDiagnosticIds(source));

            // It really is generated and registered, not silently skipped.
            var formatter = GeneratorTestHarness.GeneratedSourceContaining(source, "System_Games_Hero");
            Assert.NotNull(formatter);
            Assert.Contains("FormatterBase<global::System.Games.Hero>", formatter, StringComparison.Ordinal);
            Assert.Contains("stableName: \"System.Games.Hero\"", formatter, StringComparison.Ordinal);

            var manifest = GeneratorTestHarness.GeneratedSourceContaining(source, "RobustFormatters");
            Assert.NotNull(manifest);
            Assert.Contains("SchemaRegistry.Register(typeof(global::System.Games.Hero)", manifest, StringComparison.Ordinal);
        }

        // A wrapper must not undo that. An array (or pointer) of a user type that lives in a System.*
        // namespace is a user type spelled with one more layer: the classification has to look at the
        // type the wrapper holds, not at the wrapper's own namespace.
        [Theory]
        [InlineData("public System.Games.Hero[] Heroes;")]
        [InlineData("public System.Games.Hero[][] Heroes;")]
        [InlineData("public System.Collections.Generic.List<System.Games.Hero> Heroes;")]
        public void UserTypeInSystemNamespace_BehindAWrapper_IsStillAUserType(string member)
        {
            var source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        " + member + @"
        public int After;
    }
}

namespace System.Games
{
    [Ceras.CerasSerializable]
    public class Hero
    {
        public int Hp;
    }
}";

            Assert.DoesNotContain("CERAS029", RunGeneratorDiagnosticIds(source));

            // Discovered and generated, not skipped as a built-in shape.
            var formatter = GeneratorTestHarness.GeneratedSourceContaining(source, "System_Games_Hero");
            Assert.NotNull(formatter);
            Assert.Contains("FormatterBase<global::System.Games.Hero>", formatter, StringComparison.Ordinal);
        }

        // A pointer is the other wrapper, and the other half of the peel: the classification follows it to
        // the pointed-at type, so a pointer to a *user* type is not reported as a framework type (the
        // format cannot describe a pointer payload either way, which is a separate, pre-existing gap — the
        // member is left for the emitter to reject, exactly as before any of this).
        [Fact]
        public void PointerToUserTypeInSystemNamespace_IsNotReportedAsCERAS029()
        {
            var source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public unsafe class Root
    {
        public unsafe System.Games.Hero* Heroes;
        public int After;
    }
}

namespace System.Games
{
    [Ceras.CerasSerializable]
    public class Hero
    {
        public int Hp;
    }
}";

            Assert.DoesNotContain("CERAS029", GeneratorTestHarness.DiagnosticIds(source, allowUnsafe: true));
        }

        [Fact]
        public void PointerToFrameworkType_IsStillReported()
        {
            var source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public unsafe class Root
    {
        public unsafe int* Value;
        public int After;
    }
}";

            var d = Assert.Single(GeneratorTestHarness.Diagnostics(source, allowUnsafe: true)
                .Where(x => x.Id == "CERAS029"));
            Assert.Contains("no built-in representation", d.GetMessage(), StringComparison.Ordinal);
        }

        // The same user type reached through a *reference* instead of this compilation's sources. The        // in-memory compilation references the test assembly, which declares System.Games.Hero, so
        // nothing here declares it — only the [CerasSerializable] attribute says it is a user type.
        [Fact]
        public void ReferencedUserTypeInSystemNamespace_IsNotAFrameworkType()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        public System.Games.Hero Hero;
        public int After;
    }
}";

            Assert.DoesNotContain("CERAS029", RunGeneratorDiagnosticIds(source));

            // And the generator really does emit and register it (that is what makes treating it as a user
            // type correct rather than merely quiet): the member's public fields are reachable from here.
            var formatter = GeneratorTestHarness.GeneratedSourceContaining(source, "System_Games_Hero");
            Assert.NotNull(formatter);
            Assert.Contains("FormatterBase<global::System.Games.Hero>", formatter, StringComparison.Ordinal);
            Assert.Contains("\"Hp\"", formatter, StringComparison.Ordinal);

            var manifest = GeneratorTestHarness.GeneratedSourceContaining(source, "RobustFormatters");
            Assert.NotNull(manifest);
            Assert.Contains("SchemaRegistry.Register(typeof(global::System.Games.Hero)", manifest, StringComparison.Ordinal);
        }

        // A referenced System.* type with no [CerasSerializable] cannot be told apart from a framework
        // type by anything the generator can see: Roslyn exposes no "is this a platform assembly" test,
        // so the namespace is the only signal and the report has to happen. This test documents that
        // boundary instead of leaving it implicit — the residual of the rule, and the reason the message
        // offers [CerasSerializable] as the way out for a user's own type.
        [Fact]
        public void ReferencedUnannotatedSystemNamespaceType_IsReported()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        public System.Games.Sidekick Mate;
        public int After;
    }
}";

            var d = Assert.Single(RunGeneratorDiagnostics(source).Where(x => x.Id == "CERAS029"));
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        }

        // The same boundary at a declaration site, and the exemption working there too: declaring a
        // referenced user type is the supported way to pull it into this context.
        [Fact]
        public void ReferencedUserTypeInSystemNamespace_CanBeDeclaredAsARoot()
        {
            const string source = @"
using Ceras;
[assembly: CerasKnownType(typeof(System.Games.Hero), ""Tests"")]
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public int After; }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS030", ids);
            Assert.DoesNotContain("CERAS029", ids);

            var manifest = GeneratorTestHarness.GeneratedSourceContaining(source, "RobustFormatters");
            Assert.NotNull(manifest);
            Assert.Contains("SchemaRegistry.Register(typeof(global::System.Games.Hero)", manifest, StringComparison.Ordinal);
        }

        // The supported structural/BCL shapes are exactly the ones the format describes, so none of
        // them may be reported. (A member may still be reported by another rule — e.g. a polymorphic
        // one — so this only pins the absence of CERAS029.)
        [Theory]
        [InlineData("public int[] Values;")]
        [InlineData("public string[] Values;")]
        [InlineData("public int[][] Values;")]
        [InlineData("public System.DayOfWeek Day;")]
        [InlineData("public System.DayOfWeek? Day;")]
        [InlineData("public (int, string) Pair;")]
        [InlineData("public System.Tuple<int, string> Pair;")]
        [InlineData("public System.Collections.Generic.KeyValuePair<string, int> Pair;")]
        [InlineData("public System.Collections.Generic.List<int> Items;")]
        [InlineData("public System.Collections.Generic.Dictionary<string, int> Items;")]
        [InlineData("public System.Collections.Generic.HashSet<int> Items;")]
        [InlineData("public System.Collections.Generic.Queue<int> Items;")]
        [InlineData("public System.Collections.Generic.Stack<int> Items;")]
        [InlineData("public int? Maybe;")]
        [InlineData("public System.Guid Id2;")]
        [InlineData("public System.DateTime When;")]
        [InlineData("public System.DateTimeOffset When;")]
        [InlineData("public System.TimeSpan Span;")]
        [InlineData("public decimal Money;")]
        [InlineData("public System.Type Kind;")]
        public void SupportedBuiltinMemberType_DoesNotEmitCERAS029(string member)
            => Assert.DoesNotContain("CERAS029", RunGeneratorDiagnosticIds(RootWithMember(member)));

        // An [Exclude]d member is dropped before the shape is looked at, so it is not reported.
        [Fact]
        public void ExcludedUndescribableMember_DoesNotEmitCERAS029()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        [Exclude]
        public System.Uri Site;
        public int After;
    }
}";

            Assert.DoesNotContain("CERAS029", RunGeneratorDiagnosticIds(source));
        }

        // A type outside the System.* namespaces keeps the existing path (a minimal registration plus
        // whatever custom formatter or reflection fallback the user provides), so it is not reported
        // here — that question belongs to the robust custom-formatter rejection, not to this check.
        [Fact]
        public void NonSystemNamespaceMemberType_DoesNotEmitCERAS029()
        {
            Assert.DoesNotContain("CERAS029", RunGeneratorDiagnosticIds(
                RootWithMember("public Ceras.CerasSerializer Serializer;")));
        }

        // A banned type (delegate, enumerator, reflection metadata) is reported by its own rule, whatever
        // that rule decides. This check must not pile CERAS029 on top of it: the two questions are
        // independent, and the delegate case is being reworked separately.
        [Fact]
        public void BannedSystemNamespaceMember_IsNotAlsoReportedAsCERAS029()
        {
            Assert.DoesNotContain("CERAS029", RunGeneratorDiagnosticIds(
                RootWithMember("public System.Action Callback;")));
        }

        // A multidimensional array keeps its own diagnostic (CERAS027) and is not also reported as a
        // type with no built-in representation.
        [Fact]
        public void MultidimensionalArray_EmitsCERAS027_NotCERAS029()
        {
            var ids = RunGeneratorDiagnosticIds(RootWithMember("public int[,] Grid;"));
            Assert.Contains("CERAS027", ids);
            Assert.DoesNotContain("CERAS029", ids);
        }

        // ----------------------------------------------------------------------------------------
        // CERAS030 — an assembly-level declaration that names a type the format cannot describe. The
        // probe showed [assembly: CerasKnownType(typeof(Uri))] does not rescue the bake, so a
        // declaration that silently does nothing must be reported where it is written.
        // ----------------------------------------------------------------------------------------
        [Fact]
        public void AssemblyLevelDeclarationOfUndescribableType_EmitsCERAS030()
        {
            const string source = @"
using Ceras;
[assembly: CerasKnownType(typeof(System.Uri), ""Tests"")]
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public int After; }
}";

            var d = Assert.Single(RunGeneratorDiagnostics(source).Where(x => x.Id == "CERAS030"));
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.Contains("names 'Uri'", d.GetMessage(), StringComparison.Ordinal);
            Assert.Contains("cannot give it a schema", d.GetMessage(), StringComparison.Ordinal);

            // Reported on the declaration itself, not on a metadata location inside the BCL.
            Assert.Contains("CerasKnownType", d.Location.SourceTree!.ToString().Split('\n')[
                d.Location.GetLineSpan().StartLinePosition.Line], StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("[assembly: CerasKnownType<System.Collections.Generic.List<int>>()]")]
        [InlineData("[assembly: CerasKnownType<System.Collections.Generic.KeyValuePair<string, int>>()]")]
        [InlineData("[assembly: CerasKnownType<(int, string)>()]")]
        [InlineData("[assembly: CerasKnownType<System.DayOfWeek>()]")]
        public void AssemblyLevelDeclarationOfSupportedShape_DoesNotEmitCERAS030(string declaration)
        {
            var source = @"
using Ceras;
" + declaration + @"
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root { public int After; }
}";

            Assert.DoesNotContain("CERAS030", RunGeneratorDiagnosticIds(source));
        }
    }
}
