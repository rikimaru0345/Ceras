using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Ceras.Exceptions;
using Ceras.Helpers;

namespace Ceras.Format
{
    /// <summary>
    /// Decides whether the member list the built-in reflection fallback is about to write is the one this
    /// context's manifest describes.
    /// <para>
    /// <b>The door this closes.</b> A robust save describes every value it writes, and it describes a value
    /// two ways: the member list its manifest entry carries — count, stable names, each member's type,
    /// order — or an opaque block with a declared length and the formatter's identity in it
    /// (<see cref="RobustCustomFormatterPolicy"/>). The member list is what the source generator derived at
    /// compile time, and it is hashed into the layout fingerprint, so it is a fact about the file.
    /// <c>ReflectionFallbackResolver</c> is the last resolver in <c>CerasSerializer.GetSpecificFormatter</c>,
    /// it answers for every type nothing else claims, and it derives <em>its</em> member list from the CLR
    /// type at run time (<c>SchemaMemberComparer</c>). Nothing compared the two, and nothing recorded the
    /// runtime one.
    /// </para>
    /// <para>
    /// <b>What that produced, measured.</b> Three ways to get two member lists that differ, none of them a
    /// corrupted manifest — all three produce a file that is self-consistent, carries the fingerprint of the
    /// described layout, and decodes to a wrong value:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>A member-less placeholder.</b> The generator registers a <c>Class</c>/<c>Struct</c> entry with no
    /// fields for any type it emits no formatter for — an engine object, or one excluded with
    /// <c>[CerasGeneratorBlacklist]</c> — and the fallback writes every reflected member of it. The entry
    /// describes "no members"; the payload holds some.
    /// </description></item>
    /// <item><description>
    /// <b>A different order.</b> Same members, same types, same total width, different order — and a
    /// same-width read of the other order returns a wrong value with nothing reported.
    /// <para>
    /// This one <b>was</b> produced by the two writers themselves: the generator sorted a member on the
    /// display string of its type (<c>"(int, int)Tup"</c>) and the runtime on
    /// <see cref="Type.FullName"/> (<c>"System.ValueTuple`2[...]Tup"</c>), so a tuple-typed, nullable-typed
    /// or nested-typed member moved. Both now spell the key the one way — the canonical type identity, see
    /// "The order a type's members are written in" in LAYOUT.md and
    /// <c>TypeHelpers.MemberSortKey</c> against <c>SchemaMemberComparer</c> — so for a generated type the
    /// two lists agree and the two writers write the same bytes.
    /// </para>
    /// <para>
    /// The comparison below is kept anyway, because a manifest entry can still be written <em>by hand</em>
    /// in an order of its author's choosing (<c>SchemaRegistry.Register</c> with an explicit
    /// <see cref="SchemaTypeInfo"/> is the documented forge for a historical file's shape), and a
    /// hand-written order the writer will not use is the same defect by another route. What cannot happen
    /// any more is the two writers disagreeing with each other.
    /// </para>
    /// </description></item>
    /// <item><description>
    /// <b>A different count.</b> A runtime <c>[Exclude]</c> (or <c>ConfigField(...).Exclude()</c>) is
    /// invisible to the generator, so the manifest keeps a member the payload does not contain. Two builds
    /// that exclude different same-width members write the same number of bytes, and a reader cannot notice
    /// the swap by the length.
    /// </description></item>
    /// </list>
    /// <para>
    /// And a fourth difference that is <em>not</em> a member-list difference, so it is asked separately: a
    /// member declared as an <em>array</em> type is written through a different formatter class by each
    /// writer (<c>ArrayFormatter&lt;T&gt;</c> against
    /// <c>ReferenceFormatter_KnownSealedType&lt;T[]&gt;</c>), which puts one extra marker byte in front of
    /// the same array payload — and no member list records a member's marker. Measured, and refused by
    /// <see cref="RequireNoUndescribedMemberMarker"/>.
    /// </para>
    /// <para>
    /// A member's <em>type</em> is compared as an identity rather than as a string, because the schema
    /// section records it as a resolved type id and the name in the descriptor never reaches the wire: the
    /// generator's spelling and a hand-written <see cref="SchemaRegistry.Register"/> descriptor's
    /// <see cref="Type.FullName"/> describe the same member, and only the identity says whether they do.
    /// That is also why a hand-written descriptor that lies about the member list is refused here while a
    /// truthful one is not — which is not a closing of that forge: the comparison exists at the resolver
    /// door for one selection, and says nothing about a descriptor used with any other formatter.
    /// </para>
    /// <para>
    /// <b>Why a refusal and not a record.</b> The opaque machinery could describe this payload — a frame is
    /// formatter-source-agnostic — but not at a moment the registry can accept:
    /// <see cref="SchemaRegistry.ApplyOpaqueSelection"/> refuses once the manifest is sealed and baked, and
    /// the fallback is chosen from inside the operation, which for a type discovered mid-operation is
    /// <em>after</em> the bake. Refusing is the only answer that is correct unconditionally, and it costs
    /// nothing that was working: a selection accepted here produced a file whose payload no schema section
    /// described.
    /// </para>
    /// <para>
    /// <b>What is compared, and what is not.</b> Two independent things, asked in this order, and neither
    /// is a substitute for the other:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>The member list</b> — the count, the stable names in order, and each member's type as an
    /// identity. That is the whole of what a manifest entry records, so it is the whole of what a
    /// member-by-member payload can be checked against.
    /// </description></item>
    /// <item><description>
    /// <b>Each member's own reference marker</b> — whether a marker byte stands in front of that member's
    /// bytes. <b>This is not part of any member list</b>, so the comparison above cannot see it and does not
    /// claim to: a type can pass the member-list check and still be written with different bytes. The check
    /// that sees it is <see cref="RequireNoUndescribedMemberMarker"/>, and it refuses an array-typed member
    /// outright, because the two writers bind different formatter classes for one and nothing in the file
    /// records which. Its own remarks state the measurement the boundary comes from, and that a shape it
    /// does not refuse is accepted on the strength of that measurement rather than on a proof.
    /// </description></item>
    /// </list>
    /// <para>
    /// Neither check records, reorders or normalizes a divergence: each either agrees or refuses.
    /// </para>
    /// <para>
    /// <b>Where this is asked.</b> At the resolver line in <c>CerasSerializer.GetSpecificFormatter</c>,
    /// before the fallback is constructed and therefore before it is cached, and before the value has a
    /// byte: a refused save returns nothing and a refused load has read none of that value. Both paths
    /// resolve formatters through the same line, so both are covered. A raw/transient context is untouched:
    /// it describes nothing, so there is nothing to record and nothing to refuse.
    /// </para>
    /// <para>
    /// <b>And where it is not asked: static members.</b> A type's <em>static</em> members are a second
    /// schema, reached through <c>PrepareStaticSerializationDispatchers</c> and
    /// <c>CerasSerializer.SerializeStatic</c>, which the save/load format does not use — no manifest entry
    /// describes a static member list, and no robust save carries one. Nothing here covers that door, and
    /// nothing here should until something writes a static member list into a file.
    /// </para>
    /// </summary>
    internal static class RobustReflectionFallbackPolicy
    {
        /// <summary>
        /// Refuses the reflection fallback's member list for <paramref name="type"/> unless it is the list
        /// <paramref name="config"/>'s manifest describes, and unless no member of it is written with a
        /// marker the manifest cannot account for. Returns normally when both hold.
        /// <para>
        /// <paramref name="runtime"/> is the member list the fallback is about to write — the serializer's
        /// own <see cref="Schema"/> for the type, which is the same list the fallback builds its member
        /// array from, so this asks about the selection rather than about a second derivation of it.
        /// </para>
        /// </summary>
        public static void RequireSelectionIsDescribable(CerasRobustConfig config, Type type, Schema? runtime)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (type == null) throw new ArgumentNullException(nameof(type));

