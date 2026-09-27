using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Ceras.SourceGenerator.TypeHelpers;

namespace Ceras.SourceGenerator
{
    internal static class MemberExplorer
    {
        public static List<SerializedMember> DiscoverMembers(SourceProductionContext context, INamedTypeSymbol type, AttributeBag attrs, List<string>? blacklistPatterns = null, HashSet<INamedTypeSymbol>? explicitlyRegisteredTypes = null, List<Diagnostic>? diagnosticSink = null)
        {
            var members = new List<SerializedMember>();
            // Tracks stable names already claimed by backing fields in more-derived types.
            // Used to silently skip the base-class version when a property is shadowed via 'new'.
            var claimedBackingFieldNames = new HashSet<string>(StringComparer.Ordinal);

            // Auto-properties are handled via their backing fields.
            var typeChain = new List<INamedTypeSymbol>();
            INamedTypeSymbol? cur = type;
            while (cur != null && cur.SpecialType != SpecialType.System_Object)
            {
                typeChain.Add(cur);
                cur = cur.BaseType;
            }

            foreach (var t in typeChain)
            {
                foreach (var member in t.GetMembers())
                {
                    if (member.IsStatic)
                        continue;

                    if (member is IFieldSymbol field)
                    {
                        if (field.IsConst) continue;

                        // Type-level [Exclude] is always a hard ban, checked before anything else.
                        if (HasAttribute(field.Type, attrs.Exclude)) continue;

                        bool isBackingField = field.Name.EndsWith("k__BackingField"); // Relaxed check: name pattern is enough
                        IPropertySymbol? associatedProp = null;
                        if (isBackingField)
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(field.Name, @"^<(.*)>k__BackingField$");
                            if (match.Success)
                            {
                                string propName = match.Groups[1].Value;
                                associatedProp = t.GetMembers(propName).OfType<IPropertySymbol>().FirstOrDefault();
                            }
                        }

                        bool hasInclude = (attrs.Include != null && HasAttribute(field, attrs.Include)) || (associatedProp != null && attrs.Include != null && HasAttribute(associatedProp, attrs.Include));
                        bool hasExclude = (attrs.Exclude != null && HasAttribute(field, attrs.Exclude)) || (associatedProp != null && attrs.Exclude != null && HasAttribute(associatedProp, attrs.Exclude));

                        if (hasExclude) continue;

                        // The robust format describes only one-dimensional arrays. A multidimensional
                        // array member would otherwise emit an ArrayFormatter<T[]> that does not compile
                        // (CS1503). Report it as a hard diagnostic instead.
                        if (IsUnsupportedMultidimensionalArray(field.Type))
                        {
                            string memberName = associatedProp?.Name ?? field.Name;
                            string typeName = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            string containingTypeName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            var location = (associatedProp?.Locations.FirstOrDefault() ?? field.Locations.FirstOrDefault());
                            ReportMemberDiagnostic(diagnosticSink, Diagnostic.Create(
                                Diag.UnsupportedMultidimensionalArray,
                                location,
                                memberName, containingTypeName, typeName, ((IArrayTypeSymbol)field.Type).Rank));
                            continue;
                        }

                        // Hard-banned types (delegates, enumerators, reflection) can never round-trip
                        // and are skipped. The generator blacklist only prevents formatter *generation*
                        // for those types; a field of a blacklisted type is still included and requires
                        // a runtime custom formatter (a CERAS017 warning is emitted for engine objects).
                        bool isExplicitlyRegistered = explicitlyRegisteredTypes != null
                            && field.Type is INamedTypeSymbol fieldType
                            && explicitlyRegisteredTypes.Contains(fieldType);
                        if (!isExplicitlyRegistered && IsBanned(field.Type)) continue;

                        // Engine-object types (e.g. Godot.GodotObject subclasses) CAN be serialized
                        // when the user provides a custom formatter.  Include the field but emit a
                        // warning so the user knows a formatter is required.  Silently dropping the
                        // field would produce null on load — principle of least surprise says include.
                        if (NeedsCustomFormatter(field.Type))
                        {
                            string memberName = associatedProp?.Name ?? field.Name;
                            string typeName = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            string containingTypeName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            var location = (associatedProp?.Locations.FirstOrDefault() ?? field.Locations.FirstOrDefault());
                            ReportMemberDiagnostic(diagnosticSink, Diagnostic.Create(
                                Diag.EngineObjectNeedsCustomFormatter,
                                location,
                                memberName, containingTypeName, typeName));
                        }

                        bool isFieldPublic = field.DeclaredAccessibility == Accessibility.Public;
                        bool isPropPublic = associatedProp != null && associatedProp.DeclaredAccessibility == Accessibility.Public;

                        // Only fields are included by default. All properties are ignored.
                        bool isIncludedByConfig;
                        if (isBackingField && associatedProp != null)
                        {
                            isIncludedByConfig = true; // All fields are included by default
                        }
                        else
                        {
                            isIncludedByConfig = true; // All fields are included by default
                        }

                        if (!hasInclude && !isFieldPublic && !isBackingField && !isIncludedByConfig)
                            continue;

                        // Sanitize backing field name for the stream
                        string stableName = field.Name;
                        if (associatedProp != null)
                        {
                            stableName = associatedProp.Name;
                        }

                        // Auto-dedup: when a property is shadowed in a derived class via 'new',
                        // both the derived and base backing fields share the same stable name.
                        // Since typeChain is ordered derived→base, we've already added the
                        // more-derived version first, so silently skip the base-class duplicate.
                        if (isBackingField && !claimedBackingFieldNames.Add(stableName))
                            continue;

                        var m = MakeMember(field.Name, field.Type, isField: true, field, attrs, stableNameOverride: stableName);
                        
                        // If it's a backing field, also check attributes on the property
                        if (associatedProp != null)
                        {
                            if (attrs.SerializedAs != null)
                            {
                                foreach (var attr in associatedProp.GetAttributes())
                                {
                                    if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrs.SerializedAs) &&
                                        attr.ConstructorArguments.Length > 0 &&
                                        attr.ConstructorArguments[0].Value is string sn)
                                    {
                                        m = new SerializedMember(m.Name, sn, m.Type, m.DeclaringType, m.IsField, m.PreviousNames, m.IsMonomorphic, m.IsAccessible, m.IsReadOnly, m.IsReadAccessible, m.IsWriteAccessible, m.MemberSymbol);
                                        break;
                                    }
                                }
                            }

                            if (attrs.PreviousName != null)
                            {
                                var previousNames = new List<string>(m.PreviousNames);
                                foreach (var attr in associatedProp.GetAttributes())
                                {
                                    if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrs.PreviousName))
                                        continue;

                                    if (attr.ConstructorArguments.Length > 0 &&
                                        attr.ConstructorArguments[0].Value is string primary)
                                        previousNames.Add(primary);

                                    if (attr.ConstructorArguments.Length > 1 &&
                                        !attr.ConstructorArguments[1].IsNull)
                                    {
                                        foreach (var v in attr.ConstructorArguments[1].Values)
                                            if (v.Value is string alt)
                                                previousNames.Add(alt);
                                    }
                                }
                                if (previousNames.Count > m.PreviousNames.Length)
                                {
                                    m = new SerializedMember(m.Name, m.StableName, m.Type, m.DeclaringType, m.IsField, previousNames.ToArray(), m.IsMonomorphic, m.IsAccessible, m.IsReadOnly, m.IsReadAccessible, m.IsWriteAccessible, m.MemberSymbol);
                                }
                            }

