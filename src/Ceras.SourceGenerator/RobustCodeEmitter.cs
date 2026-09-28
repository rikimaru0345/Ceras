using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using static Ceras.SourceGenerator.TypeHelpers;
using static Ceras.SourceGenerator.MemberExplorer;

namespace Ceras.SourceGenerator {
	internal static class RobustCodeEmitter {
		public static void WriteFormatterClass(
			StringBuilder sb,
			Compilation compilation,
			SourceProductionContext context,
			INamedTypeSymbol type,
			List<SerializedMember> members,
			AttributeBag attrs,
			HashSet<INamedTypeSymbol> generatedFormatterTypes,
			HashSet<INamedTypeSymbol> discoveredTypes,
			IReadOnlyDictionary<INamedTypeSymbol, string> noFormatterReason) {
			string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			string formatterName = MakeFormatterClassName(type);
			var bestCtor = type.IsReferenceType ? GetBestConstructor(type, attrs.CerasConstructor, members) : null;

			// A callback whose signature Ceras cannot invoke would simply never run, so it is reported
			// instead of dropped.
			MemberExplorer.ReportUnsupportedCallbacks(context, type, attrs);

			var beforeSerialize = MemberExplorer.FindCallback(type, attrs, CallbackKind.Serialize, isBefore: true);
			var afterSerialize = MemberExplorer.FindCallback(type, attrs, CallbackKind.Serialize, isBefore: false);
			var onBefore = MemberExplorer.FindCallback(type, attrs, CallbackKind.Deserialize, isBefore: true);
			var onAfter = MemberExplorer.FindCallback(type, attrs, CallbackKind.Deserialize, isBefore: false);
			var callbacks = new List<CallbackInfo>();
			if (beforeSerialize != null) callbacks.Add(beforeSerialize);
			if (afterSerialize != null) callbacks.Add(afterSerialize);
			if (onBefore != null) callbacks.Add(onBefore);
			if (onAfter != null) callbacks.Add(onAfter);
			bool needsCerasField = type.IsReferenceType
				|| (beforeSerialize != null && beforeSerialize.ParameterKind == CallbackParameterKind.Serializer)
				|| (afterSerialize != null && afterSerialize.ParameterKind == CallbackParameterKind.Serializer)
				|| (onBefore != null && onBefore.ParameterKind == CallbackParameterKind.Serializer)
				|| (onAfter != null && onAfter.ParameterKind == CallbackParameterKind.Serializer);
			var accessors = new AccessorEmitter(compilation, context.ReportDiagnostic, type, members, bestCtor, callbacks.Count > 0 ? callbacks : null);

			sb.AppendLine($"    internal sealed class {formatterName} : global::Ceras.Formatters.FormatterBase<{typeFqn}>");
			sb.AppendLine("    {");
			if (needsCerasField)
				sb.AppendLine("        global::Ceras.CerasSerializer _ceras;");
			sb.AppendLine();

			// --- Formatter fields (one per distinct member type) -------------------
			// CASE A: fail early if two distinct member types collapse to the same generated field name.
			ValidateFormatterFieldNameCollisions(context, type, members);
			var seenFieldNames = new HashSet<string>();
			foreach (var m in members) {
				if (TypeHelpers.IsFastPrimitive(m.Type)) continue;

				string fieldName = MakeFormatterFieldName(m.Type);
				if (!seenFieldNames.Add(fieldName)) continue;

				string concreteFormatterType = ResolveConcreteFormatterType(context, type, m, attrs, generatedFormatterTypes, discoveredTypes, noFormatterReason);
				sb.AppendLine("        [global::Ceras.Formatters.CerasInject]");
				sb.AppendLine($"        {concreteFormatterType} {fieldName};");
			}
			sb.AppendLine();

			// --- Initialize --------------------------------------------------------
			sb.AppendLine("        protected override void Initialize()");
			sb.AppendLine("        {");
			sb.AppendLine("        }");
			sb.AppendLine();

			// --- Schema descriptor --------------------------------------------------
			WriteSchemaDescriptor(sb, type, members, attrs);
			// --- Serialize ---------------------------------------------------------
			sb.AppendLine($"        public override void Serialize(global::System.Buffers.IBufferWriter<byte> writer, {typeFqn} value)");
			sb.AppendLine("        {");

			if (type.TypeKind == TypeKind.Enum) {
				var underlying = type.EnumUnderlyingType!;
				string underlyingFqn = underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

				// Always through the shared table (no special-cased int32/uint32 branches): an enum
				// must be encoded exactly like a plain value of its underlying type, otherwise the
				// generated reader and the interpreter disagree about its width.
				string writeMethod = GetPrimitiveWriteMethod(underlying);

				sb.AppendLine($"            global::Ceras.SerializerBinary.{writeMethod}(writer, ({underlyingFqn})value);");
			}
			else {
				// [OnBeforeSerialize] runs before the first member is read, so whatever it writes into the
				// object is what ends up in the stream; [OnAfterSerialize] runs after the last member has
				// been written, so it can observe (or finish) the state the writer just captured. This
				// matches the reflection fallback's Serialize, which brackets the same member loop.
				if (beforeSerialize != null) {
					sb.AppendLine($"            {accessors.CallbackCallExpression(beforeSerialize, "value", CallbackArgument(beforeSerialize, "_ceras"))};");
				}

				var serializeMembers = members;
				foreach (var m in serializeMembers) {
					string fieldName = MakeFormatterFieldName(m.Type);
					string getAccess = accessors.MemberReadExpression(m, "value");

					if (TypeHelpers.IsFastPrimitive(m.Type)) {
						string writeMethod = GetPrimitiveWriteMethod(m.Type);
						string valueExp = getAccess;
						// The primitive Write* overloads take the integer width they write. Cast the
						// member to that width so char/sbyte members compile and so the bytes match the
						// formatter/interpreter readers (which already read back through a cast).
						if (m.Type.SpecialType == SpecialType.System_Boolean)
							valueExp = $"(byte)({valueExp} ? 1 : 0)";
						else if (m.Type.SpecialType == SpecialType.System_SByte)
							valueExp = $"(byte)({valueExp})";
						else if (m.Type.SpecialType == SpecialType.System_Char)
							valueExp = $"(short)({valueExp})";
						sb.AppendLine($"            global::Ceras.SerializerBinary.{writeMethod}(writer, {valueExp});");
					}
					else {
						sb.AppendLine($"            {fieldName}.Serialize(writer, {getAccess});");
					}
				}

				if (afterSerialize != null) {
					sb.AppendLine($"            {accessors.CallbackCallExpression(afterSerialize, "value", CallbackArgument(afterSerialize, "_ceras"))};");
				}
			}
			sb.AppendLine("        }");
			sb.AppendLine();

			// --- Deserialize -------------------------------------------------------
			sb.AppendLine($"        public override void Deserialize(ref global::System.Buffers.SequenceReader<byte> reader, ref {typeFqn} value)");
			sb.AppendLine("        {");

			if (type.TypeKind == TypeKind.Enum) {
				var underlying = type.EnumUnderlyingType!;
				string readExpr = GetPrimitiveReadExpression(underlying, "reader");

				sb.AppendLine($"            value = ({typeFqn}){readExpr};");
				sb.AppendLine("        }");
			}
			else {
				if (type.IsReferenceType) {
					sb.AppendLine("                bool __ceras_fresh = _ceras.ConsumeCurrentObjectFresh();");
					sb.AppendLine("                if (value == null)");
					sb.AppendLine("                {");
					if (accessors.CtorIsDirectNew) {
						// Accessible parameterless constructor: ordinary allocation is enough.
						// Nothing can reference the instance before it is constructed, so we register after.
						sb.AppendLine($"                    value = new {typeFqn}();");
					}
					else {
						// Allocate uninitialized so the instance can be registered before the constructor runs.
						// This preserves identity for cyclic graphs whose constructor parameters reference
						// the object currently being built.
						sb.AppendLine($"                    value = ({typeFqn})global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof({typeFqn}));");
					}
					sb.AppendLine("                    __ceras_fresh = true;");
					sb.AppendLine("                    _ceras.SetCurrentObject(value);");

					if (accessors.CtorNeedsAccessor && bestCtor != null && bestCtor.Parameters.Length == 0) {
						// Inaccessible parameterless constructor: invoke it through the accessor.
						sb.AppendLine($"                    {accessors.CtorCallTarget}__ceras_ctor(value);");
					}
					sb.AppendLine("                }");
				}

				if (onBefore != null) {
					sb.AppendLine($"                {accessors.CallbackCallExpression(onBefore, "value", CallbackArgument(onBefore, "_ceras"))};");
				}

				var bestCtorForMapping = bestCtor;

				// Read all members into locals first (to support constructors with parameters and arbitrary field order)
				foreach (var m in members) {
					string fieldName = MakeFormatterFieldName(m.Type);
					string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
					string safeName = TypeHelpers.SanitizeIdentifier(m.Name);
					string localName = "l_" + safeName;

					if (TypeHelpers.IsFastPrimitive(m.Type)) {
						sb.AppendLine($"                {memberTypeFqn} {localName} = default;");
						string readExpr = GetPrimitiveReadExpression(m.Type, "reader");
						sb.AppendLine($"                {localName} = {readExpr};");
					}
					else {
						// Seed the local from the existing member value so that Load(ref existing) and
						// field-initialized containers are populated in place instead of being replaced.
						// This mirrors ReferenceFormatter.DeserializeUntyped and the confirmed reuse
						// contract (TODO Phase 3.2): reuse iff non-null and exact stored type; collections
						// clear+refill; arrays reuse only on exact length; null sets null; aliases resolve
						// through the object cache. Members that cannot be read start from default.
						string initial = (m.Type.IsReferenceType && MemberExplorer.CanReadMember(m))
							? accessors.MemberReadExpression(m, "value")
							: "default";
						sb.AppendLine($"                {memberTypeFqn} {localName} = {initial};");
						sb.AppendLine($"                {fieldName}.Deserialize(ref reader, ref {localName});");
					}
				}

				// Invoke the constructor (with parameters) now that all members were read into locals.
				// Only when this dispatch actually produced a new instance: reusing an existing object
				// must not run its constructor (or its field initializers) again.
				if (bestCtorForMapping != null && bestCtorForMapping.Parameters.Length > 0) {
					var args = string.Join(", ", bestCtorForMapping.Parameters.Select(p => {
						string normParam = NormalizeName(p.Name);
						var match = members.FirstOrDefault(m => {
							if (NormalizeName(m.Name) == normParam) return true;

							ISymbol? symbol = null;
							var curType = type;
							while (curType != null) {
								symbol = curType.GetMembers(m.Name).FirstOrDefault();
								if (symbol != null) break;
								curType = curType.BaseType;
							}

							if (symbol is IFieldSymbol field && field.IsImplicitlyDeclared && field.Name.EndsWith("k__BackingField")) {
								var mRegex = System.Text.RegularExpressions.Regex.Match(field.Name, @"^<(.*)>k__BackingField$");
								if (mRegex.Success && NormalizeName(mRegex.Groups[1].Value) == normParam)
									return true;
							}
							if (symbol is IPropertySymbol prop && NormalizeName(prop.Name) == normParam)
								return true;
							return false;
						});
						return match != null
							? accessors.CtorParameterModifier(p) + "l_" + TypeHelpers.SanitizeIdentifier(match.Name)
							: FormatDefaultArgument(p);
					}));
					sb.AppendLine("                if (__ceras_fresh)");
					sb.AppendLine("                {");
					sb.AppendLine($"                    {accessors.CtorCallTarget}__ceras_ctor(value, {args});");
					sb.AppendLine("                }");
				}

				// Assign locals to members
				foreach (var m in members) {
					string safeName = TypeHelpers.SanitizeIdentifier(m.Name);
					string localName = "l_" + safeName;

					// If it was a constructor parameter, it's already set (for classes).
					// But for consistency and to handle readonly fields (via UnsafeAccessor), we write it anyway.
					accessors.WriteMemberSet(sb, m, "value", localName, 16);
				}

				if (onAfter != null) {
					if (type.IsReferenceType) {
						// Defer graph-inspecting work until the whole root graph is populated.
						sb.AppendLine("                _ceras.EnqueueDeserializeCallback(value, __ceras_cb_after_invoke);");
					}
					else {
						// A boxed struct copy cannot be deferred; run it once the value is fully read.
						sb.AppendLine($"                {accessors.CallbackCallExpression(onAfter, "value", CallbackArgument(onAfter, "_ceras"))};");
					}
				}
				sb.AppendLine("        }");
			}

			if (onAfter != null && type.IsReferenceType) {
				string call = accessors.CallbackCallExpression(onAfter, "__target", CallbackArgument(onAfter, "__ceras"));
				sb.AppendLine("        static readonly global::System.Action<global::Ceras.CerasSerializer, object> __ceras_cb_after_invoke =");
				sb.AppendLine($"            static (__ceras, __target) => {call};");
				sb.AppendLine();
			}

			// --- Access Helpers (UnsafeAccessor / direct access) ---
			accessors.EmitDeclarations(sb);

			sb.AppendLine("    }");
			sb.AppendLine();
		}