            // Nothing describes the payload, so there is no claim for a member list to contradict. This is
            // not a hole: a type the manifest does not hold cannot be described in a save at all, and the
            // layers that own that answer (the bake, the registry's own refusals) still refuse it. A type
            // outside the manifest is left to them, deliberately, so this guard does not replace a
            // long-standing message about an undeclared type with a new one about a member list.
            if (!config.SchemaRegistry.TryGetInfo(type, out var described))
                return;

            // No member list on the runtime side either: the fallback refuses this type itself, by name,
            // for exactly the four kinds of type that have no reflected member list (a primitive, an
            // abstract type, an interface, an open generic), so there is nothing here to disagree with.
            if (runtime == null)
                return;

            var describedFields = described.Fields ?? Array.Empty<SchemaFieldInfo>();
            var written = runtime.Members;

            // Compared position by position first, so the refusal can name the first member the two lists
            // disagree about rather than only the counts; a count difference is reported afterwards, at the
            // position where one list ran out, which is the first position they disagree about too.
            int shared = Math.Min(describedFields.Length, written.Count);
            for (int i = 0; i < shared; i++)
            {
                var field = describedFields[i];
                var member = written[i];

                if (!string.Equals(field.StableName, member.PersistentName, StringComparison.Ordinal))
                    throw Refuse(type, described, describedFields, written, i,
                        $"the manifest describes the member '{field.StableName}' there, the payload has '{member.PersistentName}'");

                // The member's type, compared as an *identity* and not as a string. The schema section
                // records a field's type as a resolved type **id**, so the name in the descriptor never
                // reaches the wire: the generator's spelling (TypeHelpers.GetRobustName, the exact match
                // first) and a hand-written descriptor's `Type.FullName` describe the same member, and
                // refusing the second would refuse a correct file on a spelling. The fallback resolves
                // through the registry's own name resolver — the same door the bake resolves a member's
                // name through — so the two cannot disagree about what a name means.
                var actualTypeName = TypeIdentity.GetCanonicalName(member.MemberType);
                bool sameType = string.Equals(field.TypeName, actualTypeName, StringComparison.Ordinal)
                             || (config.SchemaRegistry.TryResolveType(field.TypeName, out var describedType) && describedType == member.MemberType);

                if (!sameType)
                    throw Refuse(type, described, describedFields, written, i,
                        $"both sides name the member '{member.PersistentName}', but as '{field.TypeName}' and as '{actualTypeName}'");
            }

