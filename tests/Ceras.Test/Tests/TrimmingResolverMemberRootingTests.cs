using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Ceras.Formatters;
using Ceras.Resolvers;
using Xunit;

// The reflection-fallback resolver is one of the sites under test, and its [Obsolete] steers users away
// from the reflection fallback - which is precisely what this file does with it, at the four places
// where the fallback's rooting is pinned. Suppressed for the file the same way the library's own three
// uses are (CerasSerializer.cs, TypeConfigDefaults.cs): the type is the subject here, not a suggestion.
#pragma warning disable CS0618

namespace Ceras.Test.Tests
{
    /// <summary>
    /// The companion of <see cref="TrimmingFormatterRootingTests"/>: that one pins the two
    /// reference-formatter families rooted in <c>CerasSerializer.GetReferenceFormatter</c>, this one
    /// pins the twenty-odd formatter families each resolver closes over a runtime
    /// <see cref="Type"/>, and the two reflection helpers closed inside
    /// <c>ReferenceFormatter&lt;T&gt;</c>.
    /// <para>
    /// The whole point is that the ordinary suite cannot see the defect. On a full runtime every one
    /// of these constructors and interface implementations is present whether or not anything says it
    /// should be; only a trimmed build loses them, and that is
    /// <c>samples/TrimAotSmoke</c>'s <c>trim</c>/<c>aot</c> modes, which are in neither the solution
    /// nor <c>dotnet test</c>. So the thing to assert here is the attribute set that makes a trimmed
    /// build keep them - and the *granularity* of it, not merely its presence. Two of the failure
    /// modes are silent in the other direction: a dependency on a closed construction keeps exactly
    /// the one type the serializer happened to resolve while the fix was written, and a dependency
    /// that drops the members (for a type whose interface implementations nothing names) leaves a
    /// <see cref="TypeLoadException"/> that rooting a constructor cannot fix.
    /// </para>
    /// </summary>
    public class TrimmingResolverMemberRootingTests
    {
        /// <summary>
        /// What <c>Instantiator.Create</c> binds for a formatter it is handed arguments for.
        /// <c>Activator</c> is given <c>BindingFlags.Public | BindingFlags.NonPublic</c>, and the
        /// argument list of a closed instantiation is not something a dependency can pin down.
        /// </summary>
        const DynamicallyAccessedMemberTypes Constructors =
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors;

        /// <summary>
        /// The narrower set for a formatter constructed with <em>no</em> arguments: with an empty
        /// argument list only a parameterless constructor can be bound. There is no
        /// <c>NonPublicParameterlessConstructor</c> flag, so the non-public half of this is the whole
        /// non-public constructor set - which is no wider than what these families actually declare.
        /// </summary>
        const DynamicallyAccessedMemberTypes ParameterlessConstructors =
            DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.NonPublicConstructors;

        // ── helpers ───────────────────────────────────────────────────────────────

        static DynamicDependencyAttribute[] RootedAt(Type declaringType, string methodName)
        {
            var candidates = declaringType
                                 .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                 .Where(m => m.Name == methodName)
                                 .ToArray();

            // One method by that name, still taking the runtime Type first: that parameter is the reason
            // the rooting has to be stated at this method at all, so its loss is a change to notice. The
            // lookup is by name rather than by a fixed parameter list because the two dispatcher helpers
            // on ReferenceFormatter<T> are handed the formatter as well, as a second argument - a lookup
            // for "the one method taking a single Type" cannot find those two at all.
            var method = candidates.Length == 1 && candidates[0].GetParameters().FirstOrDefault()?.ParameterType == typeof(Type)
                             ? candidates[0]
                             : throw new InvalidOperationException(
                                 $"{declaringType.Name}.{methodName} is not the single method taking a runtime Type that the " +
                                 $"formatter types below are closed over ({candidates.Length} methods by that name). It is where " +
                                 "they are closed, so whatever replaced it has to state the rooting itself - a dependency on the " +
                                 "open generic definition, not on the runtime type.");

            return method.GetCustomAttributes<DynamicDependencyAttribute>().ToArray();
        }

