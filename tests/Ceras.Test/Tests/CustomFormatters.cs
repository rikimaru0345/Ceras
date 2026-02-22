using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Test
{
    using System.Drawing;
    using Formatters;
    using Xunit;
    using System.Buffers;

    public class CustomFormatters : TestBase
    {
        [Fact]
        void KnownColorFormatter()
        {
            SerializerConfig config = new SerializerConfig();

            config.ConfigType<Color>()
                  .CustomFormatter = new ColorFormatter();
            
            var colors = new Color[]
            {
                Color.Azure,
                Color.FromArgb(255, 50, 150, 10),
                Color.FromArgb(255, 255, 255, 255),
                Color.White,
            };
            
            var ceras = new CerasSerializer(config);
            var clone = ceras.Deserialize<Color[]>(ceras.Serialize(colors));

            for (int i = 0; i < colors.Length; i++)
            {
                Assert.True(colors[i] == clone[i]);
                Assert.True(colors[i].Equals(clone[i]));
            }
        }

    }
    
    // A formatter that keeps the names (and thus "identity") of the colors it serializes
    class ColorFormatter : IFormatter<Color>
    {
        public void Serialize(IBufferWriter<byte> writer, Color value)
        {
            bool isKnown = value.IsKnownColor;
            SerializerBinary.WriteByte(writer, isKnown ? (byte)1 : (byte)0);

            if (isKnown)
            {
                int knownColor = (int)value.ToKnownColor();
                SerializerBinary.WriteInt32Fixed(writer, knownColor);
            }
            else
            {
                SerializerBinary.WriteInt32Fixed(writer, value.ToArgb());
            }

        }

        public void Deserialize(ref SequenceReader<byte> reader, ref Color value)
        {
            bool isKnownColor = SerializerBinary.ReadByte(ref reader) != 0;

            if (isKnownColor)
            {
                int knownColor = SerializerBinary.ReadInt32Fixed(ref reader);
                value = Color.FromKnownColor((KnownColor)knownColor);
            }
            else
            {
                var argb = SerializerBinary.ReadInt32Fixed(ref reader);
                value = Color.FromArgb(argb);
            }
        }
    }

}
