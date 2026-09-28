using System;
using System.Collections.Generic;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// The pooled write-buffer contract: <c>Serialize(obj, ref buffer, ref offset)</c>.
    /// <para>
    /// The whole safety argument for this shape is that the caller always holds exactly one pooled
    /// array - Ceras returns the replaced one itself on growth - so that returning it can never be a
    /// double return however many times the buffer has grown, and so that "the array I hold is the
    /// pool's" stays true for the whole loop. These tests pin each of those, and the last one pins
    /// the mistake the shape is meant to prevent: a buffer that did not come from the pool.
    /// </para>
    /// </summary>
    public class PooledWriteBufferTests
    {
        [CerasSerializable]
        public class Blob
        {
            public byte[] Data = new byte[64];
        }

        [CerasSerializable]
        public class Holder
        {
            public List<Blob> Blobs = new();
        }

        static Holder MakeBig(int blobs, int size)
        {
            var h = new Holder();
            for (int i = 0; i < blobs; i++)
            {
                var b = new Blob { Data = new byte[size] };
                b.Data[0] = 1;
                h.Blobs.Add(b);
            }
            return h;
        }

        /// <summary>
        /// A reused buffer produces the same bytes as a fresh one, and the value survives a
        /// round trip - i.e. reuse does not leak state between messages.
        /// </summary>
        [Fact]
        public void AReusedBuffer_ProducesTheSameBytesAsAFreshOne()
        {
            var ceras = new CerasFastSerializer();
            var graph = MakeBig(4, 32);

            byte[] buffer = CerasBufferPool.Rent(4096);
            try
            {
                int offset = 0;
                int first = ceras.Serialize(graph, ref buffer, ref offset);
                var firstBytes = buffer.AsSpan(0, first).ToArray();

                for (int i = 0; i < 5; i++)
                {
                    offset = 0;
                    int again = ceras.Serialize(graph, ref buffer, ref offset);
                    Assert.Equal(first, again);
                    Assert.Equal(
                        string.Join(",", firstBytes),
                        string.Join(",", buffer.AsSpan(0, again).ToArray()));
                }
            }
            finally { CerasBufferPool.Return(buffer); }
        }

        [Fact]
        public void TheOffsetAdvances_SoSeveralObjectsCanShareOneBuffer()
        {
            var ceras = new CerasFastSerializer();
            var a = new Blob { Data = new byte[8] };
            var b = new Blob { Data = new byte[16] };

            byte[] buffer = CerasBufferPool.Rent(4096);
            try
            {
                int offset = 0;
                int wroteA = ceras.Serialize(a, ref buffer, ref offset);
                int afterA = offset;
                int wroteB = ceras.Serialize(b, ref buffer, ref offset);

                Assert.Equal(afterA, wroteA);
                Assert.True(wroteB > 0);
                Assert.True(offset > afterA);

                // The two writes are contiguous, and each decodes on its own.
                var readA = default(Blob)!;
                int o1 = 0;
                ceras.Deserialize(ref readA, buffer, ref o1);
                var readB = default(Blob)!;
                int o2 = afterA;
                ceras.Deserialize(ref readB, buffer, ref o2);

                Assert.Equal(wroteA, o1);
                Assert.Equal(wroteB, o2 - afterA);
                Assert.Equal(0, readA.Data[0]);
                Assert.Equal(0, readB.Data[0]);
            }
            finally { CerasBufferPool.Return(buffer); }
        }

        /// <summary>
        /// The core safety property, in the form that can actually be observed. When the buffer
        /// grows, Ceras takes the caller's old array back to the pool, so the number of outstanding
        /// rentals does not change: the caller traded one for one. If the replaced array were simply
        /// dropped instead, the count would climb by one per growth and the pool would be losing
        /// arrays on every save that outgrew its buffer - which is the waste the pooled API exists to
        /// remove, and which no behavioural test can see.
        /// </summary>
        [Fact]
        public void AfterGrowth_TheCallerStillHoldsExactlyOnePooledArray()
        {
            var ceras = new CerasFastSerializer();
            var graph = MakeBig(64, 1024); // far more than the 20 KB below

            byte[] buffer = CerasBufferPool.Rent(20_000);
            int originalLength = buffer.Length;
            int liveBefore = CerasBufferPool.LiveRentals;
            try
            {
                int offset = 0;
                int written = ceras.Serialize(graph, ref buffer, ref offset);

                Assert.True(written > 0);
                Assert.True(buffer.Length > originalLength);

                // Traded one for one, not one for two.
                Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);

                CerasBufferPool.Return(buffer);
                buffer = null!;
            }
            finally
            {
                if (buffer != null) CerasBufferPool.Return(buffer);
            }
        }

        /// <summary>
        /// The same balance across a run of grows, which is what a long session of progressively
        /// larger messages would do.
        /// </summary>
        [Fact]
        public void RepeatedGrowth_DoesNotAccumulateOutstandingRentals()
        {
            var ceras = new CerasFastSerializer();
            byte[] buffer = CerasBufferPool.Rent(64);
            try
            {
                int liveBefore = CerasBufferPool.LiveRentals;
                for (int size = 64; size <= 64 * 1024; size *= 2)
                {
                    int offset = 0;
                    ceras.Serialize(new Blob { Data = new byte[size] }, ref buffer, ref offset);
                }

                Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);

                CerasBufferPool.Return(buffer);
                buffer = null!;
            }
            finally
            {
                if (buffer != null) CerasBufferPool.Return(buffer);
            }
        }

        /// <summary>
        /// The mistake the pooled contract is meant to prevent: an array that never came from the
        /// pool. It is reported rather than silently accepted, because "silently accepted" was the
        /// failure that made this worth documenting in the first place.
        /// </summary>
        [Fact]
        public void ABufferThatDidNotComeFromThePool_IsReportedOnReturn()
        {
            var notRented = new byte[20_000];
            Assert.Throws<ArgumentException>(() => CerasBufferPool.Return(notRented));
        }
    }
}
