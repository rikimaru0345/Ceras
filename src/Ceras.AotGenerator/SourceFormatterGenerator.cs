namespace CerasAotFormatterGenerator
{
	using Ceras;
	using Ceras.Helpers;
	using System;
	using System.Text;

	static class SourceFormatterGenerator
	{
		public static StringBuilder Generate(Type type, CerasSerializer ceras, StringBuilder text)
		{
			text.AppendLine($"internal class {type.Name}Formatter : IFormatter<{type.FullName}>");
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
			foreach (var m in schema.Members)
			{
				var t = m.MemberType;
				var fieldName = MakeFormatterFieldName(t);
				text.AppendLine($" IFormatter<{t.FullName}> {fieldName};");
			}
			text.AppendLine("");
		}

		static void GenerateSerializer(StringBuilder text, Schema schema)
		{
			text.AppendLine($"public void Serialize(ref byte[] buffer, ref int offset, {schema.Type.FullName} value)");
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
			text.AppendLine($"public void Deserialize(byte[] buffer, ref int offset, ref {schema.Type.FullName} value)");
			text.AppendLine("{");

			foreach (var m in schema.Members)
			{
				var t = m.MemberType;
				var fieldName = MakeFormatterFieldName(t);
				text.AppendLine($"{fieldName}.Deserialize(buffer, ref offset, ref value.{m.MemberName});");
			}

			text.AppendLine("}");
		}


		static string MakeFormatterFieldName(Type formattedType)
		{
			string typeName = formattedType.Name;

			typeName = char.ToLowerInvariant(typeName[0]) + typeName.Remove(0, 1);

			typeName = "_" + typeName;

			typeName = typeName + "Formatter";

			return typeName;
		}
	}
}