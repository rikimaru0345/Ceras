using System;
using System.Collections.Generic;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// The public <c>Serialize(obj, ref buffer, offset)</c> overload lets the caller supply the
    /// output buffer. When that buffer is not big enough, the shared growth path replaces it - and
    /// hands the old one to <see cref="CerasBufferPool"/>, which is only legal for an array the pool
    /// actually rented. A caller that allocated its own large buffer therefore got an
    /// <see cref="ArgumentException"/> from deep inside the write, rather than a grown buffer.
    /// </summary>
    public class ForeignBufferGrowthTests
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

        [Fact]
        public void ACallerAllocatedBufferIsGrownRatherThanRejected()
        {
            var ceras = new CerasFastSerializer();
            var graph = MakeBig(64, 1024); // comfortably more than 64 KB written

            // Larger than the 0x4000 threshold that decides a buffer may go back to the pool, and
            // far too small for the payload: the growth path has to replace it.
            // Deliberately not a power of two, and above the 0x4000 threshold that decides a buffer
            // may go back to the pool. A power-of-two length happens to be a valid pool bucket and is
            // quietly accepted, which hides the problem; anything else is not a bucket the pool ever
            // handed out, and ArrayPool.Return rejects it.
            var callerBuffer = new byte[20_000];
            int originalLength = callerBuffer.Length;
            int offset = 0;
            int written = ceras.Serialize(graph, ref callerBuffer, offset);

            Assert.True(written > 0);
            Assert.True(callerBuffer.Length > originalLength,
                $"expected the caller's 20 KB buffer to be replaced, still {callerBuffer.Length} B, wrote {written} B");
            // `callerBuffer` now points at the grown array, which is the documented behaviour of the
            // ref overload: the caller's variable is replaced when the payload outgrows it.
            Holder clone = default!;
            int readOffset = 0;
            ceras.Deserialize(ref clone, callerBuffer, ref readOffset);
            Assert.Equal(graph.Blobs.Count, clone.Blobs.Count);
            Assert.Equal(graph.Blobs[0].Data[0], clone.Blobs[0].Data[0]);
        }
    }
}
