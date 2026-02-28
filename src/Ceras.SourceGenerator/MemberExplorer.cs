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
        public static List<SerializedMember> DiscoverMembers(SourceProductionContext context, INamedTypeSymbol type, AttributeBag attrs)
        {
            var members = new List<SerializedMember>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);

            TargetMemberKind currentTargetKind = ResolveEffectiveTargetMemberKind(context, type, attrs.MemberConfig);

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

                        if (IsBanned(field.Type)) continue;
                        if (HasAttribute(field.Type, attrs.Exclude)) continue;

                        bool isBackingField = field.IsImplicitlyDeclared && field.Name.EndsWith("k__BackingField");
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

                        bool isFieldPublic = field.DeclaredAccessibility == Accessibility.Public;
                        bool isPropPublic = associatedProp != null && associatedProp.DeclaredAccessibility == Accessibility.Public;

                        bool isIncludedByConfig = IsFieldIncluded(field, currentTargetKind);
                        if (!isIncludedByConfig && associatedProp != null)
                        {
                            // If the field is a backing field, check if the property is included by config
                            if ((currentTargetKind & TargetMemberKind.PublicProperties) != 0 && isPropPublic)
                                isIncludedByConfig = true;
                            else if ((currentTargetKind & TargetMemberKind.PrivateProperties) != 0 && !isPropPublic)
                                isIncludedByConfig = true;
                        }

                        if (!hasInclude && !isFieldPublic && !isBackingField && !isIncludedByConfig)
                            continue;

                        // Sanitize backing field name for the stream
                        string stableName = field.Name;
                        if (associatedProp != null)
                        {
                            stableName = associatedProp.Name;
                        }

                        members.Add(MakeMember(field.Name, field.Type, isField: true, field, attrs, isGetterOnly: false, stableNameOverride: stableName));
                    }
                    // Properties are no longer serialized directly. 
                    // Auto-properties are handled via their backing fields.
                }
            }

            string declaringFqn = type.ToDisplayString(SortKeyFormat);
            members.Sort((a, b) =>
            {
                string ka = a.Type.ToDisplayString(SortKeyFormat) + a.Name + declaringFqn + (a.IsField ? "f" : "p");
                string kb = b.Type.ToDisplayString(SortKeyFormat) + b.Name + declaringFqn + (b.IsField ? "f" : "p");
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

        public static bool IsBanned(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named)
            {
                var fqn = named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                
                // Delegates, Actions, Funcs
                if (fqn == "global::System.Delegate" || fqn == "global::System.MulticastDelegate") return true;
                var cur = named.BaseType;
                while (cur != null)
                {
                    var baseFqn = cur.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (baseFqn == "global::System.Delegate" || baseFqn == "global::System.MulticastDelegate" || baseFqn == "global::Godot.GodotObject") return true;
                    cur = cur.BaseType;
                }

                // Reflection metadata
                if (fqn.StartsWith("global::System.Reflection."))
                {
                    if (fqn == "global::System.Reflection.MemberInfo") return true;
                    if (fqn == "global::System.Reflection.MethodInfo") return true;
                    if (fqn == "global::System.Reflection.PropertyInfo") return true;
                    if (fqn == "global::System.Reflection.FieldInfo") return true;
                    if (fqn == "global::System.Reflection.EventInfo") return true;
                    if (fqn == "global::System.Reflection.ConstructorInfo") return true;
                    if (fqn == "global::System.Reflection.Assembly") return true;
                }

                // Enumerators
                if (fqn == "global::System.Collections.IEnumerator") return true;
                foreach (var iface in named.AllInterfaces)
                {
                    var ifaceFqn = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (ifaceFqn == "global::System.Collections.IEnumerator") return true;
                    if (ifaceFqn.StartsWith("global::System.Collections.Generic.IEnumerator<")) return true;
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
            if (attrs.Monomorphic != null && HasAttribute(symbol, attrs.Monomorphic))
                isMonomorphic = true;
            if (attrs.Polymorphic != null && HasAttribute(symbol, attrs.Polymorphic))
                isMonomorphic = false;

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

        static bool IsFieldIncluded(IFieldSymbol field, TargetMemberKind kind)
        {
            if (field.DeclaredAccessibility == Accessibility.Public)
                return (kind & TargetMemberKind.PublicFields) != 0;
            return (kind & TargetMemberKind.PrivateFields) != 0;
        }

        static bool IsPropertyIncluded(IPropertySymbol prop, TargetMemberKind kind)
        {
            if (prop.DeclaredAccessibility == Accessibility.Public)
                return (kind & TargetMemberKind.PublicProperties) != 0;
            return (kind & TargetMemberKind.PrivateProperties) != 0;
        }

        static TargetMemberKind ResolveEffectiveTargetMemberKind(SourceProductionContext context, INamedTypeSymbol type, INamedTypeSymbol? memberConfigAttr)
        {
            // 1. Check class hierarchy
            var cur = type;
            while (cur != null)
            {
                foreach (var attr in cur.GetAttributes())
                {
                    if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, memberConfigAttr))
                    {
                        if (TypeHelpers.TryGetAttributeArgument<int>(attr, "TargetMembers", out var val))
                            return (TargetMemberKind)val;
                        if (TypeHelpers.TryGetAttributeArgument<int>(attr, "TargetMember", out var val2))
                            return (TargetMemberKind)val2;
                    }
                }
                cur = cur.BaseType;
            }

            // 2. Check all implemented interfaces (recursively)
            var allInterfaces = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            void AddInterfaces(INamedTypeSymbol i)
            {
                if (!allInterfaces.Add(i)) return;
                foreach (var sub in i.AllInterfaces) AddInterfaces(sub);
            }
            foreach (var i in type.AllInterfaces) AddInterfaces(i);

            foreach (var iface in allInterfaces)
            {
                foreach (var attr in iface.GetAttributes())
                {
                    if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, memberConfigAttr))
                    {
                        if (TypeHelpers.TryGetAttributeArgument<int>(attr, "TargetMembers", out var val))
                            return (TargetMemberKind)val;
                        if (TypeHelpers.TryGetAttributeArgument<int>(attr, "TargetMember", out var val2))
                            return (TargetMemberKind)val2;
                    }
                }
            }

            // 3. Check containing assembly for a global default
            if (type.ContainingAssembly != null)
            {
                foreach (var attr in type.ContainingAssembly.GetAttributes())
                {
                    if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, memberConfigAttr))
                    {
                        if (TypeHelpers.TryGetAttributeArgument<int>(attr, "TargetMembers", out var val))
                            return (TargetMemberKind)val;
                        if (TypeHelpers.TryGetAttributeArgument<int>(attr, "TargetMember", out var val2))
                            return (TargetMemberKind)val2;
                    }
                }
            }

            if (type.IsValueType)
            {
                return TargetMemberKind.AllFields | TargetMemberKind.PublicProperties;
            }

            return TargetMemberKind.AllPublic;
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
            // No longer needed: Ceras robust mode no longer runs user constructors.
        }

        public static void ValidateDuplicateWireNames(SourceProductionContext context, INamedTypeSymbol type, List<SerializedMember> members)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in members)
            {
                if (!names.Add(m.StableName))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diag.DuplicateWireName, type.Locations.FirstOrDefault(), type.Name, m.StableName));
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
        /// Priority: Explicit [CerasConstructor] > Public/Internal parameterless > Constructor with most matching parameters.
        /// </summary>
        public static IMethodSymbol? GetBestConstructor(INamedTypeSymbol type, INamedTypeSymbol? cerasConstructorAttr)
        {
            var ctors = type.InstanceConstructors;

            // 1. Explicit [CerasConstructor]
            if (cerasConstructorAttr != null)
            {
                var explicitCtor = ctors.FirstOrDefault(c => HasAttribute(c, cerasConstructorAttr));
                if (explicitCtor != null) return explicitCtor;
            }

            // 2. Public/Internal parameterless
            var parameterless = ctors.FirstOrDefault(c => c.Parameters.Length == 0 && (c.DeclaredAccessibility == Accessibility.Public || c.DeclaredAccessibility == Accessibility.Internal));
            if (parameterless != null) return parameterless;

            // 3. Heuristic: Find the ctor with most parameters that can be fully satisfied by serializable members
            // This covers records and classes with primary constructors or multiple constructor options.
            var sortedCtors = ctors.Where(c => c.DeclaredAccessibility == Accessibility.Public || c.DeclaredAccessibility == Accessibility.Internal)
                                   .OrderByDescending(c => c.Parameters.Length).ToList();

            // We need a context-free way to check members here, or we pass them in.
            // For now, let's use a simplified member check based on what Ceras generally serializes.
            var members = type.GetMembers().Where(m => !m.IsStatic && (m is IFieldSymbol || m is IPropertySymbol)).ToList();

            foreach (var c in sortedCtors)
            {
                if (c.Parameters.Length == 0) continue;

                bool allMatch = true;
                foreach (var p in c.Parameters)
                {
                    string normParam = NormalizeName(p.Name);
                    bool hasMatch = members.Any(m => NormalizeName(m.Name) == normParam);
                    
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

        public static bool CanCerasConstruct(INamedTypeSymbol type, INamedTypeSymbol? cerasConstructorAttr)
        {
            if (type.IsValueType) return true;
            if (type.IsAbstract) return false;
            // Robust mode uses GetUninitializedObject for all reference types.
            return true;
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
