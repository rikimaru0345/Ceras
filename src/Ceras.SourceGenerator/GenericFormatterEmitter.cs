using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using static Ceras.SourceGenerator.TypeHelpers;
using static Ceras.SourceGenerator.MemberExplorer;

namespace Ceras.SourceGenerator
{
    /// <summary>
    /// Emits one open generic formatter template per user generic type definition and a
    /// thin per-closed-type subclass carrying the closed schema. Serialize/Deserialize are written
    /// once for the definition and reused by every closed instantiation.
    ///
    /// Member formatters are injected as <c>IFormatter&lt;openType&gt;</c> (resolved per closed
    /// instantiation by Ceras injection); the fields are <c>protected</c> because Ceras injection
    /// only sees inherited non-private fields on the derived subclass.
    /// </summary>
    internal static class GenericFormatterEmitter
    {
        public static string GenericBaseName(INamedTypeSymbol definition)
            => SanitizeIdentifier(definition.ToDisplayString(FullyQualifiedNoGlobal)) + "Formatter";

        public static void WriteGenericBase(StringBuilder sb, StringBuilder namespaceHelpers, Compilation compilation, SourceProductionContext context,
            INamedTypeSymbol definition, List<SerializedMember> members, AttributeBag attrs)
        {
            var ps = AccessorEmitter.GetAllTypeParameters(definition);
            string typeParams = "<" + string.Join(", ", ps.Select(p => p.Name)) + ">";
            string constraints = AccessorEmitter.EmitConstraintClausesPublic(ps);
            string typeFqn = definition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string baseName = GenericBaseName(definition);
            var bestCtor = definition.IsReferenceType ? GetBestConstructor(definition, attrs.CerasConstructor, members) : null;

            // Reported once per definition (the open formatter is emitted once and shared by every closed
            // instantiation), so a bad signature is diagnosed once no matter how many closures exist.
            MemberExplorer.ReportUnsupportedCallbacks(context, definition, attrs);

            var beforeSerialize = MemberExplorer.FindCallback(definition, attrs, CallbackKind.Serialize, isBefore: true);
            var afterSerialize = MemberExplorer.FindCallback(definition, attrs, CallbackKind.Serialize, isBefore: false);
            var onBefore = MemberExplorer.FindCallback(definition, attrs, CallbackKind.Deserialize, isBefore: true);
            var onAfter = MemberExplorer.FindCallback(definition, attrs, CallbackKind.Deserialize, isBefore: false);
            var callbacks = new List<CallbackInfo>();
            if (beforeSerialize != null) callbacks.Add(beforeSerialize);
            if (afterSerialize != null) callbacks.Add(afterSerialize);
            if (onBefore != null) callbacks.Add(onBefore);
            if (onAfter != null) callbacks.Add(onAfter);
            bool needsCerasField = definition.IsReferenceType
                || (beforeSerialize != null && beforeSerialize.ParameterKind == CallbackParameterKind.Serializer)
                || (afterSerialize != null && afterSerialize.ParameterKind == CallbackParameterKind.Serializer)
                || (onBefore != null && onBefore.ParameterKind == CallbackParameterKind.Serializer)
                || (onAfter != null && onAfter.ParameterKind == CallbackParameterKind.Serializer);
            var accessors = new AccessorEmitter(compilation, context.ReportDiagnostic, definition, members, bestCtor, callbacks.Count > 0 ? callbacks : null);

            sb.AppendLine($"    internal class {baseName}{typeParams} : global::Ceras.Formatters.FormatterBase<{typeFqn}>{constraints}");
            sb.AppendLine("    {");
            if (needsCerasField)
                sb.AppendLine("        protected global::Ceras.CerasSerializer _ceras;");
            sb.AppendLine();

            // Formatter fields: IFormatter<openType> so injection resolves the right formatter per
            // closed instantiation (reference wrappers included). Protected for inherited injection.
            //
            // CASE A: two distinct member types can sanitize to the same generated field name; failing
            // early is better than silently dropping the later field. (CASE B does not apply here: the
            // field type is always the universal IFormatter<openType>, so no generated formatter type is
            // named at compile time and there is no broken reference to detect.)
            ValidateFormatterFieldNameCollisions(context, definition, members);
            var seen = new HashSet<string>();
            foreach (var m in members)
            {
                if (IsFastPrimitive(m.Type)) continue;
                string fieldName = MakeFormatterFieldName(m.Type);
                if (!seen.Add(fieldName)) continue;
                string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                sb.AppendLine("        [global::Ceras.Formatters.CerasInject]");
                sb.AppendLine($"        protected global::Ceras.Formatters.IFormatter<{memberTypeFqn}> {fieldName};");
            }
            sb.AppendLine();

            sb.AppendLine("        protected override void Initialize()");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
            sb.AppendLine();

            // --- Serialize ---
            sb.AppendLine($"        public override void Serialize(global::System.Buffers.IBufferWriter<byte> writer, {typeFqn} value)");
            sb.AppendLine("        {");
            // Same bracketing as RobustCodeEmitter: [OnBeforeSerialize] before the first member read,
            // [OnAfterSerialize] after the last member write.
            if (beforeSerialize != null)
                sb.AppendLine($"            {accessors.CallbackCallExpression(beforeSerialize, "value", RobustCodeEmitter.CallbackArgument(beforeSerialize, "_ceras"))};");
            foreach (var m in members)
            {
                string fieldName = MakeFormatterFieldName(m.Type);
                string getAccess = accessors.MemberReadExpression(m, "value");
                if (IsFastPrimitive(m.Type))
                {
                    string writeMethod = RobustCodeEmitter.GetPrimitiveWriteMethod(m.Type);
                    string valueExp = getAccess;
                    if (m.Type.SpecialType == SpecialType.System_Boolean)
                        valueExp = $"(byte)({valueExp} ? 1 : 0)";
                    sb.AppendLine($"            global::Ceras.SerializerBinary.{writeMethod}(writer, {valueExp});");
                }
                else
                {
                    sb.AppendLine($"            {fieldName}.Serialize(writer, {getAccess});");
                }
            }
            if (afterSerialize != null)
                sb.AppendLine($"            {accessors.CallbackCallExpression(afterSerialize, "value", RobustCodeEmitter.CallbackArgument(afterSerialize, "_ceras"))};");
            sb.AppendLine("        }");
            sb.AppendLine();

            // --- Deserialize ---
            sb.AppendLine($"        public override void Deserialize(ref global::System.Buffers.SequenceReader<byte> reader, ref {typeFqn} value)");
            sb.AppendLine("        {");
            if (definition.IsReferenceType)
            {
                sb.AppendLine("                bool __ceras_fresh = _ceras.ConsumeCurrentObjectFresh();");
                sb.AppendLine("                if (value == null)");
                sb.AppendLine("                {");
                if (accessors.CtorIsDirectNew)
                    sb.AppendLine($"                    value = new {typeFqn}();");
                else
                    sb.AppendLine($"                    value = ({typeFqn})global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof({typeFqn}));");
                sb.AppendLine("                    __ceras_fresh = true;");
                sb.AppendLine("                    _ceras.SetCurrentObject(value);");
                if (accessors.CtorNeedsAccessor && bestCtor != null && bestCtor.Parameters.Length == 0)
                    sb.AppendLine($"                    {accessors.CtorCallTarget}__ceras_ctor(value);");
                sb.AppendLine("                }");
            }

            if (onBefore != null)
                sb.AppendLine($"                {accessors.CallbackCallExpression(onBefore, "value", RobustCodeEmitter.CallbackArgument(onBefore, "_ceras"))};");

            foreach (var m in members)
            {
                string fieldName = MakeFormatterFieldName(m.Type);
                string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                string localName = "l_" + SanitizeIdentifier(m.Name);

                if (IsFastPrimitive(m.Type))
                {
                    sb.AppendLine($"                {memberTypeFqn} {localName} = default;");
                    sb.AppendLine($"                {localName} = {RobustCodeEmitter.GetPrimitiveReadExpression(m.Type, "reader")};");
                }
                else
                {
                    // Seed the local from the existing member value so Load(ref existing) reuses it in
                    // place (see ConstructionReuseContractTests). This must NOT be gated on
                    // m.Type.IsReferenceType: for an
                    // open type parameter T that property is always false, so a closed reference-type
                    // instantiation would hand `default` to the injected ReferenceFormatter<T> and
                    // allocate a fresh object, violating the reuse contract. Seeding a value-type
                    // member is harmless because its formatter overwrites the local. Unreadable
                    // (write-only) members still start from default.
                    // The non-generic emitter (RobustCodeEmitter.WriteFormatterClass) has the same
                    // seeding but keeps the IsReferenceType guard: it never sees a bare open `T`
                    // member (closed generics are emitted here), so it needs no change.
                    string initial = CanReadMember(m)
                        ? accessors.MemberReadExpression(m, "value")
                        : "default";
                    sb.AppendLine($"                {memberTypeFqn} {localName} = {initial};");
                    sb.AppendLine($"                {fieldName}.Deserialize(ref reader, ref {localName});");
                }
            }

            if (bestCtor != null && bestCtor.Parameters.Length > 0)
            {
                var args = string.Join(", ", bestCtor.Parameters.Select(p =>
                {
                    string normParam = NormalizeName(p.Name);
                    var match = members.FirstOrDefault(m => NormalizeName(m.Name) == normParam || NormalizeName(m.StableName) == normParam);
                    return match != null
                        ? accessors.CtorParameterModifier(p) + "l_" + SanitizeIdentifier(match.Name)
                        : RobustCodeEmitter.FormatDefaultArgument(p);
                }));
                sb.AppendLine("                if (__ceras_fresh)");
                sb.AppendLine("                {");
                sb.AppendLine($"                    {accessors.CtorCallTarget}__ceras_ctor(value, {args});");
                sb.AppendLine("                }");
            }

            foreach (var m in members)
            {
                string localName = "l_" + SanitizeIdentifier(m.Name);
                accessors.WriteMemberSet(sb, m, "value", localName, 16);
            }

            if (onAfter != null)
            {
                if (definition.IsReferenceType)
                    sb.AppendLine("                _ceras.EnqueueDeserializeCallback(value, __ceras_cb_after_invoke);");
                else
                    sb.AppendLine($"                {accessors.CallbackCallExpression(onAfter, "value", RobustCodeEmitter.CallbackArgument(onAfter, "_ceras"))};");
            }
            sb.AppendLine("        }");
            sb.AppendLine();

            if (onAfter != null && definition.IsReferenceType)
            {
                string call = accessors.CallbackCallExpression(onAfter, "__target", RobustCodeEmitter.CallbackArgument(onAfter, "__ceras"));
                sb.AppendLine("        static readonly global::System.Action<global::Ceras.CerasSerializer, object> __ceras_cb_after_invoke =");
                sb.AppendLine($"            static (__ceras, __target) => {call};");
                sb.AppendLine();
            }

            accessors.EmitDeclarations(sb, namespaceHelpers);

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        public static void WriteGenericDerived(StringBuilder sb, INamedTypeSymbol closedType, INamedTypeSymbol definition,
            List<SerializedMember> closedMembers, AttributeBag attrs)
        {
            string closedName = MakeFormatterClassName(closedType);
            string baseName = GenericBaseName(definition);
            string args = AccessorEmitter.TypeArgsListPublic(closedType);

            sb.AppendLine($"    internal sealed class {closedName} : {baseName}{args}");
            sb.AppendLine("    {");
            sb.Append($"        internal static readonly global::Ceras.Format.SchemaTypeInfo Schema = ");
            sb.Append(RobustCodeEmitter.BuildSchemaExpression(closedType, closedMembers, attrs));
            sb.AppendLine(";");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        /// <summary>
        /// The closed instantiation's members in the order the shared writer actually emits them: the
        /// order the <em>definition's</em> members sort in, restricted to the members this closure has.
        /// <para>
        /// Every closure of a definition is written by one emitted class - the open definition's - so the
        /// payload order is fixed by that class and is computed from the <em>declared</em> member types.
        /// A member declared as a type parameter therefore sorts under its parameter's name
        /// (<c>T Value</c> after <c>int Tag</c>), while the same member of a closed instantiation sorts
        /// under the substituted type (<c>Legacy.Coord Value</c> before <c>System.Int32 Tag</c>). The two
        /// orders differ for most arguments, and they used to differ here as well: the per-closed schema
        /// listed its fields in the closed order while the writer emitted the definition's, so the schema
        /// described a layout no writer ever produces. Nothing notices while the layout matches - the
        /// fast path reads no schema - and on a mismatch the migration reader walks a file's fields in
        /// the order its schema lists them, so it read the wrong members: a reference member came back
        /// null, a value came from the neighbouring field, and a stream that ran out of alignment.
        /// </para>
        /// <para>
        /// Members are matched by identity - name, field or property, and the declaring type's
        /// <em>definition</em>, so an inherited member maps to the definition that declared it. A member
        /// that cannot be placed, or one the definition writes but the closed member list does not
        /// contain, is reported (CERAS032) rather than guessed at: both mean the two lists disagree about
        /// what this type is, and a guessed order is a silently misread save.
        /// </para>
        /// </summary>
        public static List<SerializedMember> OrderMembersAsWritten(
            List<SerializedMember> closedMembers, List<SerializedMember> definitionMembers,
            INamedTypeSymbol closedType, SourceProductionContext context)
        {
            // The definition's members, in the order its writer emits them, keyed by member identity.
            var written = new Dictionary<string, SerializedMember>(StringComparer.Ordinal);
            foreach (var m in definitionMembers)
            {
                if (written.ContainsKey(MemberKey(m)))
                    ReportUnplaceable(context, closedType, "the definition declares more than one member named '" + m.Name + "'", m.Name);
                else
                    written[MemberKey(m)] = m;
            }

            var ordered = new List<SerializedMember>(closedMembers.Count);
            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in closedMembers)
            {
                string key = MemberKey(m);
                if (!written.ContainsKey(key))
                {
                    // Keep the schema well-formed; the diagnostic below fails the build.
                    ReportUnplaceable(context, closedType, "member '" + m.Name + "' has no counterpart in the definition", m.Name);
                    ordered.Add(m);
                    continue;
                }

                placed.Add(key);
                ordered.Add(m);
            }

            foreach (var m in definitionMembers)
            {
                // The shared writer emits every one of these, so a schema that leaves one out describes a
                // shorter payload than the writer produces.
                if (!placed.Contains(MemberKey(m)))
                    ReportUnplaceable(context, closedType, "member '" + m.Name + "' is written for every closure but is not part of this type's schema", m.Name);
            }

            return OrderByWrittenPosition(ordered, written);
        }

        /// <summary>
        /// Sorts by the definition's member order, keeping the given order among members that share a
        /// definition position (which can only happen when the two lists disagree, and is reported above).
        /// </summary>
        static List<SerializedMember> OrderByWrittenPosition(
            List<SerializedMember> closedMembers, Dictionary<string, SerializedMember> written)
        {
            var position = new Dictionary<string, int>(StringComparer.Ordinal);
            int next = 0;
            foreach (var m in written.Values)
            {
                if (!position.ContainsKey(MemberKey(m)))
                    position[MemberKey(m)] = next++;
            }

            return closedMembers
                .Select((m, i) => (member: m, index: i, key: MemberKey(m)))
                .OrderBy(x => position.TryGetValue(x.key, out var p) ? p : int.MaxValue)
                .ThenBy(x => x.index)
                .Select(x => x.member)
                .ToList();
        }

        /// <summary>
        /// A member's identity across an open and a closed instantiation: the same name, the same kind,
        /// and the same declaring <em>definition</em> - so <c>Box&lt;T&gt;</c> and <c>Box&lt;int&gt;</c>
        /// name the same member, as do <c>Base&lt;T&gt;</c> and <c>Base&lt;int&gt;</c> for one the
        /// definition inherited.
        /// </summary>
        static string MemberKey(SerializedMember m)
            => m.Name + "|" + (m.IsField ? "f" : "p") + "|" +
               m.DeclaringType.OriginalDefinition.ToDisplayString(SortKeyFormat);

        static void ReportUnplaceable(SourceProductionContext context, INamedTypeSymbol closedType, string what, string memberName)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Diag.MemberOrderNotWritten, closedType.Locations.FirstOrDefault(), what,
                closedType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                memberName));
        }
    }
}
