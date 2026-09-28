using System.Buffers;

namespace Ceras.Test
{
    /// <summary>
    /// Reaches the shared pool directly, to pin what it does with an array it never rented. The
    /// growth path must never do this on a caller's behalf, and the only way to test that is to know
    /// what it would have done.
    /// </summary>
    static class ArrayPoolAdoption
    {
        public static void Adopt(byte[] foreign) => ArrayPool<byte>.Shared.Return(foreign);
    }
}
