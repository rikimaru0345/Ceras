using System;
using Ceras.Exceptions;

namespace Ceras.Format
{
    /// <summary>
    /// The one place that answers "may this array be used here, and if not, what do I say about it?".
    /// <para>
    /// A multidimensional array (rank &gt; 1) is not a shape the robust format can describe anywhere: the
    /// structural schema (<see cref="SchemaRegistry.TryBuildStructuralSchema"/>), the generated member
    /// (<c>CERAS027</c>), the interpreter (<c>SchemaTraversalEngine</c>) and this guard all refuse it, and
    /// they all report the same reason, because the alternatives a user can act on are the same in every
    /// position. Only the *where* differs, and each site supplies the part only it knows.
    /// </para>
    /// <para>
    /// <b>The one position the reason is genuinely different in is a polymorphic slot.</b> Everywhere else
    /// the array is described in the schema section, so the schema is what decides. A value reached
    /// through an <c>object</c>/interface/base-class slot (or a <see cref="Type"/> value) is described
    /// only by the type value written behind its reference marker, and that record has no rank in it —
    /// see <see cref="RobustTypeFormatter"/>. The consequence is worse than a refused shape: the save is
    /// accepted, and the array is read back as a one-dimensional array over the same flattened elements.
    /// <see cref="TypeValueReason"/> is that argument in one reusable sentence.
    /// </para>
    /// <para>
    /// Rejecting the write is the whole fix for now. Carrying the rank in the type value (and teaching
    /// the structural schema, the generated code and the interpreter about it) is Milestone 1 work and
    /// needs a format change; until then, failing loudly beats a save that lies.
    /// </para>
    /// </summary>
    static class MultidimensionalArrayGuard
    {
        /// <summary>
        /// The actionable half of the message, identical at every site: a jagged array is already
        /// supported, and flattening keeps the data with explicit dimension fields beside it.
        /// </summary>
        internal const string Advice =
            "Use a jagged array (T[][]) or flatten it into a one-dimensional array plus explicit dimension fields.";

        /// <summary>
        /// The reason a rank &gt; 1 array cannot travel through a slot that records only its element type.
        /// <see cref="RobustTypeFormatter"/> writes an array type as (Array, element type) and nothing
        /// else, and its reader rebuilds the type with <c>MakeArrayType()</c>, which is unconditionally
        /// rank 1 — so the shape is not merely lost, it is replaced by a plausible wrong one. The
        /// element count survives, so the byte count can even match while every index is wrong.
        /// </summary>
        internal const string TypeValueReason =
            "a robust save records the concrete type of such a value as a type id, and for an array that " +
            "record carries only the element type (the reader rebuilds it with MakeArrayType(), which is " +
            "always rank 1), so a rank > 1 array would come back as a one-dimensional array over the same " +
            "flattened elements, i.e. the wrong shape, with no error anywhere in the save or the load";

        /// <summary>True for an array whose rank the robust format cannot describe (rank &gt; 1).</summary>
        internal static bool IsMultidimensional(Type? type)
            => type != null && type.IsArray && type.GetArrayRank() > 1;

        /// <summary>
        /// The typed refusal. Always a <see cref="CerasSchemaException"/> and never a bare
        /// <see cref="CerasException"/>, so a caller can catch "this shape is not describable" without
        /// matching message text — the same contract the bake's own refusals already have.
        /// </summary>
        /// <param name="type">The offending array type. Named canonically, with its rank.</param>
        /// <param name="where">The position, phrased as the subject of "cannot be used with".</param>
        /// <param name="because">
        /// Why <em>this</em> position cannot hold it. Omitted for the sites whose reason is simply that
        /// the format describes one-dimensional arrays only.
        /// </param>
        internal static CerasSchemaException Reject(Type type, string where, string? because = null)
            => new CerasSchemaException(
                $"The multidimensional array type '{TypeIdentity.GetCanonicalName(type)}' (rank {type.GetArrayRank()}) " +
                $"cannot be used with {where}{(because == null ? "" : ": " + because)}. " +
                $"{Advice}");
    }
}
