using System;

namespace Ceras.Formatters
{
	using System.Drawing;
#if NETFRAMEWORK
	using System.Drawing.Imaging;
	using System.IO;
#endif

	// Color must be serialized by its argb value
	// We don't want to add nonsense like IsKnownColor here (that's what custom formatters are for, see unit tests)
	class ColorFormatter : IFormatter<Color>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Color value)
		{
			SerializerBinary.WriteInt32Fixed(ref buffer, ref offset, value.ToArgb());
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Color value)
		{
			var argb = SerializerBinary.ReadInt32Fixed(buffer, ref offset);
			value = Color.FromArgb(argb);
		}
	}

#if NETFRAMEWORK

	class BitmapFormatter : IFormatter<Bitmap>
	{
		[ThreadStatic]
		static MemoryStream _sharedMemoryStream;

		CerasSerializer _ceras; // set by ceras dependency injection

		BitmapMode BitmapMode => _ceras.Config.Advanced.BitmapMode;

		public BitmapFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(Bitmap));
		}

		public void Serialize(ref byte[] buffer, ref int offset, Bitmap img)
		{
			// Let the image serialize itself to the memory stream
			// Its unfortunate that there's only a stream-based api...
			// The alternative would be manually locking the bits.
			// That would be easy, but we'd potentially lose some information (animation frames etc?)

			// Prepare buffer stream
			if (_sharedMemoryStream == null)
				_sharedMemoryStream = new MemoryStream(200 * 1024);

			var stream = _sharedMemoryStream;

			// Encode image into stream
			stream.Position = 0;
			var format = BitmapModeToImgFormat(BitmapMode);
			img.Save(stream, format);

			long sizeLong = stream.Length;
			if (sizeLong > int.MaxValue)
				throw new InvalidOperationException("image too large");
			int size = (int)sizeLong;

			stream.Position = 0;
			var streamBuffer = stream.GetBuffer();

			// Write Size
			SerializerBinary.WriteUInt32Fixed(ref buffer, ref offset, (uint)size);

			// Write stream data to serialization buffer
			if (size > 0)
			{
				SerializerBinary.EnsureCapacity(ref buffer, offset, size);
				SerializerBinary.FastCopy(streamBuffer, 0, buffer, (uint)offset, (uint)size);
			}

			offset += size;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Bitmap img)
		{
			// Read data size
			int size = (int)SerializerBinary.ReadUInt32Fixed(buffer, ref offset);

			// Prepare memory stream
			if (_sharedMemoryStream == null)
				_sharedMemoryStream = new MemoryStream(size);
			else if (_sharedMemoryStream.Capacity < size)
				_sharedMemoryStream.Capacity = size;

			var stream = _sharedMemoryStream;


			if (size < 0)
				throw new InvalidOperationException($"Invalid bitmap size: {size} bytes");
			else if (size == 0)
			{
				img = null;
				return;
			}

			// Copy bitmap data into the stream-buffer
			stream.SetLength(size);
			stream.Position = 0;
			var streamBuffer = stream.GetBuffer();
			SerializerBinary.FastCopy(buffer, (uint)offset, streamBuffer, 0, (uint)size);

			stream.Position = 0;
			img = new Bitmap(stream);

			offset += size;
		}

		static ImageFormat BitmapModeToImgFormat(BitmapMode mode)
		{
			if (mode == BitmapMode.DontSerializeBitmaps)
				throw new InvalidOperationException("You need to set 'config.Advanced.BitmapMode' to any setting other than 'DontSerializeBitmaps'. Otherwise you need to skip data-members on your classes/structs that contain Image/Bitmap, or serialize them yourself using your own IFormatter<> implementation.");

			if (mode == BitmapMode.SaveAsBmp)
				return ImageFormat.Bmp;
			else if (mode == BitmapMode.SaveAsJpg)
				return ImageFormat.Jpeg;
			else if (mode == BitmapMode.SaveAsPng)
				return ImageFormat.Png;

			throw new ArgumentOutOfRangeException();
		}
	}

#endif
}
