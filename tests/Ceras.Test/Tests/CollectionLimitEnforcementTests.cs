// The configured element-count bound, on every reader.
//
// `SizeLimits.MaxCollectionSize` is the limit on how many elements a collection may declare, and three
// collection formatters never asked for it: `QueueFormatter`, `StackFormatter` and `ImmutableArrayFormatter`.
// Everything around them did - `ArrayFormatter` by `MaxArraySize`, `CollectionFormatter`
// (List/HashSet/Dictionary) and `CollectionByProxyFormatter` (the other seven immutable shapes) by
// `MaxCollectionSize` - so the one number a caller set was silently inert for those three shapes, and what
// did stop them was the runtime: `new Queue<T>(count)`, `new T[count]` and
// `ImmutableArray.CreateBuilder<T>(count)` each answer a corrupt count with an
// `ArgumentOutOfRangeException`, an `OverflowException` or an `OutOfMemoryException`. A corrupt save
// therefore arrived as an internal error from a collection constructor, and the one reader that already
// refused the same bytes - the migration interpreter, through `PayloadGrammar.ReadCollectionCount` -
// disagreed with the generated reader about the *same file*.
//
// What is pinned here, one shape and one reader at a time:
//   * the check runs on the count alone, before the allocation the count sizes (so an int.MaxValue count
//     is a corruption report and not an OutOfMemoryException) and before the caller's out value is
//     touched (a refused load leaves the object it was handed alone);
//   * both robust readers refuse the same file for the same reason - the disagreement being removed;
//   * a count within the cap still loads, on every reader, the migration interpreter included;
//   * the legal negatives survive: a Queue/Stack has no null marker, so *every* negative is corrupt
//     there, while an ImmutableArray's -1 is a legal "IsDefault" and is still read as one;
//   * a truncated payload is still reported as a truncated payload, and
//   * an element that costs zero bytes is still legal at a count the payload cannot be sized against -
//     which is why there is no `count > reader.Remaining` rule in this, and this row is what would break
//     if one were added.
//
// Counts are deliberately small: the largest that reaches an element is 300, and 300 of them are a
// zero-byte element type, so nothing here allocates more than a kilobyte. The rows whose *count* is absurd
// never allocate at all, because that is what they are checking.
//
// Two things this file deliberately does not pin, because they are different questions and not missing
// checks: `ImmutableQueueFormatter`/`ImmutableStackFormatter` write through `IFormatter<TItem[]>`, so they
// are bounded by `MaxArraySize` rather than `MaxCollectionSize`; and the array-vs-collection divergence in
// `PayloadGrammar.ReadCollectionCount` (which passes MaxCollectionSize for an array payload, whose writer
// bound is MaxArraySize) is already documented there. Unifying either is a policy change, not a bound.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.GameplayLoop;
using Ceras.Generated;
using Ceras.ImmutableCollections;

namespace Ceras.Test.CollectionLimits
{
    /// <summary>
    /// One collection per root and nothing else, so the element count sits at a known offset in the
    /// payload. A root holding two collections would need the first one's length to find the second's
    /// count, which is exactly the arithmetic a test should not depend on.
    /// </summary>
    [CerasContext("CollLimits")]
    [CerasSerializable] public class ClQueueHolder { public Queue<int> Q; public int Tail; }

    [CerasContext("CollLimits")]
    [CerasSerializable] public class ClStackHolder { public Stack<int> S; public int Tail; }

    [CerasContext("CollLimits")]
    [CerasSerializable] public class ClListHolder { public List<int> L; public int Tail; }

    /// <summary>
    /// Its own context, because an <see cref="ImmutableArray{T}"/> member is only answered correctly when
    /// the immutable resolvers are registered - without <c>UseImmutableFormatters</c> the built-in
    /// CollectionFormatter is selected for the member and cannot refill it. Which formatter answers is a
    /// pre-existing selection question, so it is stated here rather than changed.
    /// </summary>
    [CerasContext("CollLimitsImmutable")]
    [CerasSerializable] public class ClImmutableHolder { public ImmutableArray<int> A; public int Tail; }

    /// <summary>
    /// An element with nothing in it, so it costs zero bytes - the shape that makes
    /// <c>count &gt; reader.Remaining</c> unsound. Not a context root: it is reached through the
    /// collections below, which is what gives it a generated formatter without declaring one (an explicit
    /// <c>[CerasSerializable]</c> on a member-less type is what CERAS001 warns about).
    /// </summary>
    public struct ClZeroBytes { }

    [CerasContext("CollLimits")]
    [CerasSerializable] public class ClZeroBytesQueueHolder { public Queue<ClZeroBytes> Q; public int Tail; }

    public class CollectionLimitEnforcementTests : TestBase
    {
        const string Ctx = "CollLimits";
        const string ImmutableCtx = "CollLimitsImmutable";

        /// <summary>
        /// The witness every alignment check uses: a field after the collection, so a reader that consumed
        /// the wrong number of bytes is visibly wrong rather than quietly short.
        /// </summary>
        const int TailWitness = 0x0BADF00D;

        /// <summary>
        /// A cap above every count the round-trip rows use, so "within the cap" and "over the cap" can
        /// never describe the same file.
        /// </summary>
        const uint Cap = 256;

        /// <summary>
        /// Far enough over <see cref="Cap"/> that the old code allocated happily and then failed on the
        /// <em>elements</em> instead. This is the row that proves the refusal is the bound and not a
        /// truncated read - which is also why every refusal below asserts on the message.
        /// </summary>
        const int ModestlyOverCap = 1_000_000;

        /// <summary>
        /// The count no allocation survives. A legal encoding of an impossible request - four fixed bytes
        /// for a queue or a stack, the largest biased varint for the other two - and the row that shows
        /// the check precedes the allocation, because a check placed after it could not answer this at all.
        /// </summary>
        const int AbsurdCount = int.MaxValue;

        /// <summary>
        /// A count that needs more than one byte of biased varint, so it is the row that would notice a
        /// count read which stops short.
        /// </summary>
        const int WideCount = 200;

        /// <summary>The collection shapes whose count this change is about, plus the bounded one as a control.</summary>
        public enum Shape { Queue, Stack, List, ImmutableArray }

        /// <summary>Which reader a row is about.</summary>
        public enum Reader { Raw, Generating, Migrating }

