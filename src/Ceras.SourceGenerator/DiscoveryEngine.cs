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
            List<string> blacklistPatterns,
            Dictionary<INamedTypeSymbol, List<SerializedMember>>? memberCache = null,
            List<Diagnostic>? diagnosticSink = null)
        {
            var results = new Dictionary<INamedTypeSymbol, TypeDiscoveryResult>(SymbolEqualityComparer.Default);
            var queue = new Queue<INamedTypeSymbol>();

            // Cache all types in the compilation for subtype discovery
            var allTypes = GetAllNamedTypes(compilation.GlobalNamespace).ToList();

            // 1. Initial roots from [CerasSerializable]
            var initialRoots = new HashSet<INamedTypeSymbol>(types, SymbolEqualityComparer.Default);
            foreach (var t in types)
            {
                if (results.ContainsKey(t)) continue;

                // For initial roots, we ignore the blacklist! 
                // If the user explicitly asks for Vector3, they should get it,
                // even if "Godot.*" is blacklisted.

                var result = new TypeDiscoveryResult(t, null);
                results[t] = result;
                queue.Enqueue(t);
            }

            // 2. Recursive propagation through the graph
            while (queue.Count > 0)
            {
                var currentType = queue.Dequeue();
                var currentResult = results[currentType];

                // Stop at object
                if (currentType.SpecialType == SpecialType.System_Object) continue;

                bool isInstantiable = !currentType.IsAbstract && currentType.TypeKind != TypeKind.Interface;

                // Always explore generic arguments of a constructed/known type so the closed set is
                // complete even when the type itself is a well-known generic (e.g. List<KnownItem>
                // declared via [CerasKnownType<List<KnownItem>>]) (TODO Phase 2.1).
                if (currentType.IsGenericType)
                {
                    foreach (var arg in currentType.TypeArguments)
                        ExploreType(arg, currentType, currentResult.DiscoveryPath);
                }

                if (isInstantiable)
                {
                    // Skip types we shouldn't explore further (primitives, well-knowns)
                    if (!IsWellKnownType(currentType, attrs))
                    {
                        // Discover members of current type. A shared cache (and diagnostic sink) is
                        // used across the discovery, fail-early, and emission passes so member
                        // diagnostics are reported exactly once per build.
                        List<SerializedMember> members;
                        if (memberCache != null && memberCache.TryGetValue(currentType, out var cachedMembers))
                        {
                            members = cachedMembers;
                        }
                        else
                        {
                            members = DiscoverMembers(context, currentType, attrs, blacklistPatterns, diagnosticSink);
                            memberCache?.Add(currentType, members);
                        }

                        foreach (var m in members)
                        {
                            ExploreType(m.Type, currentType, currentResult.DiscoveryPath);
                        }
                    }
                }

                // SUBTYPE DISCOVERY
                // If it's something we should find subtypes for (interfaces, abstract classes, or polymorphic classes)
                // We only care about subtypes if it's not sealed and not a primitive/well-known
                if (!currentType.IsSealed && currentType.TypeKind != TypeKind.Enum && !IsWellKnownType(currentType, attrs))
                {
                    foreach (var candidate in allTypes)
                    {
                            if (candidate.IsAbstract || candidate.TypeKind == TypeKind.Interface) continue;
                            if (candidate.DeclaredAccessibility == Accessibility.Private || candidate.DeclaredAccessibility == Accessibility.Protected) continue;
                            if (results.ContainsKey(candidate)) continue;
                            if (excludeFromDiscoveryAttr != null && HasAttribute(candidate, excludeFromDiscoveryAttr)) continue;

                            // Explicit roots always win over blacklist
                            if (!initialRoots.Contains(candidate) && MatchesBlacklist(candidate, blacklistPatterns)) continue;

                            if (ImplementsOrInherits(candidate, currentType))
                            {
                                var parentPath = currentResult.DiscoveryPath;
                                var path = (parentPath == null) ? $"Implementation of {currentType.Name}" : $"{parentPath} -> Implementation of {currentType.Name}";
                                
                                var result = new TypeDiscoveryResult(candidate, path);
                                results[candidate] = result;
                                queue.Enqueue(candidate);
                            }
                        }
                }
            }

            void ExploreType(ITypeSymbol type, INamedTypeSymbol parent, string? parentPath)
            {
                if (HasAttribute(type, attrs.Exclude)) return;

                if (IsBanned(type)) return;

                // Unwrap arrays and collections
                if (type is IArrayTypeSymbol arr)
                {
                    ExploreType(arr.ElementType, parent, parentPath);
                    return;
                }

                if (type is INamedTypeSymbol named)
                {
                    // Skip Nullable<T> - handled by built-in resolver
                    if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return;

                    if (named.IsGenericType)
                    {
                        foreach (var arg in named.TypeArguments)
                            ExploreType(arg, parent, parentPath);
                        
                        // We also need the generic definition itself if it's a concrete user type?
                        // Usually no, we only care about closed types.
                    }

                    if (named.TypeKind == TypeKind.Class || named.TypeKind == TypeKind.Struct || named.TypeKind == TypeKind.Interface || named.TypeKind == TypeKind.Enum)
                    {
                        // Stop at object
                        if (named.SpecialType == SpecialType.System_Object) return;

                        // Stop at primitives
                        if (IsFastPrimitive(named)) return;

                        // Stop at System types (Collections, etc) - they should use built-in resolvers
                        if (IsWellKnownType(named, attrs)) return;

                        // Check if we already have it
                        if (results.ContainsKey(named)) return;

                        // Check blacklist
                        // Explicit roots always win over blacklist
                        if (!initialRoots.Contains(named) && MatchesBlacklist(named, blacklistPatterns)) return;

                        // Add to graph!
                        var newPath = (parentPath == null) ? $"Referenced by {parent.Name}" : $"{parentPath} -> {parent.Name}";
                        var result = new TypeDiscoveryResult(named, newPath);
                        results[named] = result;
                        queue.Enqueue(named);
                    }
                }
            }

            return results;
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
            var cur = type;
            while (cur != null)
            {
                string fqn = cur.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
                foreach (var p in blacklistPatterns)
                {
                    if (p.EndsWith("*")) { if (fqn.StartsWith(p.Substring(0, p.Length - 1))) return true; }
                    else if (fqn == p) return true;
                }
                cur = cur.BaseType;
            }
            return false;
        }
    }
}
