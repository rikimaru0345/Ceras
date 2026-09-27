using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Ceras.Test
{
    // Promoted from samples/LiveTesting/Program.cs :: InterfaceFormatterTest.
    //
    // A formatter can be asked for directly from the serializer (instead of going through
    // Serialize/Deserialize) and then driven by hand against a caller-owned buffer and
    // SequenceReader. The generic GetFormatter and GetSpecificFormatter(Type) accessors are
    // internal (this assembly reaches them through InternalsVisibleTo), so this pins the behavior
    // for the serializer's own friend assemblies rather than a public surface: a formatter resolved
    // for a declared interface type has to work and has to consume exactly what it wrote.
    public class DirectFormatterApiTests
    {
        [Fact]
        public void GetFormatter_ForAnInterface_RoundTripsThroughASequenceReader()
        {
            var ceras = new CerasFastSerializer();

            // A declared interface type, resolved as a reference formatter.
            var intListFormatter = ceras.GetFormatter<IList<int>>();
            Assert.NotNull(intListFormatter);

            List<int> list = new List<int> { 1, 2, 3, 4 };

            byte[] buffer = new byte[200];
            var writer = new CerasSerializer.FixedArrayBufferWriter(buffer, 0);
            intListFormatter.Serialize(writer, list);

            // The payload fit into the buffer we handed over, so it was not swapped out.
            Assert.Same(buffer, writer.Buffer);
            Assert.True(writer.Offset > 0, "nothing was written");

            var seq = new ReadOnlySequence<byte>(buffer, 0, writer.Offset);
            var reader = new SequenceReader<byte>(seq);
            IList<int> clonedList = null;
            intListFormatter.Deserialize(ref reader, ref clonedList);

            // Deserializing into an IList variable is no problem: the concrete type is restored.
            Assert.NotNull(clonedList);
            Assert.IsType<List<int>>(clonedList);
            Assert.Equal(new[] { 1, 2, 3, 4 }, clonedList.ToArray());

            // ... and the reader consumed the payload it just wrote, byte for byte.
            Assert.Equal(0, reader.Remaining);
        }
    }
}