        /// <summary>
        /// The shapes whose count this change bounds. <c>List</c> is absent because
        /// <c>CollectionFormatter</c> refused an over-cap count before this change and still does, with a
        /// different exception type and a different message; it has its own control row rather than being
        /// folded in here, so a reader of these rows is never told the four shapes fail alike.
        /// </summary>
        static readonly Shape[] InScope = { Shape.Queue, Shape.Stack, Shape.ImmutableArray };

        static readonly Shape[] All = { Shape.Queue, Shape.Stack, Shape.List, Shape.ImmutableArray };

        /// <summary>Every reader that can reach a given shape.</summary>
        /// <remarks>
        /// The migration interpreter is missing one combination, and the reason is unrelated to the count:
        /// it reads the count of an <c>ImmutableArray</c> (so an over-cap one is refused by the bound, which
        /// runs first) but cannot <em>construct</em> one - <c>CurrentObjectFactory.CreateCollection</c> finds
        /// no parameterless constructor, no <c>List&lt;T&gt;</c> constructor and no usable <c>Clear</c>, and
        /// refuses with a <c>CerasSchemaException</c> that says so. That is a pre-existing gap about
        /// building a value, so the combination is left out of the data instead of being asserted-and-skipped
        /// in the body.
        /// </remarks>
        static IEnumerable<Reader> ReadersFor(Shape shape)
            => shape == Shape.ImmutableArray
                ? new[] { Reader.Raw, Reader.Generating }
                : new[] { Reader.Raw, Reader.Generating, Reader.Migrating };

        // ── the data, as data ─────────────────────────────────────────────────────────────────

        /// <summary>A count at or under the cap, on every reader that can reach the shape.</summary>
        public static TheoryData<Shape, int, Reader> WithinCap => Build(0, 1, 3, WideCount);

        /// <summary>An over-cap count, on every reader that can reach the shape.</summary>
        public static TheoryData<Shape, int, Reader> OverCap => Build(ModestlyOverCap, AbsurdCount);

        /// <summary>
        /// A negative count, on every reader that can reach the shape. <c>-1</c> is missing for
        /// <c>ImmutableArray</c> because there it is the null marker and a legal value.
        /// </summary>
        public static TheoryData<Shape, int, Reader> Negative
        {
            get
            {
                var data = new TheoryData<Shape, int, Reader>();
                foreach (var shape in InScope)
                {
                    var negatives = shape == Shape.ImmutableArray ? new[] { -2, int.MinValue } : new[] { -1, -2, int.MinValue };
                    foreach (var count in negatives)
                        foreach (var reader in ReadersFor(shape))
                            data.Add(shape, count, reader);
                }
                return data;
            }
        }

        /// <summary>Every shape and every reader, with no count involved - the truncation rows.</summary>
        public static TheoryData<Shape, Reader> EveryReader
        {
            get
            {
                var data = new TheoryData<Shape, Reader>();
                foreach (var shape in All)
                    foreach (var reader in ReadersFor(shape))
                        data.Add(shape, reader);
                return data;
            }
        }

        /// <summary>
        /// The in-scope shapes and the readers that can reach them, for the rows that compare the two robust
        /// readers <em>to each other</em>. <c>List</c> is excluded because its generated reader's refusal is
        /// a wrapped <c>InvalidOperationException</c> rather than a Ceras refusal - it has its own control row,
        /// which is where that divergence is on the record.
        /// </summary>
        public static TheoryData<Shape, Reader> InScopeReaders
        {
            get
            {
                var data = new TheoryData<Shape, Reader>();
                foreach (var shape in InScope)
                    foreach (var reader in ReadersFor(shape))
                        data.Add(shape, reader);
                return data;
            }
        }

        static TheoryData<Shape, int, Reader> Build(params int[] counts)
        {
            var data = new TheoryData<Shape, int, Reader>();
            foreach (var shape in InScope)
                foreach (var count in counts)
                    foreach (var reader in ReadersFor(shape))
                        data.Add(shape, count, reader);
            return data;
        }

        // ── the readers ──────────────────────────────────────────────────────────────────────

        static CerasFastSerializer Raw(uint cap) => new CerasFastSerializer(c =>
        {
            c.UseImmutableFormatters();
            c.SizeLimits.MaxCollectionSize = cap;
        });

