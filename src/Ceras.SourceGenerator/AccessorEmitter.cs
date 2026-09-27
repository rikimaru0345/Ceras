using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using static Ceras.SourceGenerator.TypeHelpers;

namespace Ceras.SourceGenerator
{
    /// <summary>
    /// Centralizes generation of member/constructor access for the robust formatters.
    ///
    /// Rules (see TODO.md Phase 1.2):
    ///  * Accessible members use ordinary direct access (<c>value.Member</c>).
    ///  * Inaccessible members on a non-generic declaring type use a plain
    ///    <c>[UnsafeAccessor]</c> emitted directly in the formatter.
    ///  * Inaccessible members on a generic declaring type use a nested generic
    ///    accessor helper whose type parameters match the target's (the only shape
    ///    accepted by the runtime since .NET 9: "type parameters must match in form
    ///    and index", with identical constraints).
    ///  * Accessors must not walk the type hierarchy; the declaring type is named
    ///    explicitly (the runtime examines only the specified type).
    ///  * Inaccessible parameter/signature types use <c>[UnsafeAccessorType]</c> when
    ///    running on .NET 10+ (the attribute exists); otherwise a diagnostic is
    ///    reported instead of emitting malformed code.
    /// </summary>
    internal sealed class AccessorEmitter
    {
        readonly Compilation _compilation;
        readonly Action<Diagnostic> _report;
        readonly INamedTypeSymbol _formattedType;
        readonly List<SerializedMember> _members;
        readonly IMethodSymbol? _ctor;
        readonly List<CallbackInfo>? _callbacks;
        readonly bool _hasUnsafeAccessorType;

        const string CtorMethodName = "__ceras_ctor";

        public AccessorEmitter(Compilation compilation, Action<Diagnostic> report,
            INamedTypeSymbol formattedType, List<SerializedMember> members, IMethodSymbol? ctor,
            List<CallbackInfo>? callbacks = null)
        {
            _compilation = compilation;
            _report = report;
            _formattedType = formattedType;
            _members = members;
            _ctor = ctor;
            _callbacks = callbacks;
            _hasUnsafeAccessorType =
                compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.UnsafeAccessorTypeAttribute") != null;
        }

        /// <summary>True when a parameterless accessible constructor can be invoked with a plain <c>new</c>.</summary>
        public bool CtorIsDirectNew =>
            _ctor != null &&
            _ctor.Parameters.Length == 0 &&
            _compilation.IsSymbolAccessibleWithin(_ctor, _compilation.Assembly);

        /// <summary>True when the constructor must be invoked through an UnsafeAccessor (not a plain <c>new</c>).</summary>
        public bool CtorNeedsAccessor =>
            _formattedType.IsReferenceType && _ctor != null && !CtorIsDirectNew;

        // ------------------------------------------------------------------
        // Emission
        // ------------------------------------------------------------------

        public void EmitDeclarations(StringBuilder sb) => EmitDeclarations(sb, sb);

