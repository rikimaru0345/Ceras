using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Ceras.SourceGenerator.TypeHelpers;

namespace Ceras.SourceGenerator
{
    [Generator]
    public class CerasFormatterGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var provider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is BaseTypeDeclarationSyntax { AttributeLists.Count: > 0 },
                    transform: static (ctx, _) => GetTypeIfAnnotated(ctx))
                .Where(static t => t != null)!;

            var compilationAndTypes = context.CompilationProvider.Combine(provider.Collect());

            context.RegisterSourceOutput(compilationAndTypes,
                static (spc, source) => Execute(spc, source.Left, source.Right.Where(t => t != null).Cast<INamedTypeSymbol>().ToImmutableArray()));
        }

        static INamedTypeSymbol? GetTypeIfAnnotated(GeneratorSyntaxContext ctx)
        {
            var typeDecl = (BaseTypeDeclarationSyntax)ctx.Node;
            foreach (var attrList in typeDecl.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var symbol = ctx.SemanticModel.GetSymbolInfo(attr).Symbol;
                    if (symbol is IMethodSymbol method)
                    {
                        var attrType = method.ContainingType;
                        // Compare against the original definition so that the generic
                        // CerasSerializableAttribute<T> (and any constructed form of it) is matched too.
                        var attrDef = attrType.OriginalDefinition.ToDisplayString();
						if (attrDef == "Ceras.CerasSerializableAttribute" ||
							attrDef == "Ceras.CerasSerializableAttribute<T>" ||
							attrDef == "Ceras.CerasContextAttribute")
                        {
                            return ctx.SemanticModel.GetDeclaredSymbol(typeDecl);
                        }
                    }
                }
            }
            return null;
        }

        static void Execute(
            SourceProductionContext context,
            Compilation compilation,
            ImmutableArray<INamedTypeSymbol> types)
        {
            var cerasSerializableAttr = compilation.GetTypeByMetadataName(CerasSerializableFqn);
            var cerasSerializableGenericAttr = compilation.GetTypeByMetadataName(CerasSerializableFqn + "`1");
            var excludeFromDiscoveryAttr = compilation.GetTypeByMetadataName(ExcludeFromDiscoveryFqn);
            var blacklistAttr = compilation.GetTypeByMetadataName(BlacklistFqn);
            var cerasContextAttr = compilation.GetTypeByMetadataName("Ceras.CerasContextAttribute");

            // Also include assembly-level [CerasSerializable] and [CerasKnownType] targets as roots.
            // CerasKnownType explicitly authorizes closed instantiations (and their transitive
            // closure) that are hidden behind object/interface members (TODO Phase 2.1).
            var knownTypeAttr = compilation.GetTypeByMetadataName("Ceras.CerasKnownTypeAttribute");
            var knownTypeGenericAttr = compilation.GetTypeByMetadataName("Ceras.CerasKnownTypeAttribute`1");

            var includeAttr = compilation.GetTypeByMetadataName(IncludeFqn);
            var excludeAttr = compilation.GetTypeByMetadataName(ExcludeFqn);
            var previousNameAttr = compilation.GetTypeByMetadataName(PreviousNameFqn);
            var previousTypeAttr = compilation.GetTypeByMetadataName(PreviousTypeNameFqn);
            var serializedAsAttr = compilation.GetTypeByMetadataName(SerializedAsFqn);
            var cerasConstructorAttr = compilation.GetTypeByMetadataName(CerasConstructorFqn);
            var onBeforeSerializeAttr = compilation.GetTypeByMetadataName(OnBeforeSerializeFqn);
            var onAfterSerializeAttr = compilation.GetTypeByMetadataName(OnAfterSerializeFqn);
            var onBeforeDeserializeAttr = compilation.GetTypeByMetadataName(OnBeforeDeserializeFqn);
            var onAfterDeserializeAttr = compilation.GetTypeByMetadataName(OnAfterDeserializeFqn);
            var cerasSerializerType = compilation.GetTypeByMetadataName(CerasSerializerFqn);
            var migrationContextType = compilation.GetTypeByMetadataName(MigrationContextFqn);

			var attrBag = new AttributeBag(includeAttr, excludeAttr,
				previousNameAttr, previousTypeAttr, serializedAsAttr,
                cerasSerializableAttr, cerasConstructorAttr,
                onBeforeDeserializeAttr, onAfterDeserializeAttr,
                cerasSerializerType, migrationContextType,
                onBeforeSerializeAttr, onAfterSerializeAttr);

            var blacklistPatterns = new List<string>();
            foreach (var attr in compilation.Assembly.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, blacklistAttr) &&
                    attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is string pattern)
                {
                    blacklistPatterns.Add(pattern);
                }
            }

            // ------------------------------------------------------------------------------------
            // Serialization contexts.
            //
            // A context is a named, finite set of serializable types that share one generated
            // manifest, one deterministic type-ID space, and one layout fingerprint. Contexts are
            // declared by [CerasContext("name")] on root types. Each context is discovered
            // independently from its own roots; the union of all contexts' discovered types is what
            // gets a generated formatter (a formatter is context-independent). Registration is
            // emitted per context, so selecting a context only ever registers that context's
            // manifest (TODO Phase 2 "Contexts").
            // ------------------------------------------------------------------------------------
            var contextRoots = new SortedDictionary<string, HashSet<INamedTypeSymbol>>(StringComparer.Ordinal);

            void AddRoot(string contextName, INamedTypeSymbol type)
            {
                if (!contextRoots.TryGetValue(contextName, out var roots))
                    contextRoots[contextName] = roots = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                roots.Add(type);
            }

            // 1. Declared contexts and their roots: every [CerasContext] on an annotated type.
            foreach (var type in types)
            {
                foreach (var attr in type.GetAttributes())
                {
                    if (cerasContextAttr == null || !SymbolEqualityComparer.Default.Equals(attr.AttributeClass, cerasContextAttr))
                        continue;

                    string? name = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string : null;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diag.InvalidContextName,
                            type.Locations.FirstOrDefault(),
                            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                        continue;
                    }

                    // A context root that is itself a refused collection shape. Such a type gets no
                    // generated formatter (the collection formatter would serve it) and no schema the bake
                    // can accept, so the context could only ever fail on its first Save. Reported at the
                    // type, next to the [CerasContext] that put it there.
                    var refusedRoot = FindUnsupportedCollectionType(type);
                    if (refusedRoot != null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diag.UnsupportedCollectionShapeDeclaration,
                            type.Locations.FirstOrDefault(),
                            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            refusedRoot.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                        continue;
                    }

                    AddRoot(name!, type);
                }
            }

            // 2. Assembly-level [CerasSerializable<T>] / [CerasKnownType...] targets. These are roots
            //    of every declared context by default. [CerasKnownType...] additionally accepts an
            //    explicit context name; if it names a context that no type declares, that is an error.
            bool hasIntentToSerialize = false;
            foreach (var attr in compilation.Assembly.GetAttributes())
            {
                bool isSerializable = SymbolEqualityComparer.Default.Equals(attr.AttributeClass, cerasSerializableAttr) ||
                                      SymbolEqualityComparer.Default.Equals(attr.AttributeClass?.OriginalDefinition, cerasSerializableGenericAttr);
                bool isKnownType = SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownTypeAttr) ||
                                   SymbolEqualityComparer.Default.Equals(attr.AttributeClass?.OriginalDefinition, knownTypeGenericAttr);
                if (!isSerializable && !isKnownType) continue;

                // The assembly explicitly declared serialization intent, even if the target is bogus.
                hasIntentToSerialize = true;

                INamedTypeSymbol? target = null;
                string? contextName = null;

                if (attr.AttributeClass != null && attr.AttributeClass.IsGenericType)
                {
                    target = attr.AttributeClass.TypeArguments[0] as INamedTypeSymbol;

                    // The generic CerasKnownTypeAttribute<T> carries an optional context;
                    // the generic CerasSerializableAttribute<T> carries a stable name instead.
                    if (isKnownType && attr.ConstructorArguments.Length > 0)
                        contextName = attr.ConstructorArguments[0].Value as string;
                }
                else if (attr.ConstructorArguments.Length > 0)
                {
                    target = attr.ConstructorArguments[0].Value as INamedTypeSymbol;

                    // The non-generic CerasKnownTypeAttribute(Type, string) carries an optional context.
                    if (isKnownType && attr.ConstructorArguments.Length > 1)
                        contextName = attr.ConstructorArguments[1].Value as string;
                }

                if (target == null) continue;

                // A declaration cannot make a collection shape the owner has ruled out serializable: the
                // bake refuses it (Ceras.Format.SchemaRegistry.TryBuildStructuralSchema), so the generated
                // registration would only produce a context that fails on its first Save. Reported where
                // the declaration is written, and the offending type is what is named — a declaration of
                // List<ReadOnlyCollection<int>> is refused for the argument, not for the List.
                var refusedCollection = FindUnsupportedCollectionType(target);
                if (refusedCollection != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diag.UnsupportedCollectionShapeDeclaration,
                        attr.ApplicationSyntaxReference?.GetSyntax()?.GetLocation() ?? Location.None,
                        target.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        refusedCollection.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                    continue;
                }

                // A declaration cannot make a framework type the format cannot describe serializable: it
                // would be registered as a well-known type and then rejected by the bake with advice that
                // cannot work. Report it where the declaration is written instead.
                var unsupportedTarget = FindUnsupportedFrameworkType(target, attrBag);
                if (unsupportedTarget != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diag.UnsupportedFrameworkTypeDeclaration,
                        attr.ApplicationSyntaxReference?.GetSyntax()?.GetLocation() ?? Location.None,
                        target.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        unsupportedTarget.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(contextName))
                {
                    // No context given: apply to every declared context.
                    foreach (var declared in contextRoots.Keys.ToList())
                        AddRoot(declared, target);
                }
                else if (contextRoots.ContainsKey(contextName!))
                {
                    AddRoot(contextName!, target);
                }
                else
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diag.UnknownContext,
                        target.Locations.FirstOrDefault(),
                        target.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        contextName!,
                        contextRoots.Count == 0 ? "(none)" : string.Join(", ", contextRoots.Keys)));
                }
            }

            // 2b. A compilation that visibly intends to use the generated manifest (it has a
            //     type-level [CerasSerializable]/[CerasContext] annotation or an assembly-level
            //     [CerasSerializable<T>]/[CerasKnownType]) but declares no [CerasContext] root is a
            //     configuration error: Ceras deliberately has no default context, so there is no
            //     manifest to register and every UseGeneratedFormatters(...) call would throw at
            //     runtime. Report it at the compilation level and still emit compilable (empty) code.
            //     A compilation that merely references the analyzer without any such annotation is
            //     left alone.
            if (contextRoots.Count == 0 && (types.Length > 0 || hasIntentToSerialize))
            {
                context.ReportDiagnostic(Diagnostic.Create(Diag.NoSerializationContext, Location.None));
            }

            // 3. Discover each context independently; keep the first discovery path per type for the
            //    (context-independent) discovery diagnostics.
            //
            //    Member discovery reports member-level diagnostics (CERAS001/008/009/016/017, ...) as a
            //    side effect of building the member list. Discovery, the fail-early pass, and the
            //    emission loop would each invoke it, so a shared cache is used here: every type's
            //    members are discovered exactly once, which both reports each member diagnostic exactly
            //    once and avoids redundant work (see the `## Review later` entry on duplicate
            //    diagnostics).
            var results = new Dictionary<INamedTypeSymbol, TypeDiscoveryResult>(SymbolEqualityComparer.Default);
            var contextTypes = new SortedDictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
            var memberCache = new Dictionary<INamedTypeSymbol, List<SerializedMember>>(SymbolEqualityComparer.Default);
            var discoveryDiagnostics = new List<Diagnostic>();

            List<SerializedMember> DiscoverCached(INamedTypeSymbol t)
            {
                if (!memberCache.TryGetValue(t, out var cached))
                {
                    // Route member diagnostics through a buffer so they can be reported once, after the
                    // whole discovery phase, rather than immediately per discovery pass.
                    cached = MemberExplorer.DiscoverMembers(context, t, attrBag, blacklistPatterns, discoveryDiagnostics);
                    memberCache[t] = cached;
                }
                return cached;
            }

            foreach (var kv in contextRoots)
            {
                var discovered = DiscoveryEngine.DiscoverAllTypes(
                    context, compilation, kv.Value.ToImmutableArray(), attrBag, excludeFromDiscoveryAttr, blacklistPatterns,
                    memberCache, discoveryDiagnostics);

                var contextMembers = new List<INamedTypeSymbol>(discovered.Count);
                foreach (var discoveredType in discovered)
                {
                    contextMembers.Add(discoveredType.Key);
                    if (!results.ContainsKey(discoveredType.Key))
                        results[discoveredType.Key] = discoveredType.Value;
                }

                contextTypes[kv.Key] = contextMembers;
            }

            foreach (var diagnostic in discoveryDiagnostics)
                context.ReportDiagnostic(diagnostic);

            var finalTypes = results.Keys.ToImmutableArray();
            var finalTypesSet = new HashSet<INamedTypeSymbol>(finalTypes, SymbolEqualityComparer.Default);

            // Canonical identity guard: two distinct types must never share a stable type name,
            // otherwise schema/migration resolution is ambiguous (TODO Phase 2.3).
            var stableNameOwners = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
            foreach (var type in finalTypes)
            {
                if (IsOpenType(type)) continue;
                if (IsWellKnownType(type, attrBag) || MemberExplorer.IsCollectionType(type)) continue;
                string stable = RobustCodeEmitter.GetStableTypeName(type, cerasSerializableAttr);
                if (stableNameOwners.TryGetValue(stable, out var prev))
                {
                    if (!SymbolEqualityComparer.Default.Equals(prev, type))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diag.DuplicateStableTypeName,
                            type.Locations.FirstOrDefault(),
                            prev.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            stable));
                    }
                }
                else
                {
                    stableNameOwners[stable] = type;
                }
            }

            // Report discovery paths for auto-included types
            foreach (var r in results.Values)
            {
                if (r.DiscoveryPath != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diag.DiscoveredTypePropagation,
                        r.Type.Locations.FirstOrDefault(),
                        $"Generating a formatter for '{r.Type.Name}' because: {r.DiscoveryPath}"));
                }
            }

            // ------------------------------------------------------------------------------------
            // Fail-early pass (CASE A symbol names / CASE B missing formatters).
            //
            // Every generated artifact name is derived from SanitizeIdentifier(<qualified display>):
            //   formatter class name   MakeFormatterClassName(type)                 = sanitize + "Formatter"
            //   generic base name      GenericFormatterEmitter.GenericBaseName(def) = sanitize + "Formatter"
            //   AddSource hint         sanitize + ".Ceras.g.cs"
            //   registration local     "tc_" + sanitize
            // SanitizeIdentifier is not injective (punctuation collapses to '_'), so two distinct types
            // can produce the same class name / hint / local. A single dictionary keyed on the sanitized
            // display covers all four explicitly (class name, base name, hint, and tc_ local), so no
            // separate check is needed for each. The fixed "RobustFormatters.g.cs" hint is provably
            // unique because every generated hint ends in ".Ceras.g.cs". This pass mirrors the emission
            // conditions exactly once and records:
            //   * generatedFormatterTypes - types that will actually get a generated formatter class;
            //   * noFormatterReason       - why a skipped discovered type has no formatter (CASE B);
            // and reports any generated-name collision instead of letting it explode later.
            // ------------------------------------------------------------------------------------
            var generatedFormatterTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var nonConstructableTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var noFormatterReason = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
            var generatedNameOwners = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
            var collidedDefinitions = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var claimedDefinitions = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            bool ClaimGeneratedName(string name, INamedTypeSymbol owner)
            {
                if (generatedNameOwners.TryGetValue(name, out var prev))
                {
                    if (!SymbolEqualityComparer.Default.Equals(prev, owner))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diag.GeneratedNameCollision,
                            owner.Locations.FirstOrDefault() ?? prev.Locations.FirstOrDefault(),
                            owner.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            prev.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            name));
                        return false;
                    }
                    return true;
                }

                generatedNameOwners[name] = owner;
                return true;
            }

            foreach (var type in finalTypes)
            {
                if (type.TypeKind == TypeKind.Interface || type.IsAbstract) { noFormatterReason[type] = "it is an interface or abstract type"; continue; }
                if (IsOpenType(type)) { noFormatterReason[type] = "it is an open generic definition"; continue; }
                // Asked before IsWellKnownType/IsCollectionType on purpose: those two would answer "it uses a
                // built-in formatter" / "it is handled by the built-in collection formatter", which is
                // exactly the promise the policy withdraws. The reason recorded here is what a member's
                // generated field falls back to, so it has to say what is actually true.
                if (FindUnsupportedCollectionType(type) != null) { noFormatterReason[type] = "it is a collection shape Ceras does not support (CERAS033/CERAS034)"; continue; }
                if (IsWellKnownType(type, attrBag)) { noFormatterReason[type] = "it uses a built-in formatter"; continue; }
                // IsCollectionFormatterType is a strict subset of this check (it calls IsCollectionType
                // as its first condition), so one branch covers both cases.
                if (MemberExplorer.IsCollectionType(type)) { noFormatterReason[type] = "it is handled by the built-in collection formatter"; continue; }

                var members = DiscoverCached(type);
                if (!type.IsValueType && !MemberExplorer.CanCerasConstruct(type, cerasConstructorAttr, members))
                {
                    nonConstructableTypes.Add(type);
                    noFormatterReason[type] = "Ceras cannot construct it: add a parameterless constructor, a constructor whose parameters all map to serializable members, or mark one constructor [CerasConstructor]";
                    continue;
                }

                // The generic base name is emitted once per definition and shares the same
                // "sanitize + Formatter" scheme as the per-type class name, so check it as well.
                bool definitionCollided = false;
                if (type.IsGenericType)
                {
                    var definition = type.OriginalDefinition;
                    if (collidedDefinitions.Contains(definition))
                    {
                        definitionCollided = true;
                    }
                    else if (claimedDefinitions.Add(definition) &&
                             !ClaimGeneratedName(GenericFormatterEmitter.GenericBaseName(definition), definition))
                    {
                        collidedDefinitions.Add(definition);
                        definitionCollided = true;
                    }
                }

                if (definitionCollided || !ClaimGeneratedName(SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal)), type))
                {
                    noFormatterReason[type] = "another type produces the same generated formatter name";
                    continue;
                }

                generatedFormatterTypes.Add(type);
            }

            // ------------------------------------------------------------------------------------
            // Diagnostic: unsupported constructor-dependent immutable cycles (TODO Phase 3.2).
            //
            // Runs over the types that actually get a generated formatter (they are the only ones
            // whose construction Ceras controls here). The check is a pure compile-time member-graph
            // walk; it does not add any runtime/network cycle validation.
            // ------------------------------------------------------------------------------------
            foreach (var type in generatedFormatterTypes)
            {
                if (type.IsValueType) continue;

                var cycleMembers = DiscoverCached(type);
                var problems = MemberExplorer.FindUnsupportedImmutableCycles(
                    type, cycleMembers, cerasConstructorAttr, memberCache, finalTypesSet);

                foreach (var problem in problems)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diag.UnsupportedImmutableCycleConstruction,
                        problem.Member.MemberSymbol?.Locations.FirstOrDefault() ?? type.Locations.FirstOrDefault(),
                        problem.Member.Name,
                        type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        problem.CycleTypeDisplay,
                        problem.ConstructorDisplay));
                }
            }

            // ------------------------------------------------------------------------------------
            // Minimal registrations for referenced-but-not-generated types.
            //
            // The generated manifest must be complete enough to bake the schema without runtime
            // auto-registration (TODO Phase 3.4). A member can reference a concrete type that is
            // intentionally not part of the closure: blacklisted/engine types (e.g. a GodotObject
            // subclass with a user-provided custom formatter) or external types served by the
            // reflection fallback. Such a type is not discovered, so it gets no generated schema;
            // without a minimal declaration the bake would fail to resolve the field type id.
            // The field's formatter is still the runtime/custom one; only the schema placeholder is
            // emitted here, exactly like the interface/abstract placeholders below.
            // ------------------------------------------------------------------------------------
            var minimalTypeCache = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);

            List<INamedTypeSymbol> CollectMinimalTypes(INamedTypeSymbol type)
            {
                if (minimalTypeCache.TryGetValue(type, out var cached)) return cached;

                var found = new List<INamedTypeSymbol>();
                var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

                foreach (var m in DiscoverCached(type))
                    Walk(m.Type);

                minimalTypeCache[type] = found;
                return found;

                void Walk(ITypeSymbol symbol)
                {
                    switch (symbol)
                    {
                        case IArrayTypeSymbol arr:
                            Walk(arr.ElementType);
                            return;
                        case IPointerTypeSymbol ptr:
                            Walk(ptr.PointedAtType);
                            return;
                        case ITypeParameterSymbol:
                            return;
                        case INamedTypeSymbol named:
                            // Walk generic arguments first (e.g. List<BlacklistedType>).
                            if (named.IsGenericType)
                                foreach (var arg in named.TypeArguments) Walk(arg);

                            if (IsWellKnownType(named, attrBag)) return;   // System.* primitives/collections
                            if (IsOpenType(named)) return;           // closed types only
                            if (MemberExplorer.IsCollectionType(named)) return;
                            if (named.TypeKind == TypeKind.Enum) return; // runtime derives enum schemas
                            if (named.IsAbstract || named.TypeKind == TypeKind.Interface) return; // registered by the discovered loop
                            if (finalTypesSet.Contains(named)) return; // already has a generated/managed schema
                            if (seen.Add(named)) found.Add(named);
                            return;
                    }
                }
            }

            // Structural shapes declared as roots of a context (an assembly-level [CerasKnownType<...>]
            // or [CerasSerializable<...>] that targets one). They get a schema, not a formatter.
            var declaredStructuralRoots = new SortedDictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
            foreach (var kv in contextRoots)
            {
                var roots = new List<INamedTypeSymbol>();
                foreach (var root in kv.Value)
                    if (IsStructuralRootShape(root) && !roots.Contains(root, SymbolEqualityComparer.Default))
                        roots.Add(root);
                declaredStructuralRoots[kv.Key] = roots;
            }

            var contextMinimalTypes = new SortedDictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
            foreach (var kv in contextTypes)
            {
                var set = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                var list = new List<INamedTypeSymbol>();
                foreach (var type in kv.Value)
                    foreach (var minimal in CollectMinimalTypes(type))
                        if (set.Add(minimal)) list.Add(minimal);

                list.Sort((a, b) => string.Compare(GetRobustName(a), GetRobustName(b), StringComparison.Ordinal));
                contextMinimalTypes[kv.Key] = list;
            }

            // 1. Generate the 'UseRobustFormatters' file: one registration entry point per context.
            //    Deliberately does NOT emit `using Ceras.Generated;`: this file is compiled as part of
            //    the consumer's own compilation, and a using-directive for an assembly can never
            //    override the enclosing compilation's own types. In the consuming project the code
            //    lives in namespace `Ceras.Generated` itself, so `RobustFormatters` resolves directly
            //    and no `using Ceras.Generated;` is needed; emitting one only caused it to shadow the
            //    `CerasConfigExtensions` that a generator-enabled *entry* assembly emits for itself
            //    (the extension method `UseGeneratedFormatters` then could not be found, CS1061).
            //    Callers in other namespaces add `using Ceras.Generated;` themselves (as every
            //    generator-enabled consumer already does).
            var sbUse = new StringBuilder();
            sbUse.AppendLine("// <auto-generated/>");
            sbUse.AppendLine("#nullable disable");
            sbUse.AppendLine();
            sbUse.AppendLine("using Ceras;");
            sbUse.AppendLine("using Ceras.Formatters;");
            sbUse.AppendLine("using Ceras.Helpers;");
            sbUse.AppendLine("using Ceras.Format;");
            sbUse.AppendLine("using System;");
            sbUse.AppendLine("using System.Collections.Generic;");
            sbUse.AppendLine();
            sbUse.AppendLine("namespace Ceras.Generated");
            sbUse.AppendLine("{");
            sbUse.AppendLine("    public static class RobustFormatters");
            sbUse.AppendLine("    {");
            sbUse.AppendLine("        /// <summary>");
            sbUse.AppendLine("        /// The serialization contexts declared in this compilation, in ordinal order.");
            sbUse.AppendLine("        /// Pass one of these names to <see cref=\"UseRobustFormatters\"/> (or to the");
            sbUse.AppendLine("        /// <c>UseGeneratedFormatters</c> extension) to register that context's manifest.");
            sbUse.AppendLine("        /// </summary>");
            if (contextTypes.Count == 0)
            {
                sbUse.AppendLine("        public static readonly string[] ContextNames = global::System.Array.Empty<string>();");
            }
            else
            {
                sbUse.Append("        public static readonly string[] ContextNames = new string[] { ");
                sbUse.Append(string.Join(", ", contextTypes.Keys.Select(QuoteLiteral)));
                sbUse.AppendLine(" };");
            }
            sbUse.AppendLine();
            sbUse.AppendLine("        /// <summary>");
            sbUse.AppendLine("        /// Registers the generated formatters and schemas of the named serialization context into");
            sbUse.AppendLine("        /// <paramref name=\"config\"/>. Exactly one context is registered per config; an unknown");
            sbUse.AppendLine("        /// context name throws with the list of available contexts.");
            sbUse.AppendLine("        /// </summary>");
            sbUse.AppendLine("        public static void UseRobustFormatters(global::Ceras.CerasConfig config, string context)");
            sbUse.AppendLine("        {");
            sbUse.AppendLine("            switch (context)");
            sbUse.AppendLine("            {");
            foreach (var contextName in contextTypes.Keys)
            {
                sbUse.AppendLine($"                case {QuoteLiteral(contextName)}:");
                sbUse.AppendLine("                {");
                sbUse.Append(BuildContextRegistrationBody(contextTypes[contextName], contextMinimalTypes[contextName], generatedFormatterTypes, declaredStructuralRoots[contextName], attrBag));
                sbUse.AppendLine("                    break;");
                sbUse.AppendLine("                }");
            }
            sbUse.AppendLine("                default:");
            sbUse.AppendLine("                    throw new global::System.ArgumentException(");
            sbUse.AppendLine("                        \"Unknown Ceras serialization context '\" + context + \"'. Available contexts: \" +");
            sbUse.AppendLine("                        (ContextNames.Length == 0 ? \"(none declared)\" : global::System.String.Join(\", \", ContextNames)) +");
            sbUse.AppendLine("                        \". Declare a context with [CerasContext(name)] on a root type, then call UseGeneratedFormatters(name).\",");
            sbUse.AppendLine("                        nameof(context));");
            sbUse.AppendLine("            }");
            sbUse.AppendLine("        }");
            sbUse.AppendLine("    }");
            sbUse.AppendLine();
            sbUse.AppendLine("    /// <summary>");
            sbUse.AppendLine("    /// Extension methods on <see cref=\"global::Ceras.CerasConfig\"/> for registering generated formatters.");
            sbUse.AppendLine("    /// </summary>");
            sbUse.AppendLine("    public static class CerasConfigExtensions");
            sbUse.AppendLine("    {");
            sbUse.AppendLine("        /// <summary>");
            sbUse.AppendLine("        /// Registers the generated formatters and schemas of the named serialization context into the config.");
            sbUse.AppendLine("        /// Use <c>RobustFormatters.ContextNames</c> to discover the available names.");
            sbUse.AppendLine("        /// </summary>");
            sbUse.AppendLine("        public static void UseGeneratedFormatters(this global::Ceras.CerasConfig config, string context)");
            sbUse.AppendLine("            => RobustFormatters.UseRobustFormatters(config, context);");
            sbUse.AppendLine("    }");
            sbUse.AppendLine("}");

            context.AddSource("RobustFormatters.g.cs", SourceText.From(sbUse.ToString(), Encoding.UTF8));


            // 2. Generate per-type formatter files
            var emittedGenericDefs = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var type in finalTypes)
            {
                // generatedFormatterTypes / nonConstructableTypes are the single source of truth for
                // the emission conditions (computed in the fail-early pass above), so this loop cannot
                // drift from the registration loop.
                bool isGenerated = generatedFormatterTypes.Contains(type);
                if (!isGenerated && !nonConstructableTypes.Contains(type)) continue;

                MemberExplorer.ValidateIncludeExcludeConflicts(context, type, attrBag);
                var members = DiscoverCached(type);

                if (!isGenerated)
                {
                    // Validation will emit specific errors if not constructable
                    MemberExplorer.ValidateCerasConstructor(context, compilation, type, members, attrBag);
                    continue;
                }

                if (members.Count == 0 && TypeHelpers.HasAttribute(type, cerasSerializableAttr))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diag.NoSerializableMembers, type.Locations.FirstOrDefault(), type.Name));
                }

                // Run further constructor validation even if canConstruct is true to catch attribute conflicts/mismatches
                MemberExplorer.ValidateCerasConstructor(context, compilation, type, members, attrBag);

                // CERAS015 / CERAS016: Check that every polymorphic field/property type is properly
                // registered for serialization.  Polymorphic fields (interface or abstract class) need
                // [CerasSerializable] on the declared type so Ceras can discover and register all concrete
                // subtypes.  Without it, saving works accidentally (name written to stream) but loading
                // after any schema change causes a cryptic null-type-name crash deep inside Ceras.
                foreach (var m in members)
                {
                    // [Monomorphic] members: Ceras writes no type tag → no subtype registration needed.
                    if (m.IsMonomorphic) continue;

                    var coreType = UnwrapCollectionsAndNullable(compilation, m.Type);
                    if (coreType == null) continue;
                    if (coreType.TypeKind != TypeKind.Interface && !coreType.IsAbstract) continue;
                    if (IsWellKnownOrBanned(coreType, attrBag)) continue;

                    var memberLocation = type.Locations.FirstOrDefault();
                    var memberDisplay  = m.Name;
                    var typeDisplay    = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    var coreDisplay    = coreType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

                    if (!results.ContainsKey(coreType))
                    {
                        // Hard error: type is completely outside the serialization graph.
                        // Concrete subtypes will have NO formatters at all.
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diag.PolymorphicFieldTypeNotSerializable,
                            memberLocation, memberDisplay, typeDisplay, coreDisplay));
                    }
                    else if (!HasAttributeInHierarchy(coreType, cerasSerializableAttr))
                    {
                        // Soft warning: type is in the graph only via member propagation, not explicitly tagged.
                        // Subtypes ARE discovered, but the setup is implicit and breaks if the type is removed
                        // from the member list or if discovery logic changes.
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diag.PolymorphicFieldTypeImplicit,
                            memberLocation, memberDisplay, typeDisplay, coreDisplay));
                    }
                }

                MemberExplorer.ValidateDuplicateWireNames(context, type, members, attrBag);

                var sbFmt = new StringBuilder();
                AppendGeneratedHeader(sbFmt);

                // Phase 2.4: for a closed generic, reuse one open generic formatter template per
                // definition and emit only a thin per-closed-type subclass carrying the closed schema.
                bool isClosedGeneric = type.IsGenericType && !IsOpenType(type);
                if (isClosedGeneric)
                {
                    var definition = type.OriginalDefinition;

                    // The definition's members, in the order the one shared writer emits them. Every
                    // closure's schema needs that order, not only the first one emitted here, so it is
                    // discovered outside the emission branch.
                    var openMembers = DiscoverCached(definition);

                    if (emittedGenericDefs.Add(definition))
                    {
                        var sbBase = new StringBuilder();
                        var sbHelpers = new StringBuilder();
                        AppendGeneratedHeader(sbBase);
                        GenericFormatterEmitter.WriteGenericBase(sbBase, sbHelpers, compilation, context, definition, openMembers, attrBag);
                        sbBase.Append(sbHelpers);
                        sbBase.AppendLine("}");
                        sbBase.AppendLine("#nullable restore");
                        sbBase.AppendLine("#pragma warning restore 649");
                        context.AddSource($"{GenericFormatterEmitter.GenericBaseName(definition)}.generic.Ceras.g.cs", SourceText.From(sbBase.ToString(), Encoding.UTF8));
                    }

                    // The schema lists its fields in the order the shared writer emits them, not in the
                    // order this closure's members sort to once the type arguments are substituted: the
                    // two differ for most arguments, and the migration reader walks a file's fields in
                    // the order the file's schema lists them.
                    var membersAsWritten = GenericFormatterEmitter.OrderMembersAsWritten(members, openMembers, type, context);

                    GenericFormatterEmitter.WriteGenericDerived(sbFmt, type, definition, membersAsWritten, attrBag);
                }
                else
                {
                    RobustCodeEmitter.WriteFormatterClass(sbFmt, compilation, context, type, members, attrBag,
                        generatedFormatterTypes, finalTypesSet, noFormatterReason);
                }

                sbFmt.AppendLine("}");
                sbFmt.AppendLine("#nullable restore");
                sbFmt.AppendLine("#pragma warning restore 649");

                string typeName = SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal));
                context.AddSource($"{typeName}.Ceras.g.cs", SourceText.From(sbFmt.ToString(), Encoding.UTF8));
            }
        }

        /// <summary>
        /// Emits the registration calls for one context into a <c>switch</c> case block. The types are
        /// ordered by canonical stable name so the assigned type IDs are deterministic for a given
        /// compilation. Only this context's discovered types are registered, so the registry contents
        /// (and therefore the layout fingerprint) are per context.
        /// </summary>
        static string BuildContextRegistrationBody(
            List<INamedTypeSymbol> contextTypes,
            List<INamedTypeSymbol> minimalTypes,
            HashSet<INamedTypeSymbol> generatedFormatterTypes,
            List<INamedTypeSymbol> declaredStructuralRoots,
            AttributeBag attrBag)
        {
            var ordered = contextTypes
                .OrderBy(GetRobustName, StringComparer.Ordinal)
                .ToList();

            var body = new StringBuilder();
            bool usesRobustConfig = false;

            // Root shapes the context declares but no generated formatter can describe: a built-in
            // closed collection, KeyValuePair, a tuple, or an enum. A context has one finite manifest,
            // so these are registered here, up front and in canonical-name order, instead of being
            // discovered during the bake (which would make the assigned ids depend on member order).
            // Declaring such a shape as a root is what makes it serializable at all: a Save/Load whose
            // root is not part of the manifest is rejected.
            foreach (var type in declaredStructuralRoots
                .Where(t => !generatedFormatterTypes.Contains(t))
                .GroupBy(t => t, SymbolEqualityComparer.Default)
                .Select(g => g.First())
                .OrderBy(t => GetRobustName(t), StringComparer.Ordinal))
            {
                string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                body.AppendLine($"                    if (robustConfig != null) robustConfig.SchemaRegistry.DeclareRoot(typeof({typeFqn}));");
                usesRobustConfig = true;
            }

            foreach (var type in ordered)
            {
                // generatedFormatterTypes already encodes the interface/abstract/open/well-known/
                // collection/constructability and generated-name-collision filters.
                if (!generatedFormatterTypes.Contains(type)) continue;

                string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                string formatterName = MakeFormatterClassName(type);

                string varName = "tc_" + SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal));
                body.AppendLine($"                    var {varName} = config.ConfigType<{typeFqn}>();");
                body.AppendLine($"                    if ({varName}.CustomFormatter == null) {varName}.CustomFormatter = new {formatterName}();");

                body.AppendLine($"                    if (robustConfig != null) robustConfig.SchemaRegistry.Register(typeof({typeFqn}), {formatterName}.Schema);");
                usesRobustConfig = true;
            }

            // Register referenced-but-not-generated types and discovered interface/abstract types with
            // minimal schemas. These types have no generated formatters but may appear as fields or as
            // generic type arguments in serialized collections (e.g. List<ILogicalEntity>) or as members
            // served by a user-provided custom formatter (e.g. a blacklisted engine object). Without
            // pre-registration the bake cannot resolve their type ids (TODO Phase 3.4).
            foreach (var type in ordered)
            {
                if (type.TypeKind != TypeKind.Interface && !type.IsAbstract) continue;
                if (type.IsSealed) continue; // static classes (abstract+sealed) have no instances
                if (IsWellKnownType(type, attrBag)) continue;
                if (type.TypeParameters.Length > 0) continue; // open generics: closed instantiations are handled at runtime

                AppendMinimalRegistration(body, type, isStruct: false);
                usesRobustConfig = true;
            }

            // Referenced-but-not-generated concrete types (blacklisted/engine/external), collected from
            // the members of this context's types. They are intentionally absent from the closure and
            // receive no generated formatter, but the schema still needs a placeholder declaration so a
            // field can reference their type id without relying on runtime auto-registration.
            foreach (var type in minimalTypes)
            {
                AppendMinimalRegistration(body, type, isStruct: type.IsValueType);
                usesRobustConfig = true;
            }

            if (usesRobustConfig)
                body.Insert(0, "                    var robustConfig = config as global::Ceras.CerasRobustConfig;\n");

            return body.ToString();
        }

        /// <summary>
        /// True for the root shapes that no generated formatter can describe and that
        /// <c>SchemaRegistry.TryBuildStructuralSchema</c> derives at runtime: a built-in closed
        /// collection (or a collection subclass), <c>KeyValuePair</c>, a tuple, and an enum. Arrays
        /// cannot appear here because an assembly-level declaration can only name a type.
        /// <para>
        /// The generic shapes are matched by the definition identity the runtime reserves a well-known
        /// id for, so a user type that merely lives in a <c>System.*</c> namespace (and is therefore
        /// discovered as an ordinary type needing a generated formatter) is not mistaken for one, and
        /// neither is an eight-argument <c>ValueTuple</c>, which has no reserved id.
        /// </para>
        /// </summary>
        static bool IsStructuralRootShape(INamedTypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Enum) return true;
            if (type.SpecialType != SpecialType.None) return false;   // int, string, decimal, …
            if (type.TypeKind == TypeKind.Interface || type.IsAbstract) return false;
            // A refused collection shape is not a declarable root: SchemaRegistry.DeclareRoot derives its
            // schema from the CLR type and refuses it (Format/UnsupportedCollectionPolicy). It is already
            // reported at the declaration or the [CerasContext] (CERAS034), so emitting a DeclareRoot for
            // it here would only add a second, later failure to a build that has already failed.
            if (FindUnsupportedCollectionType(type) != null) return false;
            if (MemberExplorer.IsCollectionFormatterType(type)) return true;
            if (type.IsUnboundGenericType || !type.IsGenericType) return false;

            var definitionId = GetDefinitionId(type.OriginalDefinition);   // "KeyValuePair`2", "ValueTuple`2", "Tuple`2"
            return definitionId == "System.Collections.Generic.KeyValuePair`2"
                || IsTupleDefinition(definitionId);
        }

        /// <summary>
        /// Emits a minimal, field-less <see cref="SchemaTypeInfo"/> registration for a type that has no
        /// generated formatter. The type's bytes are produced by a custom/reflection formatter, so the
        /// schema entry exists only to assign a deterministic type id and stable name for the bake.
        /// </summary>
        static void AppendMinimalRegistration(StringBuilder body, INamedTypeSymbol type, bool isStruct)
        {
            string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string stableName = GetRobustName(type);
            string kindExpr = isStruct
                ? "global::Ceras.Format.SchemaTypeKind.Struct"
                : "global::Ceras.Format.SchemaTypeKind.Class";

            body.AppendLine($"                    if (robustConfig != null)");
            body.AppendLine($"                        robustConfig.SchemaRegistry.Register(typeof({typeFqn}),");
            body.AppendLine($"                            new global::Ceras.Format.SchemaTypeInfo(");
            body.AppendLine($"                                stableName: {QuoteLiteral(stableName)},");
            body.AppendLine($"                                previousTypeNames: global::System.Array.Empty<string>(),");
            body.AppendLine($"                                kind: {kindExpr},");
            body.AppendLine($"                                fields: global::System.Array.Empty<global::Ceras.Format.SchemaFieldInfo>(),");
            body.AppendLine($"                                fixedByteSize: 0));");
        }

        static void AppendGeneratedHeader(StringBuilder sb)
        {
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable disable");
            sb.AppendLine("#pragma warning disable 649");
            sb.AppendLine();
            sb.AppendLine("using Ceras;");
            sb.AppendLine("using Ceras.Formatters;");
            sb.AppendLine("using Ceras.Helpers;");
            sb.AppendLine("using Ceras.Format;");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Buffers;");
            sb.AppendLine();
            sb.AppendLine("namespace Ceras.Generated");
            sb.AppendLine("{");
        }
    }
}
