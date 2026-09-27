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
    /// Phase 2.4: emits one open generic formatter template per user generic type definition and a
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
            var onBefore = MemberExplorer.FindCallback(definition, attrs, isBefore: true);
            var onAfter = MemberExplorer.FindCallback(definition, attrs, isBefore: false);
            var callbacks = new List<CallbackInfo>();
            if (onBefore != null) callbacks.Add(onBefore);
            if (onAfter != null) callbacks.Add(onAfter);
            bool needsCerasField = definition.IsReferenceType
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
                    // place (TODO Phase 3.2). This must NOT be gated on m.Type.IsReferenceType: for an
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
    }
}
