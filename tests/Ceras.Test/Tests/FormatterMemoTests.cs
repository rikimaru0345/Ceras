using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;
using Ceras.Formatters;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// <c>GetFormatter&lt;T&gt;</c> remembers, per closed type, which slot of which serializer's table
    /// holds that type's formatter.
    /// <para>
    /// The memo is worth about half of the cost of an operation on a small message, so the fast path
    /// is an identity test rather than a lookup. That makes three things load-bearing, none of them
    /// visible in the happy path, and all three are pinned here: a memo written by one serializer must
    /// never be read by another, it must not keep a serializer alive, and it must survive the table
    /// being replaced.
    /// </para>
    /// </summary>
    public class FormatterMemoTests
    {
        [CerasSerializable]
        public class Thing { public int A = 1; public int B = 2; }

        /// <summary>
        /// Writes a distinctive first byte, so a type serialized with the wrong formatter is visible
        /// in the bytes rather than only in whether an exception happened.
        /// </summary>
        class MarkedFormatter : IFormatter<Thing>
        {
            public void Serialize(IBufferWriter<byte> writer, Thing value)
            {
                var span = writer.GetSpan(1);
                span[0] = 0xAB;
                writer.Advance(1);
                var rest = writer.GetSpan(8);
                BinaryPrimitives.WriteInt32LittleEndian(rest, value.A);
                BinaryPrimitives.WriteInt32LittleEndian(rest.Slice(4), value.B);
                writer.Advance(8);
            }

            public void Deserialize(ref SequenceReader<byte> reader, ref Thing value) => throw new NotSupportedException();
            public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Thing)value);
            public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, System.Reflection.MemberInfo member) => throw new NotSupportedException();
        }

        /// <summary>
        /// The one that would be catastrophic rather than merely slow. A formatter is wired to the
        /// serializer that built it - its config, its caches, its callbacks - so a memo read by the
        /// wrong serializer would encode with another instance's settings and report success.
        /// <para>
        /// One serializer is given a custom formatter that writes a marker byte. If the memo were keyed
        /// on the type alone, the second serializer would answer with it and inherit the marker.
        /// Interleaved, so each write follows a write that re-points the memo at the other serializer.
        /// The marker is looked for rather than indexed, because the raw format puts a null flag and an
        /// object id in front of a class's own bytes and that is not this test's business.
        /// </para>
        /// </summary>
        [Fact]
        public void TwoSerializersDoNotShareFormatters()
        {
            var marked = new CerasFastSerializer();
            marked.Config.ConfigType<Thing>().SetFormatter(new MarkedFormatter());

            var plain = new CerasFastSerializer();

            for (int i = 0; i < 20; i++)
            {
                Assert.True(HasMarker(marked.Serialize(new Thing())), "the marked serializer stopped using its own formatter");
                Assert.False(HasMarker(plain.Serialize(new Thing())), "the plain serializer borrowed the other serializer's formatter");
            }

            // And each still round-trips with its own behaviour afterwards.
            Assert.True(HasMarker(marked.Serialize(new Thing())), "the marked serializer stopped using its own formatter");
            Assert.False(HasMarker(plain.Serialize(new Thing())), "the plain serializer borrowed the other serializer's formatter");
            Assert.Equal(2, plain.Deserialize<Thing>(plain.Serialize(new Thing())).B);
        }

        static bool HasMarker(byte[] bytes) => Array.IndexOf(bytes, (byte)0xAB) >= 0;

        /// <summary>
        /// The bug this file exists for, at last: the note a closed type keeps about where its
        /// formatter lives is shared between serializers, because it is a static, and one serializer
        /// per thread is a supported arrangement. It was two fields - the owner's id and the slot - and
        /// two threads writing them could interleave into one note holding thread A's slot under
        /// thread B's id. B's check would then pass and B would encode with A's formatter, silently.
        /// <para>
        /// A sequential test cannot see that, and neither could any amount of repeating the
        /// two-serializer test above: the window is between two writes.
        /// </para>
        /// <para>
        /// Both serializers format the same shared type <see cref="Payload"/>, each writing its own
        /// marker byte, and each thread reaches it through its own envelope type. If the shared note
        /// ever names the other thread's formatter, that marker turns up in the wrong payload.
        /// </para>
        /// </summary>
        [Fact]
        public void TwoThreadsWithTheirOwnSerializersNeverUseEachOthersFormatters()
        {
            const byte MarkerA = 0xA1, MarkerB = 0xB2;
            const int Iterations = 200_000;

            var cerasA = new CerasFastSerializer();
            cerasA.Config.ConfigType<Payload>().SetFormatter(new PayloadFormatter(MarkerA));
            var cerasB = new CerasFastSerializer();
            cerasB.Config.ConfigType<Payload>().SetFormatter(new PayloadFormatter(MarkerB));

            var payload = new Payload { Value = 1 };
            var start = new ManualResetEventSlim();
            Exception failureA = null, failureB = null;

            var threadA = new Thread(() =>
            {
                start.Wait();
                failureA = Run(cerasA, payload, MarkerA, MarkerB, Iterations);
            });
            var threadB = new Thread(() =>
            {
                start.Wait();
                failureB = Run(cerasB, payload, MarkerB, MarkerA, Iterations);
            });

            threadA.Start();
            threadB.Start();
            start.Set();
            threadA.Join();
            threadB.Join();

            if (failureA != null) throw failureA;
            if (failureB != null) throw failureB;
        }

        static Exception Run(CerasFastSerializer ceras, Payload payload, byte mine, byte theirs, int iterations)
        {
            try
            {
                // One round before the timed loop, so the failure being looked for is not just a
                // cold-start artefact.
                Check(ceras, payload, mine, theirs);

                for (int i = 0; i < iterations; i++)
                    Check(ceras, payload, mine, theirs);
            }
            catch (Exception e) { return e; }
            return null;
        }

        static void Check(CerasFastSerializer ceras, Payload payload, byte mine, byte theirs)
        {
            var bytes = ceras.Serialize(new EnvelopeA { Value = payload });
            if (Array.IndexOf(bytes, theirs) >= 0)
                throw new InvalidOperationException($"encoded with the other serializer's formatter: 0x{theirs:X2} in a payload that should carry only 0x{mine:X2}");
            if (Array.IndexOf(bytes, mine) < 0)
                throw new InvalidOperationException($"encoded without this serializer's own formatter: no 0x{mine:X2} in the payload");
        }

        [CerasSerializable]
        public class Payload { public int Value; }

        [CerasSerializable]
        public class EnvelopeA { public Payload Value; }

        [CerasSerializable]
        public class EnvelopeB { public Payload Value; }

        /// <summary>Writes a marker byte, then the payload, so a borrowed formatter is visible.</summary>
        class PayloadFormatter : IFormatter<Payload>
        {
            readonly byte _marker;
            public PayloadFormatter(byte marker) => _marker = marker;

            public void Serialize(IBufferWriter<byte> writer, Payload value)
            {
                var span = writer.GetSpan(5);
                span[0] = _marker;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(1), value.Value);
                writer.Advance(5);
            }

            public void Deserialize(ref SequenceReader<byte> reader, ref Payload value) => throw new NotSupportedException();
            public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Payload)value);
            public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, System.Reflection.MemberInfo member) => throw new NotSupportedException();
        }

        /// <summary>
        /// The note a closed type keeps holds an integer, never anything that leads to a formatter. A formatter would keep its
        /// serializer - config, caches, pools, everything it was configured with - reachable for as
        /// long as the type stays loaded, which is not a trade a library makes over a cache.
        /// </summary>
        [Fact]
        public void ASerializerIsCollectableAfterItHasBeenUsed()
        {
            var reference = UseAndDropASerializer();

            for (int i = 0; i < 4; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(reference.IsAlive, "the formatter memo is still holding a serializer alive");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference UseAndDropASerializer()
        {
            var ceras = new CerasFastSerializer();
            // The point: resolve, so a memo is written for these closed types.
            ceras.Serialize(new Thing());
            ceras.Serialize(new F0());
            ceras.Serialize(new F1());
            ceras.Serialize(new Thing());
            return new WeakReference(ceras);
        }

        /// <summary>
        /// Growing the table replaces the array, and the cells of the old one have to keep meaning the
        /// same thing. A type whose cell was lost must be resolved again rather than encoded with
        /// whichever formatter happened to inherit its index.
        /// <para>
        /// The fast path's null test is what actually guarantees that: a lost cell reads as null and
        /// takes the slow path. That the copy keeps every cell is an optimisation on top - it is what
        /// stops the re-resolve from happening at all - and replacing the growth with one that does not
        /// copy is still correct, only slower. Removing the null test is not.
        /// </para>
        /// </summary>
        [Fact]
        public void AFormatterWhoseCellWasLostIsResolvedAgainRatherThanBorrowed()
        {
            var ceras = new CerasFastSerializer();
            var thing = new Thing();

            // Establish the memo while the table is untouched.
            var expected = ceras.Serialize(thing);

            // Fill past the initial capacity, so it is replaced at least once. Every arm is a distinct
            // closed type at the call site, so every one claims its own slot; a switch on a value would
            // serialize as object and claim one slot in total.
            for (int i = 0; i < 24; i++) Fill(ceras, i);

            // This type's memo now names a table nothing reads any more.
            var after = ceras.Serialize(thing);
            Assert.Equal(string.Join(",", expected), string.Join(",", after));

            // And it is still its own formatter, not a neighbour's.
            Assert.Equal(1, ceras.Deserialize<Thing>(after).A);
            Assert.Equal(2, ceras.Deserialize<Thing>(after).B);
        }

        static void Fill(CerasFastSerializer s, int i)
        {
            switch (i)
            {
                case 0: s.Serialize(new F0()); break;
                case 1: s.Serialize(new F1()); break;
                case 2: s.Serialize(new F2()); break;
                case 3: s.Serialize(new F3()); break;
                case 4: s.Serialize(new F4()); break;
                case 5: s.Serialize(new F5()); break;
                case 6: s.Serialize(new F6()); break;
                case 7: s.Serialize(new F7()); break;
                case 8: s.Serialize(new F8()); break;
                case 9: s.Serialize(new F9()); break;
                case 10: s.Serialize(new F10()); break;
                case 11: s.Serialize(new F11()); break;
                case 12: s.Serialize(new F12()); break;
                case 13: s.Serialize(new F13()); break;
                case 14: s.Serialize(new F14()); break;
                case 15: s.Serialize(new F15()); break;
                case 16: s.Serialize(new F16()); break;
                case 17: s.Serialize(new F17()); break;
                case 18: s.Serialize(new F18()); break;
                case 19: s.Serialize(new F19()); break;
                case 20: s.Serialize(new F20()); break;
                case 21: s.Serialize(new F21()); break;
                case 22: s.Serialize(new F22()); break;
                case 23: s.Serialize(new F23()); break;
            }
        }

        // Distinct closed types, so each one claims its own slot in the table.
        [CerasSerializable] class F0 { public int V; }
        [CerasSerializable] class F1 { public int V; }
        [CerasSerializable] class F2 { public int V; }
        [CerasSerializable] class F3 { public int V; }
        [CerasSerializable] class F4 { public int V; }
        [CerasSerializable] class F5 { public int V; }
        [CerasSerializable] class F6 { public int V; }
        [CerasSerializable] class F7 { public int V; }
        [CerasSerializable] class F8 { public int V; }
        [CerasSerializable] class F9 { public int V; }
        [CerasSerializable] class F10 { public int V; }
        [CerasSerializable] class F11 { public int V; }
        [CerasSerializable] class F12 { public int V; }
        [CerasSerializable] class F13 { public int V; }
        [CerasSerializable] class F14 { public int V; }
        [CerasSerializable] class F15 { public int V; }
        [CerasSerializable] class F16 { public int V; }
        [CerasSerializable] class F17 { public int V; }
        [CerasSerializable] class F18 { public int V; }
        [CerasSerializable] class F19 { public int V; }
        [CerasSerializable] class F20 { public int V; }
        [CerasSerializable] class F21 { public int V; }
        [CerasSerializable] class F22 { public int V; }
        [CerasSerializable] class F23 { public int V; }
    }
}
