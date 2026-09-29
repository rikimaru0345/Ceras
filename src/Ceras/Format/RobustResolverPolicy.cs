using System;
using System.Reflection;
using System.Text;
using System.Threading;
using Ceras.Exceptions;
using Ceras.Formatters;

namespace Ceras.Format
{
    /// <summary>
    /// Decides whether a formatter a <em>resolver</em> selected may write a value in a robust context.
    /// <para>
    /// <b>The door this closes.</b> A user custom formatter's bytes are only describable when the
    /// manifest records the selection: the entry becomes <see cref="SchemaTypeKind.Opaque"/>, the payload
    /// gets a declared length in front of it, and the formatter's identity is hashed into the layout
    /// fingerprint — so swapping a formatter changes the layout and takes the migration path instead of
    /// matching it (see <see cref="RobustCustomFormatterPolicy"/> and
    /// <c>Format/LAYOUT.md</c>, "A user custom formatter's payload is an opaque block"). Only
    /// <see cref="TypeConfig.CustomFormatter"/> ever got that: <see cref="CerasSerializer"/> asks the
    /// manifest through <c>WrapOpaqueIfSelected</c> at the moment it installs that one, and the two
    /// resolver doors — <see cref="TypeConfig.CustomResolver"/> and each
    /// <see cref="CerasConfig.OnResolveFormatter"/> callback — went straight into
    /// <c>meta.SpecificFormatter</c> without asking.
    /// </para>
    /// <para>
    /// <b>What that produced, measured.</b> A save whose formatter came from a resolver carried the
    /// <em>same layout fingerprint</em> as a save of the same graph with no formatter at all, under a
    /// manifest entry that still advertised an ordinary class, with the resolver's bytes in the payload
    /// unframed. Two builds whose resolvers answered differently therefore produced files claiming the
    /// same layout; the second read the first one's payload on the generated fast path and returned a
    /// wrong value with nothing reported. (Same tag-less encodings differing only in meaning decode each
    /// other's bytes to the exact complement, consuming the whole block — the damage is silent precisely
    /// because it is well-formed.) The fingerprint equality is the load-bearing half: without it the
    /// migration interpreter would have been consulted and would have had a chance to notice the schema
    /// and the payload disagreeing.
    /// </para>
    /// <para>
    /// <b>Why a refusal and not a frame.</b> The opaque machinery could record a resolver's answer in
    /// principle — the frame is written by <c>OpaqueFramedFormatter&lt;T&gt;</c>, which is
    /// formatter-source-agnostic — but not at a moment the registry can accept:
    /// <see cref="SchemaRegistry.ApplyOpaqueSelection"/> refuses once the manifest is sealed and baked,
    /// and a resolver's answer does not exist until formatter resolution, which for a type discovered
    /// mid-operation is <em>after</em> the bake. Recording it eagerly at <c>Seal()</c> would mean calling
    /// the application's delegates at seal time, with the serializer half-constructed. And the choice
    /// would then depend on <em>when</em> a resolver happened to be first asked, so two builds could
    /// produce different layouts from one configuration. Refusing is the only answer that is correct
    /// unconditionally, and it costs nothing that worked before: a caller on this door was already
    /// getting a file that could not be read back.
    /// </para>
    /// <para>
    /// <b>What is honoured.</b> Three selections, and each is decided by identity rather than by anything
    /// a caller can assert:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>The context's own formatter</b> — the very rule <see cref="TypeConfig.CustomFormatter"/> already
    /// uses: it declares the generator's static <c>Schema</c> field and holds the instance this context's
    /// manifest holds for the type, so its bytes are what the entry already describes.
    /// </description></item>
    /// <item><description>
    /// <b>A resolver Ceras installed itself</b>, recognised by the delegate identity rather than by the
    /// formatter type it answers with. This is deliberately narrower than it looks: the answer of
    /// <c>TypeConfigDefaults.ForceDynamicResolver</c> is a <see cref="ReflectionFallbackFormatter{T}"/>,
    /// which is <c>internal sealed</c> and therefore cannot be named by a caller — and can be constructed
    /// for <em>any</em> type by one that reaches for reflection. A formatter-type exemption would accept
    /// exactly that; scoping the approval to the resolver the library installed does not. Both facts are
    /// measured, and the corresponding test pins the reflection-constructed case.
    /// </description></item>
    /// <item><description>
    /// <b>A formatter whose exact runtime type a Ceras assembly registered</b> through
    /// <see cref="RegisterApprovedFormatterType"/>. That call is internal and reachable only from the
    /// friend assemblies, so a caller cannot approve its own formatter; the comparison itself is
    /// <see cref="Type"/> equality (against the answer's own type, or its
    /// <see cref="Type.GetGenericTypeDefinition"/> for a family), which a caller cannot satisfy by
    /// declaring a class with the same name or namespace. <c>Ceras.ImmutableCollections</c> registers its
    /// eight open generic formatter types this way, so the approval is compile-checked on that side and
    /// cannot rot when a formatter is added.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Where this is asked.</b> At the moment the resolver's answer is accepted, inside
    /// <c>CerasSerializer.GetSpecificFormatter</c> — before the value has a byte, so a refused save
    /// returns nothing and a refused load has read none of that value — and on both the Save and the Load
    /// path, since both resolve formatters through the same two lines. A raw/transient context is
    /// untouched: it describes nothing, so there is nothing to record and nothing to refuse.
    /// </para>
    /// </summary>
    internal static class RobustResolverPolicy
    {
        /// <summary>
        /// The exact runtime types of the Ceras-shipped formatter types approved by identity, as an open
        /// generic definition per formatter family. Compared against the answer's own
        /// <see cref="Type"/> or its <see cref="Type.GetGenericTypeDefinition"/>, which a caller cannot
        /// satisfy by declaring anything: their type is a different <see cref="Type"/>.
        /// <para>
        /// An array behind a copy-on-write swap rather than a <c>HashSet&lt;Type&gt;</c> under a lock,
        /// because the two sides have very different shapes. Registration happens a handful of times, from
        /// a friend assembly's type initializer, and readers are formatter resolutions that may run
        /// concurrently on several threads — so the read side should cost a field load and a scan of a
        /// very short array, not a lock it could block behind. Writers copy the array and publish it with
        /// <see cref="Volatile.Write{T}(ref T, T)"/>, so a reader either sees the old snapshot or the new
        /// one and never a half-written collection. A <c>HashSet&lt;Type&gt;</c> shared the same
        /// correctness problem — it is not safe to read while another thread writes — without offering the
        /// lock-free read this side wants.
        /// </para>
        /// </summary>
        static volatile Type[] _approvedFormatterTypes = Array.Empty<Type>();

