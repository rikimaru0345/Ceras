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
        public static List<SerializedMember> DiscoverMembers(SourceProductionContext context, INamedTypeSymbol type, AttributeBag attrs, List<string>? blacklistPatterns = null, List<Diagnostic>? diagnosticSink = null)
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

                        // A collection shape the project owner has decided Ceras does not support:
                        // ReadOnlyCollection<T>/ReadOnlyDictionary<TKey,TValue> (the built-in types and every
                        // subclass) and every List<T>/Dictionary<TKey,TValue> subclass. These are not
                        // describable-and-then-mishandled; they are three different failures, one of them
                        // silent - a List/Dictionary subclass with a copying constructor comes back empty
                        // from a migration, a ReadOnlyCollection<T> subclass cannot be written at all, and
                        // the base ReadOnlyCollection<T> cannot be told apart from the subclass. Reporting
                        // it here is the point: dropping the member used to be the only outcome, so the
                        // build succeeded and the value was gone. The member is still left out (the build
                        // fails either way, and emitting code for a member that cannot be written would
                        // only produce a second, worse error), and the type the member *reaches* is what
                        // is named, so a nested List<ReadOnlyCollection<int>> or
                        // ReadOnlyCollection<int>[] is reported for the argument that is actually refused
                        // rather than for the wrapper around it.
                        var refusedCollection = FindUnsupportedCollectionType(field.Type);
                        if (refusedCollection != null)
                        {
                            string refusedMemberName = associatedProp?.Name ?? field.Name;
                            string refusedTypeName = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            string refusedDeclaringTypeName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            string refusedName = refusedCollection.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            var refusedLocation = (associatedProp?.Locations.FirstOrDefault() ?? field.Locations.FirstOrDefault());
                            ReportMemberDiagnostic(diagnosticSink, Diagnostic.Create(
                                Diag.UnsupportedCollectionShapeMember,
                                refusedLocation,
                                refusedMemberName, refusedDeclaringTypeName,
                                refusedName == refusedTypeName ? refusedName : $"{refusedTypeName} (reaching '{refusedName}')"));
                            continue;
                        }

                        // Hard-banned types (delegates, enumerators, reflection) can never round-trip.
                        // Skipping the member used to be the only outcome: the payload simply did not
                        // contain it, so the save succeeded and the loaded value came back null with
                        // nothing to explain it. That silent loss is the bug, so it is reported and the
                        // member is still left out (the build fails either way, and emitting code for a
                        // member that cannot be written would only produce a second, worse error).
                        //
                        // The generator blacklist only prevents formatter *generation* for those types;
                        // a field of a blacklisted type is still included and requires a runtime custom
                        // formatter (a CERAS017 warning is emitted for engine objects). Neither it nor
                        // an explicit [CerasSerializable<T>] registration lifts a ban - declaring a type
                        // says "this type is part of my graph", which cannot make a shape writable - and
                        // a custom formatter is not accepted as a way out either. See the diagnostic.
                        if (IsBanned(field.Type))
                        {
                            string bannedMemberName = associatedProp?.Name ?? field.Name;
                            string bannedTypeName = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            string bannedDeclaringTypeName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            var bannedLocation = (associatedProp?.Locations.FirstOrDefault() ?? field.Locations.FirstOrDefault());
                            ReportMemberDiagnostic(diagnosticSink, Diagnostic.Create(
                                Diag.BannedMemberType,
                                bannedLocation,
                                bannedMemberName, bannedDeclaringTypeName, bannedTypeName));
                            continue;
                        }

                        // A type the robust format has no built-in representation for, that is neither
                        // declared in this compilation nor marked serializable by its author, and lives in a
                        // System.* namespace (e.g. System.Uri, System.Numerics.BigInteger). The generator
                        // used to claim such a type "uses a built-in formatter" and leave it out of the
                        // manifest, so the build succeeded and the first robust Save failed at the bake with
                        // advice (declare it, add [CerasKnownType], register a custom formatter) that cannot
                        // work for a type the format cannot describe. Report it here, at the member, and drop
                        // the member so the rest of the generated file still compiles.
                        //
                        // A banned type is left alone whatever its own reporting decides to be — the two
                        // questions are independent, and banned types are being given their own diagnostic.
                        var unsupportedType = IsBanned(field.Type) ? null : FindUnsupportedFrameworkType(field.Type, attrs);
                        if (unsupportedType != null)
                        {
                            string memberName = associatedProp?.Name ?? field.Name;
                            string typeName = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            string containingTypeName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            string unsupportedName = unsupportedType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            var location = (associatedProp?.Locations.FirstOrDefault() ?? field.Locations.FirstOrDefault());
                            ReportMemberDiagnostic(diagnosticSink, Diagnostic.Create(
                                Diag.UnsupportedFrameworkMemberType,
                                location,
                                memberName, containingTypeName, typeName, unsupportedName));
                            continue;
                        }

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
        /// <para>
        /// Without <paramref name="blacklistPatterns"/> this is exactly the shape ban the runtime
        /// applies (see <c>BannedTypes.IsBannedShape</c>) and it is the *only* reason a member is
        /// reported: a member of a type the user marked <c>[Exclude]</c> was already handled above
        /// (the type is not serialized at all, which is what the attribute asks for) and a type that
        /// merely matches the user's generator blacklist is not a ban, it only means "no generated
        /// formatter for this type".
        /// </para>
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

        public static void ValidateDuplicateWireNames(SourceProductionContext context, INamedTypeSymbol type, List<SerializedMember> members, AttributeBag attrs)
        {
            // An enum member has no serialized member list of its own, but its name is recorded in the
            // schema exactly the same way, so the same uniqueness rule applies to it.
            if (type.TypeKind == TypeKind.Enum)
            {
                ValidateDuplicateEnumWireNames(context, type, attrs);
                return;
            }

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

        /// <summary>
        /// Two enum members may not be recorded under one name. The name is how a value is matched
        /// across a migration (<c>MigrationPlanBuilder.BuildEnumMap</c>), so a duplicate would make the
        /// file's entry ambiguous rather than merely redundant.
        /// </summary>
        static void ValidateDuplicateEnumWireNames(SourceProductionContext context, INamedTypeSymbol type, AttributeBag attrs)
        {
            var nameToMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol field || !field.HasConstantValue) continue;

                string wireName = GetEnumMemberWireName(field, attrs);
                if (!nameToMembers.TryGetValue(wireName, out var members))
                    nameToMembers[wireName] = members = new List<string>();
                members.Add(field.Name);
            }

            foreach (var kvp in nameToMembers)
            {
                if (kvp.Value.Count <= 1) continue;

                var memberList = string.Join(", ", kvp.Value.Select(n => $"'{n}'"));
                context.ReportDiagnostic(Diagnostic.Create(Diag.DuplicateWireName, type.Locations.FirstOrDefault(), type.Name, kvp.Key, memberList));
            }
        }

        /// <summary>
        /// The name an enum member is recorded and matched under: <c>[CerasStableName]</c> when it has
        /// one, otherwise the C# member name. Mirrors <c>Ceras.Format.EnumWireName.Of</c>, which the
        /// runtime uses for a declared/blacklisted enum and for the migration map.
        /// </summary>
        public static string GetEnumMemberWireName(IFieldSymbol field, AttributeBag attrs)
        {
            if (attrs?.SerializedAs != null)
            {
                foreach (var attr in field.GetAttributes())
                {
                    if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrs.SerializedAs)) continue;
                    if (TypeHelpers.TryGetAttributeArgument<string>(attr, "Name", out var stableName) && !string.IsNullOrEmpty(stableName))
                        return stableName;
                }
            }

            return field.Name;
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
        /// Finds the callback of the requested kind. The search walks from the most derived type up; the
        /// first declaring type that has a matching method wins, so a derived declaration shadows a base
        /// one, while a private base callback is still found (the generated writer/reader invokes it
        /// through an UnsafeAccessor). Within one declaring type the parameterless form is preferred over
        /// <c>(MigrationContext)</c> over <c>(CerasSerializer)</c>, matching the runtime callback selection.
        /// <para>
        /// <c>(MigrationContext)</c> is only a legal shape for a <see cref="CallbackKind.Deserialize"/>
        /// callback: while saving there is no migration, so <see cref="ReportUnsupportedCallbacks"/>
        /// reports it as an error instead of it being dropped here.
        /// </para>
        /// </summary>
        public static CallbackInfo FindCallback(INamedTypeSymbol type, AttributeBag attrs, CallbackKind kind, bool isBefore)
        {
            var attrType = kind == CallbackKind.Serialize
                ? (isBefore ? attrs.OnBeforeSerialize : attrs.OnAfterSerialize)
                : (isBefore ? attrs.OnBeforeDeserialize : attrs.OnAfterDeserialize);
            if (attrType == null) return null;

            for (var t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            {
                CallbackInfo best = null;
                int bestRank = int.MaxValue;

                foreach (var member in t.GetMembers())
                {
                    if (member is not IMethodSymbol m) continue;
                    if (!HasAttribute(m, attrType)) continue;
                    var paramKind = ClassifyCallbackParameter(m, attrs, kind);
                    if (paramKind == null) continue;

                    int rank = CallbackRank(paramKind.Value);
                    if (best == null || rank < bestRank)
                    {
                        best = new CallbackInfo(m, kind, isBefore, paramKind.Value);
                        bestRank = rank;
                    }
                }

                if (best != null) return best;
            }

            return null;
        }

        /// <summary>
        /// The argument shape a callback method's parameter list has, or null when the signature is not one
        /// Ceras can invoke: wrong arity, wrong parameter type, a parameter that is not passed by value, a
        /// non-void return, a static, generic or async method, or — for a serialize callback — a
        /// <c>MigrationContext</c> parameter, which only describes a migration and there is none while
        /// saving.
        /// <para>
        /// The by-value and async rules are not stylistic. The emitted accessor declares a by-value
        /// parameter, so a <c>ref</c>/<c>out</c>/<c>in</c> target does not bind (and an
        /// <c>async void</c> callback returns at its first <c>await</c>, so it could not be ordered around
        /// the member writes it is supposed to bracket even when it does bind). Both would otherwise be
        /// reported as "supported" and produce a callback that cannot honour its contract.
        /// </para>
        /// </summary>
        static CallbackParameterKind? ClassifyCallbackParameter(IMethodSymbol m, AttributeBag attrs, CallbackKind kind)
        {
            if (m.IsStatic || !m.ReturnsVoid || m.IsGenericMethod || m.IsAsync) return null;

            var ps = m.Parameters;
            if (ps.Length == 0)
                return CallbackParameterKind.None;
            if (ps.Length != 1 || ps[0].RefKind != RefKind.None) return null;
            if (attrs.MigrationContextType != null &&
                SymbolEqualityComparer.Default.Equals(ps[0].Type, attrs.MigrationContextType))
                return kind == CallbackKind.Deserialize ? CallbackParameterKind.MigrationContext : null;
            if (attrs.CerasSerializerType != null &&
                SymbolEqualityComparer.Default.Equals(ps[0].Type, attrs.CerasSerializerType))
                return CallbackParameterKind.Serializer;
            return null;
        }

        /// <summary>
        /// A callback whose signature Ceras cannot invoke.
        /// </summary>
        public sealed class UnsupportedCallback
        {
            public IMethodSymbol Method { get; }
            public string AttributeName { get; }
            public string Reason { get; }
            public string RequiredShape { get; }

            public UnsupportedCallback(IMethodSymbol method, string attributeName, string reason, string requiredShape)
            {
                Method = method;
                AttributeName = attributeName;
                Reason = reason;
                RequiredShape = requiredShape;
            }
        }

        /// <summary>
        /// Reports every callback method on <paramref name="type"/> (including inherited ones) whose
        /// signature <see cref="FindCallback"/> cannot select. Without this a wrong signature would simply
        /// never run: the generated writer/reader would emit no call for it, so the code the author wrote
        /// — and the attribute's promise that it is called — would be silently dropped. Failing the build
        /// with the offending method and the reason is the actionable alternative.
        /// <para>
        /// All four attributes are covered, not just the serialize ones: the same rule applies to both
        /// directions, and a deserialization callback that is skipped here would be silently dropped just
        /// as silently. The two directions differ only in whether a <c>MigrationContext</c> parameter is
        /// one of the legal shapes.
        /// </para>
        /// <para>
        /// Every attributed method is reported, not just the ones that would have been selected had they
        /// been legal: a method whose shape is wrong is wrong wherever it sits in the hierarchy, and the
        /// fix (correct the signature, or drop the attribute) is the same either way. Methods of a type
        /// that gets no generated formatter are not reported here — the reflection path reports those at
        /// runtime instead.
        /// </para>
        /// </summary>
        public static void ReportUnsupportedCallbacks(
            SourceProductionContext context, INamedTypeSymbol type, AttributeBag attrs)
        {
            ReportOne(attrs.OnBeforeSerialize, "OnBeforeSerialize", CallbackKind.Serialize);
            ReportOne(attrs.OnAfterSerialize, "OnAfterSerialize", CallbackKind.Serialize);
            ReportOne(attrs.OnBeforeDeserialize, "OnBeforeDeserialize", CallbackKind.Deserialize);
            ReportOne(attrs.OnAfterDeserialize, "OnAfterDeserialize", CallbackKind.Deserialize);

            void ReportOne(INamedTypeSymbol? attrType, string attributeName, CallbackKind kind)
            {
                if (attrType == null) return;

                foreach (var problem in FindUnsupportedCallbacks(type, attrs, attrType, attributeName, kind))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diag.UnsupportedCallbackSignature,
                        problem.Method.Locations.FirstOrDefault() ?? type.Locations.FirstOrDefault(),
                        problem.AttributeName,
                        problem.Method.Name,
                        type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        problem.Method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        problem.Reason,
                        problem.RequiredShape));
                }
            }
        }

        static IEnumerable<UnsupportedCallback> FindUnsupportedCallbacks(
            INamedTypeSymbol type, AttributeBag attrs, INamedTypeSymbol attrType,
            string attributeName, CallbackKind kind)
        {
            for (var t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            {
                foreach (var member in t.GetMembers())
                {
                    if (member is not IMethodSymbol m) continue;
                    if (!HasAttribute(m, attrType)) continue;
                    if (ClassifyCallbackParameter(m, attrs, kind) != null) continue;

                    yield return new UnsupportedCallback(
                        m, attributeName,
                        DescribeUnsupportedCallback(m, attrs, kind),
                        RequiredCallbackShape(kind));
                }
            }
        }

        static string RequiredCallbackShape(CallbackKind kind) => kind == CallbackKind.Serialize
            ? "A serialize callback must be a non-static, non-generic, non-async instance method that returns void and takes either no parameter or a single CerasSerializer parameter, passed by value. A MigrationContext parameter is not available while saving, because there is no migration"
            : "A deserialize callback must be a non-static, non-generic, non-async instance method that returns void and takes either no parameter or a single parameter, passed by value, of type CerasSerializer or MigrationContext";

        /// <summary>Why a particular attributed callback method cannot be invoked, in user-facing terms.</summary>
        static string DescribeUnsupportedCallback(IMethodSymbol m, AttributeBag attrs, CallbackKind kind)
        {
            if (m.IsGenericMethod) return "it is a generic method";
            if (m.IsStatic) return "it is static; a callback must be an instance method";
            if (m.IsAsync) return "it is async, so it would return at its first await and could not be ordered around the member writes it is supposed to bracket";
            if (!m.ReturnsVoid) return $"it returns '{m.ReturnType.ToDisplayString()}' instead of void";

            var ps = m.Parameters;
            if (ps.Length != 1) return $"it takes {ps.Length} parameters; a callback takes none or exactly one";
            if (ps[0].RefKind != RefKind.None)
                return $"its parameter is passed by '{ps[0].RefKind.ToString().ToLowerInvariant()}', but a callback parameter is always passed by value";

            var paramType = ps[0].Type;
            if (kind == CallbackKind.Serialize &&
                attrs.MigrationContextType != null &&
                SymbolEqualityComparer.Default.Equals(paramType, attrs.MigrationContextType))
                return "it takes a MigrationContext parameter, but there is no migration while saving; " +
                       "a MigrationContext parameter is only supported on [OnBeforeDeserialize]/[OnAfterDeserialize]";

            return $"its parameter has type '{paramType.ToDisplayString()}'";
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