		/// <summary>
		/// Produces the C# expression used for a <see cref="CerasConstructorAttribute"/> parameter that
		/// has no matching serializable member. Such a parameter is transient construction input (e.g. an
		/// "initiallyPaid" flag): the value is not stored in the save, so we pass the parameter's own
		/// declared default. The constructor body still runs, and all real state is assigned afterwards.
		/// </summary>
		public static string FormatDefaultArgument(IParameterSymbol p) {
			string typeFqn = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

			if (!p.HasExplicitDefaultValue)
				return $"default({typeFqn})";

			object? value = p.ExplicitDefaultValue;
			if (value == null)
				return p.Type.IsValueType ? $"default({typeFqn})" : "null";

			if (p.Type.TypeKind == TypeKind.Enum)
				return $"({typeFqn})({FormatLiteral(value)})";

			return FormatLiteral(value);
		}

		/// <summary>
		/// The extra argument a callback receives on the matching-layout generated path: the serializer
		/// itself, or <see cref="MigrationContext.None"/> (no migration happened). A serialize callback can
		/// never reach the <c>MigrationContext</c> case — <see cref="MemberExplorer"/> rejects that
		/// signature with a diagnostic, because there is no migration while saving.
		/// </summary>
		public static string CallbackArgument(CallbackInfo cb, string serializerExpr) => cb.ParameterKind switch
		{
			CallbackParameterKind.Serializer => serializerExpr,
			CallbackParameterKind.MigrationContext => "global::Ceras.Format.MigrationContext.None",
			_ => "null",
		};

