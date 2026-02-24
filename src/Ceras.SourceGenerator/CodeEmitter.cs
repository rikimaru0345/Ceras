using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using static Ceras.SourceGenerator.TypeHelpers;
using static Ceras.SourceGenerator.MemberExplorer;

namespace Ceras.SourceGenerator
{
    internal static class CodeEmitter
    {
        public static void WriteFormatterClass(
            StringBuilder sb,
            INamedTypeSymbol type,
            List<SerializedMember> members,
            AttributeBag attrs,
            bool vt = false)
        {
            string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string formatterName = MakeFormatterClassName(type);

            sb.AppendLine($"    internal sealed class {formatterName} : global::Ceras.Formatters.IFormatter<{typeFqn}>");
            sb.AppendLine("    {");

            // --- Formatter fields (one per distinct member type) -------------------
            var seenFieldNames = new HashSet<string>();
            foreach (var m in members)
            {
                if (IsFastPrimitive(m.Type)) continue;

                string fieldName = MakeFormatterFieldName(m.Type);
                if (!seenFieldNames.Add(fieldName)) continue;

                string concreteFormatterType = GetConcreteFormatterType(m.Type, m.IsMonomorphic, attrs);
                sb.AppendLine($"        {concreteFormatterType} {fieldName};");
            }
            sb.AppendLine();

            // --- Schema descriptor (VersionTolerant only) --------------------------
            if (vt)
                WriteSchemaDescriptor(sb, type, members, attrs);

            // --- SerializeUntyped --------------------------------------------------
            sb.AppendLine($"        public void SerializeUntyped(global::System.Buffers.IBufferWriter<byte> writer, object value)");
            sb.AppendLine("        {");
            sb.AppendLine($"            Serialize(writer, ({typeFqn})value);");
            sb.AppendLine("        }");
            sb.AppendLine();

            // --- DeserializeUntyped ------------------------------------------------
            sb.AppendLine($"        public void DeserializeUntyped(ref global::System.Buffers.SequenceReader<byte> reader, object container, global::System.Reflection.MemberInfo member)");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (member is global::System.Reflection.FieldInfo f)");
            sb.AppendLine("            {");
            sb.AppendLine($"                var existing = f.GetValue(container);");
            sb.AppendLine($"                {typeFqn} value = default;");
            sb.AppendLine($"                if (existing is {typeFqn}) value = ({typeFqn})existing;");
            sb.AppendLine($"                Deserialize(ref reader, ref value);");
            sb.AppendLine($"                f.SetValue(container, value);");
            sb.AppendLine("            }");
            sb.AppendLine("            else if (member is global::System.Reflection.PropertyInfo p)");
            sb.AppendLine("            {");
            sb.AppendLine($"                var existing = p.GetValue(container);");
            sb.AppendLine($"                {typeFqn} value = default;");
            sb.AppendLine($"                if (existing is {typeFqn}) value = ({typeFqn})existing;");
            sb.AppendLine($"                Deserialize(ref reader, ref value);");
            sb.AppendLine($"                p.SetValue(container, value);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();

            // --- Serialize ---------------------------------------------------------
            sb.AppendLine($"        public void Serialize(global::System.Buffers.IBufferWriter<byte> writer, {typeFqn} value)");
            sb.AppendLine("        {");
            foreach (var m in members)
            {
                string fieldName = MakeFormatterFieldName(m.Type);
                string getAccess = GetGetAccess(m, typeFqn);

                if (IsFastPrimitive(m.Type))
                {
                    string writeMethod = GetPrimitiveWriteMethod(m.Type);
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

            // --- Deserialize -------------------------------------------------------
            sb.AppendLine($"        public void Deserialize(ref global::System.Buffers.SequenceReader<byte> reader, ref {typeFqn} value)");
            sb.AppendLine("        {");

            bool hasCtorParams = members.Any(m => m.IsCtorParam);

            if (hasCtorParams)
            {
                // [CerasConstructor] path
                foreach (var m in members)
                {
                    string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    sb.AppendLine($"            {memberTypeFqn} _ctor_{m.Name} = default;");
                }

                foreach (var m in members)
                {
                    string fieldName = MakeFormatterFieldName(m.Type);
                    string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                    if (IsFastPrimitive(m.Type))
                    {
                        string readExpr = GetPrimitiveReadExpression(m.Type, "reader");
                        sb.AppendLine($"            _ctor_{m.Name} = {readExpr};");
                    }
                    else if (m.IsGetterOnly)
                    {
                        string getAccess = GetGetAccess(m, typeFqn);
                        sb.AppendLine($"            if (value != null) _ctor_{m.Name} = {getAccess};");
                        sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref _ctor_{m.Name});");
                    }
                    else
                    {
                        sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref _ctor_{m.Name});");
                    }
                }

                var ctorArgParts = members.Where(m => m.IsCtorParam).Select(m => $"{m.CtorParamName}: _ctor_{m.Name}");
                string ctorArgs = string.Join(", ", ctorArgParts);
                sb.AppendLine($"            value = new {typeFqn}({ctorArgs});");

                foreach (var m in members.Where(m => !m.IsCtorParam))
                {
                    if (IsFastPrimitive(m.Type))
                    {
                        WriteSetStatement(sb, m, typeFqn, $"_ctor_{m.Name}", 12);
                    }
                    else if (m.IsGetterOnly) { }
                    else
                    {
                        WriteSetStatement(sb, m, typeFqn, $"_ctor_{m.Name}", 12);
                    }
                }
            }
            else
            {
                // Normal path
                if (type.IsReferenceType)
                {
                    sb.AppendLine($"            if (value == null) throw new global::System.InvalidOperationException($\"The instance of type '{{typeof({typeFqn}).FullName}}' was null. For types with a parameterless constructor, the instance must be provided by ReferenceFormatter.\");");
                }

                foreach (var m in members)
                {
                    string fieldName = MakeFormatterFieldName(m.Type);
                    string memberTypeFqn = m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    string? refAccess = GetRefAccess(m, typeFqn);

                    if (IsFastPrimitive(m.Type))
                    {
                        string readExpr = GetPrimitiveReadExpression(m.Type, "reader");
                        WriteSetStatement(sb, m, typeFqn, readExpr, 12);
                    }
                    else
                    {
                        if (m.IsGetterOnly)
                        {
                            string getAccess = GetGetAccess(m, typeFqn);
                            string tmp = "_m_" + m.Name;
                            sb.AppendLine($"            {memberTypeFqn} {tmp} = {getAccess};");
                            sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref {tmp});");
                        }
                        else
                        {
                            if (refAccess != null)
                            {
                                sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref {refAccess});");
                            }
                            else
                            {
                                string getAccess = GetGetAccess(m, typeFqn);
                                string tmp = "_m_" + m.Name;
                                sb.AppendLine($"            {memberTypeFqn} {tmp} = {getAccess};");
                                sb.AppendLine($"            {fieldName}.Deserialize(ref reader, ref {tmp});");
                                WriteSetStatement(sb, m, typeFqn, tmp, 12);
                            }
                        }
                    }
                }
            }
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        static bool IsFastPrimitive(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Char:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Single:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Double:
                    return true;
            }
            string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return fqn == "global::System.Guid" || fqn == "global::System.DateTime" || fqn == "global::System.TimeSpan";
        }