        /// <summary>
        /// The attribute keeps the target as a real <see cref="Type"/> when it is given one, so the
        /// check asks for that rather than comparing type-name strings: "is this still the open
        /// definition" is the property that matters, and only a real <see cref="Type"/> can answer it.
        /// </summary>
        static Type Target(DynamicDependencyAttribute dependency)
            => dependency.Type
               ?? throw new InvalidOperationException(
                   $"A DynamicDependency names its target by string ('{dependency.TypeName}, {dependency.AssemblyName}') " +
                   "rather than by Type, which the test cannot check, and which no longer follows a rename of it.");

        /// <summary>
        /// Asserts that <paramref name="site"/> roots exactly the members asked for, on the open
        /// definition of <paramref name="openDefinition"/>. "Exactly" is the point: an attribute left
        /// over from a family that no longer exists is as much a defect as a missing one, because it
        /// silently widens what the trimmer is told to keep.
        /// </summary>
        static void AssertRoots(Type site, string methodName, Type openDefinition, DynamicallyAccessedMemberTypes members)
        {
            var roots = RootedAt(site, methodName);
            var matches = roots.Where(a => Target(a) == openDefinition).ToArray();

            Assert.True(
                matches.Length == 1,
                $"expected exactly one DynamicDependency on {openDefinition} at {site.Name}.{methodName}, found " +
                $"{matches.Length}. Two would root it twice; none means the family is left to the trimmer.");

            Assert.Equal(members, matches[0].MemberTypes);

            Assert.True(
                openDefinition.IsGenericTypeDefinition,
                $"{openDefinition} is not an open generic definition, so a dependency on it covers only that one closed " +
                "instantiation - and every other type the serializer resolves at runtime still loses the member.");
        }

        static Type Nested(Type declaringType, string definitionName)
            => declaringType.GetNestedType(definitionName, BindingFlags.NonPublic)
               ?? declaringType.GetNestedType(definitionName, BindingFlags.Public)
               ?? throw new InvalidOperationException(
                   $"'{definitionName}' is no longer nested in {declaringType.Name}; if the formatter family moved, the " +
                   "DynamicDependency that has to name it moved with it.");

        static Type InAssembly(string fullName)
            => typeof(CerasSerializer).Assembly.GetType(fullName, throwOnError: true)!;

        // ── CollectionFormatterResolver: arrays and ICollection<> ─────────────────

        [Fact]
        public void CollectionFormatterResolver_RootsEveryArrayAndCollectionFamilyItCloses()
        {
            AssertRoots(typeof(CollectionFormatterResolver), "GetFormatter", typeof(ReinterpretArrayFormatter<>), Constructors);
            AssertRoots(typeof(CollectionFormatterResolver), "GetFormatter", typeof(ArrayFormatter<>), Constructors);
            AssertRoots(typeof(CollectionFormatterResolver), "GetFormatter", typeof(StackFormatter<>), ParameterlessConstructors);
            AssertRoots(typeof(CollectionFormatterResolver), "GetFormatter", typeof(QueueFormatter<>), ParameterlessConstructors);
            AssertRoots(typeof(CollectionFormatterResolver), "GetFormatter", typeof(CollectionFormatter<,>), Constructors);
        }

        /// <summary>
        /// The one that needed more than a constructor, and the reason it is worth a test of its own.
        /// <c>MultiDimensionalArrayFormatter&lt;TItem&gt;</c> implements <c>IFormatter&lt;TItem[,]&gt;</c>,
        /// <c>IFormatter&lt;TItem[,,]&gt;</c> and <c>IFormatter&lt;TItem[,,,]&gt;</c> on top of its
        /// <c>FormatterBase&lt;Array&gt;</c>; those three closed interface constructions appear in no
        /// compilation, so the methods that implement them were being dropped while the interfaces
        /// stayed in the type's interface list. The trimmed smoke failed with
        /// <c>TypeLoadException: Method 'Serialize' in type
        /// 'Ceras.Formatters.MultiDimensionalArrayFormatter`1' does not have an implementation</c>
        /// raised from the type load <c>Instantiator.Create</c> performs - so a constructor-only
        /// dependency cannot fix it and the public methods have to be named.
        /// </summary>
        [Fact]
        public void MultiDimensionalArrayFormatter_IsRootedForItsArrayInterfaceImplementations()
        {
            AssertRoots(typeof(CollectionFormatterResolver), "GetFormatter",
                        typeof(MultiDimensionalArrayFormatter<>),
                        Constructors | DynamicallyAccessedMemberTypes.PublicMethods);

            // The reason the assertion above is about the public methods: the implementation of an
            // implicitly implemented interface method is an ordinary public method, and keeping it
            // under its own name is what keeps the interface mapping intact. Assert the type really
            // has the three extra interfaces, so the requirement cannot be quietly reduced to
            // "some public methods" if one of them is ever dropped from the formatter.
            //
            // Asked of the closed instantiation, because that is the only form in which the three
            // constructions are named at all: on the open definition they are still generic
            // (IFormatter<TItem[,]>, not IFormatter<int[,]>), which is exactly the closed-over-runtime-type
            // situation the dependency above has to be stated on.
            var interfaces = typeof(MultiDimensionalArrayFormatter<int>).GetInterfaces().ToArray();

            Assert.Contains(typeof(IFormatter<int[,]>), interfaces);
            Assert.Contains(typeof(IFormatter<int[,,]>), interfaces);
            Assert.Contains(typeof(IFormatter<int[,,,]>), interfaces);

            foreach (var i in new[] { typeof(IFormatter<int[,]>), typeof(IFormatter<int[,,]>), typeof(IFormatter<int[,,,]>) })
            {
                Assert.Contains(
                    typeof(MultiDimensionalArrayFormatter<int>).GetInterfaceMap(i).TargetMethods,
                    m => m.Name == "Serialize");
            }
        }

