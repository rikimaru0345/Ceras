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

	    T Roundtrip<T>(T value)
        {
            return _s.Deserialize<T>(_s.Serialize(value));
        }

        [Fact]
        public void ByteArrayRtt()
        {            
            var nullBytes = Roundtrip((byte[])null);
            Assert.Equal((byte[])null, nullBytes);
            
            var zeroBytes = Roundtrip(new byte[0]);
            Assert.Equal(zeroBytes, new byte[0]);

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

            var zeroBytes = Roundtrip(new decimal[0]);
            Assert.Equal(zeroBytes, new decimal[0]);

            var r = new Random(DateTime.Now.GetHashCode());
            var decimalData = new decimal[r.Next(100, 200)];
            for(var i = 0; i < decimalData.Length; ++i)
                decimalData[i] = (decimal)r.NextDouble();

            var singleBytes = Roundtrip(decimalData);
            Assert.Equal(decimalData, singleBytes);
        }
    }
}