		/// <summary>True for the unsigned enum underlying types (values are stored as raw bits either way).</summary>
		static bool IsUnsignedEnumUnderlying(SpecialType underlying) => underlying
			is SpecialType.System_Byte
			 or SpecialType.System_UInt16
			 or SpecialType.System_UInt32
			 or SpecialType.System_UInt64;

		/// <summary>
		/// Formats a compiler-provided constant (a <c>[CerasConstructor]</c> parameter's declared default)
		/// as a C# literal. The string case goes through the one shared quoting primitive, because a
		/// <c>string</c> default is author-controlled text just like an attribute argument.
		/// </summary>
		static string FormatLiteral(object value) {
			switch (value) {
				case bool b: return b ? "true" : "false";
				case string s: return QuoteLiteral(s);
				case char c: return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(c, quote: true);
				case float f:
					if (float.IsNaN(f)) return "float.NaN";
					if (float.IsPositiveInfinity(f)) return "float.PositiveInfinity";
					if (float.IsNegativeInfinity(f)) return "float.NegativeInfinity";
					return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";
				case double d:
					if (double.IsNaN(d)) return "double.NaN";
					if (double.IsPositiveInfinity(d)) return "double.PositiveInfinity";
					if (double.IsNegativeInfinity(d)) return "double.NegativeInfinity";
					return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
				case decimal m: return m.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m";
				case long l: return l.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L";
				case ulong ul: return ul.ToString(System.Globalization.CultureInfo.InvariantCulture) + "UL";
				case uint ui: return ui.ToString(System.Globalization.CultureInfo.InvariantCulture) + "U";
				default: return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
			}
		}