        /// <summary>
        /// Emits accessor declarations. Non-generic accessors go into <paramref name="sb"/> (inside
        /// the formatter class); generic helper classes go into <paramref name="genericSink"/>.
        /// Generic helpers must live at namespace scope when the formatter itself is generic,
        /// otherwise the nested helper's type parameters shadow the outer formatter's and the
        /// runtime's generic signature match fails.
        /// </summary>
        public void EmitDeclarations(StringBuilder sb, StringBuilder genericSink)
        {
            // Group members that need an accessor by the generic definition of their declaring type.
            var groups = new Dictionary<INamedTypeSymbol, List<SerializedMember>>(SymbolEqualityComparer.Default);
            var order = new List<INamedTypeSymbol>();
            foreach (var m in _members)
            {
                if (MemberExplorer.GetAccessHelperRequirement(m) == AccessHelperRequirement.None)
                    continue;

                var def = m.DeclaringType.OriginalDefinition;
                if (!groups.TryGetValue(def, out var list))
                {
                    list = new List<SerializedMember>();
                    groups[def] = list;
                    order.Add(def);
                }
                list.Add(m);
            }

            var ctorDef = _formattedType.OriginalDefinition;
            if (CtorNeedsAccessor && !groups.ContainsKey(ctorDef))
            {
                groups[ctorDef] = new List<SerializedMember>();
                order.Add(ctorDef);
            }

            // Callbacks are invoked through an UnsafeAccessor as well, so a callback's declaring type
            // (which may be a base type) participates in the same grouping.
            var callbackGroups = new Dictionary<INamedTypeSymbol, List<CallbackInfo>>(SymbolEqualityComparer.Default);
            if (_callbacks != null)
            {
                foreach (var cb in _callbacks)
                {
                    if (cb.Method.IsStatic) continue;
                    var cbDef = cb.Method.ContainingType.OriginalDefinition;
                    if (!callbackGroups.TryGetValue(cbDef, out var list))
                    {
                        list = new List<CallbackInfo>();
                        callbackGroups[cbDef] = list;
                    }
                    list.Add(cb);

                    if (!groups.ContainsKey(cbDef))
                    {
                        groups[cbDef] = new List<SerializedMember>();
                        order.Add(cbDef);
                    }
                }
            }

            foreach (var def in order)
            {
                bool generic = IsGenericContext(def);
                bool accessible = IsTypeAccessible(def);
                bool hasCtor = CtorNeedsAccessor && SymbolEqualityComparer.Default.Equals(def, ctorDef);
                callbackGroups.TryGetValue(def, out var callbacks);

                if (generic)
                {
                    if (!accessible)
                    {
                        ReportUnsupported(def, "its declaring type is a generic type that generated code cannot name");
                        continue;
                    }
                    EmitGenericHelper(genericSink, def, groups[def], hasCtor, callbacks);
                }
                else if (!accessible)
                {
                    EmitInaccessibleInline(sb, def, groups[def], hasCtor, callbacks);
                }
                else
                {
                    EmitInline(sb, def, groups[def], hasCtor, callbacks);
                }
            }
        }

        void EmitInline(StringBuilder sb, INamedTypeSymbol decl, List<SerializedMember> members, bool hasCtor, List<CallbackInfo>? callbacks)
        {
            string declFqn = decl.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string recvDecl = decl.IsValueType ? "ref " + declFqn : declFqn;

            if (hasCtor)
                EmitCtorAccessor(sb, recvDecl, openTypes: false);

            foreach (var m in members)
                EmitMemberAccessors(sb, m, recvDecl, openTypes: false);

            EmitCallbackAccessors(sb, callbacks, recvDecl);
        }

        void EmitInaccessibleInline(StringBuilder sb, INamedTypeSymbol decl, List<SerializedMember> members, bool hasCtor, List<CallbackInfo>? callbacks)
        {
            if (!_hasUnsafeAccessorType)
            {
                ReportUnsupported(decl, "its declaring type is inaccessible and [UnsafeAccessorType] is unavailable (requires .NET 10+)");
                return;
            }

            string attr = $"[global::System.Runtime.CompilerServices.UnsafeAccessorType({QuoteLiteral(GetMetadataTypeName(decl))})] ";
            string recvDecl = attr + "object";

            if (hasCtor)
                EmitCtorAccessor(sb, recvDecl, openTypes: false);

            foreach (var m in members)
                EmitMemberAccessors(sb, m, recvDecl, openTypes: false);

            EmitCallbackAccessors(sb, callbacks, recvDecl);
        }

