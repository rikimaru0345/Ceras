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
                    if (member.IsStatic || member.IsImplicitlyDeclared)
                        continue;

                    if (!seenNames.Add(member.Name))
                        continue;

                    ITypeSymbol? memberType = (member as IFieldSymbol)?.Type ?? (member as IPropertySymbol)?.Type;
                    if (memberType == null) continue;

                    if (HasAttribute(memberType, attrs.Exclude))
                        continue;

                    bool hasInclude = attrs.Include != null && HasAttribute(member, attrs.Include);
                    bool hasExclude = attrs.Exclude != null && HasAttribute(member, attrs.Exclude);

                    if (hasExclude) continue;

                    bool isAccessible = member.DeclaredAccessibility == Accessibility.Public ||
                                        member.DeclaredAccessibility == Accessibility.Internal;

                    if (member is IFieldSymbol field)
                    {
                        if (field.IsConst) continue;

                        bool isFieldGetterOnly = field.IsReadOnly && IsCollectionType(field.Type);
                        bool isFieldPublic = field.DeclaredAccessibility == Accessibility.Public;

                        if (!hasInclude && !IsFieldIncluded(field, currentTargetKind))
                        {
                            if (!isFieldGetterOnly) continue;
                            if (!isFieldPublic && !hasInclude) continue;
                        }
                        else if (field.IsReadOnly && !isFieldGetterOnly && !hasInclude)
                        {
                            continue;
                        }

                        members.Add(MakeMember(field.Name, field.Type, isField: true, member, attrs, isGetterOnly: isFieldGetterOnly));
                    }
                    else if (member is IPropertySymbol prop)
                    {
                        if (prop.IsWriteOnly || prop.IsIndexer) continue;
                        if (prop.GetMethod == null) continue;

                        bool isGetterOnly = prop.IsReadOnly || prop.SetMethod == null;
                        bool isPropPublic = prop.DeclaredAccessibility == Accessibility.Public;

                        if (isGetterOnly)
                        {
                            if (!IsCollectionType(prop.Type)) continue;
                            if (!isPropPublic && !hasInclude) continue;
                        }
                        else
                        {
                            if (!isAccessible && !hasInclude && !IsPropertyIncluded(prop, currentTargetKind))
                                continue;
                        }

                        if (!hasInclude && !IsPropertyIncluded(prop, currentTargetKind)) continue;

                        members.Add(MakeMember(prop.Name, prop.Type, isField: false, member, attrs, isGetterOnly: isGetterOnly));
                    }
                }
            }

            // [CerasConstructor] parameter injection
            var cerasCtor = GetBestConstructor(type, attrs.CerasConstructor);
            if (cerasCtor != null && cerasCtor.Parameters.Length > 0)
            {
                var memberByNorm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < members.Count; i++)
                {
                    string norm = NormalizeName(members[i].Name);
                    memberByNorm[norm] = i;
                }

                foreach (var param in cerasCtor.Parameters)
                {
                    string normParam = NormalizeName(param.Name);

                    if (memberByNorm.TryGetValue(normParam, out int idx))
                    {
                        var existing = members[idx];
                        members[idx] = new SerializedMember(
                            existing.Name, existing.StableName, existing.Type, existing.DeclaringType,
                            existing.IsField, existing.PreviousNames, existing.IsMonomorphic,
                            existing.IsAccessible, existing.IsReadOnly, existing.IsGetterOnly, isCtorParam: true, 
                            ctorParamName: param.Name);
                    }
                    else
                    {
                        ISymbol? found = null;
                        ITypeSymbol? foundType = null;
                        bool foundIsField = false;

                        foreach (var t2 in typeChain)
                        {
                            foreach (var sym in t2.GetMembers())
                            {
                                if (sym.IsStatic || sym.IsImplicitlyDeclared) continue;
                                string normMember = NormalizeName(sym.Name);
                                if (normMember != normParam) continue;

                                if (sym is IFieldSymbol f2 && !f2.IsConst)
                                {
                                    found = sym; foundType = f2.Type; foundIsField = true; break;
                                }
                                else if (sym is IPropertySymbol p2 && !p2.IsIndexer && p2.GetMethod != null)
                                {
                                    found = sym; foundType = p2.Type; foundIsField = false; break;
                                }
                            }
                            if (found != null) break;
                        }

                        if (found != null && foundType != null)
                        {
                            bool mono = IsMonomorphicByDefault(foundType);
                            bool isAcc = found.DeclaredAccessibility == Accessibility.Public ||
                                         found.DeclaredAccessibility == Accessibility.Internal;

                            bool isRO = foundIsField ? ((IFieldSymbol)found).IsReadOnly : ((IPropertySymbol)found).SetMethod == null;

                            members.Add(new SerializedMember(
                                found.Name, param.Name, foundType, found.ContainingType, foundIsField,
                                Array.Empty<string>(), mono, isAcc, isRO,
                                isGetterOnly: false, isCtorParam: true, ctorParamName: param.Name));
                            memberByNorm[normParam] = members.Count - 1;
                        }
                    }
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

        static SerializedMember MakeMember(
            string name, ITypeSymbol type, bool isField,
            ISymbol symbol, AttributeBag attrs, bool isGetterOnly = false)
        {
            string stableName = name;
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

            bool isReadOnly = isField ? ((IFieldSymbol)symbol).IsReadOnly : (((IPropertySymbol)symbol).SetMethod == null || ((IPropertySymbol)symbol).SetMethod.IsInitOnly);

            if (symbol is IPropertySymbol prop)
            {
                bool getterAcc = prop.GetMethod != null &&
                                 (prop.GetMethod.DeclaredAccessibility == Accessibility.Public ||
                                  prop.GetMethod.DeclaredAccessibility == Accessibility.Internal);

                bool setterAcc = true;
                if (prop.SetMethod != null)
                {
                    setterAcc = prop.SetMethod.DeclaredAccessibility == Accessibility.Public ||
                                prop.SetMethod.DeclaredAccessibility == Accessibility.Internal;
                }
                else if (!isGetterOnly)
                {
                    setterAcc = false;
                }

                isAccessible = getterAcc && setterAcc;
            }

            return new SerializedMember(name, stableName, type, symbol.ContainingType, isField,
                previousNames.ToArray(), isMonomorphic, isAccessible, isReadOnly, isGetterOnly);
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
                        if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int val)
                            return (TargetMemberKind)val;
                    }
                }
                cur = cur.BaseType;
            }

            // 2. Check all implemented interfaces
            foreach (var iface in type.AllInterfaces)
            {
                foreach (var attr in iface.GetAttributes())
                {
                    if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, memberConfigAttr))
                    {
                        if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int val)
                            return (TargetMemberKind)val;
                    }
                }
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
            var explicitCtors = type.InstanceConstructors.Where(c => HasAttribute(c, attrs.CerasConstructor)).ToList();
            if (explicitCtors.Count > 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diag.CerasConstructorMultiple, type.Locations.FirstOrDefault(), type.Name));
                return;
            }

            var ctor = GetBestConstructor(type, attrs.CerasConstructor);
            if (ctor != null)
            {
                if (ctor.DeclaredAccessibility != Accessibility.Public && ctor.DeclaredAccessibility != Accessibility.Internal)
                {
                    // Only report error if it was explicitly marked or if it's the only choice and it's private
                    if (explicitCtors.Count > 0)
                        context.ReportDiagnostic(Diagnostic.Create(Diag.CerasConstructorNotAccessible, ctor.Locations.FirstOrDefault(), type.Name));
                }

                foreach (var param in ctor.Parameters)
                {
                    var matchingMember = members.FirstOrDefault(m => string.Equals(NormalizeName(m.Name), NormalizeName(param.Name), StringComparison.OrdinalIgnoreCase));
                    
                    if (matchingMember == null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Diag.CerasConstructorParamMismatch, param.Locations.FirstOrDefault(), param.Name, type.Name));
                    }
                    else
                    {
                        var matchingSymbol = type.GetMembers(matchingMember.Name).FirstOrDefault();
                        if (matchingSymbol != null && HasAttribute(matchingSymbol, attrs.Exclude))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Diag.CerasConstructorParamNotProvided, param.Locations.FirstOrDefault(), param.Name, type.Name));
                        }
                    }
                }
            }
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
            if (members.Any(m => !m.IsAccessible && SymbolEqualityComparer.Default.Equals(m.DeclaringType, type)))
            {
                if (!IsPartial(type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diag.PartialRequired, type.Locations.FirstOrDefault(), type.Name));
                }
            }
        }

        public static void ValidateReadonlyMembers(SourceProductionContext context, INamedTypeSymbol type, List<SerializedMember> members)
        {
            foreach (var m in members)
            {
                if (m.IsReadOnly && !m.IsCtorParam && !m.IsGetterOnly)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diag.ReadonlyMemberMustBeInCtor, type.Locations.FirstOrDefault(), m.Name, type.Name));
                }
            }
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
            return GetBestConstructor(type, cerasConstructorAttr) != null;
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
