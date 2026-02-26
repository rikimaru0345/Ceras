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
        public const string CerasSerializableFqn = "Ceras.CerasSerializableAttribute";
		public const string ExcludeFromDiscoveryFqn = "Ceras.ExcludeAttribute";
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

        /// <summary>
        /// Peels away Nullable&lt;T&gt;, arrays, and well-known generic collections (List&lt;T&gt;, HashSet&lt;T&gt;,
        /// Dictionary&lt;,&gt;, Queue&lt;T&gt;, Stack&lt;T&gt;) to get to the element/value type that Ceras actually
        /// needs a polymorphic formatter for.  Returns null if the type cannot be meaningfully unwrapped.
        /// </summary>
        public static INamedTypeSymbol? UnwrapCollectionsAndNullable(Compilation compilation, ITypeSymbol type)
        {
            // Unwrap arrays: T[] -> T
            if (type is IArrayTypeSymbol arr)
                return UnwrapCollectionsAndNullable(compilation, arr.ElementType);

            if (type is not INamedTypeSymbol named) return null;

            // Nullable<T> -> T
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && named.TypeArguments.Length == 1)
                return UnwrapCollectionsAndNullable(compilation, named.TypeArguments[0]);

            // Well-known single-element generic collections: List<T>, HashSet<T>, Queue<T>, Stack<T>
            if (named.IsGenericType && named.TypeArguments.Length == 1)
            {
                var listDef    = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
                var hashSetDef = compilation.GetTypeByMetadataName("System.Collections.Generic.HashSet`1");
                var queueDef   = compilation.GetTypeByMetadataName("System.Collections.Generic.Queue`1");
                var stackDef   = compilation.GetTypeByMetadataName("System.Collections.Generic.Stack`1");

                var def = named.OriginalDefinition;
                if (SymbolEqualityComparer.Default.Equals(def, listDef) ||
                    SymbolEqualityComparer.Default.Equals(def, hashSetDef) ||
                    SymbolEqualityComparer.Default.Equals(def, queueDef) ||
                    SymbolEqualityComparer.Default.Equals(def, stackDef))
                {
                    return UnwrapCollectionsAndNullable(compilation, named.TypeArguments[0]);
                }
            }

            // Dictionary<K,V>: unwrap the value type (keys are rarely polymorphic user types)
            if (named.IsGenericType && named.TypeArguments.Length == 2)
            {
                var dictDef = compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
                if (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, dictDef))
                {
                    return UnwrapCollectionsAndNullable(compilation, named.TypeArguments[1]);
                }
            }

            return named;
        }

        /// <summary>
        /// Returns true for types that Ceras handles natively (System.* well-known types) and therefore
        /// don't need a user-side [CerasSerializable] entry in the discovery graph.
        /// </summary>
        public static bool IsWellKnownOrBanned(INamedTypeSymbol type)
        {
            if (type.SpecialType != SpecialType.None) return true; // primitives, string, object, …
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return fqn.StartsWith("global::System.");
        }
    }
}
