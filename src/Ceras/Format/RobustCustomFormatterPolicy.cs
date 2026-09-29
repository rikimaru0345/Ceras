using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Ceras.Exceptions;
using Ceras.Formatters;

namespace Ceras.Format
{
    /// <summary>
    /// Decides, for a robust context, which formatter wrote each configured type's bytes, and keeps the
    /// manifest in step with that decision.
    /// <para>
    /// <b>The problem this now solves, and how.</b> A user custom formatter writes opaque bytes. Before
    /// the format carried opaque-schema and formatter-selection metadata, nothing in the schema section
    /// described them and nothing in the fingerprint recorded that they had been written at all, so a
    /// later build that removed, reorders or retypes a member of that type could not skip the value and
    /// silently returned wrong data for everything after it — while a build that kept the member saw a
    /// <em>matching</em> fingerprint and took the generated reader. Both halves are now facts of the
    /// file: the manifest entry for a custom-formatted type is
    /// <see cref="SchemaTypeKind.Opaque"/> (no member list, a declared length in the payload, and the
    /// formatter's identity), and the formatter's identity is hashed into the layout fingerprint. A
    /// build that swaps the formatter therefore takes the migration path and the migration reader
    /// accounts for or refuses the value explicitly.
    /// </para>
    /// <para>
    /// <b>How a formatter is recognised, and what that does and does not establish.</b> Generated
    /// formatters are not recognised by name, by namespace, or by anything a caller can assert about
    /// them — a generated formatter class is an ordinary type in the consumer's own assembly and the
    /// runtime cannot tell where it came from. They are recognised by <em>identity</em>: the generator
    /// emits a static <c>Schema</c> field on every formatter class and registers that very instance for
    /// the type, so the manifest entry and the installed formatter's own declaration are the same
    /// object. A formatter is the context's own only when that holds, and that check is unchanged by
    /// the metadata this class adds: it is still the same <c>Schema</c>-field-identity test, and a
    /// formatter that declares one is held to it rather than being treated as a user payload.
    /// </para>
    /// <para>
    /// <b>What is still refused, and why.</b> Three things, all of them shapes where the format has
    /// nowhere to record the selection (see <see cref="SchemaRegistry.RequireOpaqueSelectionsHaveARecordableIdentity"/>
    /// for the first, <see cref="SchemaRegistry.RequireOpaqueSelectionsAreDescribable"/> for the second, and
    /// <see cref="Classify"/> for the third): a type whose schema identity is a <em>reserved name</em>, which
    /// is a fixed one-byte id with no room for the metadata; a type outside the context's manifest, which
    /// has no entry to record it in at all; and a formatter that declares a generated-shaped
    /// <c>Schema</c> which is not the instance the manifest holds for the type — a hand-written schema that
    /// contradicts the type it claims to describe, which no amount of opaque metadata can make truthful.
    /// </para>
    /// <para>
    /// What is <em>not</em> refused any more: an overridden type that also has a generated formatter (the
    /// sharpest form of the old argument — its entry becomes opaque, so the schema stops advertising members
    /// the payload does not contain), a value type as well as a class, and a formatter installed for a type
    /// Ceras discovers after the serializer was constructed, from <c>OnConfigNewType</c>.
    /// </para>
    /// <para>
    /// That last one is deliberately narrow, and it is the whole of what a post-construction install can
    /// be. A serializer seals its config when it is constructed, so every direct assignment to a type
    /// configuration is refused from that point on; <c>OnConfigNewType</c> is the one door left, opened for
    /// one type and closed again when it returns. A selection made through it is honoured only while the
    /// manifest has not been sealed and baked by an earlier Save/Load: afterwards the registry refuses to
    /// record it, and a type joining a baked manifest is refused too (<see cref="SchemaRegistry.Register"/>).
    /// </para>
    /// <para>
    /// <b>The residual limit, unchanged and not widened.</b> <see cref="SchemaRegistry.Register"/> is
    /// public, so a caller can register a schema of its own and thereby satisfy the identity check with a
    /// hand-written formatter and a hand-written (possibly untrue) schema: that formatter is then the
    /// context's own, its bytes are described by an entry that need not describe them, and no opaque
    /// frame is written. That forge is pre-existing and independent of this check — on an unguarded
    /// build the same registration already lets a file's schema disagree with its payload. This change
    /// does not widen it (a forged registration is not an opaque selection, so it neither gains the
    /// frame nor loses anything), and closing it means verifying a schema against the type's real
    /// members, which is separate work. What this change does remove is the two ways the mismatch
    /// arises by accident: a custom formatter installed through
    /// <see cref="TypeConfig.CustomFormatter"/>, and one that merely imitates a generated formatter's
    /// name or namespace.
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
        /// Brings the manifest into agreement with the formatters this config actually has, and refuses
        /// the selections the format cannot record. Called at context setup and again before every
        /// Save/Load. A serializer seals its config when it is constructed, so a direct assignment after
        /// that is refused rather than half-applied; the one thing that can still add a formatter after
        /// the setup-time check is <c>OnConfigNewType</c>, which configures precisely the type it is
        /// handed and only while it runs — and only while the manifest has not been sealed and baked by an
        /// earlier Save/Load, because a selection made after that can be described by neither the schema
        /// section nor the fingerprint.
        /// <para>
        /// The scan is O(configured types) and runs before every Save/Load, so it is skipped while
        /// nothing has been assigned since the last <em>successful</em> check — a robust context cannot
        /// gain or lose a formatter without going through the <see cref="TypeConfig.CustomFormatter"/>
        /// setter, which bumps the counter, so that door cannot slip past the check either. The counter is
        /// only advanced once the scan has passed, so a context that has already been caught keeps failing.
        /// </para>
        /// </summary>
        public static void Reconcile(CerasRobustConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            if (config.VerifiedFormatterAssignmentCount == config.FormatterAssignmentCount)
                return;

            List<string>? offenders = null;

            foreach (var typeConfig in config.EnumerateConfiguredTypes())
            {
                var formatter = typeConfig.CustomFormatter;
                if (formatter == null) continue;

                var verdict = Classify(formatter, typeConfig, config.SchemaRegistry);

                switch (verdict.Kind)
                {
                    case SelectionKind.ContextOwn:
                        // The context's own formatter, described by the entry the manifest already holds.
                        config.SchemaRegistry.ClearOpaqueSelection(typeConfig.Type);
                        break;

                    case SelectionKind.UserCustom:
                        config.SchemaRegistry.ApplyOpaqueSelection(
                            typeConfig.Type,
                            OpaquePayload.FormatterToken(formatter),
                            // A value type is written inline by every writer that reaches it — the
                            // collection and array formatters inject the *specific* formatter for a value
                            // type and the reference wrapper for a reference type, so the type alone
                            // decides whether a marker is in front of the frame. The file records which,
                            // so a reader that no longer has the type can still account for the value.
                            typeConfig.Type.IsValueType);
                        break;

                    default:
                        (offenders ??= new List<string>()).Add(
                            $"  - '{TypeIdentity.GetCanonicalName(typeConfig.Type)}' -> '{formatter.GetType().FullName}'" +
                            (verdict.Reason != null ? ": " + verdict.Reason : null));
                        break;
                }
            }

            if (offenders == null)
            {
                // The selections that reached the manifest are also asked whether they can reach the *file*.
                // A type Ceras addresses by a reserved well-known name has nowhere to record which formatter
                // wrote its value, whatever the manifest holds, so it is refused here — while the caller is
                // still in the same call stack that configured it. (The other half, a type outside the
                // manifest, needs the bake to have run, so it is asked at the seal.)
                config.SchemaRegistry.RequireOpaqueSelectionsHaveARecordableIdentity();

                config.VerifiedFormatterAssignmentCount = config.FormatterAssignmentCount;
                return;
            }

            throw new CerasSchemaException(BuildMessage(offenders));
        }

