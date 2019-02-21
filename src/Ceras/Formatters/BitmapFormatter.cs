using System;

namespace Ceras.Formatters
{
#if NETFRAMEWORK
	using System.Drawing;
	using System.Drawing.Imaging;
	using System.IO;

	class BitmapFormatter : IFormatter<Bitmap>
	{
		[ThreadStatic]
		static MemoryStream _sharedMemoryStream;

		CerasSerializer _ceras;

		BitmapMode BitmapMode => _ceras.Config.Advanced.BitmapMode;

		public BitmapFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(Bitmap));
		}

		public void Serialize(ref byte[] buffer, ref int offset, Bitmap img)
		{
			if (_sharedMemoryStream == null)
				_sharedMemoryStream = new MemoryStream(1024 * 1024);
			
			// Let the image serialize itself to the memory stream
			// Its unfortunate that there's only a stream-based api...
			// The alternative would be manually locking the bits.
			// That would be easy, but we'd potentially lose some information (animation frames etc?)

			var mode = BitmapMode;
			var format = BitmapModeToImgFormat(mode);

			_sharedMemoryStream.Position = 0;
			img.Save(_sharedMemoryStream, format);

			long sizeLong = _sharedMemoryStream.Position;
			if (sizeLong > int.MaxValue)
				throw new InvalidOperationException("image too large");
			int size = (int) sizeLong;

			_sharedMemoryStream.Position = 0;
			var memoryStreamBuffer = _sharedMemoryStream.GetBuffer();

			// Write Size
			SerializerBinary.WriteUInt32Fixed(ref buffer, ref offset, (uint)size);

			// Write data into serialization buffer
			SerializerBinary.EnsureCapacity(ref buffer, offset, size);
			SerializerBinary.FastCopy(memoryStreamBuffer, 0, buffer, offset, size);
			offset += size;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Bitmap img)
		{
			if (_sharedMemoryStream == null)
				_sharedMemoryStream = new MemoryStream(1024 * 1024);
			
			// Read data size
			int size = (int) SerializerBinary.ReadUInt32Fixed(buffer, ref offset);

			// Copy data into stream
			if(_sharedMemoryStream.Capacity < size)
				_sharedMemoryStream.Capacity = size;

			var memoryStreamBuffer = _sharedMemoryStream.GetBuffer();

			SerializerBinary.FastCopy(buffer, offset, memoryStreamBuffer, 0, size);
			
			// Now we can load the image back from the stream
			_sharedMemoryStream.Position = 0;
			img = new Bitmap(_sharedMemoryStream);

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
