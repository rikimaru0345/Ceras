using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Ceras.SourceGenerator;

/// <summary>
/// Roslyn incremental source generator that emits IFormatter&lt;T&gt; implementations for
/// every type decorated with [GenerateFormatter].  The generated code mirrors what the
/// old AotGeneratorApp produced, but runs automatically at compile time.
///
/// When [GenerateFormatter(VersionTolerant = true)] is used, a SchemaTypeInfo static
/// field is also emitted and registered with SavegameSchemaRegistry for use by the
/// Ceras savegame format's migration engine.
/// </summary>
[Generator]
public sealed class CerasFormatterGenerator : IIncrementalGenerator
{
    const string GenerateFormatterFqn = "Ceras.GenerateFormatterAttribute";
    const string IncludeFqn = "Ceras.IncludeAttribute";
    const string ExcludeFqn = "Ceras.ExcludeAttribute";
    const string MemberConfigFqn = "Ceras.MemberConfigAttribute";
    const string PreviousNameFqn = "Ceras.PreviousNameAttribute";
    const string PreviousTypeNameFqn = "Ceras.PreviousTypeNameAttribute";
    const string SerializedAsFqn = "Ceras.SerializedAsAttribute";
    const string MonomorphicFqn = "Ceras.MonomorphicAttribute";
    const string PolymorphicFqn = "Ceras.PolymorphicAttribute";
    const string ExcludeFromDiscoveryFqn = "Ceras.ExcludeFromDiscoveryAttribute";
    const string BlacklistFqn = "Ceras.CerasGeneratorBlacklistAttribute";
    const string CerasConstructorFqn = "Ceras.CerasConstructorAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<INamedTypeSymbol> types = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                GenerateFormatterFqn,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol)
            .WithComparer(SymbolEqualityComparer.Default);

