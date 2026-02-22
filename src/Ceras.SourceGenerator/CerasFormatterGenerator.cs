using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

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
    const string GenerateFormatterFqn  = "Ceras.GenerateFormatterAttribute";
    const string IncludeFqn            = "Ceras.IncludeAttribute";
    const string ExcludeFqn            = "Ceras.ExcludeAttribute";
    const string MemberConfigFqn       = "Ceras.MemberConfigAttribute";
    const string PreviousNameFqn       = "Ceras.PreviousNameAttribute";
    const string PreviousTypeNameFqn   = "Ceras.PreviousTypeNameAttribute";
    const string SerializedAsFqn       = "Ceras.SerializedAsAttribute";
    const string MonomorphicFqn        = "Ceras.MonomorphicAttribute";
    const string PolymorphicFqn        = "Ceras.PolymorphicAttribute";

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
        var includeAttr        = compilation.GetTypeByMetadataName(IncludeFqn);
        var excludeAttr        = compilation.GetTypeByMetadataName(ExcludeFqn);
        var memberConfigAttr   = compilation.GetTypeByMetadataName(MemberConfigFqn);
        var previousNameAttr   = compilation.GetTypeByMetadataName(PreviousNameFqn);
        var previousTypeAttr   = compilation.GetTypeByMetadataName(PreviousTypeNameFqn);
        var serializedAsAttr   = compilation.GetTypeByMetadataName(SerializedAsFqn);
        var monomorphicAttr    = compilation.GetTypeByMetadataName(MonomorphicFqn);
        var polymorphicAttr    = compilation.GetTypeByMetadataName(PolymorphicFqn);
        var generateFmtAttr    = compilation.GetTypeByMetadataName(GenerateFormatterFqn);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// ReSharper disable All");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("#pragma warning disable 649");
        sb.AppendLine();
        sb.AppendLine("using Ceras;");
        sb.AppendLine("using Ceras.Formatters;");
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

        foreach (var type in types)
        {
            string typeFqn       = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string formatterName = MakeFormatterClassName(type);
            bool   vt            = IsVersionTolerant(type, generateFmtAttr);

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

        foreach (var type in types)
        {
            var members = DiscoverMembers(type, attrBag);

            if (members.Count == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diag.NoSerializableMembers,
                    type.Locations.FirstOrDefault(),
                    type.Name));
            }

            WriteFormatterClass(sb, type, members, attrBag);
        }

        sb.AppendLine("}"); // close namespace
        sb.AppendLine("#nullable restore");
        sb.AppendLine("#pragma warning restore 649");

        context.AddSource("CerasGeneratedFormatters.g.cs",
            SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    // -------------------------------------------------------------------------
    // Member discovery
    // -------------------------------------------------------------------------

    static List<SerializedMember> DiscoverMembers(INamedTypeSymbol type, AttributeBag attrs)
    {
        TargetMemberKind targetKind = GetTargetMemberKind(type, attrs.MemberConfig);
        var members = new List<SerializedMember>();

        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared)
                continue;

            bool hasInclude = attrs.Include != null && HasAttribute(member, attrs.Include);
            bool hasExclude = attrs.Exclude != null && HasAttribute(member, attrs.Exclude);

            if (hasExclude) continue;

            if (member is IFieldSymbol field)
            {
                if (field.IsConst || field.IsReadOnly) continue;
                if (!hasInclude && !IsFieldIncluded(field, targetKind)) continue;

                members.Add(MakeMember(field.Name, field.Type, isField: true, member, attrs));
            }
            else if (member is IPropertySymbol prop)
            {
                if (prop.IsReadOnly || prop.IsWriteOnly || prop.IsIndexer) continue;
                if (prop.GetMethod == null || prop.SetMethod == null) continue;
                if (!hasInclude && !IsPropertyIncluded(prop, targetKind)) continue;

                members.Add(MakeMember(prop.Name, prop.Type, isField: false, member, attrs));
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

    static SerializedMember MakeMember(
        string name, ITypeSymbol type, bool isField,
        ISymbol symbol, AttributeBag attrs)
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
            previousNames.ToArray(), isMonomorphic);
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
        AttributeBag attrs)
    {
        string typeFqn       = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string formatterName = MakeFormatterClassName(type);
        bool   vt            = IsVersionTolerant(type, attrs.GenerateFormatter);

        sb.AppendLine($"    internal sealed class {formatterName} : IFormatter<{typeFqn}>");
        sb.AppendLine("    {");

        // --- Formatter fields (one per distinct member type) -------------------
        var seenFieldNames = new HashSet<string>();
        foreach (var m in members)
        {
            string fieldName = MakeFormatterFieldName(m.Type);
            if (!seenFieldNames.Add(fieldName)) continue;

            string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"        IFormatter<{memberTypeFqn}> {fieldName};");
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
            sb.AppendLine($"            {fieldName}.Serialize(writer, value.{m.Name});");
        }
        sb.AppendLine("        }");
        sb.AppendLine();

        // --- Deserialize -------------------------------------------------------
        sb.AppendLine($"        public void Deserialize(ref SequenceReader<byte> reader, ref {typeFqn} value)");
        sb.AppendLine("        {");
        foreach (var m in members)
        {
            string fieldName     = MakeFormatterFieldName(m.Type);
            string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (m.IsField)
            {
                sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref value.{m.Name});");
            }
            else
            {
                string tmp = "_tmp" + m.Name;
                sb.AppendLine($"            {memberTypeFqn} {tmp} = value.{m.Name};");
                sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref {tmp});");
                sb.AppendLine($"            value.{m.Name} = {tmp};");
            }
        }
        sb.AppendLine("        }");

        sb.AppendLine("    }");
        sb.AppendLine();
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
        sb.Append    ($"                previousTypeNames: ");
        WriteStringArray(sb, prevTypeNames);
        sb.AppendLine($",");
        sb.AppendLine($"                kind: {kind},");
        sb.AppendLine($"                fields: new global::Ceras.Savegame.SchemaFieldInfo[]");
        sb.AppendLine($"                {{");
        foreach (var m in members)
        {
            string typeName = m.Type.ToDisplayString(SortKeyFormat); // CLR name, no global::
            sb.Append    ($"                    new global::Ceras.Savegame.SchemaFieldInfo(");
            sb.Append    ($"{QuoteString(m.StableName)}, ");
            WriteStringArrayInline(sb, m.PreviousNames);
            sb.Append    ($", {QuoteString(typeName)}, {(m.IsMonomorphic ? "true" : "false")}");
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
            return TargetMemberKind.PublicFields;

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
        return TargetMemberKind.PublicFields;
    }

    static bool IsFieldIncluded(IFieldSymbol field, TargetMemberKind kind)
    {
        bool pub = field.DeclaredAccessibility == Accessibility.Public;
        return ((kind & TargetMemberKind.PublicFields)  != 0 && pub)
            || ((kind & TargetMemberKind.PrivateFields) != 0 && !pub);
    }

    static bool IsPropertyIncluded(IPropertySymbol prop, TargetMemberKind kind)
    {
        bool pub = prop.DeclaredAccessibility == Accessibility.Public;
        return ((kind & TargetMemberKind.PublicProperties)  != 0 && pub)
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
            case SpecialType.System_Byte:    return 1;
            case SpecialType.System_SByte:   return 1;
            case SpecialType.System_Char:    return 2;
            case SpecialType.System_Int16:   return 2;
            case SpecialType.System_UInt16:  return 2;
            case SpecialType.System_Int32:   return 4;
            case SpecialType.System_UInt32:  return 4;
            case SpecialType.System_Single:  return 4;
            case SpecialType.System_Int64:   return 8;
            case SpecialType.System_UInt64:  return 8;
            case SpecialType.System_Double:  return 8;
            case SpecialType.System_Decimal: return 16;
            case SpecialType.System_String:  return 4; // string-table index
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
            if (clrName == "System.Guid")     return 16;

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
        string raw   = SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal));
        string lower = char.ToLowerInvariant(raw[0]) + raw.Substring(1);
        return "_" + lower + "Formatter";
    }

    static string SanitizeIdentifier(string displayName)
    {
        var sb = new StringBuilder(displayName.Length);
        foreach (char c in displayName)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
        }
        return sb.Length == 0 ? "Unknown" : sb.ToString();
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
        globalNamespaceStyle:   SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions:        SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:   SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    // For sort keys and schema type names: CLR names (System.Single, not float).
    // Must match SchemaMemberComparer.GetComparisonName which uses MemberType.FullName.
    static readonly SymbolDisplayFormat SortKeyFormat = new SymbolDisplayFormat(
        globalNamespaceStyle:   SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions:        SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:   SymbolDisplayMiscellaneousOptions.None);

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
            Include          = include;
            Exclude          = exclude;
            MemberConfig     = memberConfig;
            PreviousName     = previousName;
            PreviousTypeName = previousTypeName;
            SerializedAs     = serializedAs;
            Monomorphic      = monomorphic;
            Polymorphic      = polymorphic;
            GenerateFormatter = generateFormatter;
        }
    }

    sealed class SerializedMember
    {
        public string     Name         { get; } // C# member name
        public string     StableName   { get; } // wire name ([SerializedAs] or Name)
        public ITypeSymbol Type        { get; }
        public bool       IsField      { get; }
        public string[]   PreviousNames { get; }
        public bool       IsMonomorphic { get; }

        public SerializedMember(
            string name, string stableName, ITypeSymbol type, bool isField,
            string[] previousNames, bool isMonomorphic)
        {
            Name          = name;
            StableName    = stableName;
            Type          = type;
            IsField       = isField;
            PreviousNames = previousNames;
            IsMonomorphic = isMonomorphic;
        }
    }

    [Flags]
    enum TargetMemberKind
    {
        None              = 0,
        PublicFields      = 1,
        PrivateFields     = 2,
        PublicProperties  = 4,
        PrivateProperties = 8,
        AllPublic         = PublicFields | PublicProperties,
        AllPrivate        = PrivateFields | PrivateProperties,
        All               = AllPublic | AllPrivate,
    }
}

/// <summary>Diagnostic descriptors produced by the generator.</summary>
static class Diag
{
    public static readonly DiagnosticDescriptor NoSerializableMembers = new(
        id:                 "CERAS001",
        title:              "No serializable members found",
        messageFormat:      "Type '{0}' has [GenerateFormatter] but no serializable members were found. Check [Include]/[Exclude] attributes and TargetMembers config.",
        category:           "Ceras.SourceGenerator",
        defaultSeverity:    DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
