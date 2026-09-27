using System;
using System.Buffers;
using System.Collections.Generic;
using Ceras.Formatters;
using Xunit;

namespace Ceras.Test
{
    // Promoted from the now-deleted samples/LiveTesting sandbox :: CustomComparerFormatter.
    //
    // A HashSet<T> serialized by the default collection formatter comes back with the default
    // comparer, which silently changes the set's semantics (a content comparer becomes reference
    // equality). A user-written formatter that also writes the comparer fixes that; this pins that
    // the custom formatter is picked up and that the comparer really survives the round trip.
    public class CustomComparerFormatterTests
    {
        [Fact]
        public void CustomFormatter_KeepsTheNonDefaultComparer()
        {
            var custom = new HashSetKeepingComparerFormatter();
            var config = new CerasRawConfig();
            config.OnResolveFormatter.Add((c, t) => t == typeof(HashSet<byte[]>) ? custom : null);
            config.ConfigType<HashSet<byte[]>>().SetFormatter(custom);

            var ceras = new CerasFastSerializer(config);

            // The configured instance is the formatter Ceras uses (not a freshly built built-in one).
            Assert.Same(custom, ceras.GetSpecificFormatter(typeof(HashSet<byte[]>)));

            var set = new HashSet<byte[]>(new ByteArrayContentComparer())
            {
                new byte[] { 1, 2, 3 },
                new byte[] { 4, 5, 6 },
            };

            var clone = ceras.Deserialize<HashSet<byte[]>>(ceras.Serialize(set));

            Assert.NotNull(clone);
            Assert.NotSame(set, clone);
            Assert.IsType<ByteArrayContentComparer>(clone.Comparer);

            // The entries are there ...
            Assert.Equal(2, clone.Count);
            Assert.True(clone.Contains(new byte[] { 1, 2, 3 }), "first entry is missing");
            Assert.True(clone.Contains(new byte[] { 4, 5, 6 }), "second entry is missing");
            Assert.False(clone.Contains(new byte[] { 9 }), "an entry that was never added is present");

            // ... and the restored comparer is the content comparer, not reference equality: adding
            // a value-equal array is still rejected.
            clone.Add(new byte[] { 4, 5, 6 });
            Assert.Equal(2, clone.Count);
        }

        public class ByteArrayContentComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] x, byte[] y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                return x.AsSpan().SequenceEqual(y);
            }

            public int GetHashCode(byte[] data)
            {
                if (data == null) return 0;
                var hash = new HashCode();
                hash.AddBytes(data);
                return hash.ToHashCode();
            }
        }

        public class HashSetKeepingComparerFormatter : FormatterBase<HashSet<byte[]>>
        {
            // Both sub-formatters are set by Ceras' dependency injection (PrepareFormatter assigns
            // them by reflection); the initializers only keep the compiler from reporting a field it
            // cannot see assigned.
            IFormatter<byte[]> _byteArrayFormatter = null!;
            IFormatter<IEqualityComparer<byte[]>> _comparerFormatter = null!;

            public override void Serialize(IBufferWriter<byte> writer, HashSet<byte[]> set)
            {
                // The comparer ...
                _comparerFormatter.Serialize(writer, set.Comparer);
                // ... the number of entries ...
                SerializerBinary.WriteInt32(writer, set.Count);
                // ... and the entries themselves.
                foreach (var array in set)
                    _byteArrayFormatter.Serialize(writer, array);
            }

            public override void Deserialize(ref SequenceReader<byte> reader, ref HashSet<byte[]> set)
            {
                IEqualityComparer<byte[]> comparer = null;
                _comparerFormatter.Deserialize(ref reader, ref comparer);

                set = new HashSet<byte[]>(comparer);

                int count = SerializerBinary.ReadInt32(ref reader);
                for (int i = 0; i < count; i++)
                {
                    byte[] array = null;
                    _byteArrayFormatter.Deserialize(ref reader, ref array);
                    set.Add(array);
                }
            }
        }
    }
}