        void EmitGenericHelper(StringBuilder sb, INamedTypeSymbol originalDef, List<SerializedMember> members, bool hasCtor, List<CallbackInfo>? callbacks)
        {
            var ps = new List<ITypeParameterSymbol>();
            var args = new List<ITypeSymbol>();
            CollectTypeParamsAndArgs(originalDef, ps, args);

            string typeParams = "<" + string.Join(", ", ps.Select(p => p.Name)) + ">";
            string helperName = GenericHelperName(originalDef);
            string ownerRef = originalDef.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string recvDecl = originalDef.IsValueType ? "ref " + ownerRef : ownerRef;

            sb.AppendLine($"        internal static class {helperName}{typeParams}{EmitConstraintClauses(ps)}");
            sb.AppendLine("        {");

            if (hasCtor)
                EmitCtorAccessor(sb, recvDecl, openTypes: true);

            foreach (var m in members)
                EmitMemberAccessors(sb, m, recvDecl, openTypes: true);

            EmitCallbackAccessors(sb, callbacks, recvDecl);

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        void EmitCallbackAccessors(StringBuilder sb, List<CallbackInfo>? callbacks, string recvDecl)
        {
            if (callbacks == null) return;

            foreach (var cb in callbacks)
            {
                string args = cb.HasArgument ? ", " + cb.ArgumentTypeFqn + " arg" : "";
                sb.AppendLine($"        [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = {QuoteLiteral(cb.Method.Name)})]");
                sb.AppendLine($"        internal static extern void {CallbackAccessorName(cb)}({recvDecl} instance{args});");
                sb.AppendLine();
            }
        }

        void EmitMemberAccessors(StringBuilder sb, SerializedMember m, string recvDecl, bool openTypes)
        {
            var req = MemberExplorer.GetAccessHelperRequirement(m);
            if (req == AccessHelperRequirement.None) return;

            if (!IsMemberNameable(m, openTypes))
            {
                ReportUnsupported(m.DeclaringType, $"member '{m.Name}' has type '{m.Type.ToDisplayString()}' which generated code cannot name");
                return;
            }

            string methodBase = MemberMethodBase(m);
            string memberType = MemberSignatureType(m, openTypes);

            if (m.IsField)
            {
                sb.AppendLine($"        [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = {QuoteLiteral(m.Name)})]");
                sb.AppendLine($"        internal static extern ref {memberType} {methodBase}_ref({recvDecl} instance);");
                sb.AppendLine();
            }
            else
            {
                if ((req & AccessHelperRequirement.Read) != 0)
                {
                    sb.AppendLine($"        [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = {QuoteLiteral("get_" + m.Name)})]");
                    sb.AppendLine($"        internal static extern {memberType} {methodBase}_get({recvDecl} instance);");
                    sb.AppendLine();
                }
                if ((req & AccessHelperRequirement.Write) != 0)
                {
                    sb.AppendLine($"        [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = {QuoteLiteral("set_" + m.Name)})]");
                    sb.AppendLine($"        internal static extern void {methodBase}_set({recvDecl} instance, {memberType} value);");
                    sb.AppendLine();
                }
            }
        }

        void EmitCtorAccessor(StringBuilder sb, string recvDecl, bool openTypes)
        {
            if (_ctor == null) return;
            var parameters = openTypes ? _ctor.OriginalDefinition.Parameters : _ctor.Parameters;
            var parts = new List<string> { recvDecl + " instance" };

            foreach (var p in parameters)
            {
                if (!IsNameable(p.Type))
                {
                    if (_hasUnsafeAccessorType)
                    {
                        parts.Add($"[global::System.Runtime.CompilerServices.UnsafeAccessorType({QuoteLiteral(GetMetadataTypeName(p.Type))})] object {p.Name}");
                    }
                    else
                    {
                        ReportUnsupported(_formattedType, $"constructor parameter '{p.Name}' has type '{p.Type.ToDisplayString()}' which generated code cannot name");
                        return;
                    }
                }
                else
                {
                    parts.Add($"{ParameterModifier(p)}{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}");
                }
            }

            sb.AppendLine($"        [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \".ctor\")]");
            sb.AppendLine($"        internal static extern void {CtorMethodName}({string.Join(", ", parts)});");
            sb.AppendLine();
        }

        // ------------------------------------------------------------------
        // Call sites
        // ------------------------------------------------------------------

        /// <summary>Expression that reads a member (a <c>ref</c> for fields).</summary>
        public string MemberReadExpression(SerializedMember m, string valueExpr)
        {
            if (m.IsReadAccessible) return $"{valueExpr}.{m.Name}";
            string call = MemberCallPrefix(m) + MemberMethodBase(m) + (m.IsField ? "_ref" : "_get");
            return $"{call}({ReceiverArg(m.DeclaringType, valueExpr)})";
        }

        /// <summary>Writes the assignment of <paramref name="rhs"/> into member <paramref name="m"/>.</summary>
        public void WriteMemberSet(StringBuilder sb, SerializedMember m, string targetExpr, string rhs, int indent)
        {
            string pad = new string(' ', indent);
            if (m.IsWriteAccessible)
            {
                sb.AppendLine($"{pad}{targetExpr}.{m.Name} = {rhs};");
                return;
            }

            string call = MemberCallPrefix(m) + MemberMethodBase(m);
            string arg = ReceiverArg(m.DeclaringType, targetExpr);
            if (m.IsField)
                sb.AppendLine($"{pad}{call}_ref({arg}) = {rhs};");
            else
                sb.AppendLine($"{pad}{call}_set({arg}, {rhs});");
        }

        /// <summary>Prefix used to call the constructor accessor, e.g. <c>""</c> or <c>Helper&lt;int&gt;.</c>.</summary>
        public string CtorCallTarget
        {
            get
            {
                if (IsGenericContext(_formattedType.OriginalDefinition))
                    return GenericHelperName(_formattedType.OriginalDefinition) + TypeArgsList(_formattedType) + ".";
                return "";
            }
        }

        /// <summary>Argument list (leading comma optional) for invoking the constructor accessor.</summary>
        public string CtorParameterModifier(IParameterSymbol p) => ParameterModifier(p);

        // ------------------------------------------------------------------
        // Callbacks
        // ------------------------------------------------------------------

        // The direction is part of the name: a type can declare all four callback attributes, and the
        // "before" accessor of one direction must not collide with the "before" accessor of the other.
        static string CallbackAccessorName(CallbackInfo cb)
            => (cb.Kind == CallbackKind.Serialize ? "__ceras_cb_serialize_" : "__ceras_cb_deserialize_")
               + (cb.IsBefore ? "before" : "after");

        /// <summary>
        /// The prefix needed to reach the callback accessor from the generated formatter. Generic
        /// declaring types place their accessors in the shared generic helper, just like members.
        /// </summary>
        public string CallbackCallTarget(CallbackInfo cb)
        {
            var decl = cb.Method.ContainingType;
            if (IsGenericContext(decl))
                return GenericHelperName(decl.OriginalDefinition) + TypeArgsList(decl) + ".";
            return "";
        }

        /// <summary>Expression that invokes a callback on <paramref name="targetExpr"/>.</summary>
        public string CallbackCallExpression(CallbackInfo cb, string targetExpr, string argumentExpr)
        {
            string target = CallbackCallTarget(cb);
            string recv = ReceiverArg(cb.Method.ContainingType, targetExpr);
            if (!cb.HasArgument)
                return $"{target}{CallbackAccessorName(cb)}({recv})";
            return $"{target}{CallbackAccessorName(cb)}({recv}, {argumentExpr})";
        }

        // ------------------------------------------------------------------
        // Naming / helpers
        // ------------------------------------------------------------------

        static string MemberMethodBase(SerializedMember m)
        {
            string safeName = SanitizeIdentifier(m.Name);
            string safeDecl = SanitizeIdentifier(m.DeclaringType.ToDisplayString(FullyQualifiedNoGlobal));
            return $"__ceras_{safeDecl}_{safeName}";
        }

        static string MemberCallPrefix(SerializedMember m)
        {
            if (IsGenericContext(m.DeclaringType))
                return GenericHelperName(m.DeclaringType.OriginalDefinition) + TypeArgsList(m.DeclaringType) + ".";
            return "";
        }

        static string GenericHelperName(INamedTypeSymbol originalDef)
            => "__CerasAccessors_" + SanitizeIdentifier(originalDef.ToDisplayString(FullyQualifiedNoGlobal));

        string ReceiverArg(INamedTypeSymbol decl, string valueExpr)
        {
            if (!IsTypeAccessible(decl)) return valueExpr; // object receiver via [UnsafeAccessorType]
            if (decl.IsValueType) return "ref " + valueExpr;
            return $"(({decl.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){valueExpr})";
        }

        static string ParameterModifier(IParameterSymbol p) => p.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => ""
        };