        IncrementalValuesProvider<INamedTypeSymbol> genericTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => s is TypeDeclarationSyntax or CompilationUnitSyntax,
                transform: static (ctx, _) => GetGenericGenerateFormatterTargets(ctx))
            .SelectMany(static (x, _) => x)
            .WithComparer(SymbolEqualityComparer.Default);

        var allTypes = types.Collect().Combine(genericTypes.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToImmutableArray());

        IncrementalValueProvider<(Compilation, ImmutableArray<INamedTypeSymbol>)> combined =
            context.CompilationProvider.Combine(allTypes);

        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var (compilation, targetTypes) = pair;
            if (targetTypes.IsEmpty)
                return;
            Execute(spc, compilation, targetTypes);
        });
    }

    static ImmutableArray<INamedTypeSymbol> GetGenericGenerateFormatterTargets(GeneratorSyntaxContext ctx)
    {
        var results = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var attributes = ctx.Node switch
        {
            TypeDeclarationSyntax tds => ctx.SemanticModel.GetDeclaredSymbol(tds)?.GetAttributes(),
            CompilationUnitSyntax cus => ctx.SemanticModel.Compilation.Assembly.GetAttributes(),
            _ => null
        };

        if (attributes == null) return results.ToImmutable();

        foreach (var attr in attributes)
        {
            if (attr.AttributeClass is { IsGenericType: true } &&
                attr.AttributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).StartsWith("global::Ceras.GenerateFormatterAttribute<"))
            {
                if (attr.AttributeClass.TypeArguments[0] is INamedTypeSymbol target)
                    results.Add(target);
            }
        }

        return results.ToImmutable();
    }

    // -------------------------------------------------------------------------
    // Top-level execution
    // -------------------------------------------------------------------------

    static void Execute(
        SourceProductionContext context,
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol> types)
    {
        // Resolve attribute symbols once
        var includeAttr = compilation.GetTypeByMetadataName(IncludeFqn);
        var excludeAttr = compilation.GetTypeByMetadataName(ExcludeFqn);
        var memberConfigAttr = compilation.GetTypeByMetadataName(MemberConfigFqn);
        var previousNameAttr = compilation.GetTypeByMetadataName(PreviousNameFqn);
        var previousTypeAttr = compilation.GetTypeByMetadataName(PreviousTypeNameFqn);
        var serializedAsAttr = compilation.GetTypeByMetadataName(SerializedAsFqn);
        var monomorphicAttr = compilation.GetTypeByMetadataName(MonomorphicFqn);
        var polymorphicAttr = compilation.GetTypeByMetadataName(PolymorphicFqn);
        var generateFmtAttr = compilation.GetTypeByMetadataName(GenerateFormatterFqn);
        var excludeFromDiscoveryAttr = compilation.GetTypeByMetadataName(ExcludeFromDiscoveryFqn);
        var blacklistAttr = compilation.GetTypeByMetadataName(BlacklistFqn);
        var cerasConstructorAttr = compilation.GetTypeByMetadataName(CerasConstructorFqn);

        // --- Collect Blacklist ----------------------------------------------------
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

        bool MatchesBlacklist(INamedTypeSymbol type)
        {
            string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
            foreach (var p in blacklistPatterns)
            {
                if (p.EndsWith("*"))
                {
                    if (fqn.StartsWith(p.Substring(0, p.Length - 1))) return true;
                }
                else if (fqn == p) return true;
            }
            return false;
        }

        // --- CERAS002: interface / abstract without DiscoverSubtypes ---------------
        // Warn early — before any expansion — so the user knows the attribute has no effect.
        foreach (var t in types)
        {
            bool isInterface = t.TypeKind == TypeKind.Interface;
            bool isAbstract = t.IsAbstract && t.TypeKind != TypeKind.Interface;
            if (!isInterface && !isAbstract) continue;

            if (!HasDiscoverSubtypes(t, generateFmtAttr))
            {
                string kind = isInterface ? "interface" : "abstract class";
                context.ReportDiagnostic(Diagnostic.Create(
                    Diag.InterfaceOrAbstractWithoutDiscoverSubtypes,
                    t.Locations.FirstOrDefault(),
                    t.Name, kind));
            }
        }

        // --- DiscoverSubtypes expansion -------------------------------------------
        // For any annotated type that has DiscoverSubtypes = true, scan the whole
        // compilation and collect every non-abstract concrete type that implements
        // or inherits from it.  The VersionTolerant flag is inherited from the root.
        var expanded = new Dictionary<INamedTypeSymbol, bool /* vt */>(SymbolEqualityComparer.Default);
        var discoverRoots = new List<(INamedTypeSymbol Root, bool Vt)>();

        foreach (var t in types)
        {
            if (HasDiscoverSubtypes(t, generateFmtAttr))
                discoverRoots.Add((t, IsVersionTolerant(t, generateFmtAttr)));
            else
                expanded[t] = IsVersionTolerant(t, generateFmtAttr);
        }

        if (discoverRoots.Count > 0)
        {
            // Walk every named type symbol reachable from the compilation's global namespace.
            foreach (var candidate in GetAllNamedTypes(compilation.GlobalNamespace))
            {
                if (candidate.IsAbstract || candidate.TypeKind == TypeKind.Interface)
                    continue;
                // Skip types that are not accessible from generated code (private nested types)
                if (candidate.DeclaredAccessibility == Accessibility.Private ||
                    candidate.DeclaredAccessibility == Accessibility.Protected)
                    continue;
                // Skip types that already have an explicit [GenerateFormatter] — they
                // carry their own VersionTolerant value and are already in `expanded`.
                if (expanded.ContainsKey(candidate))
                    continue;

                // Explicitly excluded from discovery?
                if (excludeFromDiscoveryAttr != null && HasAttribute(candidate, excludeFromDiscoveryAttr))
                    continue;

                // Blacklisted?
                if (MatchesBlacklist(candidate))
                    continue;

                foreach (var (root, vt) in discoverRoots)
                {
                    if (ImplementsOrInherits(candidate, root))
                    {
                        // Most-derived explicit annotation wins for VersionTolerant;
                        // fall back to the root's value.
                        bool candidateVt = vt;
                        if (HasExplicitVersionTolerant(candidate, generateFmtAttr, out bool explicitVt))
                            candidateVt = explicitVt;

                        expanded[candidate] = candidateVt;
                        break;
                    }
                }
            }
        }

        var finalTypes = expanded.Keys.ToImmutableArray();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// ReSharper disable All");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("#pragma warning disable 649");
        sb.AppendLine();
        sb.AppendLine("using Ceras;");
        sb.AppendLine("using Ceras.Formatters;");
        sb.AppendLine("using Ceras.Helpers;");
        sb.AppendLine("using Ceras.Savegame;");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine();
        sb.AppendLine("namespace Ceras.Generated");
        sb.AppendLine("{");

        // --- Registration helper -----------------------------------------------
        sb.AppendLine("    public static class GeneratedFormatters");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Call this before creating your CerasSerializer to register all");
        sb.AppendLine("        /// pre-generated formatters and their schema descriptors.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static void UseFormatters(global::Ceras.SerializerConfig config)");
        sb.AppendLine("        {");

        foreach (var type in finalTypes)
        {
            // Interfaces and abstract types have no formatter class generated; skip registration.
            if (type.TypeKind == TypeKind.Interface || type.IsAbstract) continue;

            string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string formatterName = MakeFormatterClassName(type);
            bool vt = expanded[type];

            sb.AppendLine($"            config.ConfigType<{typeFqn}>().CustomFormatter = new {formatterName}();");

            if (vt)
            {
                sb.AppendLine($"            config.Savegame.SchemaRegistry.Register(typeof({typeFqn}), {formatterName}.Schema);");
            }
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- One formatter class per type ----------------------------------------
        var attrBag = new AttributeBag(includeAttr, excludeAttr, memberConfigAttr,
            previousNameAttr, previousTypeAttr, serializedAsAttr, monomorphicAttr,
            polymorphicAttr, generateFmtAttr, cerasConstructorAttr);

        foreach (var type in finalTypes)
        {
            // Interfaces and abstract types with DiscoverSubtypes = true are roots only;
            // they are never instantiated so no formatter class is needed for them.
            if (type.TypeKind == TypeKind.Interface || type.IsAbstract)
                continue;

            // CERAS003: can Ceras construct this type?
            // Value types always have a default constructor; check only reference types.
            if (!type.IsValueType && !CanCerasConstruct(type, cerasConstructorAttr))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diag.NotConstructable,
                    type.Locations.FirstOrDefault(),
                    type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }

            // CERAS008: members with both [Include] and [Exclude]
            ValidateIncludeExcludeConflicts(context, type, attrBag);

            var members = DiscoverMembers(type, attrBag);

            if (members.Count == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diag.NoSerializableMembers,
                    type.Locations.FirstOrDefault(),
                    type.Name));
            }

            // CERAS004/005/006/007: [CerasConstructor] validation
            ValidateCerasConstructor(context, compilation, type, members, attrBag);

            // CERAS009: duplicate stable wire names
            ValidateDuplicateWireNames(context, type, members);

            // CERAS010: partial requirement for private members
            ValidatePartialRequirement(context, type, members);

            // CERAS011: readonly members must be in ctor
            ValidateReadonlyMembers(context, type, members);

            // Emit partial class with access helpers for private members
            WritePartialClassHelper(context, type, members);

            // Use the VT flag from the expanded dict (which inherits from DiscoverSubtypes roots)
            // rather than re-checking only the type's own attribute.
            bool typeVt = expanded[type];
            WriteFormatterClass(sb, type, members, attrBag, typeVt);
        }

        sb.AppendLine("}"); // close namespace
        sb.AppendLine("#nullable restore");
        sb.AppendLine("#pragma warning restore 649");

        context.AddSource("CerasGeneratedFormatters.g.cs",
            SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    // -------------------------------------------------------------------------
    // DiscoverSubtypes helpers
    // -------------------------------------------------------------------------

    /// <summary>Enumerate every named type reachable from a namespace (recursive).</summary>
    static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamespaceOrTypeSymbol root)
    {
        foreach (var member in root.GetMembers())
        {
            if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in GetAllNamedTypes(type))
                    yield return nested;
            }
            else if (member is INamespaceSymbol ns)
            {
                foreach (var t in GetAllNamedTypes(ns))
                    yield return t;
            }
        }
    }

    /// <summary>Returns true if <paramref name="candidate"/> implements or inherits <paramref name="root"/>.</summary>
    static bool ImplementsOrInherits(INamedTypeSymbol candidate, INamedTypeSymbol root)
    {
        if (root.TypeKind == TypeKind.Interface)
        {
            foreach (var iface in candidate.AllInterfaces)
                if (SymbolEqualityComparer.Default.Equals(iface, root))
                    return true;
            return false;
        }

        // Class / abstract class: walk the base chain
        INamedTypeSymbol? cur = candidate.BaseType;
        while (cur != null && cur.SpecialType != SpecialType.System_Object)
        {
            if (SymbolEqualityComparer.Default.Equals(cur, root))
                return true;
            cur = cur.BaseType;
        }
        return false;
    }

    static bool HasDiscoverSubtypes(INamedTypeSymbol type, INamedTypeSymbol? generateFmtAttr)
    {
        if (generateFmtAttr == null) return false;
        foreach (var attr in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, generateFmtAttr))
                continue;
            foreach (var na in attr.NamedArguments)
                if (na.Key == "DiscoverSubtypes" && na.Value.Value is bool b)
                    return b;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the type has an explicit [GenerateFormatter(VersionTolerant = ...)] attribute
    /// (without DiscoverSubtypes).  Used to let concrete types override the root's flag.
    /// </summary>
    static bool HasExplicitVersionTolerant(INamedTypeSymbol type, INamedTypeSymbol? generateFmtAttr, out bool vt)
    {
        vt = false;
        if (generateFmtAttr == null) return false;
        foreach (var attr in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, generateFmtAttr))
                continue;
            foreach (var na in attr.NamedArguments)
            {
                if (na.Key == "VersionTolerant" && na.Value.Value is bool b)
                {
                    vt = b;
                    return true;
                }
            }
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // Member discovery
    // -------------------------------------------------------------------------

    static List<SerializedMember> DiscoverMembers(INamedTypeSymbol type, AttributeBag attrs)
    {
        var members = new List<SerializedMember>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        // Walk the full base-type chain from most-derived → object so that each
        // derived class formatter includes all inherited members in one flat list.
        // We stop at System.Object (or null).  Members are added most-derived-first
        // so that overrides in derived classes shadow base declarations.
        var typeChain = new List<INamedTypeSymbol>();
        INamedTypeSymbol? cur = type;
        while (cur != null && cur.SpecialType != SpecialType.System_Object)
        {
            typeChain.Add(cur);
            cur = cur.BaseType;
        }

        // Process most-derived first so derived [Exclude] or [Include] wins over base.
        // We still collect from every level; seenNames prevents duplicates.
        TargetMemberKind currentTargetKind = TargetMemberKind.AllPublic;
        foreach (var t in typeChain)
        {
            var kind = GetTargetMemberKind(t, attrs.MemberConfig);
            if (kind.HasValue)
                currentTargetKind = kind.Value;

            foreach (var member in t.GetMembers())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared)
                    continue;

                // Skip if we already added a member with this name from a more-derived type
                if (!seenNames.Add(member.Name))
                    continue;

                bool hasInclude = attrs.Include != null && HasAttribute(member, attrs.Include);
                bool hasExclude = attrs.Exclude != null && HasAttribute(member, attrs.Exclude);

                if (hasExclude) continue;

                // Generated formatters are in a separate class; they can only access Public and Internal members.
                // Private and Protected members require the class to be 'partial' so we can inject a helper.
                bool isAccessible = member.DeclaredAccessibility == Accessibility.Public ||
                                    member.DeclaredAccessibility == Accessibility.Internal;

                if (member is IFieldSymbol field)
                {
                    // [Include] overrides the readonly check; Ceras can populate readonly fields
                    // only if the containing type has a formatter that sets them directly.
                    // For generated formatters, we allow it with [Include].
                    if (field.IsConst) continue;

                    bool isFieldGetterOnly = field.IsReadOnly && IsCollectionType(field.Type);
                    bool isFieldPublic = field.DeclaredAccessibility == Accessibility.Public;

                    if (!hasInclude && !IsFieldIncluded(field, currentTargetKind))
                    {
                        // Not included by default visibility.
                        // For getter-only collections, we only allow it if it is public OR explicitly included.
                        if (!isFieldGetterOnly) continue;
                        if (!isFieldPublic && !hasInclude) continue;
                    }
                    else if (field.IsReadOnly && !isFieldGetterOnly && !hasInclude)
                    {
                        // It's a readonly non-collection field and not explicitly included.
                        // We skip it to avoid CERAS011 later unless the user specifically asked for it.
                        continue;
                    }

                    members.Add(MakeMember(field.Name, field.Type, isField: true, member, attrs, isGetterOnly: isFieldGetterOnly));
                }
                else if (member is IPropertySymbol prop)
                {
                    // Skip write-only and indexers
                    if (prop.IsWriteOnly || prop.IsIndexer) continue;
                    if (prop.GetMethod == null) continue;

                    bool isGetterOnly = prop.IsReadOnly || prop.SetMethod == null;
                    bool isPropPublic = prop.DeclaredAccessibility == Accessibility.Public;

                    if (isGetterOnly)
                    {
                        // Getter-only COLLECTION properties can be populated in-place by Ceras.
                        // All other getter-only properties require a setter.
                        if (!IsCollectionType(prop.Type)) continue;
                        
                        // Only allow if public or explicitly included
                        if (!isPropPublic && !hasInclude) continue;
                    }
                    else
                    {
                        // Skip properties whose setter is not accessible (private/protected) 
                        // UNLESS we can use the partial class trick.
                        // For now, if it's not accessible and not explicitly included, skip it.
                        if (!isAccessible && !hasInclude && !IsPropertyIncluded(prop, currentTargetKind))
                            continue;
                    }

                    if (!hasInclude && !IsPropertyIncluded(prop, currentTargetKind)) continue;

                    members.Add(MakeMember(prop.Name, prop.Type, isField: false, member, attrs, isGetterOnly: isGetterOnly));
                }
            }
        }

        // --- [CerasConstructor] parameter injection ---
        // If the type has a constructor marked [CerasConstructor], its parameters become
        // serialized members (read in Serialize, passed to ctor in Deserialize rather than
        // assigned post-construction). Parameters matching existing members just set
        // IsCtorParam = true; parameters not yet in the list are force-added if accessible.
        if (attrs.CerasConstructor != null)
        {
            IMethodSymbol? cerasCtor = null;
            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol method &&
                    method.MethodKind == MethodKind.Constructor &&
                    HasAttribute(method, attrs.CerasConstructor))
                {
                    cerasCtor = method;
                    break;
                }
            }

            if (cerasCtor != null)
            {
                // Build a lookup of existing members by normalized name (lowercase, strip leading _)
                var memberByNorm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < members.Count; i++)
                {
                    string norm = NormalizeName(members[i].Name);
                    memberByNorm[norm] = i;
                }

                foreach (var param in cerasCtor.Parameters)
                {
                    string normParam = NormalizeName(param.Name);

                    if (memberByNorm.TryGetValue(normParam, out int idx))
                    {
                        // Existing member: replace with IsCtorParam = true, preserving StableName
                        // but storing the exact constructor parameter name for named-arg emission.
                        var existing = members[idx];
                        members[idx] = new SerializedMember(
                            existing.Name, existing.StableName, existing.Type,
                            existing.IsField, existing.PreviousNames, existing.IsMonomorphic,
                            existing.IsAccessible, existing.IsReadOnly, existing.IsGetterOnly, isCtorParam: true, 
                            ctorParamName: param.Name);
                    }
                    else
                    {
                        // Not yet in list: search the type (and base types) for a matching member
                        ISymbol? found = null;
                        ITypeSymbol? foundType = null;
                        bool foundIsField = false;

                        foreach (var t2 in typeChain)
                        {
                            foreach (var sym in t2.GetMembers())
                            {
                                if (sym.IsStatic || sym.IsImplicitlyDeclared) continue;
                                // Match by normalized name
                                string normMember = NormalizeName(sym.Name);
                                if (normMember != normParam) continue;

                                if (sym is IFieldSymbol f2 && !f2.IsConst)
                                {
                                    found = sym; foundType = f2.Type; foundIsField = true; break;
                                }
                                else if (sym is IPropertySymbol p2 && !p2.IsIndexer && p2.GetMethod != null)
                                {
                                    found = sym; foundType = p2.Type; foundIsField = false; break;
                                }
                            }
                            if (found != null) break;
                        }

                        if (found != null && foundType != null)
                        {
                            bool mono = IsMonomorphicByDefault(foundType);
                            bool isAcc = found.DeclaredAccessibility == Accessibility.Public ||
                                         found.DeclaredAccessibility == Accessibility.Internal;

                            bool isRO = false;
                            if (foundIsField) isRO = ((IFieldSymbol)found).IsReadOnly;
                            else isRO = ((IPropertySymbol)found).SetMethod == null;

                            // StableName = param.Name for wire format; CtorParamName = param.Name for named-arg call.
                            members.Add(new SerializedMember(
                                found.Name, param.Name, foundType, foundIsField,
                                Array.Empty<string>(), mono, isAcc, isRO,
                                isGetterOnly: false, isCtorParam: true, ctorParamName: param.Name));
                            memberByNorm[normParam] = members.Count - 1;
                        }
                    }
                }
            }
        }

        // Sort by SchemaMemberComparer key: CLR-TypeFullName + MemberName + DeclaringTypeFqn + "f"/"p"
        // SortKeyFormat has UseSpecialTypes OFF so "float" → "System.Single" etc., matching FullName.
        string declaringFqn = type.ToDisplayString(SortKeyFormat);
        members.Sort((a, b) =>
        {
            string ka = a.Type.ToDisplayString(SortKeyFormat) + a.Name + declaringFqn + (a.IsField ? "f" : "p");
            string kb = b.Type.ToDisplayString(SortKeyFormat) + b.Name + declaringFqn + (b.IsField ? "f" : "p");
            return string.Compare(ka, kb, StringComparison.Ordinal);
        });

        return members;
    }

    static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            if (syntax is TypeDeclarationSyntax typeDecl)
            {
                if (typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                    return true;
            }
        }
        return false;
    }

    static bool IsCollectionType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol) return true;
        if (type is INamedTypeSymbol named)
        {
            foreach (var iface in named.AllInterfaces)
            {
                var ifaceFqn = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (ifaceFqn.StartsWith("global::System.Collections.Generic.ICollection<") ||
                    ifaceFqn == "global::System.Collections.ICollection")
                    return true;
            }
        }
        return false;
    }

    static SerializedMember MakeMember(
        string name, ITypeSymbol type, bool isField,
        ISymbol symbol, AttributeBag attrs, bool isGetterOnly = false)
    {
        // Stable wire name: [SerializedAs] → explicit name, else C# member name
        string stableName = name;
        if (attrs.SerializedAs != null)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrs.SerializedAs) &&
                    attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is string sn)
                {
                    stableName = (string)sn;
                    break;
                }
            }
        }

        // Previous names from [PreviousName]
        var previousNames = new List<string>();
        if (attrs.PreviousName != null)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrs.PreviousName))
                    continue;

                if (attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is string primary)
                    previousNames.Add((string)primary);

                // params string[] alternativeNames is the second arg
                if (attr.ConstructorArguments.Length > 1 &&
                    !attr.ConstructorArguments[1].IsNull)
                {
                    foreach (var v in attr.ConstructorArguments[1].Values)
                        if (v.Value is string alt)
                            previousNames.Add((string)alt);
                }
            }
        }

        // Monomorphic detection
        bool isMonomorphic = IsMonomorphicByDefault(type);
        if (attrs.Monomorphic != null && HasAttribute(symbol, attrs.Monomorphic))
            isMonomorphic = true;
        if (attrs.Polymorphic != null && HasAttribute(symbol, attrs.Polymorphic))
            isMonomorphic = false;

        // Accessibility detection: generated formatters (in Ceras.Generated) 
        // can only access Public and Internal members.
        bool isAccessible = symbol.DeclaredAccessibility == Accessibility.Public ||
                            symbol.DeclaredAccessibility == Accessibility.Internal;

        bool isReadOnly = false;
        if (isField)
        {
            isReadOnly = ((IFieldSymbol)symbol).IsReadOnly;
        }
        else
        {
            var p = (IPropertySymbol)symbol;
            isReadOnly = p.SetMethod == null || p.SetMethod.IsInitOnly;
        }

        if (symbol is IPropertySymbol prop)
        {
            // Getter must be accessible
            bool getterAcc = prop.GetMethod != null &&
                             (prop.GetMethod.DeclaredAccessibility == Accessibility.Public ||
                              prop.GetMethod.DeclaredAccessibility == Accessibility.Internal);

            // Setter must be accessible if it exists and we're not in getter-only mode
            bool setterAcc = true;
            if (prop.SetMethod != null)
            {
                setterAcc = prop.SetMethod.DeclaredAccessibility == Accessibility.Public ||
                            prop.SetMethod.DeclaredAccessibility == Accessibility.Internal;
            }
            else if (!isGetterOnly)
            {
                setterAcc = false;
            }

            isAccessible = getterAcc && setterAcc;
        }

        return new SerializedMember(name, stableName, type, isField,
            previousNames.ToArray(), isMonomorphic, isAccessible, isReadOnly, isGetterOnly);
    }

    /// <summary>
    /// A type is monomorphic (no per-element type ID needed) when it is a value type
    /// or a sealed class.  Collections are checked on their element type.
    /// </summary>
    static bool IsMonomorphicByDefault(ITypeSymbol type)
    {
        if (type.IsValueType) return true;

        // Unwrap collection element types for List<T>, T[], etc.
        if (type is IArrayTypeSymbol arr)
            return IsMonomorphicByDefault(arr.ElementType);

        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            // List<T>, HashSet<T>, Queue<T>, Stack<T> → check T
            var args = named.TypeArguments;
            if (args.Length == 1)
                return IsMonomorphicByDefault(args[0]);
            // Dictionary<K,V> → check V (values are what vary polymorphically)
            if (args.Length == 2)
                return IsMonomorphicByDefault(args[1]);
        }

        return type.IsSealed;
    }

    // -------------------------------------------------------------------------
    // Code generation
    // -------------------------------------------------------------------------

    static void WriteFormatterClass(
        StringBuilder sb,
        INamedTypeSymbol type,
        List<SerializedMember> members,
        AttributeBag attrs,
        bool vt = false)
    {
        string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string formatterName = MakeFormatterClassName(type);

        sb.AppendLine($"    internal sealed class {formatterName} : global::Ceras.Formatters.IFormatter<{typeFqn}>");
        sb.AppendLine("    {");

        // --- Formatter fields (one per distinct member type) -------------------
        var seenFieldNames = new HashSet<string>();
        foreach (var m in members)
        {
            if (IsFastPrimitive(m.Type)) continue;

            string fieldName = MakeFormatterFieldName(m.Type);
            if (!seenFieldNames.Add(fieldName)) continue;

            string concreteFormatterType = GetConcreteFormatterType(m.Type, m.IsMonomorphic, attrs);
            sb.AppendLine($"        {concreteFormatterType} {fieldName};");
        }
        sb.AppendLine();

        // --- Schema descriptor (VersionTolerant only) --------------------------
        if (vt)
            WriteSchemaDescriptor(sb, type, members, attrs);

        // --- SerializeUntyped --------------------------------------------------
        sb.AppendLine($"        public void SerializeUntyped(global::System.Buffers.IBufferWriter<byte> writer, object value)");
        sb.AppendLine("        {");
        sb.AppendLine($"            Serialize(writer, ({typeFqn})value);");
        sb.AppendLine("        }");
        sb.AppendLine();

        // --- DeserializeUntyped ------------------------------------------------
        sb.AppendLine($"        public void DeserializeUntyped(ref global::System.Buffers.SequenceReader<byte> reader, object container, global::System.Reflection.MemberInfo member)");
        sb.AppendLine("        {");
        sb.AppendLine($"            {typeFqn} value = default;");
        sb.AppendLine("            if (member is global::System.Reflection.FieldInfo f)");
        sb.AppendLine("            {");
        sb.AppendLine("                var existing = f.GetValue(container);");
        sb.AppendLine($"                if (existing is {typeFqn} t) value = t;");
        sb.AppendLine("                Deserialize(ref reader, ref value);");
        sb.AppendLine("                f.SetValue(container, value);");
        sb.AppendLine("            }");
        sb.AppendLine("            else if (member is global::System.Reflection.PropertyInfo p)");
        sb.AppendLine("            {");
        sb.AppendLine("                var existing = p.GetValue(container);");
        sb.AppendLine($"                if (existing is {typeFqn} t) value = t;");
        sb.AppendLine("                Deserialize(ref reader, ref value);");
        sb.AppendLine("                p.SetValue(container, value);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        // --- Serialize ---------------------------------------------------------
        sb.AppendLine($"        public void Serialize(global::System.Buffers.IBufferWriter<byte> writer, {typeFqn} value)");
        sb.AppendLine("        {");
        foreach (var m in members)
        {
            string fieldName = MakeFormatterFieldName(m.Type);
            string getAccess = GetGetAccess(m, typeFqn);

            if (IsFastPrimitive(m.Type))
            {
                string writeMethod = GetPrimitiveWriteMethod(m.Type);
                string valueExp = getAccess;
                if (m.Type.SpecialType == SpecialType.System_Boolean)
                    valueExp = $"(byte)({valueExp} ? 1 : 0)";
                sb.AppendLine($"            global::Ceras.SerializerBinary.{writeMethod}(writer, {valueExp});");
            }
            else
            {
                sb.AppendLine($"            {fieldName}.Serialize(writer, {getAccess});");
            }
        }
        sb.AppendLine("        }");
        sb.AppendLine();

        // --- Deserialize -------------------------------------------------------
        sb.AppendLine($"        public void Deserialize(ref global::System.Buffers.SequenceReader<byte> reader, ref {typeFqn} value)");
        sb.AppendLine("        {");

        bool hasCtorParams = members.Any(m => m.IsCtorParam);

        if (hasCtorParams)
        {
            // [CerasConstructor] path: read ALL members into locals first, then call ctor,
            // then assign any non-ctor-param members that survived the constructor.
            // Declare locals for every member.
            foreach (var m in members)
            {
                string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                sb.AppendLine($"            {memberTypeFqn} _ctor_{m.Name} = default;");
            }

            // Read each member from the stream into its local.
            foreach (var m in members)
            {
                string fieldName = MakeFormatterFieldName(m.Type);
                string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                if (IsFastPrimitive(m.Type))
                {
                    string readMethod = GetPrimitiveReadMethod(m.Type);
                    sb.AppendLine($"            _ctor_{m.Name} = global::Ceras.SerializerBinary.{readMethod};");
                }
                else if (m.IsGetterOnly)
                {
                    // Getter-only collection ctor param: read into existing collection if value exists
                    string getAccess = GetGetAccess(m, typeFqn);
                    sb.AppendLine($"            if (value != null) _ctor_{m.Name} = {getAccess};");
                    sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref _ctor_{m.Name});");
                }
                else if (m.IsField)
                {
                    sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref _ctor_{m.Name});");
                }
                else
                {
                    sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref _ctor_{m.Name});");
                }
            }

            // Find the [CerasConstructor]-marked constructor and build its argument list.
            // Parameters are matched to locals by normalized name (case-insensitive, strip _).
            // Build a map from normalized param name → local variable name.
            // Emit a named-argument constructor call using the exact C# parameter names (CtorParamName),
            // which preserves case correctly regardless of the member's stable wire name.
            var ctorArgParts = members
                .Where(m => m.IsCtorParam)
                .Select(m => $"{m.CtorParamName}: _ctor_{m.Name}");
            string ctorArgs = string.Join(", ", ctorArgParts);
            sb.AppendLine($"            value = new {typeFqn}({ctorArgs});");

                                    // Assign non-ctor-param members that need post-construction assignment.
                                    foreach (var m in members.Where(m => !m.IsCtorParam))
                                    {
                                        if (IsFastPrimitive(m.Type))
                                        {
                                            string readVal = $"_ctor_{m.Name}";
                                            WriteSetStatement(sb, m, typeFqn, readVal, 12);
                                        }
                                        else if (m.IsGetterOnly)
                                        {
                                            // Already populated in-place above; nothing to do
                                        }
                                        else
                                        {
                                            string readVal = $"_ctor_{m.Name}";
                                            WriteSetStatement(sb, m, typeFqn, readVal, 12);
                                        }
                                    }                }
                else
                {
                    // Normal path (no [CerasConstructor])
                    if (type.IsReferenceType)
                    {
                        sb.AppendLine($"            if (value == null) throw new global::System.InvalidOperationException($\"The instance of type '{{typeof({typeFqn}).FullName}}' was null. For types with a parameterless constructor, the instance must be provided by ReferenceFormatter.\");");
                    }
            
                                    foreach (var m in members)
                                    {
                                        string fieldName = MakeFormatterFieldName(m.Type);
                                        string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                                        string? refAccess = GetRefAccess(m, typeFqn);
                            
                                        if (IsFastPrimitive(m.Type))
                                        {
                                            string readMethod = GetPrimitiveReadMethod(m.Type);
                                            string readExpr = $"global::Ceras.SerializerBinary.{readMethod}";
                                            WriteSetStatement(sb, m, typeFqn, readExpr, 12);
                                        }
                                        else
                                        {
                                            if (m.IsGetterOnly)
                                            {
                                                // Getter-only collection: populate the existing instance in-place (no assignment needed)
                                                string getAccess = GetGetAccess(m, typeFqn);
                                                string tmp = "_m_" + m.Name;
                                                sb.AppendLine($"            {memberTypeFqn} {tmp} = {getAccess};");
                                                sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref {tmp});");
                                            }
                                            else
                                            {
                                                if (refAccess != null)
                                                {
                                                    // Direct field access via ref
                                                    sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref {refAccess});");
                                                }
                                                else
                                                {
                                                    // Property (accessible or not) or non-accessible readonly field: use temp var
                                                    string getAccess = GetGetAccess(m, typeFqn);
                                                    string tmp = "_m_" + m.Name;
                                                    sb.AppendLine($"            {memberTypeFqn} {tmp} = {getAccess};");
                                                    sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref {tmp});");
                                                    WriteSetStatement(sb, m, typeFqn, tmp, 12);
                                                }
                                            }
                                        }
                                    }                }
        sb.AppendLine("        }");

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static bool IsFastPrimitive(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Char:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Single:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Double:
                return true;
        }

        string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fqn == "global::System.Guid" || fqn == "global::System.DateTime" || fqn == "global::System.TimeSpan")
            return true;

        return false;
    }

    static string GetPrimitiveWriteMethod(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return "WriteByte";
            case SpecialType.System_Byte: return "WriteByte";
            case SpecialType.System_SByte: return "WriteByte";
            case SpecialType.System_Char: return "WriteInt16Fixed";
            case SpecialType.System_Int16: return "WriteInt16Fixed";
            case SpecialType.System_UInt16: return "WriteInt16Fixed";
            case SpecialType.System_Int32: return "WriteInt32";
            case SpecialType.System_UInt32: return "WriteUInt32";
            case SpecialType.System_Single: return "WriteFloat32Fixed";
            case SpecialType.System_Int64: return "WriteInt64Fixed";
            case SpecialType.System_UInt64: return "WriteInt64Fixed";
            case SpecialType.System_Double: return "WriteDouble64Fixed";
        }

        string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fqn == "global::System.Guid") return "WriteGuid";
        if (fqn == "global::System.DateTime") return "WriteDateTime";
        if (fqn == "global::System.TimeSpan") return "WriteTimeSpan";

        throw new NotSupportedException();
    }

    static string GetPrimitiveReadMethod(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return "ReadByte(ref reader) != 0";
            case SpecialType.System_Byte: return "ReadByte(ref reader)";
            case SpecialType.System_SByte: return "(sbyte)ReadByte(ref reader)";
            case SpecialType.System_Char: return "(char)ReadInt16Fixed(ref reader)";
            case SpecialType.System_Int16: return "ReadInt16Fixed(ref reader)";
            case SpecialType.System_UInt16: return "(ushort)ReadInt16Fixed(ref reader)";
            case SpecialType.System_Int32: return "ReadInt32(ref reader)";
            case SpecialType.System_UInt32: return "ReadUInt32(ref reader)";
            case SpecialType.System_Single: return "ReadFloat32Fixed(ref reader)";
            case SpecialType.System_Int64: return "ReadInt64Fixed(ref reader)";
            case SpecialType.System_UInt64: return "(ulong)ReadInt64Fixed(ref reader)";
            case SpecialType.System_Double: return "ReadDouble64Fixed(ref reader)";
        }

        string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fqn == "global::System.Guid") return "ReadGuid(ref reader)";
        if (fqn == "global::System.DateTime") return "ReadDateTime(ref reader)";
        if (fqn == "global::System.TimeSpan") return "ReadTimeSpan(ref reader)";

        throw new NotSupportedException();
    }

    static string GetConcreteFormatterType(ITypeSymbol type, bool isMonomorphic, AttributeBag attrs)
    {
        string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Primitives
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return "global::Ceras.Formatters.BoolFormatter";
            case SpecialType.System_Byte: return "global::Ceras.Formatters.ByteFormatter";
            case SpecialType.System_SByte: return "global::Ceras.Formatters.SByteFormatter";
            case SpecialType.System_Char: return "global::Ceras.Formatters.CharFormatter";
            case SpecialType.System_Int16: return "global::Ceras.Formatters.Int16Formatter";
            case SpecialType.System_UInt16: return "global::Ceras.Formatters.UInt16Formatter";
            case SpecialType.System_Int32: return "global::Ceras.Formatters.Int32Formatter";
            case SpecialType.System_UInt32: return "global::Ceras.Formatters.UInt32Formatter";
            case SpecialType.System_Single: return "global::Ceras.Formatters.FloatFormatter";
            case SpecialType.System_Int64: return "global::Ceras.Formatters.Int64Formatter";
            case SpecialType.System_UInt64: return "global::Ceras.Formatters.UInt64Formatter";
            case SpecialType.System_Double: return "global::Ceras.Formatters.DoubleFormatter";
            case SpecialType.System_String: return "global::Ceras.Formatters.IFormatter<string>";
        }

        if (typeFqn == "global::System.DateTime") return "global::Ceras.Formatters.DateTimeFormatter";
        if (typeFqn == "global::System.TimeSpan") return "global::Ceras.Formatters.TimeSpanFormatter";
        if (typeFqn == "global::System.Guid") return "global::Ceras.Formatters.GuidFormatter";

            // User-defined types marked with [GenerateFormatter]
            if (type is INamedTypeSymbol named && HasAttribute(named, attrs.GenerateFormatter))
            {
                if (type.IsValueType)
                {
                    string generatedName = MakeFormatterClassName(named);
                    return $"global::Ceras.Generated.{generatedName}";
                }
            }
        
            // Arrays
            if (type is IArrayTypeSymbol arr)
            {
                string elemFqn = arr.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                // Use the specialized collection formatter
                return $"global::Ceras.Formatters.CollectionFormatter<{typeFqn}, {elemFqn}>";
            }
        
            // Reference types or non-monomorphic types
            if (!type.IsValueType && !isMonomorphic)
            {
                return $"global::Ceras.Formatters.ReferenceFormatter<{typeFqn}>";
            }
        
            if (!type.IsValueType && isMonomorphic)
            {
                // If it's a sealed class, we can use ReferenceFormatter_KnownSealedType
                if (type.IsSealed)
                    return $"global::Ceras.Formatters.ReferenceFormatter_KnownSealedType<{typeFqn}>";
        
                return $"global::Ceras.Formatters.ReferenceFormatter<{typeFqn}>";
            }
        
            // Fallback to interface
            return $"global::Ceras.Formatters.IFormatter<{typeFqn}>";
        }
    static void WriteSchemaDescriptor(
        StringBuilder sb,
        INamedTypeSymbol type,
        List<SerializedMember> members,
        AttributeBag attrs)
    {
        // Stable type name
        string stableTypeName = GetStableTypeName(type, attrs.GenerateFormatter);

        // Previous type names from [PreviousTypeName]
        var prevTypeNames = new List<string>();
        if (attrs.PreviousTypeName != null)
        {
            foreach (var attr in type.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrs.PreviousTypeName) &&
                    attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is string ptn)
                    prevTypeNames.Add(ptn);
            }
        }

        // Type kind
        string kind;
        string underlyingType = null;
        if (type.TypeKind == TypeKind.Enum)
        {
            kind = "global::Ceras.Savegame.SchemaTypeKind.Enum";
            underlyingType = type.EnumUnderlyingType?.ToDisplayString(SortKeyFormat);
        }
        else
        {
            kind = type.IsValueType
                ? "global::Ceras.Savegame.SchemaTypeKind.Struct"
                : "global::Ceras.Savegame.SchemaTypeKind.Class";
        }

        // Fixed byte size
        int fixedSize = ComputeFixedByteSize(type);

        sb.AppendLine($"        internal static readonly global::Ceras.Savegame.SchemaTypeInfo Schema =");
        sb.AppendLine($"            new global::Ceras.Savegame.SchemaTypeInfo(");
        sb.AppendLine($"                stableName: {QuoteString(stableTypeName)},");
        sb.Append($"                previousTypeNames: ");
        WriteStringArray(sb, prevTypeNames);
        sb.AppendLine($",");
        sb.AppendLine($"                kind: {kind},");
        sb.AppendLine($"                fields: new global::Ceras.Savegame.SchemaFieldInfo[]");
        sb.AppendLine($"                {{");
        foreach (var m in members)
        {
            string typeName = m.Type.ToDisplayString(SortKeyFormat); // CLR name, no global::
            sb.Append($"                    new global::Ceras.Savegame.SchemaFieldInfo(");
            sb.Append($"{QuoteString(m.StableName)}, ");
            WriteStringArrayInline(sb, m.PreviousNames);
            sb.Append($", {QuoteString(typeName)}, {(m.IsMonomorphic ? "true" : "false")}");
            sb.AppendLine($"),");
        }
        sb.AppendLine($"                }},");
        sb.AppendLine($"                fixedByteSize: {fixedSize},");

        if (type.TypeKind == TypeKind.Enum)
        {
            sb.AppendLine($"                enumValues: new global::Ceras.Savegame.SchemaEnumValueInfo[]");
            sb.AppendLine($"                {{");
            foreach (var member in type.GetMembers())
            {
                if (member is IFieldSymbol field && field.HasConstantValue)
                {
                    sb.AppendLine($"                    new global::Ceras.Savegame.SchemaEnumValueInfo({QuoteString(field.Name)}, {field.ConstantValue}),");
                }
            }
            sb.AppendLine($"                }},");
            sb.AppendLine($"                underlyingTypeName: {QuoteString(underlyingType ?? "System.Int32")}");
        }
        else
        {
            sb.AppendLine($"                enumValues: null,");
            sb.AppendLine($"                underlyingTypeName: null");
        }

        sb.AppendLine($"            );");
        sb.AppendLine();
    }

    // -------------------------------------------------------------------------
    // Attribute / type helpers
    // -------------------------------------------------------------------------

    static bool IsVersionTolerant(INamedTypeSymbol type, INamedTypeSymbol? generateFmtAttr)
    {
        // Check standard attribute on the type
        if (generateFmtAttr != null)
        {
            foreach (var attr in type.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, generateFmtAttr))
                    continue;
                foreach (var na in attr.NamedArguments)
                    if (na.Key == "VersionTolerant" && na.Value.Value is bool b)
                        return b;
            }
        }

        // Check generic attribute on assembly or other types
        var assemblyAttrs = type.ContainingAssembly.GetAttributes();
        foreach (var attr in assemblyAttrs)
        {
            if (attr.AttributeClass is { IsGenericType: true } &&
                attr.AttributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).StartsWith("global::Ceras.GenerateFormatterAttribute<"))
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass.TypeArguments[0], type))
                {
                    foreach (var na in attr.NamedArguments)
                        if (na.Key == "VersionTolerant" && na.Value.Value is bool b)
                            return b;
                }
            }
        }

        return false;
    }

    static string GetStableTypeName(INamedTypeSymbol type, INamedTypeSymbol? generateFmtAttr)
    {
        if (generateFmtAttr != null)
        {
            foreach (var attr in type.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, generateFmtAttr))
                    continue;
                if (attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is string sn &&
                    !string.IsNullOrEmpty(sn))
                    return sn;
            }
        }

        var assemblyAttrs = type.ContainingAssembly.GetAttributes();
        foreach (var attr in assemblyAttrs)
        {
            if (attr.AttributeClass is { IsGenericType: true } &&
                attr.AttributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).StartsWith("global::Ceras.GenerateFormatterAttribute<"))
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass.TypeArguments[0], type))
                {
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string sn &&
                        !string.IsNullOrEmpty(sn))
                        return sn;
                }
            }
        }

        return type.Name; // simple class name without namespace
    }

    static string NormalizeName(string name)
    {
        string n = name;
        if (n.StartsWith("m_"))
            n = n.Substring(2);
        return n.TrimStart('_').ToLowerInvariant();
    }

    static TargetMemberKind? GetTargetMemberKind(INamedTypeSymbol type, INamedTypeSymbol? memberConfigAttr)
    {
        if (memberConfigAttr == null)
            return null;

        foreach (var attr in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, memberConfigAttr))
                continue;
            if (attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is int raw)
                return (TargetMemberKind)raw;
            foreach (var na in attr.NamedArguments)
                if (na.Key == "TargetMembers" && na.Value.Value is int rawNamed)
                    return (TargetMemberKind)rawNamed;
        }
        return null;
    }

    static bool IsFieldIncluded(IFieldSymbol field, TargetMemberKind kind)
    {
        bool pub = field.DeclaredAccessibility == Accessibility.Public;
        return ((kind & TargetMemberKind.PublicFields) != 0 && pub)
            || ((kind & TargetMemberKind.PrivateFields) != 0 && !pub);
    }

    static bool IsPropertyIncluded(IPropertySymbol prop, TargetMemberKind kind)
    {
        bool pub = prop.DeclaredAccessibility == Accessibility.Public;
        return ((kind & TargetMemberKind.PublicProperties) != 0 && pub)
            || ((kind & TargetMemberKind.PrivateProperties) != 0 && !pub);
    }

    static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
    {
        foreach (var attr in symbol.GetAttributes())
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
                return true;
        return false;
    }

    /// <summary>
    /// Returns true if Ceras can construct the type at runtime.
    /// Ceras can construct a class when it has:
    ///   - a public or internal parameterless constructor, OR
    ///   - any constructor marked with [CerasConstructor] (public or internal).
    /// </summary>
    static bool CanCerasConstruct(INamedTypeSymbol type, INamedTypeSymbol? cerasConstructorAttr)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method) continue;
            if (method.MethodKind != MethodKind.Constructor) continue;
            if (method.IsStatic) continue;

            // Accessibility: public or internal is reachable from generated code
            bool accessible = method.DeclaredAccessibility == Accessibility.Public
                           || method.DeclaredAccessibility == Accessibility.Internal;
            if (!accessible) continue;

            // Parameterless constructor
            if (method.Parameters.Length == 0) return true;

            // [CerasConstructor]-marked constructor (parameters satisfied via serialized members)
            if (cerasConstructorAttr != null && HasAttribute(method, cerasConstructorAttr))
                return true;
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // Per-type validation helpers (emit diagnostics, no code gen)
    // -------------------------------------------------------------------------

    /// <summary>CERAS008: member with both [Include] and [Exclude].</summary>
    static void ValidateIncludeExcludeConflicts(
        SourceProductionContext context,
        INamedTypeSymbol type,
        AttributeBag attrs)
    {
        if (attrs.Include == null || attrs.Exclude == null) return;

        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            if (member is not IFieldSymbol && member is not IPropertySymbol) continue;

            if (HasAttribute(member, attrs.Include) && HasAttribute(member, attrs.Exclude))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diag.IncludeAndExcludeOnSameMember,
                    member.Locations.FirstOrDefault(),
                    member.Name, type.Name));
            }
        }
    }

    /// <summary>CERAS004/005/006/007: validate [CerasConstructor] usage.</summary>
    static void ValidateCerasConstructor(
        SourceProductionContext context,
        Compilation compilation,
        INamedTypeSymbol type,
        List<SerializedMember> members,
        AttributeBag attrs)
    {
        if (attrs.CerasConstructor == null) return;

        // Collect all annotated constructors (static factory methods also allowed, but
        // for diagnostics we only check instance constructors here).
        var cerasCtors = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Constructor && HasAttribute(m, attrs.CerasConstructor))
            .ToList();

        // CERAS004: Multiple [CerasConstructor]
        if (cerasCtors.Count > 1)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Diag.MultipleCerasConstructors,
                type.Locations.FirstOrDefault(),
                type.Name));
            return; // Can't continue — ambiguous which ctor to analyse
        }

        if (cerasCtors.Count == 0) return;

        var ctor = cerasCtors[0];

        // CERAS007: [CerasConstructor] on parameterless ctor — no-op
        if (ctor.Parameters.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Diag.CerasConstructorOnParameterlessCtor,
                ctor.Locations.FirstOrDefault(),
                type.Name));
            return;
        }

        // Build normalized-name lookup of serialized members (same logic as DiscoverMembers)
        var memberByNorm = new Dictionary<string, SerializedMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
        {
            string norm = NormalizeName(m.Name);
            if (!memberByNorm.ContainsKey(norm))
                memberByNorm[norm] = m;
        }

        foreach (var param in ctor.Parameters)
        {
            string normParam = NormalizeName(param.Name);

            if (!memberByNorm.TryGetValue(normParam, out var matched))
            {
                // CERAS005: No matching serialized member
                context.ReportDiagnostic(Diagnostic.Create(
                    Diag.CerasConstructorParameterNotMatched,
                    ctor.Locations.FirstOrDefault(),
                    param.Name, type.Name));
            }
            else
            {
                // CERAS006: Type incompatibility between matched member and parameter
                // Use Compilation.ClassifyConversion to check if an implicit conversion exists.
                var conv = compilation.ClassifyConversion(matched.Type, param.Type);
                if (!conv.IsIdentity && !conv.IsImplicit)
                {
                    string paramTypeName = param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    string memberTypeName = matched.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diag.CerasConstructorParameterTypeMismatch,
                        ctor.Locations.FirstOrDefault(),
                        param.Name, paramTypeName, matched.Name, memberTypeName, type.Name));
                }
            }
        }
    }

    /// <summary>CERAS009: two serialized members share the same stable wire name.</summary>
    static void ValidateDuplicateWireNames(
        SourceProductionContext context,
        INamedTypeSymbol type,
        List<SerializedMember> members)
    {
        var seen = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            if (!seen.TryGetValue(m.StableName, out var list))
                seen[m.StableName] = list = new List<string>();
            list.Add(m.Name);
        }

        foreach (var kvp in seen)
        {
            if (kvp.Value.Count > 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diag.DuplicateStableWireName,
                    type.Locations.FirstOrDefault(),
                    type.Name, kvp.Key, string.Join(", ", kvp.Value)));
            }
        }
    }

    static void ValidatePartialRequirement(SourceProductionContext context, INamedTypeSymbol type, List<SerializedMember> members)
    {
        bool hasPrivate = members.Any(m => !m.IsAccessible);
        if (hasPrivate && !IsPartial(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Diag.NotPartial,
                type.Locations.FirstOrDefault(),
                type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    static void ValidateReadonlyMembers(SourceProductionContext context, INamedTypeSymbol type, List<SerializedMember> members)
    {
        foreach (var m in members)
        {
            if (m.IsReadOnly && !m.IsCtorParam && !m.IsGetterOnly)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diag.ReadonlyMemberCannotBeAssigned,
                    type.Locations.FirstOrDefault(),
                    m.Name, type.Name));
            }
        }
    }

    // -------------------------------------------------------------------------
    // Fixed-byte-size computation (used for SchemaTypeInfo.FixedByteSize)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the exact byte size of one value of <paramref name="type"/> as it appears
    /// in the data section, or 0 if the size is variable.
    ///
    /// Rules:
    ///   - Primitives: their known widths.
    ///   - string: 4 (string-table index, always fixed-width).
    ///   - Enum: size of underlying type.
    ///   - Struct: recursive sum; 0 if any field is variable.
    ///   - DateTime / TimeSpan: 8.  Guid: 16.
    ///   - Class / collections / Nullable / generics: 0 (variable).
    /// </summary>
    static int ComputeFixedByteSize(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return 1;
            case SpecialType.System_Byte: return 1;
            case SpecialType.System_SByte: return 1;
            case SpecialType.System_Char: return 2;
            case SpecialType.System_Int16: return 2;
            case SpecialType.System_UInt16: return 2;
            case SpecialType.System_Int32: return 4;
            case SpecialType.System_UInt32: return 4;
            case SpecialType.System_Single: return 4;
            case SpecialType.System_Int64: return 8;
            case SpecialType.System_UInt64: return 8;
            case SpecialType.System_Double: return 8;
            case SpecialType.System_Decimal: return 16;
            case SpecialType.System_String: return 4; // string-table index
        }

        if (type is INamedTypeSymbol named)
        {
            // Enum → size of underlying type
            if (named.TypeKind == TypeKind.Enum && named.EnumUnderlyingType != null)
                return ComputeFixedByteSize(named.EnumUnderlyingType);

            // Check well-known BCL structs by CLR name
            string clrName = named.ToDisplayString(SortKeyFormat);
            if (clrName == "System.DateTime") return 8;
            if (clrName == "System.TimeSpan") return 8;
            if (clrName == "System.Guid") return 16;

            // Other structs: recurse over instance fields
            if (named.IsValueType && named.TypeKind == TypeKind.Struct)
            {
                int total = 0;
                foreach (var member in named.GetMembers())
                {
                    if (member is not IFieldSymbol field) continue;
                    if (field.IsStatic || field.IsConst) continue;
                    int fieldSize = ComputeFixedByteSize(field.Type);
                    if (fieldSize == 0) return 0; // contains variable field
                    total += fieldSize;
                }
                return total;
            }
        }

        // Classes, collections, generics, arrays: variable
        return 0;
    }

    // -------------------------------------------------------------------------
    // Naming helpers
    // -------------------------------------------------------------------------

    static void WritePartialClassHelper(SourceProductionContext context, INamedTypeSymbol type, List<SerializedMember> members)
    {
        var privateMembers = members.Where(m => !m.IsAccessible).ToList();
        if (privateMembers.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();

        string? ns = type.ContainingNamespace?.IsGlobalNamespace == true ? null : type.ContainingNamespace?.ToDisplayString();
        if (ns != null)
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
        }

        // Handle nested types by opening each containing type as partial
        var typeStack = new Stack<INamedTypeSymbol>();
        var parent = type;
        while (parent != null)
        {
            typeStack.Push(parent);
            parent = parent.ContainingType;
        }

        int indent = ns != null ? 4 : 0;
        while (typeStack.Count > 0)
        {
            var t = typeStack.Pop();
            string kind = t.TypeKind == TypeKind.Struct ? "struct" : "class";
            sb.AppendLine($"{new string(' ', indent)}partial {kind} {t.Name}");
            sb.AppendLine($"{new string(' ', indent)}{{");
            indent += 4;
        }

        foreach (var m in privateMembers)
        {
            string typeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            
            if (m.IsField)
            {
                if (m.IsReadOnly)
                {
                    string getHelper = $"__ceras_get_{m.Name}";
                    sb.AppendLine($"{new string(' ', indent)}internal static {typeFqn} {getHelper}({type.Name} obj) => obj.{m.Name};");
                }
                else
                {
                    string helperName = $"__ceras_ref_{m.Name}";
                    sb.AppendLine($"{new string(' ', indent)}internal static ref {typeFqn} {helperName}({type.Name} obj)");
                    sb.AppendLine($"{new string(' ', indent)}{{");
                    sb.AppendLine($"{new string(' ', indent)}    return ref obj.{m.Name};");
                    sb.AppendLine($"{new string(' ', indent)}}}");
                }
            }
            else
            {
                // Property helpers
                string getHelper = $"__ceras_get_{m.Name}";
                sb.AppendLine($"{new string(' ', indent)}internal static {typeFqn} {getHelper}({type.Name} obj) => obj.{m.Name};");

                if (!m.IsGetterOnly)
                {
                    string setHelper = $"__ceras_set_{m.Name}";
                    sb.AppendLine($"{new string(' ', indent)}internal static void {setHelper}({type.Name} obj, {typeFqn} value) => obj.{m.Name} = value;");
                }
            }
        }

        // Close all types
        while (indent > (ns != null ? 4 : 0))
        {
            indent -= 4;
            sb.AppendLine($"{new string(' ', indent)}}}");
        }

        if (ns != null)
        {
            sb.AppendLine("}");
        }

        string fileName = $"{SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal))}.Ceras.g.cs";
        context.AddSource(fileName, SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    static string GetGetAccess(SerializedMember m, string typeFqn)
    {
        if (m.IsAccessible)
            return $"value.{m.Name}";
        if (m.IsField && !m.IsReadOnly)
            return $"{typeFqn}.__ceras_ref_{m.Name}(value)";
        return $"{typeFqn}.__ceras_get_{m.Name}(value)";
    }

    static string? GetRefAccess(SerializedMember m, string typeFqn)
    {
        if (!m.IsField || m.IsReadOnly)
            return null;
        if (m.IsAccessible)
            return $"value.{m.Name}";
        return $"{typeFqn}.__ceras_ref_{m.Name}(value)";
    }

    static void WriteSetStatement(StringBuilder sb, SerializedMember m, string typeFqn, string valueExpr, int indent)
    {
        if (m.IsReadOnly)
            return;

        string prefix = new string(' ', indent);
        if (m.IsAccessible)
        {
            sb.AppendLine($"{prefix}value.{m.Name} = {valueExpr};");
        }
        else if (m.IsField)
        {
            sb.AppendLine($"{prefix}{typeFqn}.__ceras_ref_{m.Name}(value) = {valueExpr};");
        }
        else
        {
            sb.AppendLine($"{prefix}{typeFqn}.__ceras_set_{m.Name}(value, {valueExpr});");
        }
    }

    static string MakeFormatterClassName(INamedTypeSymbol type)
        => SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal)) + "Formatter";

    static string MakeFormatterFieldName(ITypeSymbol type)
    {
        string raw = SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal));
        // We use the full sanitized name to avoid collisions between types with same name in different namespaces
        return "_" + raw + "Formatter";
    }

    static string SanitizeIdentifier(string displayName)
    {
        var sb = new StringBuilder(displayName.Length);
        foreach (char c in displayName)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
            else
                sb.Append('_');
        }
        
        // Remove consecutive underscores
        string result = sb.ToString();
        while (result.Contains("__"))
            result = result.Replace("__", "_");
            
        return result.Trim('_');
    }

    // -------------------------------------------------------------------------
    // String-literal helpers for generated code
    // -------------------------------------------------------------------------

    static string QuoteString(string s) => $"\"{EscapeString(s)}\"";

    static string EscapeString(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r");

    static void WriteStringArray(StringBuilder sb, IReadOnlyList<string> items)
    {
        if (items.Count == 0) { sb.Append("global::System.Array.Empty<string>()"); return; }
        sb.Append("new string[] { ");
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(QuoteString(items[i]));
        }
        sb.Append(" }");
    }

    static void WriteStringArrayInline(StringBuilder sb, string[] items)
        => WriteStringArray(sb, items);

    // -------------------------------------------------------------------------
    // Display formats
    // -------------------------------------------------------------------------

    // For class/field names in generated code: uses C# keyword aliases (float, int, string).
    static readonly SymbolDisplayFormat FullyQualifiedNoGlobal = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    // For sort keys and schema type names: CLR names (System.Single, not float).
    // Must match SchemaMemberComparer.GetComparisonName which uses MemberType.FullName.
    static readonly SymbolDisplayFormat SortKeyFormat = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.None);

    // -------------------------------------------------------------------------
    // Data types
    // -------------------------------------------------------------------------

    /// <summary>Bundles all resolved attribute symbols to avoid threading them individually.</summary>
    sealed class AttributeBag
    {
        public readonly INamedTypeSymbol? Include;
        public readonly INamedTypeSymbol? Exclude;
        public readonly INamedTypeSymbol? MemberConfig;
        public readonly INamedTypeSymbol? PreviousName;
        public readonly INamedTypeSymbol? PreviousTypeName;
        public readonly INamedTypeSymbol? SerializedAs;
        public readonly INamedTypeSymbol? Monomorphic;
        public readonly INamedTypeSymbol? Polymorphic;
        public readonly INamedTypeSymbol? GenerateFormatter;
        public readonly INamedTypeSymbol? CerasConstructor;

        public AttributeBag(
            INamedTypeSymbol? include, INamedTypeSymbol? exclude, INamedTypeSymbol? memberConfig,
            INamedTypeSymbol? previousName, INamedTypeSymbol? previousTypeName,
            INamedTypeSymbol? serializedAs, INamedTypeSymbol? monomorphic,
            INamedTypeSymbol? polymorphic, INamedTypeSymbol? generateFormatter,
            INamedTypeSymbol? cerasConstructor = null)
        {
            Include = include;
            Exclude = exclude;
            MemberConfig = memberConfig;
            PreviousName = previousName;
            PreviousTypeName = previousTypeName;
            SerializedAs = serializedAs;
            Monomorphic = monomorphic;
            Polymorphic = polymorphic;
            GenerateFormatter = generateFormatter;
            CerasConstructor = cerasConstructor;
        }
    }

    sealed class SerializedMember
    {
        public string Name { get; } // C# member name
        public string StableName { get; } // wire name ([SerializedAs] or Name)
        public ITypeSymbol Type { get; }
        public bool IsField { get; }
        public string[] PreviousNames { get; }
        public bool IsMonomorphic { get; }
        /// <summary>True for getter-only collection properties; populated in-place without assignment.</summary>
        public bool IsGetterOnly { get; }
        /// <summary>True when this member maps to a [CerasConstructor] parameter; value is passed to ctor on deserialize, never assigned post-construction.</summary>
        public bool IsCtorParam { get; }
        /// <summary>The exact constructor parameter name (case-sensitive) used when emitting named-argument ctor calls. Only meaningful when IsCtorParam = true.</summary>
        public string CtorParamName { get; }
        public bool IsAccessible { get; }
        public bool IsReadOnly { get; }

        public SerializedMember(
            string name, string stableName, ITypeSymbol type, bool isField,
            string[] previousNames, bool isMonomorphic, bool isAccessible,
            bool isReadOnly,
            bool isGetterOnly = false,
            bool isCtorParam = false, string ctorParamName = null)
        {
            Name = name;
            StableName = stableName;
            Type = type;
            IsField = isField;
            PreviousNames = previousNames;
            IsMonomorphic = isMonomorphic;
            IsAccessible = isAccessible;
            IsReadOnly = isReadOnly;
            IsGetterOnly = isGetterOnly;
            IsCtorParam = isCtorParam;
            CtorParamName = ctorParamName ?? stableName;
        }
    }

    [Flags]
    enum TargetMemberKind
    {
        None = 0,
        PublicFields = 1,
        PrivateFields = 2,
        PublicProperties = 4,
        PrivateProperties = 8,
        AllPublic = PublicFields | PublicProperties,
        AllPrivate = PrivateFields | PrivateProperties,
        All = AllPublic | AllPrivate,
    }
}

