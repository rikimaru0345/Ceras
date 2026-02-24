using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using static Ceras.SourceGenerator.TypeHelpers;

namespace Ceras.SourceGenerator
{
    internal static class DiscoveryEngine
    {
        public static Dictionary<INamedTypeSymbol, bool /* vt */> ExpandSubtypes(
            Compilation compilation,
            ImmutableArray<INamedTypeSymbol> types,
            INamedTypeSymbol? generateFmtAttr,
            INamedTypeSymbol? excludeFromDiscoveryAttr,
            List<string> blacklistPatterns)
        {
            var expanded = new Dictionary<INamedTypeSymbol, bool>(SymbolEqualityComparer.Default);
            var discoverRoots = new List<INamedTypeSymbol>();

            foreach (var t in types)
            {
                if (HasDiscoverSubtypes(t, generateFmtAttr))
                    discoverRoots.Add(t);
                expanded[t] = true; // Robust path is default now
            }

            if (discoverRoots.Count > 0)
            {
                foreach (var candidate in GetAllNamedTypes(compilation.GlobalNamespace))
                {
                    if (candidate.IsAbstract || candidate.TypeKind == TypeKind.Interface) continue;
                    if (candidate.DeclaredAccessibility == Accessibility.Private || candidate.DeclaredAccessibility == Accessibility.Protected) continue;
                    if (expanded.ContainsKey(candidate)) continue;
                    if (excludeFromDiscoveryAttr != null && HasAttribute(candidate, excludeFromDiscoveryAttr)) continue;
                    if (MatchesBlacklist(candidate, blacklistPatterns)) continue;

                    foreach (var root in discoverRoots)
                    {
                        if (ImplementsOrInherits(candidate, root))
                        {
                            expanded[candidate] = true;
                            break;
                        }
                    }
                }
            }

            return expanded;
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