		public static string GetPrimitiveWriteMethod(ITypeSymbol type) {
			switch (type.SpecialType) {
				case SpecialType.System_Boolean: return "WriteByte";
				case SpecialType.System_Byte: return "WriteByte";
				case SpecialType.System_SByte: return "WriteByte";
				case SpecialType.System_Char: return "WriteInt16Fixed";
				case SpecialType.System_Int16: return "WriteInt16Fixed";
				case SpecialType.System_UInt16: return "WriteUInt16Fixed";
				// The robust format is fixed-width for int32/uint32 (LAYOUT.md, "Primitive encodings"),
				// so it always uses the *Fixed overloads — never the zigzag/LEB128 varint ones, whose
				// width depends on the value and would contradict the schema's fixedByteSize.
				case SpecialType.System_Int32: return "WriteInt32Fixed";
				case SpecialType.System_UInt32: return "WriteUInt32Fixed";
				case SpecialType.System_Single: return "WriteFloat32Fixed";
				case SpecialType.System_Int64: return "WriteInt64Fixed";
				case SpecialType.System_UInt64: return "WriteUInt64Fixed";
				case SpecialType.System_Double: return "WriteDouble64Fixed";
				case SpecialType.System_Decimal: return "WriteDecimal";
			}
			string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			if (fqn == "global::System.Guid") return "WriteGuid";
			if (fqn == "global::System.DateTime") return "WriteDateTime";
			if (fqn == "global::System.TimeSpan") return "WriteTimeSpan";
			if (fqn == "global::System.DateTimeOffset") return "WriteDateTimeOffset";
			throw new NotSupportedException();
		}

		public static string GetPrimitiveReadExpression(ITypeSymbol type, string readerName) {
			string readMethod;
			string prefix = "";
			string suffix = "";

			switch (type.SpecialType) {
				case SpecialType.System_Boolean: readMethod = "ReadByte"; suffix = " != 0"; break;
				case SpecialType.System_Byte: readMethod = "ReadByte"; break;
				case SpecialType.System_SByte: readMethod = "ReadByte"; prefix = "(sbyte)"; break;
				case SpecialType.System_Char: readMethod = "ReadInt16Fixed"; prefix = "(char)"; break;
				case SpecialType.System_Int16: readMethod = "ReadInt16Fixed"; break;
				case SpecialType.System_UInt16: readMethod = "ReadUInt16Fixed"; break;
				case SpecialType.System_Int32: readMethod = "ReadInt32Fixed"; break;
				case SpecialType.System_UInt32: readMethod = "ReadUInt32Fixed"; break;
				case SpecialType.System_Single: readMethod = "ReadFloat32Fixed"; break;
				case SpecialType.System_Int64: readMethod = "ReadInt64Fixed"; break;
				case SpecialType.System_UInt64: readMethod = "ReadUInt64Fixed"; break;
				case SpecialType.System_Double: readMethod = "ReadDouble64Fixed"; break;
				case SpecialType.System_Decimal: readMethod = "ReadDecimal"; break;
				default:
					string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
					if (fqn == "global::System.Guid") readMethod = "ReadGuid";
					else if (fqn == "global::System.DateTime") readMethod = "ReadDateTime";
					else if (fqn == "global::System.TimeSpan") readMethod = "ReadTimeSpan";
					else if (fqn == "global::System.DateTimeOffset") readMethod = "ReadDateTimeOffset";
					else throw new NotSupportedException();
					break;
			}
			return $"{prefix}global::Ceras.SerializerBinary.{readMethod}(ref {readerName}){suffix}";
		}