/// <summary>Diagnostic descriptors produced by the generator.</summary>
static class Diag
{
    public static readonly DiagnosticDescriptor NoSerializableMembers = new(
        id: "CERAS001",
        title: "No serializable members found",
        messageFormat: "Type '{0}' has [GenerateFormatter] but no serializable members were found. Check [Include]/[Exclude] attributes and TargetMembers config.",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InterfaceOrAbstractWithoutDiscoverSubtypes = new(
        id: "CERAS002",
        title: "[GenerateFormatter] on interface/abstract type without DiscoverSubtypes",
        messageFormat: "'{0}' is an {1} with [GenerateFormatter] but DiscoverSubtypes is not set to true. " +
                       "No concrete subtypes will be automatically registered for serialization through this type. " +
                       "Add DiscoverSubtypes = true to have the generator find all implementations automatically.",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotConstructable = new(
        id: "CERAS003",
        title: "Serializable type has no accessible constructor",
        messageFormat: "Type '{0}' is included in serialization but Ceras cannot construct it: " +
                       "there is no accessible (public or internal) parameterless constructor and no constructor marked [CerasConstructor]. " +
                       "Add a parameterless constructor or mark one constructor with [CerasConstructor].",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MultipleCerasConstructors = new(
        id: "CERAS004",
        title: "Multiple [CerasConstructor] attributes on the same type",
        messageFormat: "Type '{0}' has more than one constructor or factory method marked with [CerasConstructor]. " +
                       "Only one is allowed — Ceras will throw at runtime if multiple are found. " +
                       "Remove [CerasConstructor] from all but one.",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CerasConstructorParameterNotMatched = new(
        id: "CERAS005",
        title: "[CerasConstructor] parameter has no matching serialized member",
        messageFormat: "Constructor parameter '{0}' on type '{1}' cannot be matched to any serialized member. " +
                       "Ceras matches parameters to members by name (case-insensitive, ignoring leading underscores). " +
                       "Either the member is not serialized, does not exist, or has a different name. " +
                       "Add a serialized member whose name matches '{0}', or rename the parameter.",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CerasConstructorParameterTypeMismatch = new(
        id: "CERAS006",
        title: "[CerasConstructor] parameter type is incompatible with its matched member",
        messageFormat: "Constructor parameter '{0}' (type '{1}') on type '{3}' was matched to member '{2}' (type '{4}') by name, " +
                       "but the types are not compatible — this will cause a compile error in the generated formatter. " +
                       "Make the parameter and member types match, or rename one to break the unintended name match.",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CerasConstructorOnParameterlessCtor = new(
        id: "CERAS007",
        title: "[CerasConstructor] on parameterless constructor has no effect",
        messageFormat: "Type '{0}' has [CerasConstructor] on a parameterless constructor. " +
                       "Ceras already prefers a parameterless constructor by default — the attribute has no effect here. " +
                       "Remove [CerasConstructor] or mark a constructor that takes parameters if you need injection.",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor IncludeAndExcludeOnSameMember = new(
        id: "CERAS008",
        title: "Member has both [Include] and [Exclude]",
        messageFormat: "Member '{0}' on type '{1}' has both [Include] and [Exclude] applied. " +
                       "Ceras will throw at runtime when it encounters this. Remove one of the two attributes.",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateStableWireName = new(
        id: "CERAS009",
        title: "Duplicate stable wire name on serialized members",
        messageFormat: "Type '{0}' has multiple serialized members that share the stable wire name '{1}' " +
                       "(members: {2}). This will cause corrupt data or a runtime exception. " +
                       "Use [SerializedAs(\"uniqueName\")] on one of them to give it a distinct name.",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotPartial = new(
        id: "CERAS010",
        title: "Type must be partial to support private member serialization",
        messageFormat: "Type '{0}' has private or protected members marked for serialization but is not declared as 'partial'. " +
                       "Make the type partial so Ceras can inject access helpers for these members.",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReadonlyMemberCannotBeAssigned = new(
        id: "CERAS011",
        title: "Read-only member cannot be assigned",
        messageFormat: "Member '{0}' on type '{1}' is read-only and cannot be assigned to during deserialization. " +
                       "Read-only members must be part of a [CerasConstructor] to be correctly initialized.",
        category: "Ceras.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
