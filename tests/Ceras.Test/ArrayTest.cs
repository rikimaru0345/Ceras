using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Ceras.Test
{    
    public class ArrayTest
    {
        CerasSerializer _s = new CerasSerializer();

        private T Roundtrip<T>(T value)
        {
            return _s.Deserialize<T>(_s.Serialize(value));
        }

        [Fact]
        public void ByteArrayRtt()
        {            
            var nullBytes = Roundtrip((byte[])null);
            Assert.Equal((byte[])null, nullBytes);
            
            var zeroBytes = Roundtrip(Array.Empty<byte>());
            Assert.Equal(zeroBytes, Array.Empty<byte>());

            var r = new Random(DateTime.Now.GetHashCode());
            var bytesData = new byte[r.Next(100, 200)];
            r.NextBytes(bytesData);
           
            var singleBytes = Roundtrip(bytesData);
            Assert.Equal(bytesData, singleBytes);
        }

        [Fact]
        public void StructArrayRtt()
        {
            var nullBytes = Roundtrip((decimal[])null);
            Assert.Equal((decimal[])null, nullBytes);

            var zeroBytes = Roundtrip(Array.Empty<decimal>());
            Assert.Equal(zeroBytes, Array.Empty<decimal>());

            var r = new Random(DateTime.Now.GetHashCode());
            var decimalData = new decimal[r.Next(100, 200)];
            for(var i = 0; i < decimalData.Length; ++i)
                decimalData[i] = (decimal)r.NextDouble();

            var singleBytes = Roundtrip(decimalData);
            Assert.Equal(decimalData, singleBytes);
        }
    }
}