		/// <summary>
		/// CASE B: resolves the concrete formatter type for a member, but fails early (with a clear
		/// diagnostic) when <see cref="GetConcreteFormatterType"/> would reference a generated formatter
		/// (<c>Ceras.Generated.&lt;X&gt;Formatter</c>) that will not exist. This happens when the member's
		/// declared <c>[CerasSerializable]</c> type was skipped during generation (for example because
		/// Ceras cannot construct it, or because it is handled as a collection). Instead of emitting the
		/// broken reference, we report the member/declaring type/referenced type and fall back to
		/// <c>IFormatter&lt;T&gt;</c> so the rest of the generated file still compiles cleanly.
		/// <para>
		/// Reached only from the formatter-field loop, which already skipped every
		/// <see cref="TypeHelpers.IsFastPrimitive"/> member. So the primitive cases inside
		/// <see cref="GetConcreteFormatterType"/> (bool through <c>decimal</c>, <c>DateTimeOffset</c>, ...)
		/// are defensive: unreachable today, and kept correct in case that loop ever stops filtering them.
		/// </para>
		/// </summary>
		static string ResolveConcreteFormatterType(
			SourceProductionContext context,
			INamedTypeSymbol declaringType,
			SerializedMember member,
			AttributeBag attrs,
			HashSet<INamedTypeSymbol> generatedFormatterTypes,
			HashSet<INamedTypeSymbol> discoveredTypes,
			IReadOnlyDictionary<INamedTypeSymbol, string> noFormatterReason) {
			if (RequiresGeneratedFormatter(member.Type, attrs, discoveredTypes, out var referencedType) &&
				!generatedFormatterTypes.Contains(referencedType)) {
				string reason = noFormatterReason.TryGetValue(referencedType, out var r)
					? r
					: "it is not part of the generated serialization graph";
				context.ReportDiagnostic(Diagnostic.Create(
					Diag.MemberTypeWithoutGeneratedFormatter,
					member.MemberSymbol?.Locations.FirstOrDefault() ?? declaringType.Locations.FirstOrDefault(),
					member.Name,
					declaringType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
					referencedType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
					reason));

				// Fall back to the universal formatter interface; the error diagnostic fails the build,
				// but this keeps the generated file from producing additional cascade errors.
				string fallbackFqn = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				return $"global::Ceras.Formatters.IFormatter<{fallbackFqn}>";
			}

			return GetConcreteFormatterType(member.Type, member.IsMonomorphic, attrs);
		}

		/// <summary>
		/// True when a member of this declared type requires a generated formatter to exist. This mirrors
		/// the first branch of <see cref="GetConcreteFormatterType"/>. A value type names the generated
		/// formatter directly (a hard compile-time reference), so it always requires one. A reference type
		/// is wrapped in <c>ReferenceFormatter&lt;T&gt;</c> and resolves its inner formatter at runtime, so
		/// we only treat it as required when it was actually discovered (an external/blacklisted type may
		/// legitimately be served by a user-provided custom formatter), and never for collection types
		/// (which use the built-in collection formatter and may also have a custom formatter).
		/// </summary>
		static bool RequiresGeneratedFormatter(ITypeSymbol type, AttributeBag attrs, HashSet<INamedTypeSymbol> discoveredTypes, out INamedTypeSymbol referencedType) {
			referencedType = null!;
			if (type is not INamedTypeSymbol named) return false;
			if (named.IsAbstract || named.TypeKind == TypeKind.Interface) return false;
			if (!HasAttribute(named, attrs.CerasSerializable)) return false;

			referencedType = named;
			if (type.IsValueType) return true;
			return discoveredTypes.Contains(named) && !IsWellKnownType(named, attrs) && !IsCollectionType(named);
		}