        /// <summary>
        /// Serializes writers only. Readers never take it: they read the snapshot field once.
        /// </summary>
        static readonly object _approvedFormatterTypesLock = new object();

        /// <summary>
        /// The formatter <c>TypeConfigDefaults.ForceDynamicResolver</c> answers with. Approved by the
        /// resolver rather than by this type — see the type's own remarks — and held only so the identity
        /// check below can be a reference comparison rather than a type-name comparison.
        /// </summary>
        internal static Type ForceDynamicAnswerType { get; } = typeof(ReflectionFallbackFormatter<>);

        /// <summary>
        /// The delegate <c>TypeConfigDefaults</c> installs as a <see cref="TypeConfig.CustomResolver"/>
        /// for the types it forces into reflection-based serialization (<c>Version</c>, an
        /// <c>Expression</c>, <c>TrueReadOnlyCollection&lt;&gt;</c>). Compared by reference to the one the
        /// application holds, so only that resolver — not any delegate of the caller's — is approved.
        /// </summary>
        internal static FormatterResolverCallback LibraryForcedResolver => TypeConfigDefaults.ForcedDynamicResolver;

        /// <summary>
        /// Approves a formatter type a Ceras-shipped resolver may return in a robust context.
        /// <para>
        /// Internal, and called by friend assemblies only (<c>Ceras.ImmutableCollections</c> for the
        /// immutable-collection formatters). It takes the <em>open generic definition</em> — pass
        /// <c>typeof(ImmutableListFormatter&lt;&gt;)</c> — and every closed instantiation of it is then
        /// approved, because such a formatter is closed over exactly one value type and therefore can
        /// only ever be installed for that one type.
        /// </para>
        /// <para>
        /// Deliberately not a name, a namespace or an assembly: all three are things a caller can
        /// reproduce, and the whole point of the approval is that they cannot. Deliberately not a public
        /// API either — a caller that could approve its own formatter would turn this back into the hole
        /// it closes.
        /// </para>
        /// <para>
        /// Idempotent, and safe to call concurrently with formatter resolution: a duplicate is dropped
        /// without publishing, and a new entry goes out as a fresh array swapped in under a write to the
        /// snapshot field, so a reader taking the field mid-update sees the previous snapshot rather than
        /// a collection being mutated underneath it.
        /// </para>
        /// </summary>
        internal static void RegisterApprovedFormatterType(Type openGenericDefinition)
        {
            if (openGenericDefinition == null) throw new ArgumentNullException(nameof(openGenericDefinition));
            if (!openGenericDefinition.IsGenericTypeDefinition)
                throw new ArgumentException(
                    $"'{TypeIdentity.GetCanonicalNameOrUnknown(openGenericDefinition)}' is not an open generic definition, so it cannot stand " +
                    "for a formatter family. Pass the open form (typeof(XFormatter<>)), not a closed instantiation.",
                    nameof(openGenericDefinition));

            lock (_approvedFormatterTypesLock)
            {
                var current = _approvedFormatterTypes;
                for (int i = 0; i < current.Length; i++)
                    if (ReferenceEquals(current[i], openGenericDefinition))
                        return;

                var next = new Type[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = openGenericDefinition;
                _approvedFormatterTypes = next;
            }
        }

        /// <summary>
        /// Whether this formatter is one of the Ceras-shipped types approved above. An exact
        /// <see cref="Type"/> test in both directions: the stored open definition, and the answer's own
        /// open definition for a family.
        /// <para>
        /// One read of the snapshot field and then a scan of a very short array — eight entries today — with
        /// no lock. That is the whole read path, and it is on the cold resolution of a resolver answer,
        /// never on a per-value one.
        /// </para>
        /// </summary>
        static bool IsApprovedFormatterType(IFormatter formatter)
        {
            // Read once: a registration landing between the two comparisons below could otherwise make the
            // two halves disagree about which snapshot they are looking at.
            var approved = _approvedFormatterTypes;

            var type = formatter.GetType();

            for (int i = 0; i < approved.Length; i++)
                if (ReferenceEquals(approved[i], type)) return true;

            if (!type.IsGenericType || type.IsGenericTypeDefinition)
                return false;

            var open = type.GetGenericTypeDefinition();
            for (int i = 0; i < approved.Length; i++)
                if (ReferenceEquals(approved[i], open)) return true;

            return false;
        }

        /// <summary>
        /// Whether <paramref name="resolver"/> is the delegate Ceras installed itself for the types it
        /// forces into reflection-based serialization.
        /// <para>
        /// A reference comparison, not a method comparison: a caller can build its own delegate over the
        /// same <c>private static</c> method through reflection, and the one it builds is a different
        /// delegate instance. What is being recognised is "this exact resolver object, installed by the
        /// library", and that is what a reference comparison answers.
        /// </para>
        /// </summary>
        static bool IsLibraryForcedResolver(FormatterResolverCallback resolver)
            => resolver != null && ReferenceEquals(resolver, LibraryForcedResolver);

        /// <summary>
        /// Whether this formatter is the one the library's own force-dynamic resolver produces for
        /// <paramref name="type"/>. Asked only after <see cref="IsLibraryForcedResolver"/> has already
        /// said yes, so it narrows that approval rather than granting one: a caller that installed its
        /// own resolver is refused whatever it answers, and a caller that cannot reach the library's
        /// answer at all cannot get here.
        /// </summary>
        static bool IsForceDynamicAnswer(IFormatter formatter, Type type)
        {
            var answer = formatter.GetType();
            if (!answer.IsGenericType) return false;

            var open = answer.GetGenericTypeDefinition();
            if (open != ForceDynamicAnswerType) return false;

            // The very type the resolver was asked about. A resolver answers for the type it is given, so
            // this holds for the library's resolver by construction; stating it keeps the approval tied to
            // the one question the resolver was asked.
            return answer.GetGenericArguments().Length == 1 && answer.GetGenericArguments()[0] == type;
        }

        /// <summary>
        /// The door a formatter was selected through, as it is named in the refusal. A resolver's answer
        /// carries one of these, so a message can point at the line the caller has to change. The
        /// library's own resolver never reaches a refusal — it is exempt, and answered before one is
        /// needed — so it has no spelling here.
        /// </summary>
        internal readonly struct Door
        {
            readonly string _description;

            Door(string description) => _description = description;

            /// <summary>The application's <see cref="TypeConfig.CustomResolver"/> for a type.</summary>
            public static Door CustomResolver => new Door(nameof(TypeConfig.CustomResolver));

            /// <summary>One entry of <see cref="CerasConfig.OnResolveFormatter"/>.</summary>
            public static Door OnResolveFormatter(int index)
                => new Door($"{nameof(CerasConfig.OnResolveFormatter)} callback #{index}");

            public override string ToString() => _description;
        }

        /// <summary>
        /// Refuses a resolver's answer unless it is one this context can already describe, and returns
        /// normally when it is. Asked at the moment the answer is accepted, before the value has a byte.
        /// <para>
        /// <paramref name="resolver"/> is the delegate the answer came from, which is what the
        /// library-installed exemption is keyed on; it is null for the built-in resolver list, whose
        /// answers are the format's own and are not consulted here at all.
        /// </para>
        /// </summary>
        public static void RequireSelectionIsDescribable(
            CerasRobustConfig config, IFormatter formatter, Type type, Door door, FormatterResolverCallback resolver)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (formatter == null) throw new ArgumentNullException(nameof(formatter));
            if (type == null) throw new ArgumentNullException(nameof(type));

            // The context's own formatter, by the identity rule CustomFormatter already uses.
            if (RobustCustomFormatterPolicy.IsContextOwnFormatter(formatter, type, config.SchemaRegistry))
                return;

            if (IsLibraryForcedResolver(resolver) && IsForceDynamicAnswer(formatter, type))
                return;

            if (IsApprovedFormatterType(formatter))
                return;

            throw new CerasSchemaException(BuildMessage(formatter, type, door));
        }

