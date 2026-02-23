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
            polymorphicAttr, generateFmtAttr);

        foreach (var type in finalTypes)
        {
            // Interfaces and abstract types with DiscoverSubtypes = true are roots only;
            // they are never instantiated so no formatter class is needed for them.
            if (type.TypeKind == TypeKind.Interface || type.IsAbstract)
                continue;

            var members = DiscoverMembers(type, attrBag);

            if (members.Count == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diag.NoSerializableMembers,
                    type.Locations.FirstOrDefault(),
                    type.Name));
            }

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
        foreach (var t in typeChain)
        {
            TargetMemberKind targetKind = GetTargetMemberKind(t, attrs.MemberConfig);

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

                // Generated formatters are in a separate class; they cannot access private members.
                // Users must declare fields/properties as internal or public to be serializable.
                if (member.DeclaredAccessibility == Accessibility.Private ||
                    member.DeclaredAccessibility == Accessibility.Protected)
                    continue;

                if (member is IFieldSymbol field)
                {
                    // [Include] overrides the readonly check; Ceras can populate readonly fields
                    // only if the containing type has a formatter that sets them directly.
                    // For generated formatters, we allow it with [Include].
                    if (field.IsConst || (field.IsReadOnly && !hasInclude)) continue;
                    if (!hasInclude && !IsFieldIncluded(field, targetKind)) continue;

                    members.Add(MakeMember(field.Name, field.Type, isField: true, member, attrs, isGetterOnly: false));
                }
                else if (member is IPropertySymbol prop)
                {
                    // Skip write-only and indexers
                    if (prop.IsWriteOnly || prop.IsIndexer) continue;
                    if (prop.GetMethod == null) continue;

                    bool isGetterOnly = prop.IsReadOnly || prop.SetMethod == null;

                    if (isGetterOnly)
                    {
                        // Getter-only COLLECTION properties can be populated in-place by Ceras.
                        // All other getter-only properties require a setter.
                        if (!hasInclude && !IsCollectionType(prop.Type)) continue;
                    }
                    else
                    {
                        // Skip properties whose setter is not accessible (private/protected) —
                        // generated code cannot call them.
                        if (prop.SetMethod!.DeclaredAccessibility != Accessibility.Public
                            && prop.SetMethod.DeclaredAccessibility != Accessibility.Internal)
                            continue;
                    }

                    if (!hasInclude && !IsPropertyIncluded(prop, targetKind)) continue;

                    members.Add(MakeMember(prop.Name, prop.Type, isField: false, member, attrs, isGetterOnly: isGetterOnly));
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

        return new SerializedMember(name, stableName, type, isField,
            previousNames.ToArray(), isMonomorphic, isGetterOnly);
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

        sb.AppendLine($"    internal sealed class {formatterName} : IFormatter<{typeFqn}>");
        sb.AppendLine("    {");

        // --- Formatter fields (one per distinct member type) -------------------
        var seenFieldNames = new HashSet<string>();
        foreach (var m in members)
        {
            if (IsPrimitive(m.Type)) continue;

            string fieldName = MakeFormatterFieldName(m.Type);
            if (!seenFieldNames.Add(fieldName)) continue;

            string concreteFormatterType = GetConcreteFormatterType(m.Type, m.IsMonomorphic);
            sb.AppendLine($"        readonly {concreteFormatterType} {fieldName};");
        }
        sb.AppendLine();

        // --- Schema descriptor (VersionTolerant only) --------------------------
        if (vt)
            WriteSchemaDescriptor(sb, type, members, attrs);

        // --- SerializeUntyped --------------------------------------------------
        sb.AppendLine($"        public void SerializeUntyped(IBufferWriter<byte> writer, object value)");
        sb.AppendLine("        {");
        sb.AppendLine($"            Serialize(writer, ({typeFqn})value);");
        sb.AppendLine("        }");
        sb.AppendLine();

        // --- DeserializeUntyped ------------------------------------------------
        sb.AppendLine($"        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, global::System.Reflection.MemberInfo member)");
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
        sb.AppendLine($"        public void Serialize(IBufferWriter<byte> writer, {typeFqn} value)");
        sb.AppendLine("        {");
        foreach (var m in members)
        {
            string fieldName = MakeFormatterFieldName(m.Type);
            if (IsPrimitive(m.Type))
            {
                string writeMethod = GetPrimitiveWriteMethod(m.Type);
                string valueExp = $"value.{m.Name}";
                if (m.Type.SpecialType == SpecialType.System_Boolean) valueExp = $"{valueExp} ? 1 : 0";
                sb.AppendLine($"            SerializerBinary.{writeMethod}(writer, {valueExp});");
            }
            else
            {
                sb.AppendLine($"            {fieldName}.Serialize(writer, value.{m.Name});");
            }
        }
        sb.AppendLine("        }");
        sb.AppendLine();

        // --- Deserialize -------------------------------------------------------
        sb.AppendLine($"        public void Deserialize(ref SequenceReader<byte> reader, ref {typeFqn} value)");
        sb.AppendLine("        {");
        foreach (var m in members)
        {
            string fieldName = MakeFormatterFieldName(m.Type);
            string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (IsPrimitive(m.Type))
            {
                string readMethod = GetPrimitiveReadMethod(m.Type);
                sb.AppendLine($"            value.{m.Name} = SerializerBinary.{readMethod};");
            }
            else
            {
                if (m.IsField)
                {
                    sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref value.{m.Name});");
                }
                else if (m.IsGetterOnly)
                {
                    // Getter-only collection: populate the existing instance in-place (no assignment needed)
                    string tmp = "_tmp" + m.Name;
                    sb.AppendLine($"            {memberTypeFqn} {tmp} = value.{m.Name};");
                    sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref {tmp});");
                }
                else
                {
                    string tmp = "_tmp" + m.Name;
                    sb.AppendLine($"            {memberTypeFqn} {tmp} = value.{m.Name};");
                    sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref {tmp});");
                    sb.AppendLine($"            value.{m.Name} = {tmp};");
                }
            }
        }
        sb.AppendLine("        }");

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static bool IsPrimitive(ITypeSymbol type)
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
            case SpecialType.System_String:
                return true;
        }
        return false;
    }

    static string GetPrimitiveWriteMethod(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return "WriteInt32"; // Bool is written as Int32 VarInt in BoolFormatter
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
            case SpecialType.System_String: return "WriteString";
        }
        throw new NotSupportedException();
    }

    static string GetPrimitiveReadMethod(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return "ReadInt32(ref reader) != 0";
            case SpecialType.System_Byte: return "ReadByte(ref reader)";
            case SpecialType.System_SByte: return "(sbyte)SerializerBinary.ReadByte(ref reader)";
            case SpecialType.System_Char: return "(char)SerializerBinary.ReadInt16Fixed(ref reader)";
            case SpecialType.System_Int16: return "ReadInt16Fixed(ref reader)";
            case SpecialType.System_UInt16: return "(ushort)SerializerBinary.ReadInt16Fixed(ref reader)";
            case SpecialType.System_Int32: return "ReadInt32(ref reader)";
            case SpecialType.System_UInt32: return "ReadUInt32(ref reader)";
            case SpecialType.System_Single: return "ReadFloat32Fixed(ref reader)";
            case SpecialType.System_Int64: return "ReadInt64Fixed(ref reader)";
            case SpecialType.System_UInt64: return "(ulong)SerializerBinary.ReadInt64Fixed(ref reader)";
            case SpecialType.System_Double: return "ReadDouble64Fixed(ref reader)";
            case SpecialType.System_String: return "ReadString(ref reader)";
        }
        throw new NotSupportedException();
    }

    static string GetConcreteFormatterType(ITypeSymbol type, bool isMonomorphic)
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
            case SpecialType.System_String: return "global::Ceras.Formatters.StringFormatter";
        }

        if (typeFqn == "global::System.DateTime") return "global::Ceras.Resolvers.StandardFormatterResolver.DateTimeFormatter";
        if (typeFqn == "global::System.TimeSpan") return "global::Ceras.Resolvers.StandardFormatterResolver.TimeSpanFormatter";
        if (typeFqn == "global::System.Guid") return "global::Ceras.Resolvers.StandardFormatterResolver.GuidFormatter"; // Wait, GuidFormatter?

        // Arrays
        if (type is IArrayTypeSymbol arr)
        {
            // For now use IFormatter for arrays unless we want to commit to a specific array formatter
            return $"IFormatter<{typeFqn}>";
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
        return $"IFormatter<{typeFqn}>";
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

    static TargetMemberKind GetTargetMemberKind(INamedTypeSymbol type, INamedTypeSymbol? memberConfigAttr)
    {
        if (memberConfigAttr == null)
            return TargetMemberKind.AllPublic; // default: public fields + public properties

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
        return TargetMemberKind.AllPublic;
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

        public AttributeBag(
            INamedTypeSymbol? include, INamedTypeSymbol? exclude, INamedTypeSymbol? memberConfig,
            INamedTypeSymbol? previousName, INamedTypeSymbol? previousTypeName,
            INamedTypeSymbol? serializedAs, INamedTypeSymbol? monomorphic,
            INamedTypeSymbol? polymorphic, INamedTypeSymbol? generateFormatter)
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

        public SerializedMember(
            string name, string stableName, ITypeSymbol type, bool isField,
            string[] previousNames, bool isMonomorphic, bool isGetterOnly = false)
        {
            Name = name;
            StableName = stableName;
            Type = type;
            IsField = isField;
            PreviousNames = previousNames;
            IsMonomorphic = isMonomorphic;
            IsGetterOnly = isGetterOnly;
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
}