        static string MemberSignatureType(SerializedMember m, bool openTypes)
        {
            ITypeSymbol t = m.Type;
            if (openTypes)
            {
                if (m.MemberSymbol is IFieldSymbol f) t = f.OriginalDefinition.Type;
                else if (m.MemberSymbol is IPropertySymbol p) t = p.OriginalDefinition.Type;
            }
            return t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        bool IsMemberNameable(SerializedMember m, bool openTypes)
        {
            if (openTypes && m.MemberSymbol != null)
            {
                ITypeSymbol t = m.MemberSymbol is IFieldSymbol f ? f.OriginalDefinition.Type
                    : m.MemberSymbol is IPropertySymbol p ? p.OriginalDefinition.Type
                    : m.Type;
                return IsNameable(t);
            }
            return IsNameable(m.Type);
        }

        static bool IsGenericContext(INamedTypeSymbol t)
        {
            for (var c = t; c != null; c = c.ContainingType)
                if (c.TypeParameters.Length > 0) return true;
            return false;
        }

        static void CollectTypeParamsAndArgs(INamedTypeSymbol t, List<ITypeParameterSymbol> ps, List<ITypeSymbol> args)
        {
            if (t.ContainingType != null) CollectTypeParamsAndArgs(t.ContainingType, ps, args);
            ps.AddRange(t.TypeParameters);
            args.AddRange(t.TypeArguments);
        }

        /// <summary>All type parameters in scope for a (possibly nested) type, outermost first.</summary>
        public static List<ITypeParameterSymbol> GetAllTypeParameters(INamedTypeSymbol t)
        {
            var ps = new List<ITypeParameterSymbol>();
            var args = new List<ITypeSymbol>();
            CollectTypeParamsAndArgs(t, ps, args);
            return ps;
        }

        /// <summary>Ordered type-argument list (e.g. <c>&lt;int, string&gt;</c>) for a constructed type.</summary>
        public static string TypeArgsListPublic(INamedTypeSymbol constructed) => TypeArgsList(constructed);

        static string TypeArgsList(INamedTypeSymbol constructed)
        {
            var ps = new List<ITypeParameterSymbol>();
            var args = new List<ITypeSymbol>();
            CollectTypeParamsAndArgs(constructed, ps, args);
            return "<" + string.Join(", ", args.Select(a => a.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))) + ">";
        }