        static CerasRobustSerializer Robust(uint cap, string context = Ctx) => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(context);
            // Only ImmutableArray needs these; the other three shapes never did.
            c.UseImmutableFormatters();
            c.SizeLimits.MaxCollectionSize = cap;
        });

        static string ContextOf(Shape shape) => shape == Shape.ImmutableArray ? ImmutableCtx : Ctx;

        // ── the counts these shapes legitimately produce ─────────────────────────────────────

        static int[] Elements(int count) => Enumerable.Range(1, count).ToArray();

        /// <summary>A stack whose <em>enumeration</em> is <paramref name="e"/>. The formatter writes a
        /// stack top-first, so the values are pushed in reverse for the enumeration to come out in order.</summary>
        static Stack<int> StackEnumeratingAs(int[] e)
        {
            var s = new Stack<int>();
            for (int i = e.Length - 1; i >= 0; i--) s.Push(e[i]);
            return s;
        }

        /// <summary>The raw rows use each shape as the root value, so the payload starts with its count.</summary>
        static byte[] SaveRaw(Shape shape, int count)
        {
            var e = Elements(count);
            var raw = Raw(uint.MaxValue);

            // A switch expression here would force every arm to one type, and the type is not incidental:
            // it is the *static* type Serialize<T> is given, so a value boxed as object would be written
            // through a reference marker and a type tag instead of as the collection's own payload, and the
            // count would not be the first byte.
            return shape switch
            {
                Shape.Queue => raw.Serialize<Queue<int>>(new Queue<int>(e)),
                Shape.Stack => raw.Serialize<Stack<int>>(StackEnumeratingAs(e)),
                Shape.List => raw.Serialize<List<int>>(new List<int>(e)),
                Shape.ImmutableArray => raw.Serialize<ImmutableArray<int>>(ImmutableArray.Create(e)),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
        }

        /// <summary>The robust rows use a holder whose only member is the collection, plus the witness field.</summary>
        static byte[] SaveRobust(Shape shape, int count)
        {
            var e = Elements(count);
            var robust = Robust(uint.MaxValue, ContextOf(shape));

            // The same reason as in SaveRaw: Save<T>'s type argument is the *declared* root, and a value
            // passed as object would be written as a polymorphic slot instead.
            return shape switch
            {
                Shape.Queue => robust.Save(new ClQueueHolder { Q = new Queue<int>(e), Tail = TailWitness }, 1),
                Shape.Stack => robust.Save(new ClStackHolder { S = StackEnumeratingAs(e), Tail = TailWitness }, 1),
                Shape.List => robust.Save(new ClListHolder { L = new List<int>(e), Tail = TailWitness }, 1),
                Shape.ImmutableArray => robust.Save(new ClImmutableHolder { A = ImmutableArray.Create(e), Tail = TailWitness }, 1),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
        }

        static byte[] Save(Shape shape, int count, Reader reader)
            => reader == Reader.Raw ? SaveRaw(shape, count) : SaveRobust(shape, count);

        /// <summary>
        /// Reads a file back. <paramref name="cap"/> is the limit the reader is configured with, which is
        /// what the raw rows exist to exercise: a formatter's own bound, with no schema, no CRC and no
        /// interpreter in the way.
        /// </summary>
        static object Load(Shape shape, byte[] file, uint cap, Reader reader)
        {
            if (reader == Reader.Raw) return LoadRaw(shape, file, cap);
            return LoadRobust(shape, file, cap, reader);
        }

        static object LoadRaw(Shape shape, byte[] file, uint cap)
        {
            var s = Raw(cap);
            return shape switch
            {
                Shape.Queue => s.Deserialize<Queue<int>>(file),
                Shape.Stack => s.Deserialize<Stack<int>>(file),
                Shape.List => s.Deserialize<List<int>>(file),
                Shape.ImmutableArray => s.Deserialize<ImmutableArray<int>>(file),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
        }

        /// <summary>
        /// Reads a robust file. <c>Migrating</c> flips the layout fingerprint (and fixes the CRC), which is
        /// the supported way to make the loader take the migration interpreter over a file describing the
        /// current schema - the trick <see cref="TestBase.SlowPath"/> documents.
        /// </summary>
        static object LoadRobust(Shape shape, byte[] file, uint cap, Reader reader)
        {
            var s = Robust(cap, ContextOf(shape));
            byte[] bytes = reader == Reader.Migrating ? SlowPath(file) : file;
            return shape switch
            {
                Shape.Queue => s.Load<ClQueueHolder>(bytes, 1),
                Shape.Stack => s.Load<ClStackHolder>(bytes, 1),
                Shape.List => s.Load<ClListHolder>(bytes, 1),
                Shape.ImmutableArray => s.Load<ClImmutableHolder>(bytes, 1),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
        }

        /// <summary>The elements a loaded value holds, whichever shape it was reached as.</summary>
        /// <summary>
        /// The elements a loaded value holds, as a count is not enough for a zero-byte element type - so this
        /// is the count of whatever it was reached as, and the typed overloads that need the values (the int
        /// rows) compare them directly instead.
        /// </summary>
        static int CountOf(object loaded) => loaded switch
        {
            Queue<int> q => q.Count,
            Stack<int> s => s.Count,
            List<int> l => l.Count,
            ImmutableArray<int> a => a.Length,
            Queue<ClZeroBytes> q => q.Count,
            ClZeroBytesQueueHolder h => h.Q.Count,
            ClQueueHolder h => h.Q.Count,
            ClStackHolder h => h.S.Count,
            ClListHolder h => h.L.Count,
            ClImmutableHolder h => h.A.Length,
            _ => throw new ArgumentException($"unexpected shape {loaded?.GetType().Name ?? "null"}", nameof(loaded)),
        };

        /// <summary>The int elements a loaded value holds, whichever shape it was reached as.</summary>
        static int[] ElementsOf(object loaded) => loaded switch
        {
            Queue<int> q => q.ToArray(),
            Stack<int> s => s.ToArray(),
            List<int> l => l.ToArray(),
            ImmutableArray<int> a => a.ToArray(),
            ClQueueHolder h => h.Q.ToArray(),
            ClStackHolder h => h.S.ToArray(),
            ClListHolder h => h.L.ToArray(),
            ClImmutableHolder h => h.A.ToArray(),
            _ => throw new ArgumentException($"unexpected shape {loaded?.GetType().Name ?? "null"}", nameof(loaded)),
        };

        /// <summary>The field after the collection, which reads back only if the stream stayed aligned.</summary>
        static int TailOf(object loaded) => loaded switch
        {
            ClQueueHolder h => h.Tail,
            ClStackHolder h => h.Tail,
            ClListHolder h => h.Tail,
            ClImmutableHolder h => h.Tail,
            ClZeroBytesQueueHolder h => h.Tail,
            _ => throw new ArgumentException($"{loaded?.GetType().Name ?? "null"} is not a robust holder", nameof(loaded)),
        };

        // ── where the count is, and what it is encoded as ────────────────────────────────────

        /// <summary>
        /// The reference marker in front of a newly written value, i.e.
        /// <c>RefNewValueSameType + RefBias</c>. Restated from <c>Protocol</c> rather than written as a
        /// literal, and asserted at every position it is assumed to occupy.
        /// </summary>
        const byte NewValueMarker = Protocol.RefNewValueSameType + Protocol.RefBias;

        /// <summary>What a shape's count is encoded as, and the type the messages name.</summary>
        readonly record struct Layout(Type CollectionType, bool FixedWidthCount);

        static Layout LayoutOf(Shape shape) => shape switch
        {
            Shape.Queue => new Layout(typeof(Queue<int>), true),
            Shape.Stack => new Layout(typeof(Stack<int>), true),
            Shape.List => new Layout(typeof(List<int>), false),
            Shape.ImmutableArray => new Layout(typeof(ImmutableArray<int>), false),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        /// <summary>The file's own definition id, which is what picks the count's encoding on a read.</summary>
        static int DefinitionIdOf(Shape shape) => shape switch
        {
            Shape.Queue => WellKnownTypes.Queue,
            Shape.Stack => WellKnownTypes.Stack,
            // An ImmutableArray is described as a List<> shape (it is an ICollection<T>), and that is the
            // encoding it writes too, so the same id describes both sides of it.
            Shape.List or Shape.ImmutableArray => WellKnownTypes.List,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        /// <summary>
        /// The offset of the element count. The raw format has no root type id, so the count follows the
        /// root's own reference marker - and a value type has no marker at all, which is why an
        /// <c>ImmutableArray</c> root's count is the very first byte. The robust format prefixes the payload
        /// with the root's type id, and a reference-typed member carries a second marker.
        /// <para>
        /// Every byte this skips is asserted rather than assumed, so a layout change fails here instead of
        /// quietly moving the patch below onto a neighbouring field.
        /// </para>
        /// </summary>
        static int CountOffset(Layout layout, byte[] file, bool robust)
        {
            if (!robust)
            {
                // The collection *is* the root here, so a reference type carries its own marker and a value
                // type (the ImmutableArray) starts with its count at byte zero.
                if (layout.CollectionType.IsValueType) return 0;
                Assert.Equal(NewValueMarker, file[0]);
                return 1;
            }

            // The root is always one of the holder classes, so the root's marker is unconditional - it is
            // the *member's* that depends on the shape. Note this holds for <c>SlowPath</c> too: flipping the
            // fingerprint changes the byte at RobustHeader.FingerprintOffset, which is nowhere near here.
            int offset = SkipLeb128(file, DataSectionOffset(file));   // the root's type id
            Assert.Equal(NewValueMarker, file[offset]);
            offset++;

            if (!layout.CollectionType.IsValueType)
            {
                Assert.Equal(NewValueMarker, file[offset]);
                offset++;
            }

            return offset;
        }

        /// <summary>
        /// The offset just past a leb128 at <paramref name="offset"/>. A manifest id is not a fixed width
        /// (a context with more than a hundred types needs two bytes), so the count offset is *computed*
        /// from the file's own encoding rather than from an assumed one - and the marker assertions below
        /// are what check the computation, since a wrong offset lands on the wrong byte.
        /// </summary>
        static int SkipLeb128(byte[] file, int offset)
        {
            int afterId = offset;
            SchemaSection.ReadLeb128(file, ref afterId);
            Assert.True(afterId > offset, "the leb128 consumed no bytes, so the offset arithmetic is wrong");
            return afterId;
        }

        static int DataSectionOffset(byte[] file)
            => (int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.DataSectionOffsetOffset));

        static int StringTableOffset(byte[] file)
            => (int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.StringTableOffsetOffset));

        /// <summary>How many payload bytes a robust file declares, i.e. everything but the root's type id.</summary>
        static int DataSectionLength(byte[] file) => StringTableOffset(file) - DataSectionOffset(file);

        /// <summary>
        /// Replaces the element count in an otherwise valid file. A varint has no fixed width, so a shorter
        /// count leaves the tail of the old encoding behind and a longer one eats the bytes after it - and
        /// neither matters, because a count that is about to be refused is never followed by an element read.
        /// The rows that <em>do</em> read elements use this to declare a count the file does not carry, which
        /// is the truncation row.
        /// <para>
        /// The CRC is recomputed, so what refuses the file is the count and not the checksum.
        /// </para>
        /// </summary>
        static byte[] WithCount(byte[] file, Layout layout, int newCount, bool robust)
        {
            var copy = (byte[])file.Clone();
            int offset = CountOffset(layout, copy, robust);

            if (layout.FixedWidthCount)
            {
                BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(offset), newCount);
            }
            else
            {
                var bytes = BiasedCountBytes(newCount);
                Assert.True(offset + bytes.Length <= copy.Length, "the file is too short to hold that count");
                bytes.CopyTo(copy.AsSpan(offset));
            }

            if (robust) FixRobustChecksum(copy);
            return copy;
        }

        static byte[] WithCount(Shape shape, int newCount, Reader reader)
            => WithCount(Save(shape, 1, reader), LayoutOf(shape), newCount, robust: reader != Reader.Raw);

        /// <summary>
        /// The biased varint bytes that declare <paramref name="count"/>.
        /// <para>
        /// Written as the <em>unsigned</em> biased value <c>(uint)(count + 1)</c>, because that is what has to
        /// be on the wire for the reader to decode <paramref name="count"/> back: it computes
        /// <c>(int)varint - 1</c>, so -1 is a zero byte and -2 needs the 32-bit pattern <c>0xFFFFFFFF</c> in
        /// five varint bytes. Going through <c>WriteUInt32Bias</c> instead would write <c>(ulong)(-1)</c> -
        /// ten bytes - which the reader refuses as a malformed varint long before it is a count, so the test
        /// would be asserting about a different failure.
        /// </para>
        /// <para>
        /// int.MaxValue is not a special case: <c>(uint)(int.MaxValue + 1)</c> is <c>0x80000000</c>, and
        /// <c>(int)0x80000000 - 1</c> wraps back to int.MaxValue, so it declares what it claims to.
        /// </para>
        /// </summary>
        static byte[] BiasedCountBytes(int count) => WriteVarInt((uint)((ulong)count + 1));

        /// <summary>A minimal 7-bits-per-byte varint of <paramref name="value"/> - the encoding
        /// <c>SerializerBinary</c> reads and writes, restated here so a patch produces exactly the bytes a
        /// writer would have.</summary>
        static byte[] WriteVarInt(uint value)
        {
            var bytes = new List<byte>();
            while (value >= 0x80)
            {
                bytes.Add((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            bytes.Add((byte)value);
            return bytes.ToArray();
        }

        // ── what a refusal has to look like ───────────────────────────────────────────────────

        /// <summary>
        /// Asserts a refusal is the <em>count</em> refusal: a Ceras corruption report naming both the count
        /// the file declared and the cap the reader was configured with. A truncated read is a corruption
        /// report too, so the message is what tells the two apart - without it a bound that simply let a
        /// huge count through to the element loop would pass.
        /// <para>
        /// The type is asserted exactly, which also pins that the refusal reaches the caller as itself: the
        /// reference formatter re-throws a Ceras refusal unwrapped and wraps anything else, so a raw
        /// <c>ArgumentOutOfRangeException</c> arriving wrapped in a <c>CerasException</c> cannot pass here.
        /// </para>
        /// </summary>
        static CerasDataCorruptionException AssertCountRefused(Action load, int declaredCount, uint cap)
        {
            var corruption = AssertCountRefused(load, declaredCount);
            Assert.Contains(cap.ToString(), corruption.Message, StringComparison.Ordinal);
            return corruption;
        }

        /// <summary>
        /// The half of the above that applies to a negative count too. A negative is refused as malformed
        /// whatever the limit is, so its message does not quote the cap - asserting that it did would be
        /// asserting a lie about what the check compared, which is the kind of detail a message assertion is
        /// supposed to catch rather than paper over.
        /// </summary>
        static CerasDataCorruptionException AssertCountRefused(Action load, int declaredCount)
        {
            var corruption = Assert.IsType<CerasDataCorruptionException>(Record.Exception(load));
            Assert.Contains(declaredCount.ToString(), corruption.Message, StringComparison.Ordinal);
            return corruption;
        }

        // ── a count within the cap ───────────────────────────────────────────────────────────

        /// <summary>
        /// The counterweight to every refusal in this file: a bound that is met by refusing everything is
        /// not a bound, it is a switch. Runs on every reader that can reach the shape, and 200 elements is
        /// the interesting count - as a biased varint it spans two bytes, so it is the row that would notice
        /// a count read that stops short.
        /// </summary>
        [Theory]
        [MemberData(nameof(WithinCap))]
        public void A_Count_Within_The_Cap_Loads_On_Every_Reader(Shape shape, int count, Reader reader)
        {
            var loaded = Load(shape, Save(shape, count, reader), Cap, reader);

            Assert.Equal(Elements(count), ElementsOf(loaded));
            if (reader != Reader.Raw) Assert.Equal(TailWitness, TailOf(loaded));
        }

        /// <summary>
        /// A count at the cap is still loaded: the bound is inclusive, so raising a cap by one is what it
        /// takes to accept one more element rather than a change of meaning.
        /// </summary>
        [Theory]
        [MemberData(nameof(WithinCap))]
        public void A_Count_Exactly_At_The_Cap_Loads(Shape shape, int count, Reader reader)
        {
            var loaded = Load(shape, Save(shape, count, reader), (uint)count, reader);
            Assert.Equal(Elements(count), ElementsOf(loaded));
        }

        // ── a count over the cap ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A count above the configured cap is refused, on every reader, and named. The two counts are
        /// different witnesses: <see cref="ModestlyOverCap"/> allocates comfortably, so a reader that
        /// ignored the bound went on to fail on the elements instead; <see cref="AbsurdCount"/> cannot be
        /// allocated at all, so only a check placed <em>before</em> the allocation can answer it.
        /// </summary>
        [Theory]
        [MemberData(nameof(OverCap))]
        public void A_Count_Over_The_Cap_Is_Refused_On_Every_Reader(Shape shape, int declaredCount, Reader reader)
        {
            AssertCountRefused(() => Load(shape, WithCount(shape, declaredCount, reader), Cap, reader), declaredCount, Cap);
        }

        /// <summary>
        /// A refused count must not have touched the caller's object either. Every one of these formatters
        /// assigns its out parameter - so the raw overloads that seed a target are asked to refuse into an
        /// existing one, and it has to come back untouched. A check written after the assignment would pass
        /// every other row in this file and fail this one.
        /// </summary>
        [Theory]
        [MemberData(nameof(OverCap))]
        public void A_Refused_Count_Leaves_The_Target_It_Was_Handed_Alone(Shape shape, int declaredCount, Reader reader)
        {
            if (reader != Reader.Raw) return;   // the robust loaders hand out a fresh root; there is no target to keep

            var file = WithCount(shape, declaredCount, reader);
            var s = Raw(Cap);

            switch (shape)
            {
                case Shape.Queue:
                {
                    var seed = new Queue<int>(new[] { 7, 8, 9 });
                    AssertCountRefused(() => s.Deserialize(ref seed, file), declaredCount, Cap);
                    Assert.Equal(new[] { 7, 8, 9 }, seed.ToArray());
                    break;
                }
                case Shape.Stack:
                {
                    var seed = StackEnumeratingAs(new[] { 7, 8, 9 });
                    AssertCountRefused(() => s.Deserialize(ref seed, file), declaredCount, Cap);
                    Assert.Equal(new[] { 7, 8, 9 }, seed.ToArray());
                    break;
                }
                case Shape.ImmutableArray:
                {
                    var seed = ImmutableArray.Create(7, 8, 9);
                    AssertCountRefused(() => s.Deserialize(ref seed, file), declaredCount, Cap);
                    Assert.Equal(new[] { 7, 8, 9 }, seed.ToArray());
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }

        /// <summary>
        /// The defect, stated as one assertion: one file, two readers, one answer. The migration
        /// interpreter has bounded every collection count through <c>PayloadGrammar</c> from the start, so
        /// before this change the same over-cap file was refused by the interpreter and read happily by the
        /// generated reader. Both must also say the same thing, not merely both refuse - one rule, one
        /// message.
        /// </summary>
        [Theory]
        [MemberData(nameof(InScopeReaders))]
        public void The_Generating_And_The_Migrating_Reader_Agree_On_An_Over_Cap_Count(Shape shape, Reader reader)
        {
            if (reader == Reader.Raw) return;   // the raw path has one reader; its row is the other half

            var file = WithCount(SaveRobust(shape, 1), LayoutOf(shape), ModestlyOverCap, robust: true);

            var generated = AssertCountRefused(() => LoadRobust(shape, file, Cap, Reader.Generating), ModestlyOverCap, Cap);
            var migrating = AssertCountRefused(() => LoadRobust(shape, file, Cap, Reader.Migrating), ModestlyOverCap, Cap);

            // The two readers are two paths to the same count, so they have to name the same two numbers. The
            // wording is deliberately not compared: the interpreter's is
            // <c>PayloadGrammar.ReadCollectionCount</c>'s, which is older and says the same thing in its own
            // words (and says more, about the fixed-width encoding, where that applies). Two sentences
            // agreeing about one count is the claim; two sentences being the same characters is not.
            foreach (var number in new[] { ModestlyOverCap.ToString(), Cap.ToString() })
            {
                Assert.Contains(number, generated.Message, StringComparison.Ordinal);
                Assert.Contains(number, migrating.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// The refusal names the collection it rejected, which is what makes it actionable when a save holds
        /// a dozen different ones. The name is resolved only on the way to a refusal, since a
        /// <c>Type.FriendlyName</c> per value read is exactly the cost the allocation row below forbids.
        /// </summary>
        [Fact]
        public void The_Refusal_Names_The_Collection_It_Rejected()
        {
            foreach (var shape in InScope)
            {
                var typeName = Ceras.Helpers.ReflectionHelper.FriendlyName(LayoutOf(shape).CollectionType);

                var corruption = AssertCountRefused(
                    () => LoadRaw(shape, WithCount(shape, ModestlyOverCap, Reader.Raw), Cap),
                    ModestlyOverCap, Cap);

                Assert.Contains(typeName, corruption.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>The negative half of the same agreement.</summary>
        [Theory]
        [MemberData(nameof(InScopeReaders))]
        public void The_Generating_And_The_Migrating_Reader_Agree_On_A_Negative_Count(Shape shape, Reader reader)
        {
            if (reader == Reader.Raw || (shape == Shape.ImmutableArray && reader == Reader.Migrating)) return;

            // -2 and not -1: an ImmutableArray's -1 is its null marker, so it is a value rather than a
            // defect there, and the row about that is A_Null_Marker_Is_Still_A_Value.
            var file = WithCount(SaveRobust(shape, 1), LayoutOf(shape), -2, robust: true);

            var generated = AssertCountRefused(() => LoadRobust(shape, file, Cap, Reader.Generating), -2);
            var migrating = AssertCountRefused(() => LoadRobust(shape, file, Cap, Reader.Migrating), -2);

            // Both name the count; the wording differs because the interpreter's message is
            // PayloadGrammar's, and only the number is the shared fact. See the over-cap row for why the
            // sentences are not compared.
            Assert.Contains("-2", generated.Message, StringComparison.Ordinal);
            Assert.Contains("-2", migrating.Message, StringComparison.Ordinal);
        }

        // ── a negative count, and the one negative that is a value ───────────────────────────

        /// <summary>
        /// A negative count is corruption on every reader, and the reason differs by encoding: a queue or a
        /// stack writes a plain 4-byte element count with no null marker, so no negative value in it can be a
        /// null, while an <c>ImmutableArray</c>'s -1 is exactly that and is read before the check. So -1 is a
        /// defect for the first two and a value for the third, and both halves are pinned - reading a
        /// queue's -1 as "no queue" would lose the elements silently.
        /// </summary>
        [Theory]
        [MemberData(nameof(Negative))]
        public void A_Negative_Count_Is_Corruption_And_Not_A_Null_Collection(Shape shape, int declaredCount, Reader reader)
        {
            // A negative is refused as malformed whatever the cap is, so the cap is deliberately not
            // asserted here - see the AssertCountRefused overload's own comment.
            var corruption = AssertCountRefused(
                () => Load(shape, WithCount(shape, declaredCount, reader), Cap, reader), declaredCount);

            if (shape is Shape.Queue or Shape.Stack && reader != Reader.Migrating)
            {
                // The message has to say why a negative is impossible for this shape, because the
                // collections next door do have a null for one to be, and a reader - or a user - would
                // otherwise go looking in a payload that cannot carry it. Only the formatters' own rows are
                // held to that wording: the interpreter's message is PayloadGrammar's and says the same thing
                // in its own words ("a negative one is a corrupt payload, not a null collection"), which the
                // over-cap agreement row already checks it agrees on.
                Assert.Contains("no null marker", corruption.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_Null_Marker_Is_Still_A_Value_And_Not_A_Rejected_Count()
        {
            // Both directions, because both are ways to lose a value. A *default* ImmutableArray - the one
            // value whose marker is the -1 - has to come back as a default one rather than as an empty array
            // (the two are different: `ImmutableArray<T>.IsDefault` is a third state, and the -1 is the only
            // thing on the wire that distinguishes them). And a file whose count has been overwritten with
            // the marker has to be read as that value rather than refused by the check that follows it.
            var loaded = LoadRobust(Shape.ImmutableArray, SaveDefaultImmutable(), Cap, Reader.Generating);
            var holder = Assert.IsType<ClImmutableHolder>(loaded);
            Assert.True(holder.A.IsDefault);
            Assert.Equal(TailWitness, holder.Tail);

            // The distinction is real, not an artifact: the same reader turns a declared 0 into a non-default
            // empty array, which is what makes the marker load-bearing.
            Assert.False(Assert.IsType<ClImmutableHolder>(
                LoadRobust(Shape.ImmutableArray, SaveRobust(Shape.ImmutableArray, 0), Cap, Reader.Generating)).A.IsDefault);

            var fromRaw = Raw(Cap).Deserialize<ImmutableArray<int>>(
                WithCount(SaveRaw(Shape.ImmutableArray, 1), LayoutOf(Shape.ImmutableArray), -1, robust: false));
            Assert.True(fromRaw.IsDefault);
        }

        /// <summary>A save whose <c>ImmutableArray</c> member is the default value, i.e. the -1 marker.</summary>
        static byte[] SaveDefaultImmutable()
            => Robust(uint.MaxValue, ImmutableCtx).Save(
                new ClImmutableHolder { A = default, Tail = TailWitness }, 1);

        // ── truncation is still truncation ───────────────────────────────────────────────────

        /// <summary>
        /// A count that promises more elements than the file carries is a truncated payload, not an over-cap
        /// one. The count is legal and inside the cap - the bound passes it, on purpose - so the failure has
        /// to come from the elements, which is the answer to why this check is not a length check.
        /// </summary>
        [Theory]
        [MemberData(nameof(EveryReader))]
        public void A_Count_The_File_Does_Not_Carry_Is_A_Truncated_Payload(Shape shape, Reader reader)
        {
            // A file that carries one element and a witness field, declaring three. The count is the only
            // thing patched, so a refusal that mentions the cap or the count instead of the payload would be
            // the wrong refusal.
            var file = WithCount(shape, 3, reader);

            var corruption = Assert.IsType<CerasDataCorruptionException>(
                Record.Exception(() => Load(shape, file, Cap, reader)));
            Assert.Contains("truncated payload", corruption.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ── the shape that forbids a count-vs-remaining rule ────────────────────────────────

        /// <summary>
        /// 300 elements of a type that costs zero bytes, so the payload holds 300 elements' worth of nothing
        /// and any <c>count &gt; reader.Remaining</c> rule would refuse it. The cap is 300, i.e. the count is
        /// inside it, so this is about the absence of a length rule; the companion at the end of the test
        /// then takes the same file one element over the cap and expects the bound.
        /// </summary>
        [Theory]
        [InlineData(Reader.Raw)]
        [InlineData(Reader.Generating)]
        [InlineData(Reader.Migrating)]
        public void Zero_Byte_Elements_Are_Not_Bounded_By_The_Bytes_That_Follow_Them(Reader reader)
        {
            const int Count = 300;
            var zero = default(ClZeroBytes);
            var layout = new Layout(typeof(Queue<ClZeroBytes>), true);

            byte[] rawFile = Raw(uint.MaxValue).Serialize(QueueOfZeros(Count, zero));
            byte[] robustFile = SaveZeroBytes(Count);

            // The proof that the elements really are free: 1 and 300 of them occupy the same payload,
            // because the only thing that grows is the count - and the count is a fixed four bytes either
            // way. 300 ints would be 1200 bytes longer, which is the second half of the same claim.
            var zeroBytes = DataSectionLength(robustFile);
            Assert.Equal(DataSectionLength(SaveZeroBytes(1)), zeroBytes);
            Assert.Equal(zeroBytes + 4 * Count, DataSectionLength(SaveRobust(Shape.Queue, Count)));

            object loaded = reader switch
            {
                Reader.Raw => Raw((uint)Count).Deserialize<Queue<ClZeroBytes>>(rawFile),
                _ => Assert.IsType<ClZeroBytesQueueHolder>(Robust((uint)Count).Load<ClZeroBytesQueueHolder>(
                    reader == Reader.Migrating ? SlowPath(robustFile) : robustFile, 1)),
            };
            Assert.Equal(Count, CountOf(loaded));
            if (reader != Reader.Raw) Assert.Equal(TailWitness, TailOf(loaded));

            // One element over, and the same file is refused - by the bound, which is the only thing that
            // could tell 300 from 301 when the bytes behind both are the same none.
            AssertCountRefused(
                () => Robust((uint)Count).Load<ClZeroBytesQueueHolder>(
                    WithCount(robustFile, layout, Count + 1, robust: true), 1),
                Count + 1, (uint)Count);
        }

        static Queue<ClZeroBytes> QueueOfZeros(int count, ClZeroBytes element)
            => new Queue<ClZeroBytes>(Enumerable.Repeat(element, count));

        static byte[] SaveZeroBytes(int count)
            => Robust(uint.MaxValue).Save(
                new ClZeroBytesQueueHolder { Q = QueueOfZeros(count, default), Tail = TailWitness }, 1);

        // ── the control: the shape that was already bounded ───────────────────────────────────

        /// <summary>
        /// <c>List</c> is bounded by <c>CollectionFormatter</c>, which refuses an over-cap count with an
        /// <c>InvalidOperationException("Max size exceeded")</c> that the reference formatter then wraps -
        /// so it is refused, but as a different type with different words from the three this change brings
        /// in line. Pinned rather than left out, so that this file's rows cannot be satisfied by refusing
        /// every collection, and so the divergence is on the record as a decision instead of an oversight.
        /// </summary>
        [Theory]
        [InlineData(Reader.Generating)]
        [InlineData(Reader.Migrating)]
        public void The_Already_Bounded_List_Still_Refuses_An_Over_Cap_Count_On_Both_Readers(Reader reader)
        {
            var file = WithCount(SaveRobust(Shape.List, 1), LayoutOf(Shape.List), ModestlyOverCap, robust: true);

            var ex = Record.Exception(() => LoadRobust(Shape.List, file, Cap, reader));

            if (reader == Reader.Generating)
            {
                // Not a Ceras refusal, so the reference formatter wraps it - which is the whole difference
                // from the three shapes that now refuse as themselves.
                var wrapper = Assert.IsType<CerasException>(ex);
                Assert.IsType<InvalidOperationException>(wrapper.InnerException);
                Assert.Contains("Max size exceeded", wrapper.InnerException.Message, StringComparison.Ordinal);
            }
            else
            {
                // The interpreter's bound, which is the Ceras refusal the other shapes now match.
                var corruption = Assert.IsType<CerasDataCorruptionException>(ex);
                Assert.Contains(ModestlyOverCap.ToString(), corruption.Message, StringComparison.Ordinal);
                Assert.Contains(Cap.ToString(), corruption.Message, StringComparison.Ordinal);
            }
        }

        // ── one limit, and an unchanged default ──────────────────────────────────────────────

        /// <summary>
        /// One configured number bounds every shape. The three that were unbounded are asked in one loop,
        /// because "MaxCollectionSize is honoured" is a claim about the setting rather than about any one
        /// formatter, and a loop is what stops one formatter's answer from standing in for the others'.
        /// </summary>
        [Fact]
        public void One_Configured_Number_Bounds_Every_Collection_Shape_It_Is_Asked_About()
        {
            const uint cap = 4;

            foreach (var shape in InScope)
            {
                var file = WithCount(SaveRobust(shape, 1), LayoutOf(shape), (int)cap + 1, robust: true);
                AssertCountRefused(() => LoadRobust(shape, file, cap, Reader.Generating), (int)cap + 1, cap);

                // The raw reader, with the same number configured on a different config object: the limit
                // is read from the config a serializer was built with, not from anything global.
                var rawFile = WithCount(SaveRaw(shape, 1), LayoutOf(shape), (int)cap + 1, robust: false);
                AssertCountRefused(() => LoadRaw(shape, rawFile, cap), (int)cap + 1, cap);
            }
        }

        /// <summary>
        /// With nothing configured nothing is refused: the default is <see cref="uint.MaxValue"/>, so the
        /// comparison cannot fail and a save nobody capped reads exactly as before. Pinned as a default
        /// because a limit that is opt-in has to stay opt-in - a check that started here with a sane-sounding
        /// constant would break every existing save.
        /// </summary>
        [Fact]
        public void The_Default_Limit_Is_Unlimited_And_Changes_No_Ordinary_Save()
        {
            Assert.Equal(uint.MaxValue, new CerasRawConfig().SizeLimits.MaxCollectionSize);
            Assert.Equal(uint.MaxValue, new CerasRobustConfig().SizeLimits.MaxCollectionSize);

            foreach (var shape in InScope)
                Assert.Equal(Elements(WideCount), ElementsOf(Load(shape, Save(shape, WideCount, Reader.Raw), uint.MaxValue, Reader.Raw)));

            var holder = Assert.IsType<ClQueueHolder>(LoadRobust(Shape.Queue, SaveRobust(Shape.Queue, WideCount), uint.MaxValue, Reader.Generating));
            Assert.Equal(Elements(WideCount), holder.Q.ToArray());

            // And a cap nobody exceeded leaves the values alone, which is the difference between a limit and
            // a truncation: the same file, the same values, one configured number higher.
            var under = Assert.IsType<ClQueueHolder>(LoadRobust(Shape.Queue, SaveRobust(Shape.Queue, 3), 3, Reader.Generating));
            Assert.Equal(Elements(3), under.Q.ToArray());
        }

        // ── the accepted path costs nothing ─────────────────────────────────────────────────

        /// <summary>
        /// The guard allocates nothing for a count it accepts, so a valid load costs what it did before the
        /// guard existed.
        /// <para>
        /// Measured twice, because either alone would be weak. The helper is called directly on a valid count
        /// and must allocate exactly zero bytes - that is the property, and a guard that resolved the type
        /// name or built a message before testing the count would fail it. Then a whole valid load is measured
        /// against a control that isolates the guard: the same file and the same shape, read by two serializers
        /// that differ only in whether a limit is configured, so the accepted path has to cost the same either
        /// way.
        /// </para>
        /// <para>
        /// The control is deliberately not a different collection shape. Queue, Stack and List have allocation
        /// profiles of their own, so "Queue costs more than List" would be a fact about those types rather
        /// than about the guard - and a byte allowance wide enough to absorb it would be wide enough to hide
        /// the per-load string or type name this is looking for.
        /// </para>
        /// <para>
        /// Warmed by time rather than by iteration count, for the reason <c>LoopMeasurementRunner.WarmUp</c>
        /// documents: tiered compilation holds a newly-called method at tier-0 for 100 ms. Best of three
        /// passes, so one unlucky collection decides nothing.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Guard_Adds_No_Per_Call_Allocation_To_A_Valid_Count()
        {
            // The property, on its own: a valid count is two comparisons.
            static void Accept() => Formatters.CollectionCountLimit.Require(3, 256, typeof(Queue<int>), countHasNoNullMarker: true);
            static void Refuse() => Formatters.CollectionCountLimit.Require(-1, 256, typeof(Queue<int>), countHasNoNullMarker: true);

            LoopMeasurementRunner.WarmUp(Accept, 64, LoopMeasurementRunner.DefaultWarmupMilliseconds);

            long best = long.MaxValue;
            for (int pass = 0; pass < 3; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) Accept();
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            Assert.Equal(0, best);

            // And the guard is not inert: the same call with a count it refuses still produces the refusal.
            var refused = Record.Exception(() =>
            {
                for (int i = 0; i < 1000; i++) Refuse();
            });
            Assert.IsType<CerasDataCorruptionException>(refused);

            // Then the whole load. A cap only changes the number MaxCount returns, so an accepted count has to
            // cost what it costs with the default.
            foreach (var shape in InScope)
            {
                var file = SaveRaw(shape, 4);
                // One serializer per arm, built outside the measured body.
                var withCap = Raw(Cap);
                var withoutCap = Raw(uint.MaxValue);

                Assert.Equal(MeasureWarm(() => LoadRawWith(withCap, shape, file)),
                             MeasureWarm(() => LoadRawWith(withoutCap, shape, file)));
            }
        }

        /// <summary>
        /// <c>LoadRaw</c> against a serializer the caller already built. <c>LoadRaw</c> constructs one per
        /// call, and a construction is larger than everything it would be measuring.
        /// </summary>
        static object LoadRawWith(CerasFastSerializer serializer, Shape shape, byte[] file) => shape switch
        {
            Shape.Queue => serializer.Deserialize<Queue<int>>(file),
            Shape.Stack => serializer.Deserialize<Stack<int>>(file),
            Shape.List => serializer.Deserialize<List<int>>(file),
            Shape.ImmutableArray => serializer.Deserialize<ImmutableArray<int>>(file),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        /// <summary>
        /// The smallest allocation <paramref name="body"/> does per call, over three passes of 64 iterations
        /// after a time-based warm-up.
        /// </summary>
        static long MeasureWarm(Action body)
        {
            LoopMeasurementRunner.WarmUp(body, 64, LoopMeasurementRunner.DefaultWarmupMilliseconds);
            long best = long.MaxValue;
            for (int pass = 0; pass < 3; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 64; i++) body();
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return best / 64;
        }

        // ── the count's own encoding is none of this change's business ──────────────────────

        /// <summary>
        /// The count a <c>Queue</c>/<c>Stack</c> writes is a fixed 4-byte little-endian element count, not a
        /// varint, and bounding it did not change that. Two counts are used because they disagree: 1 and 200
        /// are one and two bytes as a biased varint, so a count "fixed" by switching to the shared encoding
        /// would move the 200 by a byte and be caught here.
        /// <para>
        /// <c>PayloadGrammar</c> is then asked to read the count this writer produced, off these very bytes,
        /// and must land on it having consumed exactly four - so the two halves of the format agreeing is
        /// pinned from the bytes rather than taken from a comment.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(WideCount)]
        public void A_Queue_And_A_Stack_Still_Write_A_Fixed_Four_Byte_Element_Count(int count)
        {
            foreach (var shape in new[] { Shape.Queue, Shape.Stack })
            {
                var file = SaveRaw(shape, count);
                var offset = CountOffset(LayoutOf(shape), file, robust: false);

                Assert.Equal((byte)(count & 0xFF), file[offset]);
                Assert.Equal((byte)((count >> 8) & 0xFF), file[offset + 1]);
                Assert.Equal((byte)((count >> 16) & 0xFF), file[offset + 2]);
                Assert.Equal((byte)((count >> 24) & 0xFF), file[offset + 3]);

                var definitionId = DefinitionIdOf(shape);
                Assert.True(PayloadGrammar.UsesFixedElementCount(definitionId), $"{shape}'s count is read as a fixed one");

                var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(file, offset, file.Length - offset));
                Assert.Equal(count, PayloadGrammar.ReadCollectionCount(definitionId, uint.MaxValue, ref reader));
                Assert.Equal(4, reader.Consumed);
            }
        }
    }
}
