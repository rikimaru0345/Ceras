namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;

	sealed class ByteArrayFormatter : IFormatter<byte[]>
	{
		readonly uint _maxSize;

		public ByteArrayFormatter(CerasSerializer ceras)
		{
			_maxSize = ceras.Config.Advanced.SizeLimits.MaxByteArraySize;
		}

		public void Serialize(ref byte[] buffer, ref int offset, byte[] ar)
		{
			if (ar == null)
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, -1, 1);
				return;
			}

			var len = ar.Length;

			// Ensure we have enough space for the worst-case VarInt plus the byte array itself
			SerializerBinary.EnsureCapacity(ref buffer, offset, 5 + len);

			// Write the length, no need to check the capacity (we did that here)
			SerializerBinary.WriteUInt32BiasNoCheck(ref buffer, ref offset, len, 1);

			// Blit the array
			System.Array.Copy(ar, 0, buffer, offset, len);
			offset += len;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref byte[] ar)
		{
			int length = SerializerBinary.ReadUInt32Bias(buffer, ref offset, 1);

			if (length == -1)
			{
				ar = null;
				return;
			}

			if (length > _maxSize)
				throw new InvalidOperationException($"The data contains a byte-array of size '{length}', which exceeds the allowed limit of '{_maxSize}'");

			if (ar == null || ar.Length != length)
				ar = new byte[length];

			System.Array.Copy(buffer, offset, ar, 0, length);
			offset += length;
		}
	}

	
	sealed class IntArrayFormatter : IFormatter<int[]>
	{
		readonly uint _maxSize;

		public IntArrayFormatter(CerasSerializer ceras)
		{
			_maxSize = ceras.Config.Advanced.SizeLimits.MaxArraySize;
		}

		public void Serialize(ref byte[] buffer, ref int offset, int[] ar)
		{
			if (ar == null)
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, -1, 1);
				return;
			}

			var len = ar.Length;

			var byteLen = len * 4;

			// Ensure we have enough space for the worst-case VarInt plus the byte array itself
			SerializerBinary.EnsureCapacity(ref buffer, offset, 5 + byteLen);

			// Write the length, no need to check the capacity (we did that here)
			SerializerBinary.WriteUInt32BiasNoCheck(ref buffer, ref offset, len, 1);

			// Blit the array
			Buffer.BlockCopy(ar, 0, buffer, offset, byteLen);

			offset += byteLen;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref int[] ar)
		{
			int length = SerializerBinary.ReadUInt32Bias(buffer, ref offset, 1);

			if (length == -1)
			{
				ar = null;
				return;
			}

			if (length > _maxSize)
				throw new InvalidOperationException($"The data contains a byte-array of size '{length}', which exceeds the allowed limit of '{_maxSize}'");

			if (ar == null || ar.Length != length)
				ar = new int[length];

			int byteLen = length * 4;

			Buffer.BlockCopy(buffer, offset, ar, 0, byteLen);

			offset += byteLen;
		}
	}

	sealed class ListFormatter<TItem> : IFormatter<List<TItem>>
	{
		IFormatter<TItem> _itemFormatter;
		readonly uint _maxSize;

		public ListFormatter(CerasSerializer serializer)
		{
			var itemType = typeof(TItem);
			_itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatter(itemType);

			// We'll handle instantiation ourselves in order to call the capacity ctor
			CerasSerializer.AddFormatterConstructedType(typeof(List<TItem>));

			_maxSize = serializer.Config.Advanced.SizeLimits.MaxCollectionSize;
		}

		public void Serialize(ref byte[] buffer, ref int offset, List<TItem> value)
		{
			// Write how many items do we have
			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)value.Count);

			// Write each item
			var f = _itemFormatter;
			for (var i = 0; i < value.Count; i++)
			{
				var item = value[i];
				f.Serialize(ref buffer, ref offset, item);
			}
		}

		public void Deserialize(byte[] buffer, ref int offset, ref List<TItem> value)
		{
			// How many items?
			var itemCount = SerializerBinary.ReadUInt32(buffer, ref offset);

			if (itemCount > _maxSize)
				throw new InvalidOperationException($"The data contains a '{typeof(TItem)}'-List with '{itemCount}' elements, which exceeds the allowed limit of '{_maxSize}'");

			if (value == null)
				value = new List<TItem>((int)itemCount);
			else
				value.Clear();

			var f = _itemFormatter;

			for (int i = 0; i < itemCount; i++)
			{
				TItem item = default;
				f.Deserialize(buffer, ref offset, ref item);
				value.Add(item);
			}
		}
	}

	sealed class DictionaryFormatter<TKey, TValue> : IFormatter<Dictionary<TKey, TValue>>
	{
		IFormatter<KeyValuePair<TKey, TValue>> _itemFormatter;
		readonly uint _maxSize;

		public DictionaryFormatter(CerasSerializer serializer)
		{
			var itemType = typeof(KeyValuePair<TKey, TValue>);
			_itemFormatter = (IFormatter<KeyValuePair<TKey, TValue>>)serializer.GetReferenceFormatter(itemType);

			// We'll handle instantiation ourselves in order to call the capacity ctor
			CerasSerializer.AddFormatterConstructedType(typeof(Dictionary<TKey, TValue>));

			_maxSize = serializer.Config.Advanced.SizeLimits.MaxCollectionSize;
		}

		public void Serialize(ref byte[] buffer, ref int offset, Dictionary<TKey, TValue> value)
		{
			// Write how many items do we have
			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)value.Count);

			// Write each item
			var f = _itemFormatter;
			foreach (var kvp in value)
				f.Serialize(ref buffer, ref offset, kvp);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Dictionary<TKey, TValue> value)
		{
			// How many items?
			var itemCount = SerializerBinary.ReadUInt32(buffer, ref offset);

			if (itemCount > _maxSize)
				throw new InvalidOperationException($"The data contains a '{typeof(TKey)} {typeof(TValue)}'-Dictionary with '{itemCount}' elements, which exceeds the allowed limit of '{_maxSize}'");

			if (value == null)
				value = new Dictionary<TKey, TValue>((int)itemCount);
			else
				value.Clear();

			var f = _itemFormatter;

			for (int i = 0; i < itemCount; i++)
			{
				KeyValuePair<TKey, TValue> item = default;
				f.Deserialize(buffer, ref offset, ref item);
				value.Add(item.Key, item.Value);
			}
		}
	}
}