            if (describedFields.Length != written.Count)
            {
                bool manifestRanOut = describedFields.Length < written.Count;
                string extra = manifestRanOut
                    ? $"the manifest describes {describedFields.Length} member(s) and the payload has {written.Count}, so member #" +
                      $"{describedFields.Length} ('{written[describedFields.Length].PersistentName}') has no entry in the schema section"
                    : $"the manifest describes {describedFields.Length} member(s) and the payload has {written.Count}, so member #" +
                      $"{written.Count} ('{describedFields[written.Count].StableName}') is described but never written";

                throw Refuse(type, described, describedFields, written, Math.Min(describedFields.Length, written.Count), extra);
            }

            // The member list agrees. That is not the same as the payload agreeing, and the second half of
            // the check is separate for exactly that reason — see the marker check below, and the note on
            // this class that the count/name/type comparison cannot see a marker.
            RequireNoUndescribedMemberMarker(type, described, describedFields, written);
        }

        /// <summary>
        /// Refuses a member list that is right about every member and still writes different bytes, because
        /// one member's <em>reference marker</em> is not part of any member list the manifest holds.
        /// <para>
        /// The shape, measured. A member <b>declared</b> as an array type is written through a different
        /// formatter class by each writer:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// the generated writer binds <c>ArrayFormatter&lt;T&gt;</c> directly (<c>RobustCodeEmitter</c>:
        /// "if (type is IArrayTypeSymbol arr) return ArrayFormatter&lt;TItem&gt;"), which writes the length
        /// and the elements and no marker;
        /// </description></item>
        /// <item><description>
        /// the reflection fallback takes the specific formatter for a member only when the member is a
        /// <em>value</em> type (<c>ReflectionFallbackFormatter</c>: <c>m.IsMonomorphic &amp;&amp;
        /// m.MemberType.IsValueType</c>), and an array is a sealed reference type, so the member goes
        /// through <c>ReferenceFormatter_KnownSealedType&lt;T[]&gt;</c> — one extra marker byte in front of
        /// exactly the same array payload.
        /// </description></item>
        /// </list>
        /// <para>
        /// Nothing in the file records that byte. The manifest entry carries the member's name, its type
        /// and a flag about its <em>elements</em> (<see cref="SchemaFieldInfo.IsMonomorphic"/> — whether
        /// a per-element type id is written, which is a different question and is not this one), and the
        /// schema section writes the same three things. Two builds that disagree about a member's marker
        /// therefore carry the same layout fingerprint, and the reader takes the matching-layout fast path
        /// and reads a payload one byte out from the first array member on.
        /// </para>
        /// <para>
        /// <b>The boundary is measured, not derived, and it is a member's own type.</b> Every shape below
        /// was written by both writers and compared byte for byte on a raw context, where the guard does not
        /// apply:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <b>differ</b> — a member declared as <c>T[]</c>, <c>T?[]</c>, <c>T[][]</c>, <c>List&lt;T&gt;[]</c> or
        /// <c>S[]</c>: in every case exactly one extra marker byte;
        /// </description></item>
        /// <item><description>
        /// <b>agree</b> — a member declared as a primitive, <c>string</c>, a struct, a sealed class, an open
        /// class, <c>List&lt;T&gt;</c> or <c>Dictionary&lt;K,V&gt;</c>, and also <c>List&lt;T[]&gt;</c> and
        /// <c>Dictionary&lt;K,T[]&gt;</c>, because the array there is an <em>element</em> of a collection
        /// rather than a member: both writers reach it through the same runtime
        /// <see cref="Formatters.CollectionFormatter{TCollection, TItem}"/>, which asks
        /// <c>GetReferenceFormatterRequired(TItem)</c> on both sides. A nested array is therefore not a
        /// reason to refuse, and this check does not refuse one.
        /// </description></item>
        /// </list>
        /// <para>
        /// The rule is deliberately the conservative one — <b>refuse an array-typed member outright</b>,
        /// rather than try to prove for every CLR shape that the two routes agree. A shape not on the list
        /// is accepted on the strength of that measurement, not on a proof, and that is stated here so it
        /// cannot be read as more than it is.
        /// </para>
        /// </summary>
        static void RequireNoUndescribedMemberMarker(Type type, SchemaTypeInfo described, SchemaFieldInfo[] describedFields, List<SchemaMember> written)
        {
            for (int i = 0; i < written.Count; i++)
            {
                if (written[i].MemberType.IsArray)
                    throw RefuseMarker(type, described, describedFields, written, i);
            }
        }