        static string BuildMessage(IFormatter formatter, Type type, Door door)
        {
            string typeName = TypeIdentity.GetCanonicalNameOrUnknown(type);
            string formatterName = TypeIdentity.GetCanonicalNameOrUnknown(formatter.GetType());

            var sb = new StringBuilder();
            sb.Append("Cannot serialize '").Append(typeName).Append("' through a formatter selected by ").Append(door).AppendLine(":");
            sb.Append("  ").Append(typeName).Append(" -> ").Append(formatterName).AppendLine();
            sb.AppendLine();
            sb.AppendLine(
                "A robust save has to describe every value it writes, and it describes a value two ways: the generated member list its");
            sb.AppendLine(
                "manifest entry carries, or an opaque block — a declared length in front of the formatter's own bytes, with the formatter's");
            sb.AppendLine("identity in the schema section and hashed into the layout fingerprint. Which one is written is a fact about the file.");
            sb.AppendLine();
            sb.AppendLine("A resolver callback is asked for a formatter at the moment Ceras needs one, which is in the middle of an operation, so by");
            sb.AppendLine(
                "then the manifest has already been sealed and baked and there is nowhere left to record the selection — and a value written that");
            sb.AppendLine("way is invisible to both. Such a save would carry the same layout fingerprint as one written with no formatter at all, so a");
            sb.AppendLine(
                "later build whose resolver answers differently would match it, read the payload with a formatter that never wrote it, and get a wrong");
            sb.AppendLine("value with nothing reported. That is why the selection is refused here rather than written.");
            sb.AppendLine();
            sb.AppendLine("Three selections are allowed, and this one is not one of them:");
            sb.AppendLine(
                "  - the context's own generated formatter, recognised by the static `Schema` field the source generator emits holding the very");
            sb.AppendLine("    instance this context's manifest holds for the type. Its bytes are what the entry already describes.");
            sb.AppendLine(
                "  - a resolver Ceras installed itself. These are approved by the resolver, never by the formatter's type: a type a caller cannot name");
            sb.AppendLine(
                "    can still be constructed by one that uses reflection, so approving a formatter type here would approve the caller's as well.");
            sb.AppendLine(
                "  - a formatter shipped by a Ceras assembly and registered by it. Only Ceras and its friend assemblies can say so, and the approval is");
            sb.AppendLine("    the formatter's exact runtime type, which a caller cannot satisfy by declaring a class with the same name or namespace.");
            sb.AppendLine();
            sb.AppendLine("Use one of these instead:");
            sb.AppendLine(
                $"  - Install the formatter through TypeConfig.CustomFormatter (config.ConfigType<{typeName}>().CustomFormatter = formatter). That door is");
            sb.AppendLine(
                "    supported: the entry becomes an opaque block, the payload gets a declared length, and the formatter's identity is hashed into the");
            sb.AppendLine("    fingerprint, so a build with a different formatter is refused by name instead of decoding. A formatter that needs the");
            sb.AppendLine(
                "    serializer can declare a CerasSerializer field and Ceras will inject it (see PrepareFormatter), which is what the resolver was");
            sb.AppendLine("    being used for.");
            sb.AppendLine(
                "  - If the type is only ever serialized transiently, use a raw context for it (new CerasFastSerializer(...)), where a resolver's formatter");
            sb.AppendLine("    writes exactly the bytes it always has: a raw context describes nothing and accounts for nothing.");
            sb.AppendLine(
                "  - If this selection came from a Ceras-shipped resolver that should be approved here, it is a defect in Ceras: register the formatter");
            sb.AppendLine("    type with RobustResolverPolicy.RegisterApprovedFormatterType from the assembly that ships it.");
            return sb.ToString();
        }
    }
}