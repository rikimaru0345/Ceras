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
			var accessors = new AccessorEmitter(compilation, context.ReportDiagnostic, type, members, bestCtor);

			sb.AppendLine($"    internal sealed class {formatterName} : global::Ceras.Formatters.FormatterBase<{typeFqn}>");
			sb.AppendLine("    {");
			if (type.IsReferenceType)
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

				string writeMethod;
				if (underlying.SpecialType == SpecialType.System_Int32) writeMethod = "WriteInt32";
				else if (underlying.SpecialType == SpecialType.System_UInt32) writeMethod = "WriteUInt32";
				else writeMethod = GetPrimitiveWriteMethod(underlying);

				sb.AppendLine($"            global::Ceras.SerializerBinary.{writeMethod}(writer, ({underlyingFqn})value);");
			}
			else {
				var serializeMembers = members;
				foreach (var m in serializeMembers) {
					string fieldName = MakeFormatterFieldName(m.Type);
					string getAccess = accessors.MemberReadExpression(m, "value");

					if (TypeHelpers.IsFastPrimitive(m.Type)) {
						string writeMethod = GetPrimitiveWriteMethod(m.Type);
						string valueExp = getAccess;
						if (m.Type.SpecialType == SpecialType.System_Boolean)
							valueExp = $"(byte)({valueExp} ? 1 : 0)";
						sb.AppendLine($"            global::Ceras.SerializerBinary.{writeMethod}(writer, {valueExp});");
					}
					else {
						sb.AppendLine($"            {fieldName}.Serialize(writer, {getAccess});");
					}
				}
			}
			sb.AppendLine("        }");
			sb.AppendLine();

			// --- Deserialize -------------------------------------------------------
			sb.AppendLine($"        public override void Deserialize(ref global::System.Buffers.SequenceReader<byte> reader, ref {typeFqn} value)");
			sb.AppendLine("        {");

			if (type.TypeKind == TypeKind.Enum) {
				var underlying = type.EnumUnderlyingType!;
				string readExpr;
				if (underlying.SpecialType == SpecialType.System_Int32) readExpr = "global::Ceras.SerializerBinary.ReadInt32(ref reader)";
				else if (underlying.SpecialType == SpecialType.System_UInt32) readExpr = "global::Ceras.SerializerBinary.ReadUInt32(ref reader)";
				else readExpr = GetPrimitiveReadExpression(underlying, "reader");

				sb.AppendLine($"            value = ({typeFqn}){readExpr};");
				sb.AppendLine("        }");
			}
			else {
				if (type.IsReferenceType) {
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
					sb.AppendLine("                    _ceras.SetCurrentObject(value);");

					if (accessors.CtorNeedsAccessor && bestCtor != null && bestCtor.Parameters.Length == 0) {
						// Inaccessible parameterless constructor: invoke it through the accessor.
						sb.AppendLine($"                    {accessors.CtorCallTarget}__ceras_ctor(value);");
					}
					sb.AppendLine("                }");
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
					sb.AppendLine($"                {accessors.CtorCallTarget}__ceras_ctor(value, {args});");
				}

				// Assign locals to members
				foreach (var m in members) {
					string safeName = TypeHelpers.SanitizeIdentifier(m.Name);
					string localName = "l_" + safeName;

					// If it was a constructor parameter, it's already set (for classes).
					// But for consistency and to handle readonly fields (via UnsafeAccessor), we write it anyway.
					accessors.WriteMemberSet(sb, m, "value", localName, 16);
				}
				sb.AppendLine("        }");
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

		static string FormatLiteral(object value) {
			switch (value) {
				case bool b: return b ? "true" : "false";
				case string s: return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(s, quote: true);
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
				case SpecialType.System_Int32: return "WriteInt32";
				case SpecialType.System_UInt32: return "WriteUInt32";
				case SpecialType.System_Single: return "WriteFloat32Fixed";
				case SpecialType.System_Int64: return "WriteInt64Fixed";
				case SpecialType.System_UInt64: return "WriteUInt64Fixed";
				case SpecialType.System_Double: return "WriteDouble64Fixed";
			}
			string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			if (fqn == "global::System.Guid") return "WriteGuid";
			if (fqn == "global::System.DateTime") return "WriteDateTime";
			if (fqn == "global::System.TimeSpan") return "WriteTimeSpan";
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
				case SpecialType.System_Int32: readMethod = "ReadInt32"; break;
				case SpecialType.System_UInt32: readMethod = "ReadUInt32"; break;
				case SpecialType.System_Single: readMethod = "ReadFloat32Fixed"; break;
				case SpecialType.System_Int64: readMethod = "ReadInt64Fixed"; break;
				case SpecialType.System_UInt64: readMethod = "ReadUInt64Fixed"; break;
				case SpecialType.System_Double: readMethod = "ReadDouble64Fixed"; break;
				default:
					string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
					if (fqn == "global::System.Guid") readMethod = "ReadGuid";
					else if (fqn == "global::System.DateTime") readMethod = "ReadDateTime";
					else if (fqn == "global::System.TimeSpan") readMethod = "ReadTimeSpan";
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
			return discoveredTypes.Contains(named) && !IsWellKnownType(named) && !IsCollectionType(named);
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
				case SpecialType.System_String: return "global::Ceras.Formatters.IFormatter<string>";
			}
			if (typeFqn == "global::System.DateTime") return "global::Ceras.Formatters.DateTimeFormatter";
			if (typeFqn == "global::System.TimeSpan") return "global::Ceras.Formatters.TimeSpanFormatter";
			if (typeFqn == "global::System.Guid") return "global::Ceras.Formatters.GuidFormatter";

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

			if (IsCollectionType(type)) {
				if (type is INamedTypeSymbol namedColl && namedColl.TypeArguments.Length > 0) {
					bool isDict = false;
					ITypeSymbol? keyType = null, valType = null;
					var fqn = namedColl.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
					if (fqn.StartsWith("global::System.Collections.Generic.IDictionary<") || fqn.StartsWith("global::System.Collections.Generic.Dictionary<")) {
						isDict = true; keyType = namedColl.TypeArguments[0]; valType = namedColl.TypeArguments[1];
					}
					else {
						foreach (var iface in namedColl.AllInterfaces) {
							var ifaceFqn = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
							if (ifaceFqn.StartsWith("global::System.Collections.Generic.IDictionary<")) {
								isDict = true; keyType = iface.TypeArguments[0]; valType = iface.TypeArguments[1]; break;
							}
						}
					}
					string concrete;
					if (isDict && keyType != null && valType != null) {
						string k = keyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
						string v = valType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
						string kvp = $"global::System.Collections.Generic.KeyValuePair<{k}, {v}>";
						concrete = $"global::Ceras.Formatters.CollectionFormatter<{typeFqn}, {kvp}>";
					}
					else {
						string elemFqn = namedColl.TypeArguments[namedColl.TypeArguments.Length - 1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
						concrete = $"global::Ceras.Formatters.CollectionFormatter<{typeFqn}, {elemFqn}>";
					}

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
			int fixedSize = ComputeFixedByteSize(type);

			var sb = new StringBuilder();
			sb.AppendLine();
			sb.AppendLine($"            new global::Ceras.Format.SchemaTypeInfo(");
			sb.AppendLine($"                stableName: {QuoteString(stableTypeName)},");
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
				sb.Append($"{QuoteString(m.StableName)}, ");
				WriteStringArrayInline(sb, m.PreviousNames);
				sb.Append($", {QuoteString(fieldTypeName)}, {(m.IsMonomorphic ? "true" : "false")}");
				sb.AppendLine($"),");
			}
			sb.AppendLine($"                }},");
			sb.AppendLine($"                fixedByteSize: {fixedSize},");
			if (type.TypeKind == TypeKind.Enum) {
				sb.AppendLine($"                enumValues: new global::Ceras.Format.SchemaEnumValueInfo[]");
				sb.AppendLine($"                {{");
				foreach (var member in type.GetMembers())
					if (member is IFieldSymbol field && field.HasConstantValue)
						sb.AppendLine($"                    new global::Ceras.Format.SchemaEnumValueInfo({QuoteString(field.Name)}, {field.ConstantValue}),");
				sb.AppendLine($"                }},");
				sb.AppendLine($"                underlyingTypeName: {QuoteString(underlyingTypeName ?? "System.Int32")}");
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


		static void WriteStringArray(StringBuilder sb, List<string> strings) {
			if (strings.Count == 0) { sb.Append("global::System.Array.Empty<string>()"); return; }
			sb.Append("new string[] { ");
			for (int i = 0; i < strings.Count; i++) {
				sb.Append(QuoteString(strings[i]));
				if (i < strings.Count - 1) sb.Append(", ");
			}
			sb.Append(" }");
		}

		static void WriteStringArrayInline(StringBuilder sb, string[] strings) {
			if (strings == null || strings.Length == 0) { sb.Append("global::System.Array.Empty<string>()"); return; }
			sb.Append("new string[] { ");
			for (int i = 0; i < strings.Length; i++) {
				sb.Append(QuoteString(strings[i]));
				if (i < strings.Length - 1) sb.Append(", ");
			}
			sb.Append(" }");
		}

		static int ComputeFixedByteSize(ITypeSymbol type) {
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
				if (named.TypeKind == TypeKind.Enum && named.EnumUnderlyingType != null) return ComputeFixedByteSize(named.EnumUnderlyingType);
				string clrName = named.ToDisplayString(SortKeyFormat);
				if (clrName == "System.DateTime") return 8;
				if (clrName == "System.TimeSpan") return 8;
				if (clrName == "System.Guid") return 16;
				if (named.IsValueType && named.TypeKind == TypeKind.Struct) {
					int total = 0;
					foreach (var member in named.GetMembers()) {
						if (member is not IFieldSymbol field || field.IsStatic || field.IsConst) continue;
						int fieldSize = ComputeFixedByteSize(field.Type);
						if (fieldSize == 0) return 0;
						total += fieldSize;
					}
					return total;
				}
			}
			return 0;
		}
	}
}