                            // Removed Monomorphic/Polymorphic attribute checks as they are no longer supported.
                        }

                        members.Add(m);
                    }
                    // Properties are no longer serialized directly. 
                    // Auto-properties are handled via their backing fields.
                }
            }

            // Sort key must match SchemaMemberComparer used at runtime by DynamicFormatter:
            //   MemberType.FullName + Member.Name + Member.DeclaringType.FullName + "f"/"p"
            // Use each member's own DeclaringType (not the formatted type) so inherited members
            // sort identically between source-generator and runtime.
            members.Sort((a, b) =>
            {
                string ka = a.Type.ToDisplayString(SortKeyFormat) + a.Name + a.DeclaringType.ToDisplayString(SortKeyFormat) + (a.IsField ? "f" : "p");
                string kb = b.Type.ToDisplayString(SortKeyFormat) + b.Name + b.DeclaringType.ToDisplayString(SortKeyFormat) + (b.IsField ? "f" : "p");
                return string.Compare(ka, kb, StringComparison.Ordinal);
            });

            return members;
        }

        /// <summary>
        /// Routes a member-level diagnostic either to the compilation (default) or to a buffer so the
        /// caller can defer and de-duplicate it (see the duplicate-diagnostics cleanup).
        /// </summary>
        static void ReportMemberDiagnostic(List<Diagnostic>? sink, Diagnostic diagnostic)
        {
            sink?.Add(diagnostic);
        }

        public static bool IsCollectionType(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol) return true;
            if (type is INamedTypeSymbol named)
            {
                var fqn = named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (fqn.StartsWith("global::System.Collections.Generic.ICollection<") ||
                    fqn.StartsWith("global::System.Collections.Generic.IDictionary<") ||
                    fqn == "global::System.Collections.ICollection" ||
                    fqn == "global::System.Collections.IDictionary")
                    return true;

                foreach (var iface in named.AllInterfaces)
                {
                    var ifaceFqn = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (ifaceFqn.StartsWith("global::System.Collections.Generic.ICollection<") ||
                        ifaceFqn.StartsWith("global::System.Collections.Generic.IDictionary<") ||
                        ifaceFqn == "global::System.Collections.ICollection" ||
                        ifaceFqn == "global::System.Collections.IDictionary")
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True when the type is served by the built-in <c>CollectionFormatter&lt;TCollection, TItem&gt;</c>
        /// at runtime (a collection subclass such as <c>MyList&lt;T&gt; : List&lt;T&gt;</c>, or a
        /// <c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> special-case). Such a type is written as an explicit
        /// element count plus elements, so the generator must not emit an empty ordinary-object formatter
        /// for it (a field-based formatter would override the built-in collection one). The structural
        /// schema is derived at bake time from the same predicate.
        /// </summary>
        public static bool IsCollectionFormatterType(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol) return false;
            if (type is not INamedTypeSymbol named) return false;
            if (named.TypeKind == TypeKind.Interface || named.IsAbstract) return false;
            if (!IsCollectionType(named)) return false;

            // Queue<>/Stack<> are concrete classes served by their dedicated formatters; they are not
            // caught by the ICollection<> check above (they do not implement ICollection<T>), so add them.
            foreach (var iface in named.AllInterfaces)
            {
                var ifaceFqn = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (ifaceFqn.StartsWith("global::System.Collections.Generic.ICollection<") ||
                    ifaceFqn.StartsWith("global::System.Collections.Generic.IDictionary<"))
                    return true;
            }

            for (var t = named.BaseType; t != null; t = t.BaseType)
            {
                var baseFqn = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (baseFqn.StartsWith("global::System.Collections.Generic.Queue<") ||
                    baseFqn.StartsWith("global::System.Collections.Generic.Stack<"))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// A multidimensional array member (rank &gt; 1). The robust format describes only one-dimensional
        /// arrays, so this is reported as a hard diagnostic instead of emitting an <c>ArrayFormatter</c>
        /// that does not compile for a <c>T[,]</c> member.
        /// </summary>
        public static bool IsUnsupportedMultidimensionalArray(ITypeSymbol type)
            => type is IArrayTypeSymbol { Rank: > 1 };

        /// <summary>
        /// Hard ban: types that can NEVER round-trip regardless of formatters.
        /// Delegates carry method references; enumerators are ephemeral; reflection
        /// metadata is not restorable.  These are skipped unconditionally.
        /// </summary>
        public static bool IsBanned(ITypeSymbol type, List<string>? blacklistPatterns = null)
        {
            if (type is INamedTypeSymbol named)
            {
                var fqnWithGlobal = named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var fqn = fqnWithGlobal.Replace("global::", "");

                // Check against blacklist if provided
                if (blacklistPatterns != null)
                {
                    var curBlacklist = named;
                    while (curBlacklist != null)
                    {
                        string curFqn = curBlacklist.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
                        foreach (var p in blacklistPatterns)
                        {
                            if (p.EndsWith("*")) { if (curFqn.StartsWith(p.Substring(0, p.Length - 1))) return true; }
                            else if (curFqn == p) return true;
                        }
                        curBlacklist = curBlacklist.BaseType;
                    }
                }

                // Delegates, Actions, Funcs
                if (fqn == "System.Delegate" || fqn == "System.MulticastDelegate") return true;
                var cur = named.BaseType;
                while (cur != null)
                {
                    var baseFqn = cur.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
                    if (baseFqn == "System.Delegate" || baseFqn == "System.MulticastDelegate") return true;
                    cur = cur.BaseType;
                }

                // Reflection metadata
                if (fqn.StartsWith("System.Reflection."))
                {
                    if (fqn == "System.Reflection.MemberInfo") return true;
                    if (fqn == "System.Reflection.MethodInfo") return true;
                    if (fqn == "System.Reflection.PropertyInfo") return true;
                    if (fqn == "System.Reflection.FieldInfo") return true;
                    if (fqn == "System.Reflection.EventInfo") return true;
                    if (fqn == "System.Reflection.ConstructorInfo") return true;
                    if (fqn == "System.Reflection.Assembly") return true;
                }

                // Enumerators
                if (fqn == "System.Collections.IEnumerator") return true;
                foreach (var iface in named.AllInterfaces)
                {
                    var ifaceFqn = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
                    if (ifaceFqn == "System.Collections.IEnumerator") return true;
                    if (ifaceFqn.StartsWith("System.Collections.Generic.IEnumerator<")) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Soft warning: types that CAN be serialized only with a user-provided custom formatter.
        /// The field is still included so the schema is complete — the user just needs to register
        /// a formatter at runtime.  Emitting a warning is better than silently dropping the field
        /// (which would produce null on load and a mysterious NullReferenceException at runtime).
        /// </summary>
        public static bool NeedsCustomFormatter(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named)
            {
                var cur = named.BaseType;
                while (cur != null)
                {
                    var baseFqn = cur.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    // Godot engine objects — cannot serialize with default field-based formatters
                    if (baseFqn == "global::Godot.GodotObject") return true;
                    cur = cur.BaseType;
                }
            }
            return false;
        }

        static SerializedMember MakeMember(
            string name, ITypeSymbol type, bool isField,
            ISymbol symbol, AttributeBag attrs, string? stableNameOverride = null)
        {
            string stableName = stableNameOverride ?? name;
            if (attrs.SerializedAs != null)
            {
                foreach (var attr in symbol.GetAttributes())
                {
                    if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrs.SerializedAs) &&
                        attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string sn)
                    {
                        stableName = sn;
                        break;
                    }
                }
            }

            var previousNames = new List<string>();
            if (attrs.PreviousName != null)
            {
                foreach (var attr in symbol.GetAttributes())
                {
                    if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrs.PreviousName))
                        continue;

                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string primary)
                        previousNames.Add(primary);

                    if (attr.ConstructorArguments.Length > 1 &&
                        !attr.ConstructorArguments[1].IsNull)
                    {
                        foreach (var v in attr.ConstructorArguments[1].Values)
                            if (v.Value is string alt)
                                previousNames.Add(alt);
                    }
                }
            }

            bool isMonomorphic = IsMonomorphicByDefault(type);

            bool isAccessible = symbol.DeclaredAccessibility == Accessibility.Public ||
                                symbol.DeclaredAccessibility == Accessibility.Internal;

            bool isReadAccessible = isAccessible;
            bool isWriteAccessible = isAccessible;

            bool isReadOnly = false;
            if (symbol is IFieldSymbol field)
            {
                isReadOnly = field.IsReadOnly;
            }

            if (symbol is IPropertySymbol prop)
            {
                isReadOnly = prop.SetMethod == null || prop.SetMethod.IsInitOnly;

                isReadAccessible = prop.GetMethod != null &&
                                 (prop.GetMethod.DeclaredAccessibility == Accessibility.Public ||
                                  prop.GetMethod.DeclaredAccessibility == Accessibility.Internal);

                isWriteAccessible = true;
                if (prop.SetMethod != null)
                {
                    isWriteAccessible = prop.SetMethod.DeclaredAccessibility == Accessibility.Public ||
                                        prop.SetMethod.DeclaredAccessibility == Accessibility.Internal;
                }
                else
                {
                    isWriteAccessible = false;
                }

                isAccessible = isReadAccessible && isWriteAccessible;
            }

            if (isReadOnly) isWriteAccessible = false;

            return new SerializedMember(name, stableName, type, symbol.ContainingType, isField,
                previousNames.ToArray(), isMonomorphic, isAccessible, isReadOnly,
                isReadAccessible: isReadAccessible, isWriteAccessible: isWriteAccessible, memberSymbol: symbol);
        }

        public static bool IsMonomorphicByDefault(ITypeSymbol type)
        {
            if (type.IsValueType) return true;

            if (type is IArrayTypeSymbol arr)
                return IsMonomorphicByDefault(arr.ElementType);

            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                var args = named.TypeArguments;
                if (args.Length == 1)
                    return IsMonomorphicByDefault(args[0]);
                if (args.Length == 2)
                    return IsMonomorphicByDefault(args[1]);
            }

            return type.IsSealed;
        }

        public static void ValidateIncludeExcludeConflicts(SourceProductionContext context, INamedTypeSymbol type, AttributeBag attrs)
        {
            foreach (var member in type.GetMembers())
            {
                if (HasAttribute(member, attrs.Include) && HasAttribute(member, attrs.Exclude))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diag.IncludeExcludeConflict, member.Locations.FirstOrDefault(), member.Name, type.Name));
                }
            }
        }

        public static void ValidateCerasConstructor(SourceProductionContext context, Compilation compilation, INamedTypeSymbol type, List<SerializedMember> members, AttributeBag attrs)
        {
            if (type.IsValueType || type.IsAbstract) return;

            var ctors = type.InstanceConstructors;
            var cerasCtors = ctors.Where(c => HasAttribute(c, attrs.CerasConstructor)).ToList();

            if (cerasCtors.Count > 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diag.CerasConstructorMultiple, type.Locations.FirstOrDefault(), type.Name));
                return;
            }

            if (cerasCtors.Count == 1)
            {
                var ctor = cerasCtors[0];

                foreach (var p in ctor.Parameters)
                {
                    string normParam = NormalizeName(p.Name);
                    var match = members.FirstOrDefault(m => {
                        if (NormalizeName(m.Name) == normParam) return true;
                        // StableName is the property name for auto-property backing fields (e.g. "Position"
                        // for "<Position>k__BackingField"), so this correctly matches inherited properties.
                        if (NormalizeName(m.StableName) == normParam) return true;
                        return false;
                    });
                    if (match == null && !p.HasExplicitDefaultValue)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Diag.CerasConstructorParamMismatch, p.Locations.FirstOrDefault(), p.Name, type.Name));
                    }
                }
            }
            else
            {
                // No [CerasConstructor], check if we can find any suitable constructor
                var best = GetBestConstructor(type, attrs.CerasConstructor, members);
                if (best == null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diag.NotConstructable, type.Locations.FirstOrDefault(), type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }
            }
        }

        public static void ValidateDuplicateWireNames(SourceProductionContext context, INamedTypeSymbol type, List<SerializedMember> members)
        {
            var nameToMembers = new Dictionary<string, List<SerializedMember>>(StringComparer.Ordinal);
            foreach (var m in members)
            {
                if (!nameToMembers.TryGetValue(m.StableName, out var list))
                    nameToMembers[m.StableName] = list = new List<SerializedMember>();
                list.Add(m);
            }

            foreach (var kvp in nameToMembers)
            {
                if (kvp.Value.Count > 1)
                {
                    var memberList = string.Join(", ", kvp.Value.Select(m => $"'{m.Name}'"));
                    context.ReportDiagnostic(Diagnostic.Create(Diag.DuplicateWireName, type.Locations.FirstOrDefault(), type.Name, kvp.Key, memberList));
                }
            }
        }

        public static AccessHelperRequirement GetAccessHelperRequirement(SerializedMember m)
        {
            AccessHelperRequirement req = AccessHelperRequirement.None;

            if (!m.IsReadAccessible && CanReadMember(m))
                req |= AccessHelperRequirement.Read;

            if (!m.IsWriteAccessible)
                req |= AccessHelperRequirement.Write;

            return req;
        }

        /// <summary>
        /// True when the generated code can read the member: fields can always be read through their
        /// <c>_ref</c> accessor; properties require a getter (the accessor may still be needed for
        /// access).
        /// </summary>
        public static bool CanReadMember(SerializedMember m)
        {
            if (m.IsField) return true;
            if (m.MemberSymbol is IPropertySymbol p) return p.GetMethod != null;
            return m.IsReadAccessible;
        }

        public static bool NeedsAccessHelper(SerializedMember m) => GetAccessHelperRequirement(m) != AccessHelperRequirement.None;

        /// <summary>
        /// Finds the best constructor to use for serialization.
        /// Priority: Explicit [CerasConstructor] > Parameterless > Constructor with most matching parameters.
        /// </summary>
        public static IMethodSymbol? GetBestConstructor(INamedTypeSymbol type, INamedTypeSymbol? cerasConstructorAttr, List<SerializedMember> members)
        {
            var ctors = type.InstanceConstructors;

            // 1. Explicit [CerasConstructor]
            if (cerasConstructorAttr != null)
            {
                var explicitCtor = ctors.FirstOrDefault(c => HasAttribute(c, cerasConstructorAttr));
                if (explicitCtor != null) return explicitCtor;
            }

            // 2. Parameterless (any accessibility)
            var parameterless = ctors.FirstOrDefault(c => c.Parameters.Length == 0);
            if (parameterless != null) return parameterless;

            // 3. Heuristic: Find the ctor with most parameters that can be fully satisfied by serializable members
            // This covers records and classes with primary constructors or multiple constructor options.
            var sortedCtors = ctors.OrderByDescending(c => c.Parameters.Length).ToList();

            foreach (var c in sortedCtors)
            {
                if (c.Parameters.Length == 0) continue;

                bool allMatch = true;
                foreach (var p in c.Parameters)
                {
                    string normParam = NormalizeName(p.Name);
                    bool hasMatch = members.Any(m => {
                        if (NormalizeName(m.Name) == normParam) return true;
                        if (NormalizeName(m.StableName) == normParam) return true;
                        return false;
                    });

                    if (!hasMatch)
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch) return c;
            }

            return null;
        }
        public static bool CanCerasConstruct(INamedTypeSymbol type, INamedTypeSymbol? cerasConstructorAttr, List<SerializedMember> members)
        {
            if (type.IsValueType) return true;
            if (type.IsAbstract) return false;

            // Robust mode needs to be able to find a suitable constructor
            return GetBestConstructor(type, cerasConstructorAttr, members) != null;
        }

        /// <summary>
        /// Finds the deserialization callback of the requested kind. The search walks from the most
        /// derived type up; the first declaring type that has a matching method wins, so a derived
        /// declaration shadows a base one, while a private base callback is still found (the generated
        /// reader invokes it through an UnsafeAccessor). Within one declaring type the parameterless
        /// form is preferred over <c>(MigrationContext)</c> over <c>(CerasSerializer)</c>, matching the
        /// runtime migration callback selection.
        /// </summary>
        public static CallbackInfo FindCallback(INamedTypeSymbol type, AttributeBag attrs, bool isBefore)
        {
            var attrType = isBefore ? attrs.OnBeforeDeserialize : attrs.OnAfterDeserialize;
            if (attrType == null) return null;

            for (var t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            {
                CallbackInfo best = null;
                int bestRank = int.MaxValue;

                foreach (var member in t.GetMembers())
                {
                    if (member is not IMethodSymbol m) continue;
                    if (m.IsStatic || !m.ReturnsVoid || m.IsGenericMethod) continue;
                    if (!HasAttribute(m, attrType)) continue;

                    var ps = m.Parameters;
                    CallbackParameterKind kind;
                    if (ps.Length == 0)
                        kind = CallbackParameterKind.None;
                    else if (ps.Length == 1 && attrs.MigrationContextType != null &&
                             SymbolEqualityComparer.Default.Equals(ps[0].Type, attrs.MigrationContextType))
                        kind = CallbackParameterKind.MigrationContext;
                    else if (ps.Length == 1 && attrs.CerasSerializerType != null &&
                             SymbolEqualityComparer.Default.Equals(ps[0].Type, attrs.CerasSerializerType))
                        kind = CallbackParameterKind.Serializer;
                    else
                        continue;

                    int rank = CallbackRank(kind);
                    if (best == null || rank < bestRank)
                    {
                        best = new CallbackInfo(m, isBefore, kind);
                        bestRank = rank;
                    }
                }

                if (best != null) return best;
            }

            return null;
        }

        static int CallbackRank(CallbackParameterKind kind) => kind switch
        {
            CallbackParameterKind.None => 0,
            CallbackParameterKind.MigrationContext => 1,
            _ => 2,
        };

        // --------------------------------------------------------------------------------------------
        // Unsupported constructor-dependent immutable cycles (TODO Phase 3.2).
        //
        // A reference cycle is only constructible when every object on the cycle can be allocated
        // (uninitialized) and registered before its constructor runs, which is what Ceras does. That
        // fails for an object whose cycled members must be initialized by its constructor: the
        // constructor runs after allocation, so it can only set members from already-read locals, and a
        // member that participates in the cycle cannot satisfy that ordering.
        //
        // This is a *diagnosis*, not network/live cycle validation: it does not walk runtime object
        // graphs. It only inspects the compile-time member graph of the generated closure and fires
        // when a type cannot possibly be constructed under the selected constructor strategy:
        //   * the type has a reference-type member that is readonly or getter-only,
        //   * that member is NOT a parameter of the chosen constructor (so it is never assigned during
        //     construction), and
        //   * the member's own type reaches back to the declaring type through declared member types.
        // In that case Ceras can only leave the member at its default (null) value, producing a broken
        // immutable cycle. The remedy is an explicit construction strategy: mark a constructor
        // [CerasConstructor] that takes the member, add a parameterless construction path, or supply a
        // custom formatter.
        //
        // The rule is deliberately conservative (declared-type reachability only). Anything that is
        // resolved at runtime (interfaces, generics, reflection fallback) is left to the runtime guard.
        // --------------------------------------------------------------------------------------------

        /// <summary>Result of <see cref="FindUnsupportedImmutableCycles"/>.</summary>
        public sealed class ImmutableCycleProblem
        {
            public SerializedMember Member { get; }
            public string CycleTypeDisplay { get; }
            public string ConstructorDisplay { get; }

            public ImmutableCycleProblem(SerializedMember member, string cycleTypeDisplay, string constructorDisplay)
            {
                Member = member;
                CycleTypeDisplay = cycleTypeDisplay;
                ConstructorDisplay = constructorDisplay;
            }
        }

        public static List<ImmutableCycleProblem> FindUnsupportedImmutableCycles(
            INamedTypeSymbol type,
            List<SerializedMember> members,
            INamedTypeSymbol? cerasConstructorAttr,
            Dictionary<INamedTypeSymbol, List<SerializedMember>> typeMembers,
            HashSet<INamedTypeSymbol> closureTypes)
        {
            var problems = new List<ImmutableCycleProblem>();
            if (!type.IsReferenceType) return problems;

            // A parameterless construction path lets Ceras allocate+register+construct before reading
            // members, so a cycle is always constructible; nothing to diagnose.
            var bestCtor = GetBestConstructor(type, cerasConstructorAttr, members);
            if (bestCtor == null || bestCtor.Parameters.Length == 0) return problems;

            var ctorParams = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in bestCtor.Parameters) ctorParams.Add(NormalizeName(p.Name));

            foreach (var m in members)
            {
                if (!m.IsReadOnly) continue;
                if (!IsCycleMemberType(m.Type)) continue;

                // Is the member initialized by the constructor? (by name or stable name)
                if (ctorParams.Contains(NormalizeName(m.Name)) || ctorParams.Contains(NormalizeName(m.StableName)))
                    continue;

                if (ReachesType(m.Type, type, typeMembers, closureTypes, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)))
                {
                    string memberType = m.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    problems.Add(new ImmutableCycleProblem(m, memberType, DescribeConstructor(bestCtor)));
                    break; // one diagnostic per type is enough to prompt a fix
                }
            }

            return problems;
        }

        static bool IsCycleMemberType(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol arr) return !arr.ElementType.IsValueType;
            if (type is INamedTypeSymbol named)
            {
                if (named.TypeKind != TypeKind.Class && named.TypeKind != TypeKind.Interface) return false;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when <paramref name="from"/> can reach <paramref name="target"/> through declared
        /// member-type edges of the closed set (interfaces/abstract/base types are widened to their
        /// concrete implementors, conservatively over-approximating but never false-positive at the
        /// "declared member of a real type" level).
        /// </summary>
        static bool ReachesType(
            ITypeSymbol from,
            INamedTypeSymbol target,
            Dictionary<INamedTypeSymbol, List<SerializedMember>> typeMembers,
            HashSet<INamedTypeSymbol> closureTypes,
            HashSet<INamedTypeSymbol> visited)
        {
            foreach (var next in SuccessorTypes(from))
            {
                if (next == null) continue;
                if (SymbolEqualityComparer.Default.Equals(next, target)) return true;
                if (!closureTypes.Contains(next)) continue;
                if (!typeMembers.TryGetValue(next, out var nextMembers)) continue;
                if (!visited.Add(next)) continue;

                if (CanReachDeclaredTarget(nextMembers, target, typeMembers, closureTypes, visited)) return true;
            }
            return false;
        }

        static bool CanReachDeclaredTarget(
            List<SerializedMember> members,
            INamedTypeSymbol target,
            Dictionary<INamedTypeSymbol, List<SerializedMember>> typeMembers,
            HashSet<INamedTypeSymbol> closureTypes,
            HashSet<INamedTypeSymbol> visited)
        {
            foreach (var m in members)
            {
                foreach (var next in SuccessorTypes(m.Type))
                {
                    if (next == null) continue;
                    if (SymbolEqualityComparer.Default.Equals(next, target)) return true;
                    if (!closureTypes.Contains(next)) continue;
                    if (!typeMembers.TryGetValue(next, out var nextMembers)) continue;
                    if (!visited.Add(next)) continue;
                    if (CanReachDeclaredTarget(nextMembers, target, typeMembers, closureTypes, visited)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The set of declared reference types a member type can be an instance of, ignoring value types
        /// and well-known/system types that have no generated members.
        /// </summary>
        static IEnumerable<INamedTypeSymbol?> SuccessorTypes(ITypeSymbol type)
        {
            switch (type)
            {
                case IArrayTypeSymbol arr:
                    yield return arr.ElementType as INamedTypeSymbol;
                    yield break;
                case ITypeParameterSymbol:
                    yield break;
                case INamedTypeSymbol named:
                    if (named.TypeKind != TypeKind.Class && named.TypeKind != TypeKind.Interface) yield break;
                    if (named.IsGenericType)
                        foreach (var arg in named.TypeArguments)
                            if (arg is INamedTypeSymbol a && !a.IsValueType && a.SpecialType == SpecialType.None)
                                yield return a;
                    yield return named;
                    yield break;
            }
        }

        static string DescribeConstructor(IMethodSymbol ctor)
        {
            if (ctor.Parameters.Length == 0) return "parameterless";
            return "(" + string.Join(", ", ctor.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + p.Name)) + ")";
        }
    }
}