        static string GetPrimitiveWriteMethod(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean: return "WriteByte";
                case SpecialType.System_Byte: return "WriteByte";
                case SpecialType.System_SByte: return "WriteByte";
                case SpecialType.System_Char: return "WriteInt16Fixed";
                case SpecialType.System_Int16: return "WriteInt16Fixed";
                case SpecialType.System_UInt16: return "WriteInt16Fixed";
                case SpecialType.System_Int32: return "WriteInt32";
                case SpecialType.System_UInt32: return "WriteUInt32";
                case SpecialType.System_Single: return "WriteFloat32Fixed";
                case SpecialType.System_Int64: return "WriteInt64Fixed";
                case SpecialType.System_UInt64: return "WriteInt64Fixed";
                case SpecialType.System_Double: return "WriteDouble64Fixed";
            }
            string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (fqn == "global::System.Guid") return "WriteGuid";
            if (fqn == "global::System.DateTime") return "WriteDateTime";
            if (fqn == "global::System.TimeSpan") return "WriteTimeSpan";
            throw new NotSupportedException();
        }

        static string GetPrimitiveReadExpression(ITypeSymbol type, string readerName)
        {
            string readMethod;
            string prefix = "";
            string suffix = "";
            
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean: readMethod = "ReadByte"; suffix = " != 0"; break;
                case SpecialType.System_Byte: readMethod = "ReadByte"; break;
                case SpecialType.System_SByte: readMethod = "ReadByte"; prefix = "(sbyte)"; break;
                case SpecialType.System_Char: readMethod = "ReadInt16Fixed"; prefix = "(char)"; break;
                case SpecialType.System_Int16: readMethod = "ReadInt16Fixed"; break;
                case SpecialType.System_UInt16: readMethod = "ReadInt16Fixed"; prefix = "(ushort)"; break;
                case SpecialType.System_Int32: readMethod = "ReadInt32"; break;
                case SpecialType.System_UInt32: readMethod = "ReadUInt32"; break;
                case SpecialType.System_Single: readMethod = "ReadFloat32Fixed"; break;
                case SpecialType.System_Int64: readMethod = "ReadInt64Fixed"; break;
                case SpecialType.System_UInt64: readMethod = "ReadInt64Fixed"; prefix = "(ulong)"; break;
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

        static string GetConcreteFormatterType(ITypeSymbol type, bool isMonomorphic, AttributeBag attrs)
        {
            string typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            switch (type.SpecialType)
            {
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

            if (type is INamedTypeSymbol named && HasAttribute(named, attrs.GenerateFormatter))
            {
                if (!named.IsAbstract && named.TypeKind != TypeKind.Interface)
                {
                    string generatedName = MakeFormatterClassName(named);
                    string innerFormatter = $"global::Ceras.Generated.{generatedName}";
                    if (type.IsValueType) return innerFormatter;
                    if (type.IsSealed) return $"global::Ceras.Formatters.ReferenceFormatter_KnownSealedType<{typeFqn}>";
                    return $"global::Ceras.Formatters.ReferenceFormatter<{typeFqn}>";
                }
            }

            if (type is IArrayTypeSymbol arr)
            {
                string elemFqn = arr.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                return $"global::Ceras.Formatters.ArrayFormatter<{elemFqn}>";
            }

            if (IsCollectionType(type))
            {
                if (type is INamedTypeSymbol namedColl && namedColl.TypeArguments.Length > 0)
                {
                    bool isDict = false;
                    ITypeSymbol? keyType = null, valType = null;
                    var fqn = namedColl.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (fqn.StartsWith("global::System.Collections.Generic.IDictionary<") || fqn.StartsWith("global::System.Collections.Generic.Dictionary<"))
                    {
                        isDict = true; keyType = namedColl.TypeArguments[0]; valType = namedColl.TypeArguments[1];
                    }
                    else
                    {
                        foreach (var iface in namedColl.AllInterfaces)
                        {
                            var ifaceFqn = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            if (ifaceFqn.StartsWith("global::System.Collections.Generic.IDictionary<"))
                            {
                                isDict = true; keyType = iface.TypeArguments[0]; valType = iface.TypeArguments[1]; break;
                            }
                        }
                    }
                    if (isDict && keyType != null && valType != null)
                    {
                        string k = keyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        string v = valType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        string kvp = $"global::System.Collections.Generic.KeyValuePair<{k}, {v}>";
                        return $"global::Ceras.Formatters.CollectionFormatter<{typeFqn}, {kvp}>";
                    }
                    string elemFqn = namedColl.TypeArguments[namedColl.TypeArguments.Length - 1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    return $"global::Ceras.Formatters.CollectionFormatter<{typeFqn}, {elemFqn}>";
                }
            }

            if (!type.IsValueType && !isMonomorphic) return $"global::Ceras.Formatters.ReferenceFormatter<{typeFqn}>";
            if (!type.IsValueType && isMonomorphic)
            {
                if (type.IsSealed) return $"global::Ceras.Formatters.ReferenceFormatter_KnownSealedType<{typeFqn}>";
                return $"global::Ceras.Formatters.ReferenceFormatter<{typeFqn}>";
            }
            return $"global::Ceras.Formatters.IFormatter<{typeFqn}>";
        }

        static void WriteSchemaDescriptor(StringBuilder sb, INamedTypeSymbol type, List<SerializedMember> members, AttributeBag attrs)
        {
            string stableTypeName = GetStableTypeName(type, attrs.GenerateFormatter);
            var prevTypeNames = new List<string>();
            if (attrs.PreviousTypeName != null)
            {
                foreach (var attr in type.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrs.PreviousTypeName) && attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string ptn)
                        prevTypeNames.Add(ptn);
            }
            string kind = type.TypeKind == TypeKind.Enum ? "global::Ceras.Format.SchemaTypeKind.Enum" : (type.IsValueType ? "global::Ceras.Format.SchemaTypeKind.Struct" : "global::Ceras.Format.SchemaTypeKind.Class");
            string? underlyingType = type.TypeKind == TypeKind.Enum ? type.EnumUnderlyingType?.ToDisplayString(SortKeyFormat) : null;
            int fixedSize = ComputeFixedByteSize(type);

            sb.AppendLine($"        internal static readonly global::Ceras.Format.SchemaTypeInfo Schema =");
            sb.AppendLine($"            new global::Ceras.Format.SchemaTypeInfo(");
            sb.AppendLine($"                stableName: {QuoteString(stableTypeName)},");
            sb.Append($"                previousTypeNames: ");
            WriteStringArray(sb, prevTypeNames);
            sb.AppendLine($",");
            sb.AppendLine($"                kind: {kind},");
            sb.AppendLine($"                fields: new global::Ceras.Format.SchemaFieldInfo[]");
            sb.AppendLine($"                {{");
            foreach (var m in members)
            {
                string typeName = m.Type.ToDisplayString(SortKeyFormat);
                sb.Append($"                    new global::Ceras.Format.SchemaFieldInfo(");
                sb.Append($"{QuoteString(m.StableName)}, ");
                WriteStringArrayInline(sb, m.PreviousNames);
                sb.Append($", {QuoteString(typeName)}, {(m.IsMonomorphic ? "true" : "false")}");
                sb.AppendLine($"),");
            }
            sb.AppendLine($"                }},");
            sb.AppendLine($"                fixedByteSize: {fixedSize},");
            if (type.TypeKind == TypeKind.Enum)
            {
                sb.AppendLine($"                enumValues: new global::Ceras.Format.SchemaEnumValueInfo[]");
                sb.AppendLine($"                {{");
                foreach (var member in type.GetMembers())
                    if (member is IFieldSymbol field && field.HasConstantValue)
                        sb.AppendLine($"                    new global::Ceras.Format.SchemaEnumValueInfo({QuoteString(field.Name)}, {field.ConstantValue}),");
                sb.AppendLine($"                }},");
                sb.AppendLine($"                underlyingTypeName: {QuoteString(underlyingType ?? "System.Int32")}");
            }
            else
            {
                sb.AppendLine($"                enumValues: null,");
                sb.AppendLine($"                underlyingTypeName: null");
            }
            sb.AppendLine($"            );");
            sb.AppendLine();
        }

        static string GetStableTypeName(INamedTypeSymbol type, INamedTypeSymbol? generateFmtAttr)
        {
            if (generateFmtAttr != null)
            {
                foreach (var attr in type.GetAttributes())
                {
                    if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, generateFmtAttr)) continue;
                    if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string sn && !string.IsNullOrEmpty(sn))
                        return sn;
                }
            }
            return type.Name;
        }

        static void WriteStringArray(StringBuilder sb, List<string> strings)
        {
            if (strings.Count == 0) { sb.Append("global::System.Array.Empty<string>()"); return; }
            sb.Append("new string[] { ");
            for (int i = 0; i < strings.Count; i++)
            {
                sb.Append(QuoteString(strings[i]));
                if (i < strings.Count - 1) sb.Append(", ");
            }
            sb.Append(" }");
        }

        static void WriteStringArrayInline(StringBuilder sb, string[] strings)
        {
            if (strings == null || strings.Length == 0) { sb.Append("global::System.Array.Empty<string>()"); return; }
            sb.Append("new string[] { ");
            for (int i = 0; i < strings.Length; i++)
            {
                sb.Append(QuoteString(strings[i]));
                if (i < strings.Length - 1) sb.Append(", ");
            }
            sb.Append(" }");
        }

        static string GetGetAccess(SerializedMember m, string typeFqn)
        {
            if (m.IsAccessible) return $"value.{m.Name}";
            string declaringTypeFqn = m.DeclaringType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (m.IsField) return $"{declaringTypeFqn}.__ceras_ref_{m.Name}(value)";
            return $"{declaringTypeFqn}.__ceras_get_{m.Name}(value)";
        }

        static string? GetRefAccess(SerializedMember m, string typeFqn)
        {
            if (m.IsField && m.IsAccessible && !m.IsReadOnly) return $"value.{m.Name}";
            return null;
        }

        static void WriteSetStatement(StringBuilder sb, SerializedMember m, string typeFqn, string valueExpr, int indent)
        {
            string prefix = new string(' ', indent);
            if (m.IsAccessible) { sb.AppendLine($"{prefix}value.{m.Name} = {valueExpr};"); return; }
            string declaringTypeFqn = m.DeclaringType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (m.IsField) sb.AppendLine($"{prefix}{declaringTypeFqn}.__ceras_ref_{m.Name}(value) = {valueExpr};");
            else sb.AppendLine($"{prefix}{declaringTypeFqn}.__ceras_set_{m.Name}(value, {valueExpr});");
        }

        static int ComputeFixedByteSize(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
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
            if (type is INamedTypeSymbol named)
            {
                if (named.TypeKind == TypeKind.Enum && named.EnumUnderlyingType != null) return ComputeFixedByteSize(named.EnumUnderlyingType);
                string clrName = named.ToDisplayString(SortKeyFormat);
                if (clrName == "System.DateTime") return 8;
                if (clrName == "System.TimeSpan") return 8;
                if (clrName == "System.Guid") return 16;
                if (named.IsValueType && named.TypeKind == TypeKind.Struct)
                {
                    int total = 0;
                    foreach (var member in named.GetMembers())
                    {
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
