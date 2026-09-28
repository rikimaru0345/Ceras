using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
    internal static partial class HashHelpers
    {
        internal static readonly int[] SizeOneIntArray = new int[1];

        /// <summary>
        /// CRC-32/ISO-HDLC (ITU-T V.42 / IEEE 802.3, reflected polynomial 0xEDB88320) - the zlib,
        /// PKZIP and gzip CRC-32 - computed with <see cref="Crc32"/>.
        /// <para>
        /// This is deliberately the *ISO-HDLC* CRC and not CRC-32C (Castagnoli). The two generally
        /// differ and are not interchangeable, so substituting one for the other would invalidate
        /// essentially every save and every checked-in fixture (see <c>Format/LAYOUT.md</c>).
        /// </para>
        /// <para>
        /// Why it is not simply one instruction: x86's SSE4.2 <c>crc32</c> instruction computes
        /// CRC-32C only, so it cannot produce this polynomial at all, and <see cref="Crc32"/> folds
        /// the input with carry-less multiply instead - PCLMULQDQ on x86/x64. On ARM the hardware
        /// <c>Arm.Crc32</c> instruction *is* ISO-HDLC, so short inputs go straight through it, and
        /// the vectorized path (which uses the AES polynomial-multiply instructions there) only
        /// takes over for long inputs. The last-resort fallback is a 256-entry table walk generated
        /// from the same reflected polynomial - the algorithm that was here before - so a machine
        /// with neither intrinsic lands on the old performance rather than on something worse.
        /// Every one of those paths returns the same value; this is a speed change only.
        /// </para>
        /// <para>
        /// Uses the static one-shot entry point, which creates no hash object, so this method
        /// allocates nothing in any build configuration.
        /// </para>
        /// </summary>
        internal static uint CalculateCrc32(ReadOnlySpan<byte> data)
        {
            return Crc32.HashToUInt32(data);
        }

        /// <summary>
        /// CRC32 over <paramref name="data"/> where the <paramref name="zeroLength"/> bytes starting at
        /// <paramref name="zeroStart"/> are fed to the hash as zero. Used by the robust header, whose
        /// checksum is stored in the middle of the buffer and is therefore zero during computation.
        /// <para>
        /// The same <see cref="Crc32"/> - and so the same polynomial, the same value and the same
        /// stored bytes as <see cref="CalculateCrc32"/> - is used here. The streaming API is required
        /// rather than a one-shot call, because the stored checksum is only zero on the writer's
        /// side: on load the buffer still holds the saved value, and the parameter is read-only, so
        /// the zeroed range has to be fed as its own segment instead of being masked out of a single
        /// span. The run of zeros comes from a stack buffer in bounded chunks, so a large
        /// <paramref name="zeroLength"/> never materializes a heap buffer for the zeroes.
        /// </para>
        /// <para>
        /// Allocation, measured with <c>GC.GetAllocatedBytesForCurrentThread</c>: <see cref="Crc32"/>
        /// is a sealed <em>class</em>, so <c>new Crc32()</c> is a 24-byte heap object in principle,
        /// but the JIT's object stack allocation elides it once this method is optimized. In Release
        /// that is 0 B/op - verified with tiered compilation on, disabled, and with PGO disabled, and
        /// with none of the first 200 calls from a cold process allocating. In a minopts (Debug)
        /// build the elision does not happen and the object costs a real 24 B/op.
        /// <see cref="Crc32"/> is not <see cref="IDisposable"/>, so there is nothing to dispose and
        /// <c>using</c> is not available.
        /// </para>
        /// </summary>
        internal static uint CalculateCrc32WithZeroedRange(ReadOnlySpan<byte> data, int zeroStart, int zeroLength)
        {
            var crc = new Crc32();
            if (zeroStart > 0)
                crc.Append(data.Slice(0, zeroStart));
            if (zeroLength > 0)
            {
                Span<byte> zeros = stackalloc byte[Math.Min(zeroLength, 256)];
                zeros.Clear();
                int remaining = zeroLength;
                while (remaining > 0)
                {
                    int chunk = Math.Min(remaining, zeros.Length);
                    crc.Append(zeros.Slice(0, chunk));
                    remaining -= chunk;
                }
            }
            if (zeroStart + zeroLength < data.Length)
                crc.Append(data.Slice(zeroStart + zeroLength));
            return crc.GetCurrentHashAsUInt32();
        }

        internal static int PowerOf2(int v)
        {
            if ((v & (v - 1)) == 0) return v;
            int i = 2;
            while (i < v) i <<= 1;
            return i;
        }
    }
}
