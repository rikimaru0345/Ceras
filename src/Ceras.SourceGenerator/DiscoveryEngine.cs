using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using static Ceras.SourceGenerator.TypeHelpers;
using static Ceras.SourceGenerator.MemberExplorer;

namespace Ceras.SourceGenerator
{
    internal static class DiscoveryEngine
    {
        public static Dictionary<INamedTypeSymbol, TypeDiscoveryResult> DiscoverAllTypes(
            SourceProductionContext context,
            Compilation compilation,
            ImmutableArray<INamedTypeSymbol> types,
            AttributeBag attrs,
            INamedTypeSymbol? excludeFromDiscoveryAttr,
            List<string> blacklistPatterns)
        {
            var results = new Dictionary<INamedTypeSymbol, TypeDiscoveryResult>(SymbolEqualityComparer.Default);
            var queue = new Queue<INamedTypeSymbol>();

            // 1. Initial roots from [CerasSerializable]
            foreach (var t in types)
            {
                if (results.ContainsKey(t)) continue;

                var result = new TypeDiscoveryResult(t, isVersionTolerant: true, discoveryPath: null);
                results[t] = result;
                queue.Enqueue(t);
            }

            // 2. Discover subtypes for roots with DiscoverSubtypes = true (default)
            var discoverRoots = types.Where(t => HasDiscoverSubtypes(t, attrs.GenerateFormatter)).ToList();
            if (discoverRoots.Count > 0)
            {
                foreach (var candidate in GetAllNamedTypes(compilation.GlobalNamespace))
                {
                    if (candidate.IsAbstract || candidate.TypeKind == TypeKind.Interface) continue;
                    if (candidate.DeclaredAccessibility == Accessibility.Private || candidate.DeclaredAccessibility == Accessibility.Protected) continue;
                    if (results.ContainsKey(candidate)) continue;
                    if (excludeFromDiscoveryAttr != null && HasAttribute(candidate, excludeFromDiscoveryAttr)) continue;
                    if (MatchesBlacklist(candidate, blacklistPatterns)) continue;

                    foreach (var root in discoverRoots)
                    {
                        if (ImplementsOrInherits(candidate, root))
                        {
                            var path = $"Inherits from {root.Name} which is marked as [CerasSerializable]";
                            var result = new TypeDiscoveryResult(candidate, isVersionTolerant: true, discoveryPath: path);
                            results[candidate] = result;
                            queue.Enqueue(candidate);
                            break;
                        }
                    }
                }
            }

            // 3. Recursive propagation through the graph
            while (queue.Count > 0)
            {
                var currentType = queue.Dequeue();
                var currentResult = results[currentType];

                // Skip types we shouldn't explore further (primitives, well-knowns)
                if (IsWellKnownType(currentType)) continue;

                // Discover members of current type
                var members = DiscoverMembers(context, currentType, attrs);
                foreach (var m in members)
                {
                    ExploreType(m.Type, currentType, currentResult.DiscoveryPath);
                }
            }

            void ExploreType(ITypeSymbol type, INamedTypeSymbol parent, string? parentPath)
            {
                // Unwrap arrays and collections
                if (type is IArrayTypeSymbol arr)
                {
                    ExploreType(arr.ElementType, parent, parentPath);
                    return;
                }

                if (type is INamedTypeSymbol named)
                {
                    if (named.IsGenericType)
                    {
                        foreach (var arg in named.TypeArguments)
                            ExploreType(arg, parent, parentPath);
                        
                        // We also need the generic definition itself if it's a concrete user type?
                        // Usually no, we only care about closed types.
                    }

                    if (named.TypeKind == TypeKind.Class || named.TypeKind == TypeKind.Struct)
                    {
                        // Stop at object, interfaces, abstract types
                        if (named.SpecialType == SpecialType.System_Object) return;
                        if (named.IsAbstract || named.TypeKind == TypeKind.Interface) return;
                        
                        // Stop at primitives
                        if (IsFastPrimitive(named)) return;

                        // Stop at System types (Collections, etc) - they should use built-in resolvers
                        if (IsWellKnownType(named)) return;

                        // Check if we already have it
                        if (results.ContainsKey(named)) return;

                        // Check blacklist
                        if (MatchesBlacklist(named, blacklistPatterns)) return;

                        // Add to graph!
                        var newPath = (parentPath == null) ? $"Referenced by {parent.Name}" : $"{parentPath} -> {parent.Name}";
                        var result = new TypeDiscoveryResult(named, isVersionTolerant: true, discoveryPath: newPath);
                        results[named] = result;
                        queue.Enqueue(named);
                    }
                }
            }

            return results;
        }

        static bool IsWellKnownType(INamedTypeSymbol type)
        {
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (fqn.StartsWith("global::System."))
            {
                // Explore only if it's a collection we care about?
                // No, we handle collections in ExploreType specifically.
                return true;
            }
            return false;
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
                case SpecialType.System_String:
                case SpecialType.System_DateTime:
                    return true;
            }
            return false;
        }

        static bool HasDiscoverSubtypes(INamedTypeSymbol type, INamedTypeSymbol? generateFmtAttr)
        {
            if (generateFmtAttr == null) return true;
            foreach (var attr in type.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, generateFmtAttr)) continue;
                foreach (var na in attr.NamedArguments)
                    if (na.Key == "DiscoverSubtypes" && na.Value.Value is bool b)
                        return b;
            }
            return true;
        }

        static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamespaceOrTypeSymbol root)
        {
            foreach (var member in root.GetMembers())
            {
                if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var nested in GetAllNamedTypes(type)) yield return nested;
                }
                else if (member is INamespaceSymbol ns)
                {
                    foreach (var t in GetAllNamedTypes(ns)) yield return t;
                }
            }
        }

        static bool ImplementsOrInherits(INamedTypeSymbol candidate, INamedTypeSymbol root)
        {
            var cur = candidate;
            while (cur != null)
            {
                if (SymbolEqualityComparer.Default.Equals(cur, root)) return true;
                cur = cur.BaseType;
            }
            foreach (var iface in candidate.AllInterfaces)
                if (SymbolEqualityComparer.Default.Equals(iface, root)) return true;
            return false;
        }

        static bool MatchesBlacklist(INamedTypeSymbol type, List<string> blacklistPatterns)
        {
            string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
            foreach (var p in blacklistPatterns)
            {
                if (p.EndsWith("*")) { if (fqn.StartsWith(p.Substring(0, p.Length - 1))) return true; }
                else if (fqn == p) return true;
            }
            return false;
        }
    }
}
