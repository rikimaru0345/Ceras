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
							attrDef == "Ceras.CerasSerializableAttribute<T>")
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
            var initialRoots = new HashSet<INamedTypeSymbol>(types, SymbolEqualityComparer.Default);

            var cerasSerializableAttr = compilation.GetTypeByMetadataName(CerasSerializableFqn);
            var cerasSerializableGenericAttr = compilation.GetTypeByMetadataName(CerasSerializableFqn + "`1");
            var excludeFromDiscoveryAttr = compilation.GetTypeByMetadataName(ExcludeFromDiscoveryFqn);
            var blacklistAttr = compilation.GetTypeByMetadataName(BlacklistFqn);

            // Also include assembly-level [CerasSerializable] and [CerasKnownType] targets as roots.
            // CerasKnownType explicitly authorizes closed instantiations (and their transitive
            // closure) that are hidden behind object/interface members (TODO Phase 2.1).
            var knownTypeAttr = compilation.GetTypeByMetadataName("Ceras.CerasKnownTypeAttribute");
            var knownTypeGenericAttr = compilation.GetTypeByMetadataName("Ceras.CerasKnownTypeAttribute`1");

            foreach (var attr in compilation.Assembly.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, cerasSerializableAttr) ||
                    SymbolEqualityComparer.Default.Equals(attr.AttributeClass?.OriginalDefinition, cerasSerializableGenericAttr) ||
                    SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownTypeAttr) ||
                    SymbolEqualityComparer.Default.Equals(attr.AttributeClass?.OriginalDefinition, knownTypeGenericAttr))
                {
                    if (attr.AttributeClass!.IsGenericType)
                    {
                        var target = attr.AttributeClass.TypeArguments[0] as INamedTypeSymbol;
                        if (target != null) initialRoots.Add(target);
                    }
                    else if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is INamedTypeSymbol target)
                    {
                        initialRoots.Add(target);
                    }
                }
            }

            var finalRoots = initialRoots.ToImmutableArray();

            var includeAttr = compilation.GetTypeByMetadataName(IncludeFqn);
            var excludeAttr = compilation.GetTypeByMetadataName(ExcludeFqn);
            var previousNameAttr = compilation.GetTypeByMetadataName(PreviousNameFqn);
            var previousTypeAttr = compilation.GetTypeByMetadataName(PreviousTypeNameFqn);
            var serializedAsAttr = compilation.GetTypeByMetadataName(SerializedAsFqn);
            var cerasConstructorAttr = compilation.GetTypeByMetadataName(CerasConstructorFqn);

			var attrBag = new AttributeBag(includeAttr, excludeAttr,
				previousNameAttr, previousTypeAttr, serializedAsAttr,
                cerasSerializableAttr, cerasConstructorAttr);

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

            var results = DiscoveryEngine.DiscoverAllTypes(
                context, compilation, finalRoots, attrBag, excludeFromDiscoveryAttr, blacklistPatterns);

            var finalTypes = results.Keys.ToImmutableArray();
            var finalTypesSet = new HashSet<INamedTypeSymbol>(finalTypes, SymbolEqualityComparer.Default);

            // Canonical identity guard: two distinct types must never share a stable type name,
            // otherwise schema/migration resolution is ambiguous (TODO Phase 2.3).
            var stableNameOwners = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
            foreach (var type in finalTypes)
            {
                if (IsOpenType(type)) continue;
                if (IsWellKnownType(type) || MemberExplorer.IsCollectionType(type)) continue;
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
            var memberCache = new Dictionary<INamedTypeSymbol, List<SerializedMember>>(SymbolEqualityComparer.Default);
            List<SerializedMember> DiscoverCached(INamedTypeSymbol t)
            {
                if (!memberCache.TryGetValue(t, out var cached))
                    memberCache[t] = cached = MemberExplorer.DiscoverMembers(context, t, attrBag, blacklistPatterns, finalTypesSet);
                return cached;
            }

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
                if (IsWellKnownType(type)) { noFormatterReason[type] = "it uses a built-in formatter"; continue; }
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

            // 1. Generate the 'UseRobustFormatters' file
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
            sbUse.AppendLine("        public static void UseRobustFormatters(global::Ceras.CerasConfig config)");
            sbUse.AppendLine("        {");
            sbUse.AppendLine("            var robustConfig = config as global::Ceras.CerasRobustConfig;");
            sbUse.AppendLine();

            foreach (var type in finalTypes)
            {
                // generatedFormatterTypes already encodes the interface/abstract/open/well-known/
                // collection/constructability and generated-name-collision filters.
                if (!generatedFormatterTypes.Contains(type)) continue;

                string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                string formatterName = MakeFormatterClassName(type);

                string varName = "tc_" + SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal));
                sbUse.AppendLine($"            var {varName} = config.ConfigType<{typeFqn}>();");
                sbUse.AppendLine($"            if ({varName}.CustomFormatter == null) {varName}.CustomFormatter = new {formatterName}();");

                sbUse.AppendLine($"            if (robustConfig != null) robustConfig.SchemaRegistry.Register(typeof({typeFqn}), {formatterName}.Schema);");
            }
            // Register discovered interface and abstract types with minimal schemas.
            // These types have no formatters but may appear as generic type arguments in
            // serialized collections (e.g. List<ILogicalEntity>). Without pre-registration
            // they get dynamically assigned IDs during serialization AFTER SchemaSection.Write
            // has already run, so their IDs are never written to the schema section.
            // On load, RobustContext.IdToType cannot resolve those IDs → null type →
            // "Failed to deserialize generic argument 0 for type 'List`1'".
            foreach (var type in finalTypes)
            {
                if (type.TypeKind != TypeKind.Interface && !type.IsAbstract) continue;
                if (type.IsSealed) continue; // static classes (abstract+sealed) have no instances
                if (IsWellKnownType(type)) continue;
                if (type.TypeParameters.Length > 0) continue; // open generics: closed instantiations are handled at runtime

                string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                string stableName = GetRobustName(type);
                sbUse.AppendLine($"            if (robustConfig != null)");
                sbUse.AppendLine($"                robustConfig.SchemaRegistry.Register(typeof({typeFqn}),");
                sbUse.AppendLine($"                    new global::Ceras.Format.SchemaTypeInfo(");
                sbUse.AppendLine($"                        stableName: {QuoteString(stableName)},");
                sbUse.AppendLine($"                        previousTypeNames: global::System.Array.Empty<string>(),");
                sbUse.AppendLine($"                        kind: global::Ceras.Format.SchemaTypeKind.Class,");
                sbUse.AppendLine($"                        fields: global::System.Array.Empty<global::Ceras.Format.SchemaFieldInfo>(),");
                sbUse.AppendLine($"                        fixedByteSize: 0));");
            }

            sbUse.AppendLine("        }");
            sbUse.AppendLine("    }");
            sbUse.AppendLine();
            sbUse.AppendLine("    /// <summary>");
            sbUse.AppendLine("    /// Extension methods on <see cref=\"global::Ceras.CerasConfig\"/> for registering generated formatters.");
            sbUse.AppendLine("    /// </summary>");
            sbUse.AppendLine("    public static class CerasConfigExtensions");
            sbUse.AppendLine("    {");
            sbUse.AppendLine("        /// <summary>Registers all generated formatters and schemas into the config.</summary>");
            sbUse.AppendLine("        public static void UseGeneratedFormatters(this global::Ceras.CerasConfig config)");
            sbUse.AppendLine("            => RobustFormatters.UseRobustFormatters(config);");
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
                    if (IsWellKnownOrBanned(coreType)) continue;

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

                MemberExplorer.ValidateDuplicateWireNames(context, type, members);

                var sbFmt = new StringBuilder();
                AppendGeneratedHeader(sbFmt);

                // Phase 2.4: for a closed generic, reuse one open generic formatter template per
                // definition and emit only a thin per-closed-type subclass carrying the closed schema.
                bool isClosedGeneric = type.IsGenericType && !IsOpenType(type);
                if (isClosedGeneric)
                {
                    var definition = type.OriginalDefinition;
                    if (emittedGenericDefs.Add(definition))
                    {
                        var openMembers = DiscoverCached(definition);

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

                    GenericFormatterEmitter.WriteGenericDerived(sbFmt, type, definition, members, attrBag);
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