        public static string EmitConstraintClausesPublic(List<ITypeParameterSymbol> ps) => EmitConstraintClauses(ps);

        static string EmitConstraintClauses(List<ITypeParameterSymbol> ps)
        {
            var sb = new StringBuilder();
            foreach (var p in ps)
            {
                var parts = new List<string>();
                if (p.HasUnmanagedTypeConstraint) parts.Add("unmanaged");
                else if (p.HasValueTypeConstraint) parts.Add("struct");
                else if (p.HasReferenceTypeConstraint) parts.Add("class");
                else if (p.HasNotNullConstraint) parts.Add("notnull");

                foreach (var ct in p.ConstraintTypes)
                {
                    if (p.HasValueTypeConstraint && ct.SpecialType == SpecialType.System_ValueType) continue;
                    if (p.HasUnmanagedTypeConstraint) continue;
                    parts.Add(ct.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                }

                if (p.HasConstructorConstraint && !p.HasValueTypeConstraint && !p.HasUnmanagedTypeConstraint)
                    parts.Add("new()");

                if (parts.Count > 0)
                    sb.Append(" where ").Append(p.Name).Append(" : ").Append(string.Join(", ", parts));
            }
            return sb.ToString();
        }

        bool IsNameable(ITypeSymbol t)
        {
            switch (t)
            {
                case ITypeParameterSymbol:
                    return true;
                case IArrayTypeSymbol a:
                    return IsNameable(a.ElementType);
                case IPointerTypeSymbol p:
                    return IsNameable(p.PointedAtType);
                case INamedTypeSymbol n:
                    if (!IsTypeAccessible(n)) return false;
                    if (n.IsGenericType)
                        foreach (var a in n.TypeArguments)
                            if (!IsNameable(a)) return false;
                    return true;
                default:
                    return true;
            }
        }

        bool IsTypeAccessible(INamedTypeSymbol t)
        {
            for (var c = t; c != null; c = c.ContainingType)
                if (!_compilation.IsSymbolAccessibleWithin(c, _compilation.Assembly))
                    return false;
            return true;
        }

        static string GetMetadataTypeName(ITypeSymbol t)
        {
            if (t is not INamedTypeSymbol named) return t.ToDisplayString();

            var chain = new List<INamedTypeSymbol>();
            for (var c = named; c != null; c = c.ContainingType) chain.Add(c);
            chain.Reverse();

            var sb = new StringBuilder();
            if (named.ContainingNamespace != null && !named.ContainingNamespace.IsGlobalNamespace)
                sb.Append(named.ContainingNamespace.ToDisplayString()).Append('.');
            sb.Append(string.Join("+", chain.Select(c => c.MetadataName)));

            var asm = named.ContainingAssembly?.Name;
            if (!string.IsNullOrEmpty(asm)) sb.Append(", ").Append(asm);
            return sb.ToString();
        }

        void ReportUnsupported(INamedTypeSymbol type, string reason)
        {
            _report(Diagnostic.Create(Diag.UnsupportedAccessShape, type.Locations.FirstOrDefault(),
                type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), reason));
        }
    }
}