        enum SelectionKind
        {
            /// <summary>The context's own formatter: this context's schema section describes its bytes.</summary>
            ContextOwn,

            /// <summary>A user custom formatter: opaque bytes, framed and recorded as such.</summary>
            UserCustom,

            /// <summary>Refused. <see cref="SelectionVerdict.Reason"/> says why, in one clause.</summary>
            Refused,
        }

        readonly struct SelectionVerdict
        {
            public readonly SelectionKind Kind;
            public readonly string? Reason;

            public SelectionVerdict(SelectionKind kind, string? reason = null)
            {
                Kind = kind;
                Reason = reason;
            }
        }

        /// <summary>
        /// What a configured formatter is, for this context. Three answers, and the middle one is the
        /// identity check: a formatter that declares the generator's <c>Schema</c> field <em>is</em> a
        /// generated formatter and is held to the instance the manifest holds, not taken on trust.
        /// </summary>
        static SelectionVerdict Classify(IFormatter formatter, TypeConfig typeConfig, SchemaRegistry registry)
        {
            // The library's own schema-aware string/Type formatters, on type alone. They are
            // `internal sealed`, so nothing outside Ceras can be one, and they have to qualify this way:
            // `string` and `System.Type` are well-known fixed ids rather than manifest entries, so there
            // is no manifest entry to match them against.
            if (formatter is RobustStringFormatter || formatter is RobustTypeFormatter)
                return new SelectionVerdict(SelectionKind.ContextOwn);

            if (!TryGetDeclaredSchema(formatter.GetType(), out var declared))
                return new SelectionVerdict(SelectionKind.UserCustom);   // a hand-written formatter declares no schema, by construction

            if (!registry.TryGetInfo(typeConfig.Type, out var registered))
                return new SelectionVerdict(SelectionKind.Refused,
                    "it declares a generated formatter's static Schema field, but this context's manifest holds no entry for the type, so " +
                    "there is nothing for that declaration to be identical to");

            return ReferenceEquals(declared, registered)
                ? new SelectionVerdict(SelectionKind.ContextOwn)
                : new SelectionVerdict(SelectionKind.Refused,
                    "it declares a generated formatter's static Schema field holding an instance that is not the entry this context's " +
                    "manifest holds for the type, so the schema this context would write and the bytes this formatter writes are not the same " +
                    "thing. Let the generated registration install this formatter, or do not declare the field at all");
        }

