using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Ceras.Exceptions;

namespace Ceras.Format
{
    /// <summary>
    /// A robust save is only as truthful as its schema section: the migration reader skips and
    /// interprets every byte by consulting the schema the *file* carries, and the layout fingerprint
    /// that selects the generated reader is computed from that same schema. Formatter selection is not
    /// part of either.
    /// <para>
    /// A user custom formatter writes opaque bytes. Nothing in the schema describes them, and nothing in
    /// the fingerprint records that they were written at all, so a later build that removes, reorders or
    /// retypes a member of that type cannot skip the value and will silently return wrong data for
    /// everything after it — while a build that kept the member sees a *matching* fingerprint and takes
    /// the generated reader. Overriding a type that also has a generated formatter is the sharpest form
    /// of this: the generated schema still advertises the generated fields while the payload holds the
    /// custom bytes.
    /// </para>
    /// <para>
    /// This is therefore a <b>temporary</b> rejection: a user custom formatter is refused in a robust
    /// context and accepted in a raw/transient one, as before. It is lifted again once the file format
    /// carries explicit opaque-schema and formatter-selection metadata (Milestone 1).
    /// </para>
    /// <para>
    /// <b>How a formatter is recognised, and what that does and does not establish.</b> Generated
    /// formatters are not recognised by name, by namespace, or by anything a caller can assert about
    /// them — a generated formatter class is an ordinary type in the consumer's own assembly and the
    /// runtime cannot tell where it came from. They are recognised by <em>identity</em>: the generator
    /// emits a static <c>Schema</c> field on every formatter class and registers that very instance for
    /// the type, so the manifest entry and the installed formatter's own declaration are the same object.
    /// A formatter is the context's own only when that holds.
    /// <para>
    /// The residual limit is explicit: <see cref="SchemaRegistry.Register"/> is public, so a caller can
    /// register a schema of its own and thereby satisfy the identity check with a hand-written formatter
    /// and a hand-written (possibly untrue) schema. That forge is pre-existing and independent of this
    /// check — on an unguarded build the same registration already lets a file's schema disagree with
    /// its payload. Closing it means verifying a schema against the type's real members, which is
    /// Milestone 1 work. What this check removes is the two ways the mismatch arises by accident: a
    /// custom formatter installed through <see cref="TypeConfig.CustomFormatter"/>, and one that merely
    /// imitates a generated formatter's name or namespace.
    /// </para>
    /// </summary>
    internal static class RobustCustomFormatterPolicy
    {
        /// <summary>
        /// The name the source generator gives the static schema descriptor on every formatter class it
        /// emits (<c>internal static readonly SchemaTypeInfo Schema</c>). Read by reflection off the
        /// runtime type of the installed formatter, so it cannot be spoofed by declaring a type with a
        /// similar name: only this exact member, holding the exact instance the manifest registered, counts.
        /// </summary>
        const string GeneratedSchemaFieldName = "Schema";

        /// <summary>
        /// Throws when the config has any formatter the context's own schema does not describe, naming
        /// the type, the formatter and the way out. Called at context setup and again before every
        /// Save/Load: a type config is sealed lazily (on the type's first use), so a formatter can still
        /// be installed after the serializer was constructed but before the first save.
        /// </summary>
        public static void VerifyNoUserCustomFormatters(CerasRobustConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            // The scan below is O(configured types) and runs before every Save/Load, so it is skipped
            // while nothing has been assigned since the last *successful* check. A robust context cannot
            // gain a custom formatter without going through the CustomFormatter setter, which bumps this
            // counter, so a late install can never be missed. The counter is only advanced once the scan
            // has passed, so a context that has already been caught keeps failing.
            if (config.VerifiedFormatterAssignmentCount == config.FormatterAssignmentCount)
                return;

            List<string>? offenders = null;

            foreach (var typeConfig in config.EnumerateConfiguredTypes())
            {
                var formatter = typeConfig.CustomFormatter;
                if (formatter == null) continue;
                if (DescribesSchemaBytes(formatter, typeConfig, config.SchemaRegistry)) continue;

                (offenders ??= new List<string>()).Add(
                    $"  - '{TypeIdentity.GetCanonicalName(typeConfig.Type)}' -> '{formatter.GetType().FullName}'");
            }

            if (offenders == null)
            {
                config.VerifiedFormatterAssignmentCount = config.FormatterAssignmentCount;
                return;
            }

            throw new CerasSchemaException(BuildMessage(offenders));
        }

