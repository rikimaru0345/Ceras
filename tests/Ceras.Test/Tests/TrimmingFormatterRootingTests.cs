using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Ceras.Formatters;
using Xunit;

namespace Ceras.Test.Tests
{
    /// <summary>
    /// The one thing about a trimming defect the ordinary test suite cannot exercise is the trimming
    /// itself: every test here runs on a full runtime, where <c>ReferenceFormatter&lt;T&gt;</c>'s
    /// constructor is present whether or not anything says it should be. What the suite *can* check is
    /// the thing the fix is made of - the attributes that make a trimmed build keep it - so that
    /// deleting or loosening them fails here instead of only failing in
    /// <c>samples/TrimAotSmoke</c>'s <c>trim</c> mode, which is in neither the solution nor
    /// <c>dotnet test</c>.
    /// <para>
    /// The granularity is the part worth pinning, not just the presence. The defect is "the closed
    /// instantiation's constructor is removed", and that instantiation only ever exists as a
    /// <c>MakeGenericType</c> result, so the dependency has to be stated on the open generic
    /// definition: a dependency on a runtime type is not expressible there at all, and one naming a
    /// single closed construction would keep that one type while every other type the library
    /// formatter-resolves at runtime still lost its constructor.
    /// </para>
    /// </summary>
    public class TrimmingFormatterRootingTests
    {
        /// <summary>
        /// Both constructors are <c>internal</c>, so only the non-public half of the requirement
        /// matters for the two Ceras builds itself; the public half is stated as well because
        /// <see cref="Activator"/> is handed <c>BindingFlags.Public | BindingFlags.NonPublic</c>, and a
        /// formatter that later became public would otherwise be a second, identical defect.
        /// </summary>
        const DynamicallyAccessedMemberTypes Constructors =
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors;

        static DynamicDependencyAttribute[] RootedConstructors()
        {
            var method = typeof(CerasSerializer).GetMethod(
                              "GetReferenceFormatter",
                              BindingFlags.Instance | BindingFlags.NonPublic,
                              binder: null,
                              types: new[] { typeof(Type) },
                              modifiers: null)
                          ?? throw new InvalidOperationException(
                              "CerasSerializer.GetReferenceFormatter(Type) no longer exists, or no longer takes a single Type. " +
                              "It is the only place either reference-formatter type is closed over a runtime Type, so whatever " +
                              "replaced it has to state the rooting itself - see Instantiator.Create.");

            return method.GetCustomAttributes<DynamicDependencyAttribute>().ToArray();
        }

        /// <summary>
        /// The attribute keeps the target as a <see cref="Type"/> when it is given one, so the check
        /// asks that rather than comparing type-name strings: "is this still the open definition" is
        /// the property that matters, and only a real <see cref="Type"/> can answer it.
        /// </summary>
        static Type Target(DynamicDependencyAttribute dependency)
            => dependency.Type
               ?? throw new InvalidOperationException(
                   $"A DynamicDependency on GetReferenceFormatter names its target by string rather than by Type " +
                   $"('{dependency.TypeName}, {dependency.AssemblyName}'), which the test cannot check. Stating the " +
                   "formatter type as a typeof(...) is what makes the dependency follow a rename of it.");

        [Theory]
        [InlineData(false)] // ReferenceFormatter<>
        [InlineData(true)]  // ReferenceFormatter_KnownSealedType<>
        public void GetReferenceFormatter_RootsTheConstructorOfBothReferenceFormatterFamilies(bool knownSealed)
        {
            var expected = knownSealed
                ? typeof(ReferenceFormatter_KnownSealedType<>)
                : typeof(ReferenceFormatter<>);

            var dependency = Assert.Single(RootedConstructors(), a => Target(a) == expected);

            Assert.Equal(Constructors, dependency.MemberTypes);
        }

        [Fact]
        public void TheDependenciesAreStatedOnTheOpenGenericDefinitions()
        {
            // A closed construction would keep exactly one type. The library resolves a reference
            // formatter for every type in a payload, and under a trimmer that is an open-ended set,
            // so the only statement that covers it is the one on the definition.
            var dependencies = RootedConstructors();
            Assert.NotEmpty(dependencies);

            foreach (var dependency in dependencies)
            {
                var target = Target(dependency);

                Assert.True(target.IsGenericTypeDefinition,
                    $"{target} is not an open generic definition, so the dependency covers only that one closed " +
                    "instantiation, and every other type the serializer resolves at runtime still loses its constructor.");
            }
        }

        [Fact]
        public void InstantiatorCreateCarriesNoUnmeetableRequirement()
        {
            // The mirror image, and the reason its type parameter is not annotated: a
            // DynamicallyAccessedMembers requirement on Instantiator.Create's parameter could not be met
            // by any of its callers (they all pass a MakeGenericType result over a runtime type), and
            // pushing it out far enough to satisfy them reaches the public Serialize<T>/Deserialize<T>
            // surface, where it would tell callers to preserve the constructors of their own serialized
            // types. Asserted so that "somebody annotated it" cannot happen unnoticed: if it is ever
            // annotated, the reasoning in CerasSerializer.GetReferenceFormatter and in Instantiator.Create
            // has to be rewritten with it, because both document its absence.
            var create = typeof(CerasSerializer).Assembly
                             .GetType("Ceras.Helpers.Instantiator")!
                             .GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

            var requirement = create.GetParameters()[0]
                .GetCustomAttribute<DynamicallyAccessedMembersAttribute>();

            Assert.Null(requirement);
        }
    }
}