        /// <summary>
        /// The refusal, and everything a caller needs to act on it: the type, the two lists side by side, the
        /// first position they disagree about, the door it came through, and the routes that do work.
        /// </summary>
        static CerasSchemaException Refuse(
            Type type, SchemaTypeInfo described, SchemaFieldInfo[] describedFields, List<SchemaMember> written, int at, string difference)
        {
            string typeName = TypeIdentity.GetCanonicalName(type);

            var sb = new StringBuilder();
            sb.Append("Cannot write '").Append(typeName).AppendLine("' through the reflection fallback in a robust context: the member list that formatter writes is not the one this context's manifest describes.");
            sb.AppendLine();
            sb.Append("  the manifest entry for '").Append(typeName).Append("' (").Append(described.Kind).Append(") describes ")
              .Append(describedFields.Length).AppendLine(describedFields.Length == 1 ? " member:" : " members:");
            AppendDescribed(sb, describedFields);
            sb.Append("  the reflection fallback would write ").Append(written.Count).Append(written.Count == 1 ? " member, in this order:" : " members, in this order:").AppendLine();
            AppendWritten(sb, written);
            sb.AppendLine();
            sb.Append("  first difference at member #").Append(at).Append(": ").Append(difference).AppendLine(".");
            sb.AppendLine();

            sb.AppendLine("A robust save has to describe every value it writes, and a value written member by member is described by the member");
            sb.AppendLine("list in the file's schema section: how many members, their stable names, their types, and their order. That list is hashed");
            sb.AppendLine("into the layout fingerprint, which is what tells a reader whether it may take the matching-layout fast path or has to run the");
            sb.AppendLine("migration interpreter. This formatter derives its member list from the CLR type while the operation runs; the manifest's list");
            sb.AppendLine("was derived at compile time by the source generator. Nothing records the runtime one, so two builds whose two lists differ");
            sb.AppendLine("produce files that claim the same layout: the reader takes the fast path, reads this payload member by member in *its* order, and");
            sb.AppendLine("returns a wrong value with nothing reported. The member list is the whole of the claim, so it cannot be written past.");
            sb.AppendLine();
            sb.AppendLine("Use one of these instead:");
            sb.AppendLine($"  - declare the type so the generator emits a formatter and the matching schema for this context: [CerasSerializable] on '{typeName}'");
            sb.AppendLine("    together with a [CerasContext] of the context this serializer registers (the name you passed to UseGeneratedFormatters), and remove");
            sb.AppendLine("    whatever kept the generated formatter away from it - this selection was reached because no generated formatter was installed for");
            sb.AppendLine("    the type at all.");
            sb.AppendLine($"  - install a formatter of your own through TypeConfig.CustomFormatter (config.ConfigType<{typeName}>().CustomFormatter = ...). That");
            sb.AppendLine("    door is supported: the entry becomes an opaque block, the payload gets a declared length in front of it, and the formatter's");
            sb.AppendLine("    identity is hashed into the fingerprint, so a build with a different formatter is refused by name instead of decoding.");
            sb.AppendLine("  - make the two lists agree: the same members, the same types and the same order on both sides. A member only reflection finds (a private");
            sb.AppendLine("    field) and a member only the generator sees (one dropped by a runtime [Exclude]) both have to go, or the manifest has to name them.");
            sb.AppendLine("  - if the type is only ever serialized transiently, use a raw context (new CerasFastSerializer(...)), where the reflection fallback's bytes");
            sb.AppendLine("    are exactly what they always were: a raw context describes nothing and accounts for nothing.");
            sb.AppendLine();
            sb.AppendLine("What is compared here is exactly the member list: the count, the stable names in order, and each member's type as an");
            sb.AppendLine("identity. A member's own reference marker is a separate question, asked separately (RequireNoUndescribedMemberMarker),");
            sb.AppendLine("because a marker is in no member list.");

            return new CerasSchemaException(sb.ToString());
        }