		static string GetConcreteFormatterType(ITypeSymbol type, bool isMonomorphic, AttributeBag attrs) {
			string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			switch (type.SpecialType) {
				case SpecialType.System_Boolean: return "global::Ceras.Formatters.BoolFormatter";
				case SpecialType.System_Byte: return "global::Ceras.Formatters.ByteFormatter";
				case SpecialType.System_SByte: return "global::Ceras.Formatters.SByteFormatter";
				case SpecialType.System_Char: return "global::Ceras.Formatters.CharFormatter";
				case SpecialType.System_Int16: return "global::Ceras.Formatters.Int16Formatter";
				case SpecialType.System_UInt16: return "global::Ceras.Formatters.UInt16Formatter";
				case SpecialType.System_Int32: return "global::Ceras.Formatters.Int32Formatter";
				case SpecialType.System_UInt32: return "global::Ceras.Formatters.UInt32Formatter";
				case SpecialType.System_Single: return "global::Ceras.Formatters.FloatFormatter";
				case SpecialType.System_Int64: return "global::Ceras.Formatters.Int64Formatter";
				case SpecialType.System_UInt64: return "global::Ceras.Formatters.UInt64Formatter";
				case SpecialType.System_Double: return "global::Ceras.Formatters.DoubleFormatter";
				case SpecialType.System_Decimal: return "global::Ceras.Formatters.DecimalFormatter";
				case SpecialType.System_String: return "global::Ceras.Formatters.IFormatter<string>";
			}
			if (typeFqn == "global::System.DateTime") return "global::Ceras.Formatters.DateTimeFormatter";
			if (typeFqn == "global::System.DateTimeOffset") return "global::Ceras.Formatters.DateTimeOffsetFormatter";
			if (typeFqn == "global::System.TimeSpan") return "global::Ceras.Formatters.TimeSpanFormatter";
			if (typeFqn == "global::System.Guid") return "global::Ceras.Formatters.GuidFormatter";
			// System.Type is written inline by the robust type formatter (it replaces BOTH the specific
			// and reference formatter for System.Type in the serializer constructor). Naming a
			// ReferenceFormatter<System.Type> here would make injection fail at runtime, so request the
			// formatter interface and let injection supply the configured specific formatter.
			if (typeFqn == "global::System.Type") return "global::Ceras.Formatters.IFormatter<global::System.Type>";

			if (type is INamedTypeSymbol named && HasAttribute(named, attrs.CerasSerializable)) {
				if (!named.IsAbstract && named.TypeKind != TypeKind.Interface) {
					string generatedName = MakeFormatterClassName(named);
					string innerFormatter = $"global::Ceras.Generated.{generatedName}";
					if (type.IsValueType) return innerFormatter;
					if (type.IsSealed) return $"global::Ceras.Formatters.ReferenceFormatter_KnownSealedType<{typeFqn}>";
					return $"global::Ceras.Formatters.ReferenceFormatter<{typeFqn}>";
				}
			}

			if (type is IArrayTypeSymbol arr) {
				string elemFqn = arr.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				return $"global::Ceras.Formatters.ArrayFormatter<{elemFqn}>";
			}

			// Collection shape emitted by the built-in CollectionFormatter: a collection subclass such as
			// MyList<T> : List<T> (or a Queue/Stack subclass). This must be resolved before the generic
			// ReferenceFormatter fallback so the field type matches what the runtime injects.
			if (IsCollectionFormatterType(type)) {
				string? concrete = GetCollectionConcreteFormatterType(type);
				if (concrete != null)
					return type.IsValueType ? concrete : $"global::Ceras.Formatters.ReferenceFormatter<{typeFqn}>";
			}

			if (IsCollectionType(type)) {
				string? concrete = GetCollectionConcreteFormatterType(type);
				if (concrete != null) {
					if (type.IsValueType) return concrete;
					return $"global::Ceras.Formatters.ReferenceFormatter<{typeFqn}>";
				}
			}

			if (!type.IsValueType && !isMonomorphic) return $"global::Ceras.Formatters.ReferenceFormatter<{typeFqn}>";
			if (!type.IsValueType && isMonomorphic) {
				if (type.IsSealed) return $"global::Ceras.Formatters.ReferenceFormatter_KnownSealedType<{typeFqn}>";
				return $"global::Ceras.Formatters.ReferenceFormatter<{typeFqn}>";
			}
			return $"global::Ceras.Formatters.IFormatter<{typeFqn}>";
		}

		/// <summary>
		/// The concrete <c>CollectionFormatter&lt;TCollection, TItem&gt;</c> field type for a collection
		/// symbol, or null when the element/key/value type cannot be determined. Shared by the
		/// built-in-collection and collection-subclass branches so both resolve identically to the
		/// runtime's <c>CollectionFormatterResolver</c>.
		/// </summary>
		static string? GetCollectionConcreteFormatterType(ITypeSymbol type) {
			if (type is not INamedTypeSymbol namedColl) return null;
			string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

			ITypeSymbol? keyType = null, valType = null;
			foreach (var iface in namedColl.AllInterfaces) {
				var ifaceFqn = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				if (ifaceFqn.StartsWith("global::System.Collections.Generic.IDictionary<")) {
					keyType = iface.TypeArguments[0];
					valType = iface.TypeArguments[1];
					break;
				}
			}

			// A collection whose own declaration is (or derives from) Dictionary<K,V>.
			if (keyType == null && namedColl.TypeArguments.Length == 2) {
				var fqn = namedColl.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				if (fqn.StartsWith("global::System.Collections.Generic.IDictionary<") || fqn.StartsWith("global::System.Collections.Generic.Dictionary<")) {
					keyType = namedColl.TypeArguments[0];
					valType = namedColl.TypeArguments[1];
				}
			}

			if (keyType != null && valType != null) {
				string k = keyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				string v = valType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				return $"global::Ceras.Formatters.CollectionFormatter<{typeFqn}, global::System.Collections.Generic.KeyValuePair<{k}, {v}>>";
			}

			ITypeSymbol? elemType = null;
			foreach (var iface in namedColl.AllInterfaces) {
				var ifaceFqn = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				if (ifaceFqn.StartsWith("global::System.Collections.Generic.ICollection<")) {
					elemType = iface.TypeArguments[0];
					break;
				}
			}

			// A Queue<>/Stack<> subclass does not implement ICollection<T>; take the element from the
			// built-in base type, which is what the runtime CollectionFormatterResolver uses.
			if (elemType == null) {
				for (var t = namedColl.BaseType; t != null; t = t.BaseType) {
					var baseFqn = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
					if ((baseFqn.StartsWith("global::System.Collections.Generic.Queue<") || baseFqn.StartsWith("global::System.Collections.Generic.Stack<"))
						&& t.TypeArguments.Length > 0) {
						elemType = t.TypeArguments[0];
						break;
					}
				}
			}

			// A collection whose own declaration is an ICollection<T> (e.g. MyCollection<T> : Collection<T>).
			if (elemType == null && namedColl.TypeArguments.Length > 0)
				elemType = namedColl.TypeArguments[namedColl.TypeArguments.Length - 1];

			if (elemType == null) return null;
			string elemFqn = elemType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			return $"global::Ceras.Formatters.CollectionFormatter<{typeFqn}, {elemFqn}>";
		}

