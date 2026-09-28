using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.Tests
{
    // ── the types the assertions below are about ────────────────────────────────────────────────
    // A context of its own, so adding it cannot change another context's manifest, its type-id
    // space or its layout fingerprint. ContextTests only asserts that the declared names are
    // *contained* in RobustFormatters.ContextNames, so a new one is invisible to it.

    [CerasContext("TrimmingRoots")]
    [CerasSerializable]
    public class TrimmingRootsSubject
    {
        [CerasStableName("Value")] public int Value;

        [CerasStableName("Other")] public TrimmingRootsOther Other;

        [CerasStableName("Boxed")] public TrimmingRootsBox<int> Boxed;
    }

    // A base type that contributes an inherited field, and is never itself formatted under its own
    // name. The runtime member walk reaches it, so it is the case a root naming only the formatted
    // type would miss.
    public abstract class TrimmingRootsBase
    {
        [CerasStableName("Inherited")] public int Inherited;
    }

    [CerasSerializable]
    public class TrimmingRootsOther : TrimmingRootsBase
    {
        [CerasStableName("Own")] public string Own;
    }

    // A generic type reached as a *closed* instantiation. The root has to name the closed type: a
    // dependency on the open definition does not reach the members of a closed instantiation's
    // fields, which is what NativeAOT's member walk needs.
    [CerasSerializable]
    public class TrimmingRootsBox<T>
    {
        [CerasStableName("Value")] public T Value;
    }

    /// <summary>
    /// The attributes that keep a trimmed or NativeAOT build able to reflect over the generated
    /// formatters and the types they format.
    /// <para>
    /// The trimming itself cannot be exercised here - every test in the suite runs on a full
    /// runtime, where all of this is present whether or not anything says it should be. What *can*
    /// be checked, and is what the fix is made of, is that the generated registration states the
    /// dependencies, so that deleting or loosening them fails here instead of only failing in
    /// <c>samples/TrimAotSmoke</c>'s <c>aot</c> mode, which is in neither the solution nor
    /// <c>dotnet test</c>. The companion to <see cref="TrimmingFormatterRootingTests"/>, which does
    /// the same for the library's own formatter families.
    /// </para>
    /// <para>
    /// Each assertion is about a distinct failure the AOT build actually produced: without the
    /// formatted type's own root, <c>TypeConfig&lt;T&gt;</c> comes back with <i>zero</i> members
    /// instead of erroring, so <c>MemberConfig&lt;T&gt;</c> is never built at all; without the
    /// formatter's root, <c>ReferenceFormatter&lt;T&gt;</c> cannot bind its dispatch delegate and the
    /// robust context's <c>RobustCustomFormatterPolicy</c> reports every generated formatter as a
    /// user custom formatter it has to refuse.
    /// </para>
    /// </summary>
    public class TrimmingGeneratedRootingTests
    {
        /// <summary>
        /// What Ceras reads off a type's data members: the constructor of <c>TypeConfig</c> walks
        /// <c>GetFields</c> / <c>GetProperties</c> with public <i>and</i> non-public flags over a
        /// runtime <see cref="Type"/> (<c>ReflectionHelper.EnumerateMembers</c>), so both halves are
        /// asked for - a root naming only the public half comes back with the private members
        /// missing, which is the same silent short member list as no root at all.
        /// <c>PublicParameterlessConstructor | NonPublicConstructors</c> is what
        /// <c>TypeConfigDefaults.ApplyConstructorDefaults</c> looks for and what
        /// <c>SpecificConstructor</c> invokes.
        /// </summary>
        const DynamicallyAccessedMemberTypes DataMembers =
            DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields
            | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor
            | DynamicallyAccessedMemberTypes.NonPublicConstructors;

        /// <summary>
        /// The same, minus the constructors: a base type that only contributes a field never has an
        /// instance built, so its requirement is the members alone.
        /// </summary>
        const DynamicallyAccessedMemberTypes InheritedMembers =
            DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields
            | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties;

        /// <summary>
        /// What is read off a generated formatter class: <c>Serialize</c> / <c>Deserialize</c>, which
        /// <c>ReferenceFormatter&lt;T&gt;</c> looks up by name, and the <c>internal static readonly
        /// Schema</c> field, which <c>RobustCustomFormatterPolicy</c> reads to recognise the
        /// context's own formatters.
        /// </summary>
        const DynamicallyAccessedMemberTypes FormatterSurface =
            DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods
            | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields;

        /// <summary>
        /// The generated registration entry point, which is where the dependencies are stated. It is
        /// the one emitted member every generator-enabled application calls - through
        /// <c>UseGeneratedFormatters</c> or directly - so a dependency stated here is honoured for a
        /// raw context and a robust one alike.
        /// </summary>
        static MethodInfo Registration()
            => typeof(RobustFormatters).GetMethod(
                   nameof(RobustFormatters.UseRobustFormatters),
                   BindingFlags.Public | BindingFlags.Static,
                   binder: null,
                   types: new[] { typeof(Ceras.CerasConfig), typeof(string) },
                   modifiers: null)
               ?? throw new InvalidOperationException(
                   "Ceras.Generated.RobustFormatters.UseRobustFormatters(CerasConfig, string) no longer exists. " +
                   "It is the one emitted member every generator-enabled application calls, so it is where the " +
                   "trimming roots for the generated formatters have to be stated - see Ceras.SourceGenerator.TrimmingRoots.");

        static DynamicDependencyAttribute[] Dependencies()
            => Registration().GetCustomAttributes<DynamicDependencyAttribute>().ToArray();

        /// <summary>The distinct member-type sets the generated registration roots <paramref name="type"/> for.</summary>
        static List<DynamicallyAccessedMemberTypes> RootsFor(Type type)
            => Dependencies()
                .Where(d => d.Type == type)
                .Select(d => d.MemberTypes)
                .ToList();

        /// <summary>
        /// The entry point carries dependencies, and every one of them names a real type and a
        /// distinct requirement. An emitted <c>typeof(X)</c> that does not resolve is a compile error
        /// in the consumer, so that cannot happen; what can is a dependency that is emitted twice,
        /// which would mean the per-type sets are being recomputed rather than stated once.
        /// </summary>
        [Fact]
        public void EveryDependencyNamesATypeAndNoneIsDuplicated()
        {
            var dependencies = Dependencies();
            Assert.NotEmpty(dependencies);
            Assert.All(dependencies, d => Assert.NotNull(d.Type));

            var keys = dependencies.Select(d => (d.Type, d.MemberTypes)).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        [Fact]
        public void FormattedType_ItsDataMembersAndParameterlessConstructorAreRooted()
        {
            var roots = RootsFor(typeof(TrimmingRootsSubject));
            Assert.Contains(DataMembers, roots);
        }

        [Fact]
        public void ClosedGenericType_TheClosedInstantiationIsRooted_NotTheOpenDefinition()
        {
            // The member walk runs on the closed type, so a root naming only the open definition
            // leaves the closed instantiation with no members - and therefore a TypeConfig with an
            // empty member list, silently.
            var roots = RootsFor(typeof(TrimmingRootsBox<int>));
            Assert.Contains(DataMembers, roots);
        }

        [Fact]
        public void BaseType_ContributingAnInheritedMemberIsRooted_ForItsMembersAlone()
        {
            // Not for constructors: nothing ever builds a TrimmingRootsBase.
            var roots = RootsFor(typeof(TrimmingRootsBase));
            Assert.Contains(InheritedMembers, roots);
            Assert.DoesNotContain(DataMembers, roots);
        }

        [Fact]
        public void GeneratedFormatter_ItsDispatchMethodsAndSchemaFieldAreRooted()
        {
            var formatter = typeof(RobustFormatters).Assembly.GetType("Ceras.Generated.Ceras_Test_Tests_TrimmingRootsSubjectFormatter");
            Assert.NotNull(formatter);

            var roots = RootsFor(formatter!);
            Assert.Contains(FormatterSurface, roots);
        }

        [Fact]
        public void GeneratedFormatter_ForAClosedGenericIsRooted_SoAreTheMethodsItInherits()
        {
            // A closed generic's Serialize/Deserialize are declared once, on the emitted generic
            // base; the dependency is stated on the per-closed-type class that is what actually gets
            // instantiated, and it has to reach the inherited methods.
            var formatter = typeof(RobustFormatters).Assembly.GetType("Ceras.Generated.Ceras_Test_Tests_TrimmingRootsBox_System_Int32_Formatter");
            Assert.NotNull(formatter);

            var roots = RootsFor(formatter!);
            Assert.Contains(FormatterSurface, roots);
        }
    }
}
