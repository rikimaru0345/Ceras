using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Ceras.Formatters;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// The write path reuses one <c>FixedArrayBufferWriter</c> per recursion level rather than
    /// allocating one per message.
    /// <para>
    /// That is only sound because no two live writers share a level. The property is not visible
    /// from the formatter contract - a formatter is handed a writer and is expected to write and
    /// return - so it is pinned here directly, by a formatter that re-enters the write path from
    /// inside its own <c>Serialize</c> and keeps writing afterwards. If the writers were shared, the
    /// outer call would resume at whatever offset the nested call finished at, and the bytes would
    /// be wrong in a way no round-trip test can distinguish from a legitimate layout.
    /// </para>
    /// </summary>
    public class ReusedWriteWriterTests
    {
        [CerasSerializable]
        public struct Inner
        {
            public int A;
            public int B;
        }

        [CerasSerializable]
        public struct Outer
        {
            public int Head;
            public Inner Nested;
            public int Tail;
        }

        /// <summary>
        /// Writes a marker, then makes a nested pooled write into its own buffer - the shape a
        /// callback that serializes, or a formatter that embeds another value, produces - then
        /// writes a second marker.
        /// </summary>
        class ReentrantFormatter : IFormatter<Outer>
        {
            readonly CerasSerializer _ser;

            public ReentrantFormatter(CerasSerializer ser) => _ser = ser;

            public void Serialize(IBufferWriter<byte> writer, Outer value)
            {
                var head = writer.GetSpan(4);
                head[0] = 0xF0; head[1] = 0xF1; head[2] = 0xF2; head[3] = 0xF3;
                writer.Advance(4);

                byte[] nested = CerasBufferPool.Rent(1024);
                int offset = 0;
                _ser.Serialize(value.Nested, ref nested, ref offset);
                CerasBufferPool.Return(nested);

                var tail = writer.GetSpan(4);
                tail[0] = 0xE0; tail[1] = 0xE1; tail[2] = 0xE2; tail[3] = 0xE3;
                writer.Advance(4);
            }

            public void Deserialize(ref SequenceReader<byte> reader, ref Outer value) => throw new NotSupportedException();

            public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Outer)value);
            public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, System.Reflection.MemberInfo member) => throw new NotSupportedException();
        }

        /// <summary>
        /// The decisive case: the outer call writes four bytes, re-enters, writes four more, and all
        /// eight must land contiguously in the outer call's own buffer. A shared writer would resume
        /// the outer write at the nested call's offset - 0, since the nested call wrote into its own
        /// buffer - and overwrite the head marker.
        /// <para>
        /// <c>Outer</c> is a struct, so there is no null-flag in front of the formatter's own bytes.
        /// </para>
        /// <para>
        /// The formatter is installed through a <c>CustomResolver</c> rather than handed to
        /// <c>SetFormatter</c>, because it needs the serializer it will re-enter - and the serializer
        /// only exists once the constructor is running, by which time the config is sealed. A resolver
        /// is the shape that closes that circle: it is configured before the serializer exists and is
        /// handed the serializer when <c>Outer</c> is first resolved, which is the same instance a
        /// formatter installed by hand used to get. See PostSealMutationTests for why a change made
        /// after construction is refused.
        /// </para>
        /// </summary>
        [Fact]
        public void ANestedWriteDoesNotDisturbTheWriterItWasMadeFrom()
        {
            var ceras = new CerasFastSerializer(c => c.ConfigType<Outer>().CustomResolver = (s, t) => new ReentrantFormatter(s));

            var value = new Outer { Head = 1, Nested = new Inner { A = 2, B = 3 }, Tail = 4 };

            byte[] buffer = CerasBufferPool.Rent(256);
            try
            {
                int offset = 0;
                int written = ceras.Serialize(value, ref buffer, ref offset);

                Assert.Equal(8, written);
                Assert.Equal(8, offset);
                Assert.Equal(0xF0, buffer[0]);
                Assert.Equal(0xF3, buffer[3]);
                Assert.Equal(0xE0, buffer[4]);
                Assert.Equal(0xE3, buffer[7]);
            }
            finally { CerasBufferPool.Return(buffer); }
        }

        /// <summary>
        /// Nesting must keep working across a run, not just once: a reused writer that leaked its
        /// previous buffer or offset would show up on the second call.
        /// </summary>
        [Fact]
        public void NestingKeepsWorkingAcrossRepeatedCalls()
        {
            var ceras = new CerasFastSerializer(c => c.ConfigType<Outer>().CustomResolver = (s, t) => new ReentrantFormatter(s));

            byte[] buffer = CerasBufferPool.Rent(256);
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    int offset = 0;
                    Assert.Equal(8, ceras.Serialize(new Outer(), ref buffer, ref offset));
                    Assert.Equal(0xF0, buffer[0]);
                    Assert.Equal(0xE3, buffer[7]);
                }
            }
            finally { CerasBufferPool.Return(buffer); }
        }

        /// <summary>
        /// What the reuse is for. A message loop is the shape that matters: tens of writes per frame
        /// into one buffer, and nothing allocated per message.
        /// </summary>
        [Fact]
        public void AMessageLoop_StopsAllocatingPerMessage()
        {
            var ceras = new CerasFastSerializer();
            var messages = new Inner[32];
            for (int i = 0; i < messages.Length; i++) messages[i] = new Inner { A = i, B = i * 2 };

            byte[] buffer = CerasBufferPool.Rent(4096);
            try
            {
                // Once to pay for anything the first call creates (formatters, caches).
                int warm = 0;
                ceras.Serialize(messages[0], ref buffer, ref warm);
                warm = 0;

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < messages.Length; i++)
                    ceras.Serialize(messages[i], ref buffer, ref warm);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                // The writers are reused and the pooled buffer is already large enough not to grow,
                // so a loop of messages allocates nothing at all.
                Assert.Equal(0, allocated);
            }
            finally { CerasBufferPool.Return(buffer); }
        }

        /// <summary>
        /// A spare writer must not hold the buffer it last wrote into, or a long-lived serializer
        /// pins an array for the rest of the process.
        /// <para>
        /// The array is the caller's own, not a rental: a pooled one would be kept alive by the pool
        /// regardless of what the writer does, and the test would pass for the wrong reason. It is
        /// written in a separate frame so the only possible remaining reference is the writer's.
        /// </para>
        /// <para>
        /// It goes through <c>SerializeStatic</c> rather than the pooled <c>Serialize(obj, ref
        /// buffer, ref offset)</c>, and that is not incidental. Both reach the same per-depth spare
        /// writer - the subject of this test - but only <c>SerializeStatic</c> declares the buffer
        /// caller-owned (<c>bufferIsPooled: false</c>), which is the only thing that makes a
        /// <see cref="WeakReference"/> to it meaningful. A caller-owned array on the pooled entry is
        /// now refused outright, so that combination is no longer reachable from a test - or from a
        /// caller - and the property this pins is checked on the path that still supports it.
        /// </para>
        /// </summary>
        [Fact]
        public void ASpareWriterDoesNotKeepTheLastBufferAlive()
        {
            var ceras = new CerasFastSerializer();
            var reference = WriteToACallerBuffer(ceras, typeof(StaticHolder));

            for (int i = 0; i < 4; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(reference.IsAlive, "a retained writer is still holding the last buffer");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference WriteToACallerBuffer(CerasSerializer ceras, Type type)
        {
            var buffer = new byte[1024];
            int offset = 0;
            int written = ceras.SerializeStatic(type, ref buffer, ref offset);

            // Not "> 0" alone: the property under test is that the *writer* does not keep the
            // buffer, which is only meaningful if the writer really wrote into this one. A static
            // type with no members would return 0 and leave the assertion below vacuous.
            Assert.Equal(offset, written);
            Assert.True(written > 0, $"SerializeStatic wrote nothing for {type}, so no writer held the buffer");
            Assert.Equal(1024, buffer.Length);   // it was big enough, so nothing replaced it

            return new WeakReference(buffer);
        }

        /// <summary>Carries a static member, so <c>SerializeStatic</c> actually writes into the buffer.
        /// The field is written to and read back by the serializer rather than by the test, which is
        /// the point: <c>SerializeStatic</c> writes a static type's members, and a type with none
        /// would make the call write nothing and the test would pass without exercising the writer.
        /// </summary>
        [CerasSerializable]
        static class StaticHolder
        {
            public static int Value = 1;
        }
    }
}