		static void WriteSchemaDescriptor(StringBuilder sb, INamedTypeSymbol type, List<SerializedMember> members, AttributeBag attrs) {
			sb.AppendLine($"        internal static readonly global::Ceras.Format.SchemaTypeInfo Schema =");
			sb.Append(BuildSchemaExpression(type, members, attrs));
			sb.AppendLine(";");
			sb.AppendLine();
		}

		/// <summary>
		/// Builds the <c>new SchemaTypeInfo(...)</c> expression for a (closed) type. Used both as the
		/// static field of a non-generic formatter and as the static field of a per-closed generic
		/// subclass (Phase 2.4).
		/// <para>
		/// <see cref="stableTypeName"/>, <see cref="prevTypeNames"/>, each member's
		/// <see cref="SerializedMember.StableName"/>/<see cref="SerializedMember.PreviousNames"/> and each
		/// enum member's wire name are all arbitrary text the author wrote in an attribute (or, for the
		/// canonical identities, text derived from a possibly-non-ASCII CLR name). They are emitted as
		/// C# string literals, so every one of them must go through <see cref="TypeHelpers.QuoteLiteral"/>:
		/// a <c>"</c>, <c>\</c>, newline or control character in an alias would otherwise produce broken
		/// or silently-different generated C#.
		/// </para>
		/// </summary>
		public static string BuildSchemaExpression(INamedTypeSymbol type, List<SerializedMember> members, AttributeBag attrs) {
			string stableTypeName = GetStableTypeName(type, attrs.CerasSerializable);
			var prevTypeNames = new List<string>();
			if (attrs.PreviousTypeName != null) {
				foreach (var attr in type.GetAttributes())
					if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrs.PreviousTypeName) && attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string ptn)
						prevTypeNames.Add(ptn);
			}
			string kind = type.TypeKind == TypeKind.Enum ? "global::Ceras.Format.SchemaTypeKind.Enum" : (type.IsValueType ? "global::Ceras.Format.SchemaTypeKind.Struct" : "global::Ceras.Format.SchemaTypeKind.Class");
			string? underlyingTypeName = type.TypeKind == TypeKind.Enum && type.EnumUnderlyingType != null ? GetRobustName(type.EnumUnderlyingType) : null;
			int fixedSize = ComputeFixedByteSize(type, members);

