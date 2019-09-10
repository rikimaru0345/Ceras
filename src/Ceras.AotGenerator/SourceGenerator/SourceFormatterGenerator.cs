using Ceras;
using Ceras.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;

namespace CerasAotFormatterGenerator
{
	static class SourceFormatterGenerator
	{
		public static void GenerateAll(List<Type> targets, CerasSerializer ceras, StringBuilder text)
		{
			text.AppendLine("using Ceras;");
			text.AppendLine("using Ceras.Formatters;");
			text.AppendLine("using Ceras.Formatters.AotGenerator;");
			text.AppendLine("");
			text.AppendLine("namespace Ceras.GeneratedFormatters");
			text.AppendLine("{");

			var setCustomFormatters = targets.Select(t => $"\t\t\tconfig.ConfigType<{t.ToFriendlyName(true)}>().CustomFormatter = new {t.ToVariableSafeName()}Formatter();");
			text.AppendLine(
$@"	public static class GeneratedFormatters
	{{
		public static void UseFormatters(SerializerConfig config)
		{{
{string.Join(Environment.NewLine, setCustomFormatters)}
		}}
	}}
");

			foreach (var t in targets)
				Generate(t, ceras, text);

			text.Length -= Environment.NewLine.Length;
			text.AppendLine("}");
		}

		static void Generate(Type type, CerasSerializer ceras, StringBuilder text)
		{
			text.AppendLine($"\tinternal class {type.ToVariableSafeName()}Formatter : IFormatter<{type.ToFriendlyName(true)}>");
			text.AppendLine("\t{");
			GenerateClassContent(text, ceras, type);
			text.AppendLine("\t}");
			text.AppendLine("");
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
				text.AppendLine($"\t\tIFormatter<{t.ToFriendlyName(true)}> {fieldName};");
			}
			text.AppendLine("");
		}

		static void GenerateSerializer(StringBuilder text, Schema schema)
		{
			text.AppendLine($"\t\tpublic void Serialize(ref byte[] buffer, ref int offset, {schema.Type.ToFriendlyName(true)} value)");
			text.AppendLine("\t\t{");

			foreach (var m in schema.Members)
			{
				var t = m.MemberType;
				var fieldName = MakeFormatterFieldName(t);
				text.AppendLine($"\t\t\t{fieldName}.Serialize(ref buffer, ref offset, value.{m.MemberName});");
			}

			text.AppendLine("\t\t}");
			text.AppendLine("");
		}

		static void GenerateDeserializer(StringBuilder text, Schema schema)
		{
			text.AppendLine($"\t\tpublic void Deserialize(byte[] buffer, ref int offset, ref {schema.Type.ToFriendlyName(true)} value)");
			text.AppendLine("\t\t{");

			// If there are any properties, we use temp local vars. And then the code gets a bit hard to read.
			bool addEmptyLines = schema.Members.Any(sm => sm.MemberInfo is PropertyInfo);

			foreach (var m in schema.Members)
			{
				var t = m.MemberType;
				var fieldName = MakeFormatterFieldName(t);

				if(m.MemberInfo is FieldInfo)
				{
					// Field
					text.AppendLine($"\t\t\t{fieldName}.Deserialize(buffer, ref offset, ref value.{m.MemberName});");
				}
				else
				{
					// Prop
					text.AppendLine($"\t\t\tvar _temp{m.MemberName} = value.{m.MemberName};");
					text.AppendLine($"\t\t\t{fieldName}.Deserialize(buffer, ref offset, ref _temp{m.MemberName});");
					text.AppendLine($"\t\t\tvalue.{m.MemberName} = _temp{m.MemberName};");
				}

				if(addEmptyLines)
					text.AppendLine("");
			}

			text.AppendLine("\t\t}");
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
}