        // ── PrimitiveResolver: the two enum formatters ────────────────────────────

        [Fact]
        public void PrimitiveResolver_RootsBothEnumFormatterFamilies()
        {
            // EnumFormatter<> is a private nested class of the resolver, so the test has to ask for it
            // the way the resolver does: by name, through the declaring type.
            AssertRoots(typeof(PrimitiveResolver), "GetFormatter", Nested(typeof(PrimitiveResolver), "EnumFormatter`1"),
                        ParameterlessConstructors);

            // ReinterpretFormatter<> is closed here *and* in ReinterpretFormatterResolver, which
            // another file owns. One dependency on the definition already covers every closed
            // construction, so the two sites cannot disagree - but only if this one stays, which is
            // what this asserts.
            AssertRoots(typeof(PrimitiveResolver), "GetFormatter", typeof(ReinterpretFormatter<>),
                        ParameterlessConstructors);
        }

        // ── StandardFormatterResolver: Nullable, KeyValuePair and the tuples ───────

        [Fact]
        public void StandardFormatterResolver_RootsNullableAndKeyValuePair()
        {
            var nullable = Nested(typeof(StandardFormatterResolver), "NullableFormatter`1");
            var keyValuePair = Nested(typeof(StandardFormatterResolver), "KeyValuePairFormatter`2");

            AssertRoots(typeof(StandardFormatterResolver), "GetFormatter", nullable, Constructors);
            AssertRoots(typeof(StandardFormatterResolver), "GetFormatter", keyValuePair, Constructors);
        }