        /// <summary>
        /// True for a formatter whose bytes this context's schema section describes.
        /// <para>
        /// Two disjoint cases qualify:
        /// <list type="bullet">
        /// <item>The library's own schema-aware string/Type formatters, on type alone. They are
        /// <c>internal sealed</c>, so nothing outside Ceras can be one, and they have to qualify this way:
        /// <c>string</c> and <c>System.Type</c> are well-known fixed ids rather than manifest entries, so
        /// there is no manifest entry to match them against.</item>
        /// <item>The manifest's own generated formatter for the type: the schema the context registered for
        /// the type is the very instance declared by the static <c>Schema</c> field of the installed
        /// formatter's own type. Identity, not a name, a namespace, or a caller-side claim.</item>
        /// </list>
        /// </para>
        /// </summary>
        public static bool DescribesSchemaBytes(Formatters.IFormatter formatter, TypeConfig typeConfig, SchemaRegistry registry)
        {
            if (formatter is RobustStringFormatter || formatter is RobustTypeFormatter)
                return true;

            if (!registry.TryGetInfo(typeConfig.Type, out var registered))
                return false;   // not in this context's manifest: nothing describes it, nothing can skip it

            if (!TryGetDeclaredSchema(formatter.GetType(), out var declared))
                return false;   // a hand-written formatter declares no schema, by construction

            return ReferenceEquals(declared, registered);
        }

        /// <summary>
        /// The <c>internal static readonly SchemaTypeInfo Schema</c> the generator emits on every formatter
        /// class, read off the formatter's runtime type.
        /// </summary>
        static bool TryGetDeclaredSchema(Type formatterType, out SchemaTypeInfo schema)
        {
            var field = formatterType.GetField(GeneratedSchemaFieldName,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            schema = field?.GetValue(null) as SchemaTypeInfo;
            return schema != null;
        }

        static string BuildMessage(List<string> offenders)
        {
            const int maxListed = 5;

            var sb = new StringBuilder();
            sb.Append("A robust context cannot use a user custom formatter, but ");
            sb.Append(offenders.Count == 1 ? "one type has" : offenders.Count + " types have");
            sb.Append(" one:").AppendLine();

            for (int i = 0; i < offenders.Count && i < maxListed; i++)
                sb.AppendLine(offenders[i]);

            if (offenders.Count > maxListed)
                sb.Append("  - … and ").Append(offenders.Count - maxListed).AppendLine(" more");

            sb.AppendLine();
            sb.AppendLine(
                "A robust save describes its values in the schema section it carries, and the layout fingerprint that chooses the");
            sb.AppendLine(
                "generated reader is computed from that schema — neither records which formatter wrote the bytes. A custom formatter's");
            sb.AppendLine(
                "bytes are opaque to both, so a later build that removes, reorders or retypes a member of the type cannot skip them and");
            sb.AppendLine(
                "silently returns wrong data for everything after it, and a build that kept the member still sees a *matching* fingerprint.");
            sb.AppendLine(
                "Ceras recognises the context's own generated formatters by identity — the formatter's declared schema is the very instance");
            sb.AppendLine(
                "the context registered for the type — not by name, namespace, or any flag you can set, so copying the generated");
            sb.AppendLine("registration code does not make a custom formatter acceptable.");
            sb.AppendLine(
                "This is temporary: custom formatters stay rejected in robust contexts until the format carries explicit opaque-schema and");
            sb.AppendLine("formatter-selection metadata. Raw/transient serializers (CerasSerializer/CerasFastSerializer) are unaffected.");
            sb.AppendLine();
            sb.AppendLine("Use one of these instead:");
            sb.AppendLine("  - Keep the custom encoding, but store it as an id or a structural value: a serializable id field resolved by your");
            sb.AppendLine("    own loading code, or a [CerasSerializable] type the generator emits a formatter and a schema for. The custom");
            sb.AppendLine("    formatter then belongs to a raw/transient serializer over that value, not to the robust graph.");
            sb.AppendLine("  - Give the type a generated representation (a [CerasSerializable] class/struct whose members are all describable) and");
            sb.AppendLine("    drop the override.");
            sb.Append("  - If this type is only serialized transiently, use a raw context for it: new CerasFastSerializer(...)");
            return sb.ToString();
        }
    }
}
