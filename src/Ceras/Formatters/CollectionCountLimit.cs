using System;
using Ceras.Exceptions;
using Ceras.Helpers;

namespace Ceras.Formatters
{
    /// <summary>
    /// The check three collection formatters apply to a decoded element count, so the configured bound
    /// cannot differ between them. The three are <c>QueueFormatter</c>, <c>StackFormatter</c> and
    /// <c>ImmutableArrayFormatter</c> - the shapes whose formatter reads a count and then allocates from it
    /// without a bound of its own. The formatters that already had a bound keep their own.
    /// <para>
    /// Two refusals, kept apart because they are two different defects:
    /// </para>
    /// <list type="bullet">
    /// <item><b>A negative count is malformed whatever the configured limit is.</b> It must not reach the
    /// allocation: <c>new Queue&lt;T&gt;(count)</c>, <c>new T[count]</c> and
    /// <c>ImmutableArray.CreateBuilder&lt;T&gt;(count)</c> each answer it differently -
    /// <c>ArgumentOutOfRangeException</c>, <c>OverflowException</c> - so an unbounded reader reported a
    /// corrupt save as an internal error from a collection constructor. The migration interpreter has always
    /// refused both (<c>PayloadGrammar.ReadCollectionCount</c>), so this is what makes the two readers agree
    /// on the same bytes.</item>
    /// <item><b>A count over <see cref="SizeLimits.MaxCollectionSize"/> is the configured bound doing its
    /// job.</b> That limit defaults to <see cref="uint.MaxValue"/>, so a serializer with no limit configured
    /// cannot fail this comparison and an ordinary save is unaffected.</item>
    /// </list>
    /// <para>
    /// <b>It does not compare the count against the bytes that remain.</b> An element is not a fixed number
    /// of bytes - a struct with no serialized members is zero bytes - so there is no size for <c>count</c> to
    /// be compared against <c>reader.Remaining</c>. A count that promises more elements than the payload
    /// holds is caught where the elements are read (<c>SerializerBinary</c> reports a truncated payload),
    /// which is where the evidence for it is.
    /// </para>
    /// <para>
    /// The call belongs immediately after the count is decoded and before anything is allocated, so a
    /// refused count neither reserves the memory it asked for nor leaves a half-built collection behind.
    /// </para>
    /// <para>
    /// The null marker is <b>not</b> handled here. A biased count's <c>-1</c> is a legal value, and each
    /// caller reads it before calling this, because only the caller knows which encoding its shape uses. A
    /// negative count that reaches <see cref="Require"/> is therefore always malformed, never a null.
    /// </para>
    /// </summary>
    static class CollectionCountLimit
    {
        /// <summary>
        /// The configured maximum, or <see cref="uint.MaxValue"/> when no <see cref="SizeLimits"/> reached
        /// the formatter - which is what a directly-constructed one gets, and the same answer as an unlimited
        /// configuration.
        /// </summary>
        internal static uint MaxCount(SizeLimits? sizeLimits) => sizeLimits?.MaxCollectionSize ?? uint.MaxValue;

        /// <summary>
        /// Refuses <paramref name="count"/> if it is negative or above <paramref name="maxCount"/>, and
        /// returns normally otherwise. <paramref name="countHasNoNullMarker"/> only says what the message
        /// needs to add for a shape whose count is a plain fixed-width element count.
        /// <para>
        /// The accepted path is two comparisons and nothing else - no name, no reflection, no string. The
        /// type name is resolved inside each failure branch, so a valid count costs nothing; see
        /// <c>CollectionLimitEnforcementTests.The_Guard_Adds_No_Per_Call_Allocation_To_A_Valid_Count</c>.
        /// </para>
        /// </summary>
        internal static void Require(int count, uint maxCount, Type collectionType, bool countHasNoNullMarker)
        {
            if (count < 0)
                throw new CerasDataCorruptionException(
                    $"Corrupt save: {collectionType.FriendlyName()} declares {count} elements, which is not a valid count. "
                    + (countHasNoNullMarker
                        // Worth stating for a shape with no null marker: the reader is not mistaking one for
                        // a null, because such a collection cannot be null.
                        ? "Its count is a plain fixed-width element count with no null marker, so the negative value is corrupt data. "
                        : "The only negative count this encoding defines is the null-collection marker, which is read before this check, so the negative value is corrupt data. ")
                    + "Nothing was allocated from it.");

            if ((uint)count > maxCount)
                throw new CerasDataCorruptionException(
                    $"Corrupt save: {collectionType.FriendlyName()} declares {count} elements, outside the configured maximum of {maxCount}. "
                    + "Raise CerasConfig.SizeLimits.MaxCollectionSize if this save is legitimate.");
        }
    }
}
