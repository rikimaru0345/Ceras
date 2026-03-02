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
        public static List<SerializedMember> DiscoverMembers(SourceProductionContext context, INamedTypeSymbol type, AttributeBag attrs, List<string>? blacklistPatterns = null, HashSet<INamedTypeSymbol>? explicitlyRegisteredTypes = null)
        {
            var members = new List<SerializedMember>();
            // Tracks stable names already claimed by backing fields in more-derived types.
            // Used to silently skip the base-class version when a property is shadowed via 'new'.
            var claimedBackingFieldNames = new HashSet<string>(StringComparer.Ordinal);

            // TargetMemberKind logic removed
            // TargetMemberKind currentTargetKind = ResolveEffectiveTargetMemberKind(context, type, attrs.MemberConfig);

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

                        // Hard-banned types (delegates, enumerators, reflection, blacklisted) can never round-trip.
                        // Exception: if the type was explicitly registered with [CerasSerializable] (e.g. via
                        // assembly-level [CerasSerializable<Vector2I>]), it overrides a wildcard blacklist pattern.
                        bool isExplicitlyRegistered = explicitlyRegisteredTypes != null
                            && field.Type is INamedTypeSymbol fieldType
                            && explicitlyRegisteredTypes.Contains(fieldType);
                        if (!isExplicitlyRegistered && IsBanned(field.Type, blacklistPatterns)) continue;

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
                            context.ReportDiagnostic(Diagnostic.Create(
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

                        var m = MakeMember(field.Name, field.Type, isField: true, field, attrs, isGetterOnly: false, stableNameOverride: stableName);
                        
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
                                        m = new SerializedMember(m.Name, sn, m.Type, m.DeclaringType, m.IsField, m.PreviousNames, m.IsMonomorphic, m.IsAccessible, m.IsReadOnly, m.IsGetterOnly, m.IsCtorParam, m.CtorParamName, m.IsReadAccessible, m.IsWriteAccessible);
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
                                    m = new SerializedMember(m.Name, m.StableName, m.Type, m.DeclaringType, m.IsField, previousNames.ToArray(), m.IsMonomorphic, m.IsAccessible, m.IsReadOnly, m.IsGetterOnly, m.IsCtorParam, m.CtorParamName, m.IsReadAccessible, m.IsWriteAccessible);
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

        public static bool IsPartial(INamedTypeSymbol type)
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
            ISymbol symbol, AttributeBag attrs, bool isGetterOnly = false, string stableNameOverride = null)
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

            bool isReadOnly = isField ? ((IFieldSymbol)symbol).IsReadOnly : (((IPropertySymbol)symbol).SetMethod == null || ((IPropertySymbol)symbol).SetMethod.IsInitOnly);
            
            if (isReadOnly) isWriteAccessible = false;

            if (symbol is IPropertySymbol prop)
            {
                isReadAccessible = prop.GetMethod != null &&
                                 (prop.GetMethod.DeclaredAccessibility == Accessibility.Public ||
                                  prop.GetMethod.DeclaredAccessibility == Accessibility.Internal);

                isWriteAccessible = true;
                if (prop.SetMethod != null)
                {
                    isWriteAccessible = prop.SetMethod.DeclaredAccessibility == Accessibility.Public ||
                                        prop.SetMethod.DeclaredAccessibility == Accessibility.Internal;
                }
                else if (!isGetterOnly)
                {
                    isWriteAccessible = false;
                }

                isAccessible = isReadAccessible && isWriteAccessible;
            }

            return new SerializedMember(name, stableName, type, symbol.ContainingType, isField,
                previousNames.ToArray(), isMonomorphic, isAccessible, isReadOnly, isGetterOnly,
                isReadAccessible: isReadAccessible, isWriteAccessible: isWriteAccessible);
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
                    if (match == null)
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

        public static void ValidatePartialRequirement(SourceProductionContext context, Compilation compilation, INamedTypeSymbol type, List<SerializedMember> members)
        {
            // No longer needed because we generate UnsafeAccessors directly in the formatter class,
            // so the target type does not need to be partial.
        }

        public static AccessHelperRequirement GetAccessHelperRequirement(SerializedMember m)
        {
            AccessHelperRequirement req = AccessHelperRequirement.None;

            if (!m.IsReadAccessible)
                req |= AccessHelperRequirement.Read;

            if (!m.IsWriteAccessible)
            {
                bool handledElsewhere = m.IsCtorParam || m.IsGetterOnly;
                if (!handledElsewhere)
                    req |= AccessHelperRequirement.Write;
            }

            return req;
        }

        public static bool NeedsAccessHelper(SerializedMember m) => GetAccessHelperRequirement(m) != AccessHelperRequirement.None;

        public static void ValidateReadonlyMembers(SourceProductionContext context, INamedTypeSymbol type, List<SerializedMember> members)
        {
            // No longer needed: we populate in-place and use UnsafeAccessors to write directly
            // to readonly fields and init-only properties.
        }

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
        [Flags]
        public enum TargetMemberKind
        {
            None = 0,
            PublicFields = 1 << 0,
            PrivateFields = 1 << 1,
            PublicProperties = 1 << 2,
            PrivateProperties = 1 << 3,
            AllPublic = PublicFields | PublicProperties,
            AllPrivate = PrivateFields | PrivateProperties,
            AllFields = PublicFields | PrivateFields,
            AllProperties = PublicProperties | PrivateProperties,
            All = PublicFields | PrivateFields | PublicProperties | PrivateProperties
        }
    }
}
