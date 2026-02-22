using System;
using System.Buffers;
using System.Drawing;

namespace Ceras.Formatters
{
#if NETFRAMEWORK
    sealed class ColorFormatter : IFormatter<Color>
    {
        public void Serialize(IBufferWriter<byte> writer, Color value) => SerializerBinary.WriteInt32Fixed(writer, value.ToArgb());
        public void Deserialize(ref SequenceReader<byte> reader, ref Color value) => value = Color.FromArgb(SerializerBinary.ReadInt32Fixed(ref reader));
    }

    sealed class BitmapFormatter : IFormatter<Bitmap>
    {
        public void Serialize(IBufferWriter<byte> writer, Bitmap img)
        {
            if (img == null) { SerializerBinary.WriteInt32(writer, -1); return; }
            using (var ms = new System.IO.MemoryStream())
            {
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                byte[] data = ms.ToArray();
                SerializerBinary.WriteInt32(writer, data.Length);
                writer.Write(data);
            }
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref Bitmap value)
        {
            int len = SerializerBinary.ReadInt32(ref reader);
            if (len == -1) { value = null; return; }
            byte[] data = new byte[len];
            reader.TryCopyTo(data);
            reader.Advance(len);
            using (var ms = new System.IO.MemoryStream(data)) value = new Bitmap(ms);
        }
    }
#endif
}
