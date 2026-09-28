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
    /// .NET runtimes.
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

        /// <summary>
        /// The canonical name, or a placeholder when there is no type at all. <b>For diagnostics only.</b>
        /// A real canonical name never has a null type, so <see cref="GetCanonicalName(Type)"/> throws
        /// instead - but an error message that is built from a type the reader could not resolve must not
        /// throw a second, unrelated exception while reporting the first.
        /// </summary>
        public static string GetCanonicalNameOrUnknown(Type? type)
            => type == null ? "<unknown>" : GetCanonicalName(type);

        /// <summary>
        /// The element type of an array or a pointer. Reflection annotates the accessor as maybe-null
        /// because it answers null for every other kind of type, so this states the invariant its callers
        /// already rely on: each of them names the element of a type that passed <c>IsArray</c> or
        /// <c>IsPointer</c>, where the runtime always has one. It is not a caller error, so it is not
        /// reported as one — a shape that violates it cannot be named at all, and naming the shape is
        /// the only thing there is to say. <see cref="SchemaRegistry"/> describes an array through the
        /// same answer, so the check is shared rather than restated there.
        /// </summary>
        internal static Type ElementTypeOf(Type type)
            => type.GetElementType() ?? throw new InvalidOperationException(
                $"The runtime reports no element type for the array or pointer type '{type.Name}', so it has no canonical name.");

        static void AppendCanonicalName(StringBuilder sb, Type type)
        {
            if (type.IsArray)
            {
                AppendCanonicalName(sb, ElementTypeOf(type));
                sb.Append('[');
                for (int i = 1; i < type.GetArrayRank(); i++) sb.Append(',');
                sb.Append(']');
                return;
            }

            if (type.IsPointer)
            {
                AppendCanonicalName(sb, ElementTypeOf(type));
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

        /// <summary>
        /// The type a nested type is declared in. Annotated maybe-null by reflection because a
        /// top-level type has none, which is the one case the <c>IsNested</c> below has already
        /// excluded. Stated as its own check so a nested type's name cannot silently lose its
        /// declaring-type prefix.
        /// </summary>
        static Type DeclaringTypeOf(Type type)
            => type.DeclaringType ?? throw new InvalidOperationException(
                $"The runtime reports no declaring type for the nested type '{type.Name}', so it has no canonical name.");

        static void AppendDefinitionName(StringBuilder sb, Type type)
        {
            if (type.IsNested)
            {
                AppendDefinitionName(sb, DeclaringTypeOf(type));
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
        /// <paramref name="resolveDefinition"/>, which answers null for a name this build does not have
        /// — that is how a registry or a binder reports "no such type"
        /// (<c>SchemaRegistry.ResolveDefinitionName</c> is written to that contract), and it is a miss
        /// rather than a half-answer. Generic parameters cannot be resolved without context and return
        /// false. This is only used on cold paths (bake and migration), never per save.
        /// </summary>
        internal static bool TryParseCanonicalName(string name, Func<string, Type?> resolveDefinition, out Type type)
        {
            type = null!;
            if (string.IsNullOrEmpty(name) || resolveDefinition == null) return false;
            return TryParse(name, resolveDefinition, out type);
        }

        static bool TryParse(string name, Func<string, Type?> resolveDefinition, out Type type)
        {
            type = null!;

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
                try
                {
                    // Rank 1 is the *vector* array, so it has to be built as one. `MakeArrayType(1)` also
                    // takes rank 1 but returns a distinct Type - it names its rank as unspecified
                    // (`Int32[*]` rather than `Int32[]`) - which is not assignable to `typeof(T[])` even
                    // though the two are the same shape. Anything resolved from a canonical name is then
                    // used as a field's declared type, a generic argument or a CLR instance to construct,
                    // and every one of those goes through an assignability or a cast check, so the wrong
                    // Type fails much later and far from here - as a bare reflection ArgumentException out
                    // of FieldInfo.SetValue, naming a type that does not look like any in the model. The
                    // grammar has no separate spelling for an unspecified rank (GetCanonicalName writes
                    // `T[]` for rank 1 and `T[,]` above it, and so does the generator's BuildRobustName),
                    // so `[]` can only ever mean the vector.
                    type = rank == 1 ? element.MakeArrayType() : element.MakeArrayType(rank);
                }
                catch { return false; }
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
                // A definition name the resolver does not have is a miss, not a half-answer: the parser
                // reports false and leaves the out parameter null, which is what every other miss here
                // does too.
                var resolved = resolveDefinition(name);
                if (resolved == null) return false;

                type = resolved;
                return true;
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