        /// <summary>
        /// The refusal for a member list that is right about every member and still writes different bytes:
        /// it names the member, the two formatter classes the two writers bind for it, the measured byte
        /// difference, why nothing in the file can tell the two layouts apart, and what to do instead.
        /// </summary>
        static CerasSchemaException RefuseMarker(
            Type type, SchemaTypeInfo described, SchemaFieldInfo[] describedFields, List<SchemaMember> written, int at)
        {
            string typeName = TypeIdentity.GetCanonicalName(type);
            var member = written[at];
            var memberTypeName = TypeIdentity.GetCanonicalName(member.MemberType);
            var field = at < describedFields.Length ? describedFields[at] : null;

            var sb = new StringBuilder();
            sb.Append("Cannot write '").Append(typeName).AppendLine("' through the reflection fallback in a robust context: its member list is the one the manifest describes, but member #")
              .Append(at).Append(" ('").Append(member.PersistentName).Append("') is an array, and the two writers put different bytes in front of it.");
            sb.AppendLine();
            sb.Append("  the manifest describes member #").Append(at).Append(" as: ").Append(field?.StableName ?? member.PersistentName)
              .Append(" : ").AppendLine(field?.TypeName ?? memberTypeName);
            sb.Append("  the generated writer binds ").Append("ArrayFormatter<").Append(ElementTypeNameOf(member.MemberType)).AppendLine("> for it, which writes the length and the elements and no marker;");
            sb.Append("  the reflection fallback binds ReferenceFormatter_KnownSealedType<").Append(memberTypeName)
              .AppendLine("> for it, which writes a reference marker in front of exactly those bytes");
            sb.AppendLine();
            sb.AppendLine("  a value of the same shape written by each, as hex:");
            sb.AppendLine("    generated  020700000003090000000A000000     (Sentinel = 7, Values = { 9, 10 })");
            sb.AppendLine("    reflected  02070000000203090000000A000000     <- the extra 02 is the marker");
            sb.AppendLine();
            sb.AppendLine("Nothing in the file can tell those two layouts apart. A manifest entry records each member's name, its type, and");
            sb.AppendLine("whether a per-element type id is written for it (SchemaFieldInfo.IsMonomorphic, which is a different question from this one and");
            sb.AppendLine("is not the member's own marker). The schema section writes those same three things and the layout fingerprint hashes them, so a");
            sb.AppendLine("build that writes the member with a marker and a build that does not produce files claiming the same layout, and a reader takes");
            sb.AppendLine("the matching-layout fast path and reads the payload one byte out from the first array member on.");
            sb.AppendLine();
            sb.AppendLine("This is refused here rather than left to be found, and it is refused for any array-typed member, not only for the shapes that");
            sb.AppendLine("have been compared: the two writers bind different formatter classes for one, and proving they agree for every other shape of");
            sb.AppendLine("CLR type is not something this check can do. An array that is an *element* of a collection is a different case and is fine - both");
            sb.AppendLine("writers reach it through the same runtime collection formatter - so nothing has to change for a List<int[]> or a");
            sb.AppendLine("Dictionary<string, int[]> member.");
            sb.AppendLine();
            sb.AppendLine("Use one of these instead:");
            sb.AppendLine($"  - declare the type so the generator emits a formatter and the matching schema for this context: [CerasSerializable] on '{typeName}'");
            sb.AppendLine("    together with a [CerasContext] of the context this serializer registers. The generated formatter is the one that binds");
            sb.AppendLine("    ArrayFormatter, so a declared type never reaches this door - and if the type already has a generated formatter, the reason this");
            sb.AppendLine("    door was reached at all is that something took it away (TypeConfig.CustomFormatter = null, or a config that clears it).");
            sb.AppendLine($"  - install a formatter of your own through TypeConfig.CustomFormatter (config.ConfigType<{typeName}>().CustomFormatter = ...),");
            sb.AppendLine("    which is supported: the entry becomes an opaque block, the payload gets a declared length, and the formatter's identity is");
            sb.AppendLine("    hashed into the fingerprint, so the bytes behind the frame no longer have to match anybody's member list.");
            sb.AppendLine($"  - keep the array out of this context's member list: [Exclude] on the member, or");
            sb.AppendLine($"    config.ConfigType<{typeName}>().ConfigField(\"{member.PersistentName}\").Exclude(). An excluded member is in neither list, so there is");
            sb.AppendLine("    nothing left to disagree about.");
            sb.AppendLine("  - if the type is only ever serialized transiently, use a raw context (new CerasFastSerializer(...)), where the reflection fallback's");
            sb.AppendLine("    bytes are exactly what they always were: a raw context describes nothing, and a member list it does not describe is not a claim");
            sb.AppendLine("    anything can check.");

            return new CerasSchemaException(sb.ToString());
        }

        /// <summary>
        /// The element type of an array, for the formatter name in the refusal. Dimensionality is dropped
        /// on purpose: the name is there to identify the class the generated writer binds, and
        /// <c>ArrayFormatter&lt;T&gt;</c> is what it binds for a jagged member too.
        /// </summary>
        static string ElementTypeNameOf(Type arrayType)
            => TypeIdentity.GetCanonicalName(arrayType.GetElementType()!);

        static void AppendDescribed(StringBuilder sb, SchemaFieldInfo[] fields)
        {
            for (int i = 0; i < fields.Length; i++)
                sb.Append("    #").Append(i).Append(" ").Append(fields[i].StableName).Append(" : ").AppendLine(fields[i].TypeName);

            if (fields.Length == 0)
                sb.AppendLine("    (none - this is the member-less entry the generator registers for a type it emits no formatter for)");
        }

        static void AppendWritten(StringBuilder sb, List<SchemaMember> members)
        {
            for (int i = 0; i < members.Count; i++)
                sb.Append("    #").Append(i).Append(" ").Append(members[i].PersistentName).Append(" : ")
                  .AppendLine(TypeIdentity.GetCanonicalName(members[i].MemberType));
        }
    }
}
