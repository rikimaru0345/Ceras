namespace CerasAotFormatterGenerator
{
	using Ceras;
	using Ceras.Helpers;
	using System;
	using System.Collections.Generic;
	using System.Text;
	using Ceras.Formatters;
    using System.Reflection;
    using System.Linq;

    static class SourceFormatterGenerator
	{
		public static StringBuilder Generate(Type type, CerasSerializer ceras, StringBuilder text)
		{
			text.AppendLine($"internal class {type.ToVariableSafeName()}Formatter : IFormatter<{type.ToFriendlyName(true)}>");
			text.AppendLine("{");
			GenerateClassContent(text, ceras, type);
			text.AppendLine("}");
			text.AppendLine("");

			return text;
		}

		static void GenerateClassContent(StringBuilder text, CerasSerializer ceras, Type type)
		{
			var meta = ceras.GetTypeMetaData(type);
			var schema = meta.PrimarySchema;

			GenerateFormatterFields(text, schema);

			GenerateSerializer(text, schema);

			GenerateDeserializer(text, schema);
		}

		static void GenerateFormatterFields(StringBuilder text, Schema schema)
		{
			foreach (var m in schema.Members.DistinctBy(m => m.MemberType))
			{
				var t = m.MemberType;
				var fieldName = MakeFormatterFieldName(t);
				text.AppendLine($" IFormatter<{t.ToFriendlyName(true)}> {fieldName};");
			}
			text.AppendLine("");
		}

		static void GenerateSerializer(StringBuilder text, Schema schema)
		{
			text.AppendLine($"public void Serialize(ref byte[] buffer, ref int offset, {schema.Type.ToFriendlyName(true)} value)");
			text.AppendLine("{");

			foreach (var m in schema.Members)
			{
				var t = m.MemberType;
				var fieldName = MakeFormatterFieldName(t);
				text.AppendLine($"{fieldName}.Serialize(ref buffer, ref offset, value.{m.MemberName});");
			}

			text.AppendLine("}");
			text.AppendLine("");
		}

		static void GenerateDeserializer(StringBuilder text, Schema schema)
		{
			text.AppendLine($"public void Deserialize(byte[] buffer, ref int offset, ref {schema.Type.ToFriendlyName(true)} value)");
			text.AppendLine("{");

			// If there are any properties, we use temp local vars. And then the code gets a bit hard to read.
			bool addEmptyLines = schema.Members.Any(sm => sm.MemberInfo is PropertyInfo);

			foreach (var m in schema.Members)
			{
				var t = m.MemberType;
				var fieldName = MakeFormatterFieldName(t);

				if(m.MemberInfo is FieldInfo)
				{
					// Field
					text.AppendLine($"{fieldName}.Deserialize(buffer, ref offset, ref value.{m.MemberName});");
				}
				else
				{
					// Prop
					text.AppendLine($"_temp{m.MemberName} = value.{m.MemberName};");
					text.AppendLine($"{fieldName}.Deserialize(buffer, ref offset, ref _temp{m.MemberName});");
					text.AppendLine($"value.{m.MemberName} = _temp{m.MemberName};");
				}

				if(addEmptyLines)
					text.AppendLine("");
			}

			text.AppendLine("}");
		}


		static string MakeFormatterFieldName(Type formattedType)
		{
			string typeName = formattedType.ToVariableSafeName();

			// lowercase first char
			typeName = char.ToLowerInvariant(typeName[0]) + typeName.Remove(0, 1);

			typeName = "_" + typeName + "Formatter";

			return typeName;
		}
	}

	// Since we must use the same encoding that non-aot Ceras uses, we emulate the code it generates.
	static class EnumGenerator
	{
		public static void Generate(Type enumType, StringBuilder text)
		{
			var enumBaseTypeName = enumType.ToFriendlyName(true);
			var baseType = enumType.GetEnumUnderlyingType();
			
			text.AppendLine($@"
class EnumFormatter : IFormatter<{enumBaseTypeName}>
{{
		IFormatter<{baseType.ToFriendlyName(true)}> _valueFormatter;

		public void Serialize(ref byte[] buffer, ref int offset, {enumBaseTypeName} value)
		{{
			_valueFormatter.Serialize(ref buffer, ref offset, ({baseType.ToFriendlyName(true)})value);
		}}

		public void Deserialize(byte[] buffer, ref int offset, ref {enumBaseTypeName} value)
		{{
			{baseType.ToFriendlyName(true)} x = default({baseType.ToFriendlyName(true)});
			_valueFormatter.Deserialize(buffer, ref offset, ref x);
			value = x;
		}}
}}");
		}
	}

	
}