        /// <summary>
        /// The fourteen tuple formatters the two static tables can select, one per arity. The test
        /// enumerates the very same tables the resolver indexes, so a formatter added to a table
        /// without a dependency fails here instead of only in a trimmed build.
        /// </summary>
        [Theory]
        [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
        [InlineData(5)] [InlineData(6)] [InlineData(7)]
        public void StandardFormatterResolver_RootsEveryTupleFormatterItCanSelect(int arity)
        {
            var resolver = typeof(StandardFormatterResolver);

            foreach (var tableName in new[] { "_tupleFormatterTypes", "_valueTupleFormatterTypes" })
            {
                var table = (Type[])resolver.GetField(tableName, BindingFlags.Static | BindingFlags.NonPublic)!
                                                        .GetValue(null)!;

                // Index 0 is a null placeholder and the resolver indexes by argument count, so the
                // entry for an arity is the one the resolver would actually close.
                var formatter = table[arity];
                Assert.NotNull(formatter);

                AssertRoots(resolver, "GetFormatter", formatter, ParameterlessConstructors);
            }
        }

        [Fact]
        public void TheTwoTupleTablesHoldOnlyOpenDefinitions()
        {
            // A closed construction in either table would make every one of the dependencies above a
            // dependency on a single type, which is the defect the whole scheme exists to avoid.
            var resolver = typeof(StandardFormatterResolver);

            foreach (var tableName in new[] { "_tupleFormatterTypes", "_valueTupleFormatterTypes" })
            {
                var table = (Type[])resolver.GetField(tableName, BindingFlags.Static | BindingFlags.NonPublic)!
                                                        .GetValue(null)!;

                foreach (var formatter in table.Where(t => t != null))
                {
                    Assert.True(formatter.IsGenericTypeDefinition,
                                $"{formatter} in {tableName} is not an open definition; the resolver closes it with the " +
                                "payload's own type arguments.");
                }
            }
        }

        // ── the two reflection resolvers ─────────────────────────────────────────

        [Fact]
        public void ReflectionFormatterResolver_RootsMemberInfoFormatter()
            => AssertRoots(typeof(ReflectionFormatterResolver), "GetFormatter", typeof(MemberInfoFormatter<>), Constructors);

        [Fact]
        public void ReflectionFallbackResolver_RootsReflectionFallbackFormatter()
            => AssertRoots(typeof(ReflectionFallbackResolver), "GetFormatter",
                           InAssembly("Ceras.Formatters.ReflectionFallbackFormatter`1"), Constructors);

        // ── ReferenceFormatter's two dispatcher helpers ───────────────────────────

        [Fact]
        public void ReferenceFormatter_RootsBothDispatcherHelpersForTheirInterfaceImplementations()
        {
            // These two are the shape MultiDimensionalArrayFormatter has: the method that matters is
            // not an override of anything, it is an implementation of a private interface that the
            // only call in the assembly (`helper.Serialize` / `helper.Deserialize`) binds to. The
            // dispatcher methods are private statics, so the test has to name them as such.
            AssertRoots(typeof(ReferenceFormatter<object>), "CreateSpecificSerializerDispatcher",
                        InAssembly("Ceras.Formatters.ReflectionSerializationHelper`2"),
                        Constructors | DynamicallyAccessedMemberTypes.PublicMethods);

            AssertRoots(typeof(ReferenceFormatter<object>), "CreateSpecificDeserializerDispatcher",
                        InAssembly("Ceras.Formatters.ReflectionDeserializationHelper`2"),
                        Constructors | DynamicallyAccessedMemberTypes.PublicMethods);
        }

        // ── the scheme itself ────────────────────────────────────────────────────

        /// <summary>
        /// Every dependency on every closing site in this group, and the one property they all have
        /// to share: the target is the open generic definition. Stated once here so that a future
        /// family added to a resolver cannot come in as a closed construction.
        /// </summary>
        [Fact]
        public void EveryResolverDependencyTargetsAnOpenGenericDefinition()
        {
            var sites = new (Type DeclaringType, string Method)[]
            {
                (typeof(CollectionFormatterResolver), "GetFormatter"),
                (typeof(PrimitiveResolver), "GetFormatter"),
                (typeof(StandardFormatterResolver), "GetFormatter"),
                (typeof(ReflectionFormatterResolver), "GetFormatter"),
                (typeof(ReflectionFallbackResolver), "GetFormatter"),
                (typeof(ReferenceFormatter<object>), "CreateSpecificSerializerDispatcher"),
                (typeof(ReferenceFormatter<object>), "CreateSpecificDeserializerDispatcher"),
            };

            var total = 0;

            foreach (var (declaringType, methodName) in sites)
            {
                foreach (var dependency in RootedAt(declaringType, methodName))
                {
                    var target = Target(dependency);
                    total++;

                    Assert.True(target.IsGenericTypeDefinition,
                                $"{target} (rooted by {declaringType.Name}.{methodName}) is not an open generic " +
                                "definition, so the dependency covers only that one closed instantiation.");
                }
            }

            // 6 (collection) + 2 (enum) + 16 (standard) + 1 (member info) + 1 (fallback) + 2 (helpers)
            Assert.Equal(28, total);
        }

        [Fact]
        public void InstantiatorCreateStillCarriesNoUnmeetableRequirement()
        {
            // Unchanged from TrimmingFormatterRootingTests, and repeated here because these families
            // are the other half of the reason: every one of the dependencies above exists *because*
            // the parameter cannot be annotated, and someone annotating it later would have to
            // rewrite the reasoning in all seven sites plus CerasSerializer.GetReferenceFormatter.
            var create = typeof(CerasSerializer).Assembly
                             .GetType("Ceras.Helpers.Instantiator")!
                             .GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

            Assert.Null(create.GetParameters()[0].GetCustomAttribute<DynamicallyAccessedMembersAttribute>());
        }

        [Fact]
        public void NoDependencyIsStatedAsASuppression()
        {
            // The fix roots members. A blanket suppression of the IL2xxx warnings would leave the
            // members trimmed and merely stop saying so, and UnconditionalSuppressMessage anywhere in
            // these files would mean the same thing quietly.
            foreach (var file in new[]
                     {
                         typeof(CollectionFormatterResolver), typeof(PrimitiveResolver),
                         typeof(StandardFormatterResolver), typeof(ReflectionFormatterResolver),
                         typeof(ReflectionFallbackResolver), typeof(ReferenceFormatter<>),
                     })
            {
                foreach (var attribute in file.GetCustomAttributes(inherit: true))
                {
                    Assert.False(
                        attribute is UnconditionalSuppressMessageAttribute,
                        $"{file.Name} carries an UnconditionalSuppressMessageAttribute; these families are rooted, not silenced.");
                }
            }
        }

        /// <summary>
        /// The families these dependencies cover, named as data so the two resolvers that own more
        /// than one of them cannot silently lose one. The coarse net under the per-family tests above:
        /// it fails when a family is dropped from a site, and it fails when one is renamed or removed -
        /// and unlike the tests above it says which, by name.
        /// </summary>
        [Fact]
        public void TheRootedFamiliesAreTheOnesTheResolversClose()
        {
            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "Ceras.Formatters.ReinterpretArrayFormatter`1",
                "Ceras.Formatters.ArrayFormatter`1",
                "Ceras.Formatters.MultiDimensionalArrayFormatter`1",
                "Ceras.Formatters.StackFormatter`1",
                "Ceras.Formatters.QueueFormatter`1",
                "Ceras.Formatters.CollectionFormatter`2",
                "Ceras.Resolvers.PrimitiveResolver+EnumFormatter`1",
                "Ceras.Formatters.ReinterpretFormatter`1",
                "Ceras.Resolvers.StandardFormatterResolver+NullableFormatter`1",
                "Ceras.Resolvers.StandardFormatterResolver+KeyValuePairFormatter`2",
                "Ceras.Formatters.TupleFormatter`1", "Ceras.Formatters.TupleFormatter`2",
                "Ceras.Formatters.TupleFormatter`3", "Ceras.Formatters.TupleFormatter`4",
                "Ceras.Formatters.TupleFormatter`5", "Ceras.Formatters.TupleFormatter`6",
                "Ceras.Formatters.TupleFormatter`7",
                "Ceras.Formatters.ValueTupleFormatter`1", "Ceras.Formatters.ValueTupleFormatter`2",
                "Ceras.Formatters.ValueTupleFormatter`3", "Ceras.Formatters.ValueTupleFormatter`4",
                "Ceras.Formatters.ValueTupleFormatter`5", "Ceras.Formatters.ValueTupleFormatter`6",
                "Ceras.Formatters.ValueTupleFormatter`7",
                "Ceras.Formatters.MemberInfoFormatter`1",
                "Ceras.Formatters.ReflectionFallbackFormatter`1",
                "Ceras.Formatters.ReflectionSerializationHelper`2",
                "Ceras.Formatters.ReflectionDeserializationHelper`2",
            };

            var sites = new (Type DeclaringType, string Method)[]
            {
                (typeof(CollectionFormatterResolver), "GetFormatter"),
                (typeof(PrimitiveResolver), "GetFormatter"),
                (typeof(StandardFormatterResolver), "GetFormatter"),
                (typeof(ReflectionFormatterResolver), "GetFormatter"),
                (typeof(ReflectionFallbackResolver), "GetFormatter"),
                (typeof(ReferenceFormatter<object>), "CreateSpecificSerializerDispatcher"),
                (typeof(ReferenceFormatter<object>), "CreateSpecificDeserializerDispatcher"),
            };

            // Target() hands back a real Type on purpose (see its own comment: "is this still the open
            // definition" is a question only a Type can answer), and this is the one place that
            // compares by name instead - so it is FullName that closes the gap. That is the spelling
            // the expected set is written in, '+' included for the two private nested formatters,
            // which cannot be written as a typeof(...) here at all.
            var actual = new HashSet<string>(
                sites.SelectMany(s => RootedAt(s.DeclaringType, s.Method))
                     .Select(Target)
                     .Select(t => t.FullName),
                StringComparer.Ordinal);

            Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal),
                        actual.OrderBy(n => n, StringComparer.Ordinal));
        }
    }
}
