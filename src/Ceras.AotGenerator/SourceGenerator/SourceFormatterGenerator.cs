namespace CerasAotFormatterGenerator
{
	using Ceras;
	using Ceras.Helpers;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using Ceras.Formatters;
	using System.Reflection;
	using AgileObjects.ReadableExpressions;

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
				GenerateFormatter(t, ceras, text);

			text.Length -= Environment.NewLine.Length;
			text.AppendLine("}");
		}

		static void GenerateFormatter(Type type, CerasSerializer ceras, StringBuilder text)
		{
			var meta = ceras.GetTypeMetaData(type);
			var schema = meta.PrimarySchema;

			text.AppendLine($"\tinternal class {type.ToVariableSafeName()}Formatter : IFormatter<{type.ToFriendlyName(true)}>");
			text.AppendLine("\t{");

			GenerateFormatterFields(text, schema);
			GenerateSerializer(text, ceras, schema, false);
			GenerateDeserializer(text, ceras, schema, false);

			text.AppendLine("\t}");
			text.AppendLine("");
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

		static void GenerateSerializer(StringBuilder text, CerasSerializer ceras, Schema schema, bool isVersionTolerant)
		{
			text.AppendLine($"\t\tpublic void Serialize(ref byte[] buffer, ref int offset, {schema.Type.ToFriendlyName(true)} value)");
			text.AppendLine("\t\t{");

			var serializerExpr = DynamicFormatter.GenerateSerializer(schema.Type, ceras, schema, isVersionTolerant);
			var serializerCode = serializerExpr.ToReadableString(_translationConfig);
			var methodBody = ExtractMethodBody(serializerCode);

			foreach (var line in methodBody.Split('\n'))
				text.AppendLine("\t\t\t" + line.Trim());

			text.AppendLine("\t\t}");
			text.AppendLine("");
		}

		static void GenerateDeserializer(StringBuilder text, CerasSerializer ceras, Schema schema, bool isVersionTolerant)
		{
			text.AppendLine($"\t\tpublic void Deserialize(byte[] buffer, ref int offset, ref {schema.Type.ToFriendlyName(true)} value)");
			text.AppendLine("\t\t{");

			var deserializerExpr = DynamicFormatter.GenerateDeserializer(schema.Type, ceras, schema, isVersionTolerant);
			var deserializerCode = deserializerExpr.ToReadableString(_translationConfig);
			var methodBody = ExtractMethodBody(deserializerCode);

			foreach (var line in methodBody.Split('\n'))
				text.AppendLine("\t\t\t" + line.Trim());

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

		static string ExtractMethodBody(string methodDefinition)
		{
			var start = methodDefinition.IndexOf('{');
			var end = methodDefinition.LastIndexOf('}');

			if (start == -1 || end == -1)
				throw new InvalidOperationException("Unexpected formatter code");

			var length = end - start;
			var body = methodDefinition.Substring(start, length);

			return body;
		}


		static readonly Func<TranslationSettings, TranslationSettings> _translationConfig = s
				=> s.TranslateConstantsUsing((type, obj) =>
				{
					var formattedType = type.FindClosedArg(typeof(IFormatter<>));
					if (formattedType == null)
						throw new InvalidOperationException("cannot find formatted type from formatter-type argument");

					return MakeFormatterFieldName(formattedType);
				});
	}
}