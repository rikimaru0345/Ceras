using System;
using System.Collections.Generic;
using System.Text;

namespace Ceras.Format
{
    /// <summary>
    /// The single canonical, runtime-version-independent stable identity for robust schema types.
    /// <para>
    /// This MUST match the source generator's <c>Ceras.SourceGenerator.TypeHelpers.GetRobustName</c>
    /// exactly: <c>Namespace.Outer+Nested`Arity</c> plus ordered generic arguments (containing-type
    /// arguments first), arrays as <c>T[]</c>/<c>T[,]</c>, pointers as <c>T*</c>, and generic type
    /// parameters as <c>!ordinal</c>. Unlike <see cref="Type.FullName"/>, it never embeds assembly
    /// versions or public key tokens, so the same logical layout fingerprints identically across
    /// .NET runtimes (TODO Phase 2.3 / 3.3).
    /// </para>
    /// </summary>
    public static class TypeIdentity
    {
        /// <summary>Returns the canonical stable name for <paramref name="type"/>.</summary>
        public static string GetCanonicalName(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            var sb = new StringBuilder();
            AppendCanonicalName(sb, type);
            return sb.ToString();
        }

        static void AppendCanonicalName(StringBuilder sb, Type type)
        {
            if (type.IsArray)
            {
                AppendCanonicalName(sb, type.GetElementType());
                sb.Append('[');
                for (int i = 1; i < type.GetArrayRank(); i++) sb.Append(',');
                sb.Append(']');
                return;
            }

            if (type.IsPointer)
            {
                AppendCanonicalName(sb, type.GetElementType());
                sb.Append('*');
                return;
            }

            if (type.IsGenericParameter)
            {
                sb.Append('!').Append(type.GenericParameterPosition);
                return;
            }

            if (type.IsGenericType && !type.ContainsGenericParameters)
            {
                AppendDefinitionName(sb, type.GetGenericTypeDefinition());

                var args = type.GetGenericArguments();
                sb.Append('<');
                for (int i = 0; i < args.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendCanonicalName(sb, args[i]);
                }
                sb.Append('>');
                return;
            }

            AppendDefinitionName(sb, type);
        }

        static void AppendDefinitionName(StringBuilder sb, Type type)
        {
            if (type.IsNested)
            {
                AppendDefinitionName(sb, type.DeclaringType);
                sb.Append('+');
            }
            else if (!string.IsNullOrEmpty(type.Namespace))
            {
                sb.Append(type.Namespace);
                sb.Append('.');
            }

            // Type.Name carries the metadata arity for generic definitions (e.g. "List`1").
            sb.Append(type.Name);
        }

        // ── Inverse parsing (bake-time / interpreter identity resolution) ─────────────────

        /// <summary>
        /// Parses a canonical name produced by <see cref="GetCanonicalName"/> (or by the generator's
        /// <c>GetRobustName</c>) back into a <see cref="Type"/>. Definition names are resolved through
        /// <paramref name="resolveDefinition"/>. Generic parameters cannot be resolved without context
        /// and return false. This is only used on cold paths (bake and migration), never per save.
        /// </summary>
        internal static bool TryParseCanonicalName(string name, Func<string, Type> resolveDefinition, out Type type)
        {
            type = null;
            if (string.IsNullOrEmpty(name) || resolveDefinition == null) return false;
            return TryParse(name, resolveDefinition, out type);
        }

        static bool TryParse(string name, Func<string, Type> resolveDefinition, out Type type)
        {
            type = null;

            // Arrays: T[] / T[,] / T[,,] ...
            if (name.Length > 0 && name[name.Length - 1] == ']')
            {
                int open = name.LastIndexOf('[');
                if (open < 0) return false;

                int rank = 1;
                for (int i = open + 1; i < name.Length - 1; i++)
                {
                    if (name[i] != ',') return false;
                    rank++;
                }

                if (!TryParse(name.Substring(0, open), resolveDefinition, out var element)) return false;
                try { type = element.MakeArrayType(rank); } catch { return false; }
                return true;
            }

            // Pointers: T*
            if (name.Length > 0 && name[name.Length - 1] == '*')
            {
                if (!TryParse(name.Substring(0, name.Length - 1), resolveDefinition, out var pointee)) return false;
                try { type = pointee.MakePointerType(); } catch { return false; }
                return true;
            }

            // Generic type parameters need a declaring context.
            if (name.Length > 0 && name[0] == '!') return false;

            int lt = name.IndexOf('<');
            if (lt < 0)
            {
                type = resolveDefinition(name);
                return type != null;
            }

            if (name[name.Length - 1] != '>') return false;

            string definitionName = name.Substring(0, lt);
            string argsBody = name.Substring(lt + 1, name.Length - lt - 2);

            var argNames = SplitTopLevelArguments(argsBody);
            if (argNames == null || argNames.Count == 0) return false;

            var argTypes = new Type[argNames.Count];
            for (int i = 0; i < argNames.Count; i++)
                if (!TryParse(argNames[i], resolveDefinition, out argTypes[i]))
                    return false;

            var definition = resolveDefinition(definitionName);
            if (definition == null) return false;

            try { type = definition.MakeGenericType(argTypes); } catch { return false; }
            return true;
        }

        static List<string> SplitTopLevelArguments(string body)
        {
            var result = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c == ',' && depth == 0)
                {
                    result.Add(body.Substring(start, i - start));
                    start = i + 1;
                }
            }
            result.Add(body.Substring(start));
            return result;
        }
    }
}