        /// <summary>
        /// The <c>internal static readonly SchemaTypeInfo Schema</c> the generator emits on every formatter
        /// class, read off the formatter's runtime type. The out is nullable because a formatter the
        /// generator did not emit declares no such field, which is the <c>false</c> answer.
        /// </summary>
        static bool TryGetDeclaredSchema(Type formatterType, out SchemaTypeInfo? schema)
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
            sb.Append("A robust context cannot use these formatters, but ");
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
                "generated reader is computed from that schema. A user custom formatter is now supported: its bytes are framed with a declared");
            sb.AppendLine(
                "length, the manifest entry records it as an opaque block with no member list, and the formatter's identity is hashed into the");
            sb.AppendLine("fingerprint, so swapping a formatter changes the layout and takes the migration path instead of the generated one.");
            sb.AppendLine();
            sb.AppendLine("What is refused above is the remainder, where the format has nowhere to record that:");
            sb.AppendLine(
                "  - a type Ceras addresses by a reserved well-known id (a scalar, string, Type, a closed built-in collection, an array, a");
            sb.AppendLine(
                "    Nullable, a tuple) has a fixed id with no room for formatter-selection metadata, and a type outside this context's manifest");
            sb.AppendLine("    has no entry to record it in. Either way the payload would be invisible to the schema and to the fingerprint.");
            sb.AppendLine(
                "  - a formatter that declares the source generator's static `Schema` field is a generated formatter and is recognised by identity");
            sb.AppendLine(
                "    - the very instance the context registered for the type - not by name, namespace, or any flag you can set. A declaration that");
            sb.AppendLine(
                "    contradicts the registered entry is a schema that does not describe the bytes, and no opaque framing can fix that.");
            sb.AppendLine();
            sb.AppendLine("Use one of these instead:");
            sb.AppendLine(
                "  - Give the value a type of its own that the context's manifest describes: a [CerasSerializable] class or struct, and a custom");
            sb.AppendLine(
                "    formatter for it. The formatter's bytes are then framed and accounted for like any other opaque payload.");
            sb.AppendLine(
                "  - Keep the custom encoding, but store it as an id or a structural value: a serializable id field resolved by your own loading");
            sb.AppendLine(
                "    code, or a [CerasSerializable] type the generator emits a formatter and a schema for. The custom formatter then belongs to a");
            sb.AppendLine("    raw/transient serializer over that value, not to the robust graph.");
            sb.Append("  - If this type is only serialized transiently, use a raw context for it: new CerasFastSerializer(...)");
            return sb.ToString();
        }
    }
}