			var sb = new StringBuilder();
			sb.AppendLine();
			sb.AppendLine($"            new global::Ceras.Format.SchemaTypeInfo(");
			sb.AppendLine($"                stableName: {QuoteLiteral(stableTypeName)},");
			sb.Append($"                previousTypeNames: ");
			WriteStringArray(sb, prevTypeNames);
			sb.AppendLine($",");
			sb.AppendLine($"                kind: {kind},");
			sb.AppendLine($"                fields: new global::Ceras.Format.SchemaFieldInfo[]");
			sb.AppendLine($"                {{");
			foreach (var m in members) {
				// Canonical field-type identity (TODO Phase 2.3/3.3): never Type.FullName, which is
				// assembly-version-qualified for closed generics and would make the fingerprint
				// runtime-dependent. Must match Ceras.Format.TypeIdentity.GetCanonicalName exactly.
				string fieldTypeName = GetRobustName(m.Type);
				sb.Append($"                    new global::Ceras.Format.SchemaFieldInfo(");
				sb.Append($"{QuoteLiteral(m.StableName)}, ");
				WriteStringArrayInline(sb, m.PreviousNames);
				sb.Append($", {QuoteLiteral(fieldTypeName)}, {(m.IsMonomorphic ? "true" : "false")}");
				sb.AppendLine($"),");
			}
			sb.AppendLine($"                }},");
			sb.AppendLine($"                fixedByteSize: {fixedSize},");
			if (type.TypeKind == TypeKind.Enum) {
				sb.AppendLine($"                enumValues: new global::Ceras.Format.SchemaEnumValueInfo[]");
				sb.AppendLine($"                {{");
				// Values are stored as raw 64-bit bits, so the literal has to compile for either
				// signedness: a negative signed constant does not implicitly convert to ulong, and a
				// constant above long.MaxValue must not be written through a long. Widening through the
				// enum's own underlying type and casting to ulong covers both.
				bool unsignedUnderlying = IsUnsignedEnumUnderlying(type.EnumUnderlyingType?.SpecialType ?? SpecialType.System_Int32);
				foreach (var member in type.GetMembers())
					if (member is IFieldSymbol field && field.HasConstantValue)
					{
						string literal = FormatLiteral(field.ConstantValue);
						string bits = unsignedUnderlying
							? $"unchecked((ulong)({literal}))"
							: $"unchecked((ulong)(long)({literal}))";
						// The wire name, not the C# member name: this is the name the value is matched by
						// when an older file is migrated onto the current enum, so recording anything else
						// here (and the C# name was recorded before) makes a member pinned with
						// [CerasStableName] impossible to migrate unless the reader happens to guess the
						// name the file does not contain.
						string wireName = MemberExplorer.GetEnumMemberWireName(field, attrs);
						sb.AppendLine($"                    new global::Ceras.Format.SchemaEnumValueInfo({QuoteLiteral(wireName)}, {bits}),");
					}
				sb.AppendLine($"                }},");
				sb.AppendLine($"                underlyingTypeName: {QuoteLiteral(underlyingTypeName ?? "System.Int32")}");
			}
			else {
				sb.AppendLine($"                enumValues: null,");
				sb.AppendLine($"                underlyingTypeName: null");
			}
			sb.Append($"            )");
			return sb.ToString();
		}

		public static string GetStableTypeName(INamedTypeSymbol type, INamedTypeSymbol? cerasSerializableAttr) {
			if (cerasSerializableAttr != null) {
				foreach (var attr in type.GetAttributes()) {
					if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, cerasSerializableAttr)) continue;

					if (TypeHelpers.TryGetAttributeArgument<string>(attr, "StableName", out var sn) && !string.IsNullOrEmpty(sn))
						return sn;

					return GetRobustName(type);
				}
			}

			return GetRobustName(type);
		}


		/// <summary>
		/// Emits a <c>string[]</c> of user-written alias strings. Every element is author-controlled
		/// ([PreviousTypeName] / [PreviousName] arguments) and is quoted through
		/// <see cref="TypeHelpers.QuoteLiteral"/>.
		/// </summary>
		static void WriteStringArray(StringBuilder sb, List<string> strings) {
			if (strings.Count == 0) { sb.Append("global::System.Array.Empty<string>()"); return; }
			sb.Append("new string[] { ");
			for (int i = 0; i < strings.Count; i++) {
				sb.Append(QuoteLiteral(strings[i]));
				if (i < strings.Count - 1) sb.Append(", ");
			}
			sb.Append(" }");
		}

		/// <summary>As <see cref="WriteStringArray"/>, for the previous names of a single member.</summary>
		static void WriteStringArrayInline(StringBuilder sb, string[] strings) {
			if (strings == null || strings.Length == 0) { sb.Append("global::System.Array.Empty<string>()"); return; }
			sb.Append("new string[] { ");
			for (int i = 0; i < strings.Length; i++) {
				sb.Append(QuoteLiteral(strings[i]));
				if (i < strings.Length - 1) sb.Append(", ");
			}
			sb.Append(" }");
		}

		/// <summary>
		/// The exact number of bytes the generated writer emits for <paramref name="type"/>, or 0 when
		/// that width cannot be proven here. 0 is always safe — the interpreter then skips the value
		/// member by member — while a wrong non-zero value makes it Advance() over the wrong number of
		/// bytes, so everything not provably fixed is 0.
		/// <para>
		/// The table below must mirror the encoder exactly: <c>WriteInt32Fixed</c>/<c>WriteUInt32Fixed</c>
		/// and the fixed 4-byte string-table index are 4 bytes, and every other primitive uses its
		/// fixed-width overload. <paramref name="members"/> is the *serialized* member list of the
		/// declaring type (what the writer actually emits); pass null when recursing, which makes a
		/// nested struct variable — it carries its own fixed width in its own schema entry, so it is
		/// still skipped with a single Advance() there.
		/// </para>
		/// </summary>
		static int ComputeFixedByteSize(ITypeSymbol type, List<SerializedMember>? members) {
			switch (type.SpecialType) {
				case SpecialType.System_Boolean: return 1;
				case SpecialType.System_Byte: return 1;
				case SpecialType.System_SByte: return 1;
				case SpecialType.System_Char: return 2;
				case SpecialType.System_Int16: return 2;
				case SpecialType.System_UInt16: return 2;
				case SpecialType.System_Int32: return 4;
				case SpecialType.System_UInt32: return 4;
				case SpecialType.System_Single: return 4;
				case SpecialType.System_Int64: return 8;
				case SpecialType.System_UInt64: return 8;
				case SpecialType.System_Double: return 8;
				case SpecialType.System_Decimal: return 16;
				case SpecialType.System_String: return 4;
			}
			if (type is INamedTypeSymbol named) {
				if (named.TypeKind == TypeKind.Enum && named.EnumUnderlyingType != null) return ComputeFixedByteSize(named.EnumUnderlyingType, null);
				string clrName = named.ToDisplayString(SortKeyFormat);
				if (clrName == "System.DateTime") return 8;
				if (clrName == "System.DateTimeOffset") return 10;
				if (clrName == "System.TimeSpan") return 8;
				if (clrName == "System.Guid") return 16;
				// A user struct is only "fixed size" if every one of its *serialized* members is a
				// fixed-width scalar. Generic value types (Nullable<>, ValueTuple<>, KeyValuePair<,>),
				// nested structs and anything with a variable-width or formatter-owned member are
				// variable: claiming a fixed size for them would make the schema/interpreter skip the
				// wrong number of bytes for a generic/struct value type.
				if (named.IsValueType && named.TypeKind == TypeKind.Struct && !named.IsGenericType) {
					if (members == null) return 0; // nested: use this type's own schema entry instead
					int total = 0;
					bool anyMember = false;
					foreach (var member in members) {
						if (member.Type == null) continue;
						anyMember = true;
						int memberSize = ComputeFixedByteSize(member.Type, null);
						if (memberSize == 0) return 0;
						total += memberSize;
					}
					return anyMember ? total : 0;
				}
			}
			return 0;
		}
	}
}
