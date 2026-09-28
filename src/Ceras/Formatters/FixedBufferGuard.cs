using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ceras.Exceptions;

namespace Ceras.Formatters
{
    /// <summary>
    /// The one place that answers "may a type declare a C# <c>fixed</c> buffer, and if not, what do I
    /// say about it?".
    /// <para>
    /// A fixed buffer is not a field the reflection API exposes as an array. The compiler replaces the
    /// declaration with a generated one-element struct — <c>&lt;Buffer&gt;e__FixedBuffer</c> — and
    /// records the declared length in a <see cref="FixedBufferAttribute"/> <em>on the field</em>, so
    /// the length is nowhere in the type of the member. That is the whole problem: a member-by-member
    /// encoder resolves a member's <em>type</em>, and that type has exactly one element in it. So
    /// neither member-by-member path can encode the buffer:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// the reflection fallback resolves the member's type, finds the single element it really has, and
    /// writes it — one byte for a 25-byte <c>fixed byte[25]</c>;
    /// </description></item>
    /// <item><description>
    /// the source generator resolves the C# declaration, sees the <c>byte*</c> the surface really has,
    /// and refuses the type at compile time (<c>CERAS035</c> — a fixed buffer has its own diagnostic
    /// rather than the framework-type one, because the framework-type reason is not its reason).
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>What that cost before this guard existed.</b> <c>unsafe struct Fb { public fixed byte
    /// Buffer[25]; public int Value; }</c> is 32 bytes of memory (25 buffer bytes + 3 of padding + the
    /// 4-byte <c>int</c>), and the reflection fallback wrote 5 of them: one byte for the buffer, four
    /// for the <c>int</c>. Nothing threw, the stream stayed aligned, the save succeeded, and
    /// <c>Buffer[1..24]</c> loaded back as zeros. The raw (transient) path's
    /// <c>ReinterpretFormatter</c> wrote all 32 and was correct — which is what kept the bug
    /// invisible: the two encodings of one value differed by the declared length of one member, and
    /// the only thing that said which of them ran was the config line
    /// <c>UseReinterpretFormatter</c>.
    /// </para>
    /// <para>
    /// So the shape is refused here, and the blit
    /// (<see cref="Helpers.ReflectionHelper.IsBlittableType"/>) is refused with it. The blit was
    /// lossless, so giving it up is a real loss of capability, and it is given up deliberately. The
    /// exemption it used to carry claimed there was "no second encoding to disagree with", but there
    /// <em>was</em> one — the reflection fallback's, measured at 5 bytes against the image's 32 — so
    /// the exemption was not protecting against a hypothetical disagreement, it was covering up a
    /// present one. And the blit's safety depended on the generator refusing the type: the moment
    /// <c>CERAS035</c> stops reporting the shape as a framework type, the same build would blit one shape
    /// and member-write another, which is precisely the failure the blit-selection rule exists to prevent.
    /// See <c>Format/LAYOUT.md</c>, "Which types the raw path may reinterpret".
    /// </para>
    /// <para>
    /// A member the payload does not contain is not this guard's business: <c>[Exclude]</c> and
    /// <c>[NonSerialized]</c> are the documented ways to keep a member out of a type's payload, and
    /// they work here exactly as they do everywhere else. The check is therefore made against the
    /// members Ceras actually resolved for the type, not against every field the CLR has.
    /// </para>
    /// </summary>
    static class FixedBufferGuard
    {
        /// <summary>
        /// The actionable half of the message, identical at every site. A <c>byte[]</c> member is
        /// already supported everywhere (a biased count, then the elements), and a custom formatter for
        /// the declaring type is the other door out: Ceras hands the type to it whole and never looks
        /// at the buffer again.
        /// </summary>
        internal const string Advice =
            "Store the buffer as a byte[] member instead, mark the member [Exclude] if it is intentionally " +
            "not serialized, or register a custom IFormatter<{0}> for the type and write the buffer in it.";

        /// <summary>
        /// The reason, in the one sentence the source generator's <c>CERAS035</c> states too
        /// (<c>Ceras.SourceGenerator.Diag.FixedBufferCause</c>). The two cannot share a constant: the
        /// generator is a separate compilation that references no Ceras assembly. So the sentence is written
        /// once here and once there, and
        /// <c>FixedBufferDiagnosticTests.The_Two_Refusals_Say_The_Same_Thing</c> pins both rendered messages
        /// against this copy. It deliberately names nothing runtime-specific — a compile error has no
        /// reflection in it, and a reason phrased around what reflection sees would be false in one of the
        /// two places.
        /// </summary>
        internal const string Cause =
            "A fixed buffer is not an array: the compiler replaces it with a generated one-element struct and " +
            "keeps the declared length in an attribute on the field, so the length is nowhere in the member's " +
            "type and no member-by-member encoding can see it.";

        /// <summary>
        /// The consequence, stated as a measurement rather than as a principle, because that is what
        /// makes it credible: the reflection fallback writes <em>one</em> element for a buffer that
        /// declares <paramref name="declared"/> of them, the save still succeeds, and the rest comes
        /// back as zeros. This is the failure the whole guard exists to prevent, so the refusal
        /// carries the numbers that make it real rather than a statement about support.
        /// </summary>
        static string LossDescription(FieldInfo field, FixedBufferAttribute declared)
        {
            int bytes = DeclaredByteLength(declared);
            int written = Helpers.ReflectionHelper.UnsafeGetSize(declared.ElementType);

            return $"the reflection fallback sees '{field.Name}' as its generated one-element struct and writes " +
                   $"{written} of the {bytes} bytes the member declares, so the save succeeds, the stream still " +
                   $"lines up, and the other {bytes - written} bytes load back as zeros";
        }

        /// <summary>The declared size of the buffer in bytes: its element count times its element size.</summary>
        static int DeclaredByteLength(FixedBufferAttribute buffer)
            => buffer.Length * Helpers.ReflectionHelper.UnsafeGetSize(buffer.ElementType);

        /// <summary>
        /// Is <paramref name="field"/> a C# <c>fixed</c> buffer? The attribute is the compiler's own
        /// record of the declaration and is the only place the declared length exists, so this is the
        /// test — the field's type alone (a generated struct) says nothing.
        /// </summary>
        internal static bool IsFixedBuffer(FieldInfo field)
            => field.GetCustomAttribute<FixedBufferAttribute>() != null;

        /// <summary>
        /// The typed refusal. A <see cref="CerasSchemaException"/>, never a bare
        /// <see cref="CerasException"/>, so a caller can catch "this shape is not describable" without
        /// matching message text — the same contract <c>Format.MultidimensionalArrayGuard</c> and the
        /// bake's own refusals already have.
        /// <para>
        /// Every buffer the type declares is named, not just the first one found, because the fixes are
        /// per member and a message that named one of them would only move the failure to the next save.
        /// </para>
        /// </summary>
        /// <param name="type">The type that declares the buffers, named canonically.</param>
        /// <param name="buffers">The offending fields, with the compiler's own record of each
        /// declaration — the only place a declared length exists.</param>
        /// <param name="isStatic">Which member list was being resolved.</param>
        internal static CerasSchemaException Reject(Type type, List<(FieldInfo field, FixedBufferAttribute declared)> buffers, bool isStatic)
        {
            var scoped = Format.TypeIdentity.GetCanonicalNameOrUnknown(type);

            // 'Sixth' (fixed System.Byte[24], 24 bytes), 'Third' (fixed System.Byte[3], 3 bytes)
            var named = string.Join(", ", buffers.Select(b =>
                $"'{b.field.Name}' (fixed {Format.TypeIdentity.GetCanonicalNameOrUnknown(b.declared.ElementType)}" +
                $"[{b.declared.Length}], {DeclaredByteLength(b.declared)} bytes)"));

            // The smallest one, because that is the least a save would have written for the member and
            // so the one whose loss a user is least likely to notice. Stated for one member, because with
            // several the numbers only multiply and the single figure is the one that carries the point.
            var worst = buffers.OrderBy(b => DeclaredByteLength(b.declared)).First();

            return new CerasSchemaException(
                $"{(buffers.Count == 1 ? "Member" : "Members")} {named} of {(isStatic ? "static " : "")}type " +
                $"'{scoped}' {(buffers.Count == 1 ? "is a C# fixed buffer" : "are C# fixed buffers")}, and Ceras " +
                $"cannot write {(buffers.Count == 1 ? "it" : "them")} as {(buffers.Count == 1 ? "a member" : "members")}. " +
                $"{Cause} Writing " +
                $"{(buffers.Count == 1 ? "it" : "them")} anyway would lose data silently, because " +
                $"{LossDescription(worst.field, worst.declared)}. Ceras refuses the type on every path instead, " +
                $"the raw path's memory-image blit included, so that the one shape Ceras cannot describe is not " +
                $"the one that happens to round-trip. {string.Format(Advice, scoped)}");
        }
    }
}
