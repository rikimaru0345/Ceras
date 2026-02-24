using System;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Ceras.SourceGenerator
{
    internal static class TypeHelpers
    {
        // --- Attributes ---
        public const string IncludeFqn = "Ceras.IncludeAttribute";
        public const string ExcludeFqn = "Ceras.ExcludeAttribute";
        public const string MemberConfigFqn = "Ceras.MemberConfigAttribute";
        public const string PreviousNameFqn = "Ceras.PreviousNameAttribute";
        public const string PreviousTypeNameFqn = "Ceras.PreviousTypeNameAttribute";
        public const string SerializedAsFqn = "Ceras.CerasStableNameAttribute";
        public const string MonomorphicFqn = "Ceras.MonomorphicAttribute";
        public const string PolymorphicFqn = "Ceras.PolymorphicAttribute";
        public const string GenerateFormatterFqn = "Ceras.CerasSerializableAttribute";
        public const string ExcludeFromDiscoveryFqn = "Ceras.ExcludeFromDiscoveryAttribute";
        public const string BlacklistFqn = "Ceras.CerasGeneratorBlacklistAttribute";
        public const string CerasConstructorFqn = "Ceras.CerasConstructorAttribute";

        // --- Naming ---
        public static readonly SymbolDisplayFormat FullyQualifiedNoGlobal = new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandNullable);

        public static readonly SymbolDisplayFormat SortKeyFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

        public static string MakeFormatterClassName(INamedTypeSymbol type)
            => SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal)) + "Formatter";

        public static string MakeFormatterFieldName(ITypeSymbol type)
        {
            string raw = SanitizeIdentifier(type.ToDisplayString(FullyQualifiedNoGlobal));
            return "_" + raw + "Formatter";
        }

        public static string SanitizeIdentifier(string displayName)
        {
            var sb = new StringBuilder(displayName.Length);
            foreach (char c in displayName)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }

        public static string GetRobustName(ITypeSymbol type)
        {
            var sb = new StringBuilder();
            BuildRobustName(sb, type);
            return sb.ToString();
        }

        static void BuildRobustName(StringBuilder sb, ITypeSymbol type)
        {
            if (type.ContainingType != null)
            {
                BuildRobustName(sb, type.ContainingType);
                sb.Append('+');
            }
            else if (type.ContainingNamespace != null && !type.ContainingNamespace.IsGlobalNamespace)
            {
                sb.Append(type.ContainingNamespace.ToDisplayString());
                sb.Append('.');
            }

            sb.Append(type.MetadataName);
        }

        public static string QuoteString(string s) => s == null ? "null" : $"\"{s}\"";

        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string norm = name.ToLowerInvariant();
            if (norm.StartsWith("_")) norm = norm.Substring(1);
            return norm;
        }

        // --- Semantic Checks ---
        public static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType)
        {
            if (attributeType == null) return false;
            foreach (var attr in symbol.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
                    return true;
            }
            return false;
        }
    }
}
