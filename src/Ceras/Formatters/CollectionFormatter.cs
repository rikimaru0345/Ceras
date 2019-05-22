namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;
	using System.Linq.Expressions;
	using System.Reflection;
	using Helpers;

	// todo: Can we optimize the call to IFormater<TItem> ? We cache it into a local already, but but can we do better?

	// Simple formatter for Arrays.
	// Writes elements one by one. ReinterpretArrayFormatter is faster and gets used whenever possible.
	public sealed class ArrayFormatter<TItem> : IFormatter<TItem[]>
	{
		readonly IFormatter<TItem> _itemFormatter;
		readonly uint _maxCount;

		public ArrayFormatter(CerasSerializer serializer, uint maxCount)
		{
			_maxCount = maxCount;
			var itemType = typeof(TItem);
			_itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatter(itemType);
		}

		public void Serialize(ref byte[] buffer, ref int offset, TItem[] ar)
		{
			if (ar == null)
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, -1, 1);
				return;
			}

			SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, ar.Length, 1);

			var f = _itemFormatter; // Cache into local to prevent ram fetches
			for (int i = 0; i < ar.Length; i++)
				f.Serialize(ref buffer, ref offset, ar[i]);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref TItem[] ar)
		{
			int length = SerializerBinary.ReadUInt32Bias(buffer, ref offset, 1);

			if (length == -1)
			{
				ar = null;
				return;
			}

			if (length > _maxCount)
				throw new InvalidOperationException($"The data contains a '{typeof(TItem)}'-array of size '{length}', which exceeds the allowed limit of '{_maxCount}'");

			if (ar == null || ar.Length != length)
				ar = new TItem[length];

			var f = _itemFormatter; // Cache into local to prevent ram fetches
			for (int i = 0; i < length; i++)
				f.Deserialize(buffer, ref offset, ref ar[i]);
		}
	}

	public sealed class MultiDimensionalArrayFormatter<TItem> :
		IFormatter<Array>,
		IFormatter<TItem[,]>, // 2D
		IFormatter<TItem[,,]>, // 3D
		IFormatter<TItem[,,,]>, // 4D
		IFormatter<TItem[,,,,]>, // 5D
		IFormatter<TItem[,,,,,]> // 6D
	{
		readonly uint _maxCount;
		readonly IFormatter<TItem> _itemFormatter;

		public MultiDimensionalArrayFormatter(CerasSerializer serializer, uint maxCount)
		{
			_maxCount = maxCount;
			var itemType = typeof(TItem);
			_itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatter(itemType);
		}



		static void ReadLastDimension2D(byte[] buffer, ref int offset, IFormatter<TItem> formatter, TItem[,] array, int[] index, int max)
		{
			for (int i = 0; i < max; i++)
				formatter.Deserialize(buffer, ref offset, ref array[index[0], i]);
		}
		static void ReadLastDimension3D(byte[] buffer, ref int offset, IFormatter<TItem> formatter, TItem[,,] array, int[] index, int max)
		{
			for (int i = 0; i < max; i++)
				formatter.Deserialize(buffer, ref offset, ref array[index[0], index[1], i]);
		}
		static void ReadLastDimension4D(byte[] buffer, ref int offset, IFormatter<TItem> formatter, TItem[,,,] array, int[] index, int max)
		{
			for (int i = 0; i < max; i++)
				formatter.Deserialize(buffer, ref offset, ref array[index[0], index[1], index[2], i]);
		}
		static void ReadLastDimension5D(byte[] buffer, ref int offset, IFormatter<TItem> formatter, TItem[,,,,] array, int[] index, int max)
		{
			for (int i = 0; i < max; i++)
				formatter.Deserialize(buffer, ref offset, ref array[index[0], index[1], index[2], index[3], i]);
		}
		static void ReadLastDimension6D(byte[] buffer, ref int offset, IFormatter<TItem> formatter, TItem[,,,,,] array, int[] index, int max)
		{
			for (int i = 0; i < max; i++)
				formatter.Deserialize(buffer, ref offset, ref array[index[0], index[1], index[2], index[2], index[4], i]);
		}



		public void Serialize(ref byte[] buffer, ref int offset, Array baseAr)
		{
			// Dimensions
			int dimensions = baseAr.Rank;
			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)dimensions);

			// Dimension sizes
			for (int d = 0; d < dimensions; d++)
			{
				var size = baseAr.GetLength(d);
				SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)size);
			}

			var f = _itemFormatter;
			foreach (var item in baseAr)
				f.Serialize(ref buffer, ref offset, (TItem)item);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Array baseAr)
		{
			// Dimensions
			int dimensions = (int)SerializerBinary.ReadUInt32(buffer, ref offset);

			// Dimension sizes
			var dimensionSizes = new int[dimensions];
			for (int d = 0; d < dimensions; d++)
			{
				var size = (int)SerializerBinary.ReadUInt32(buffer, ref offset);
				dimensionSizes[d] = size;
			}

			// Count
			int count = dimensionSizes[0];
			for (int d = 1; d < dimensions; d++)
				count *= dimensionSizes[d];

			if (count > _maxCount)
				throw new InvalidOperationException($"The data describes an array with '{count}' elements, which exceeds the allowed limit of '{_maxCount}'");

			// Create array
			baseAr = Array.CreateInstance(typeof(TItem), dimensionSizes);

			// Read
			var indices = new int[dimensions];
			ReadArrayEntry(buffer, ref offset, _itemFormatter, baseAr, indices, dimensionSizes, 0);
		}

		static void ReadArrayEntry(byte[] buffer, ref int offset, IFormatter<TItem> formatter, Array array, int[] index, int[] dimensionSizes, int depth)
		{
			var max = dimensionSizes[depth];

			if (depth == dimensionSizes.Length - 1)
			{
				// We're at the last dimension, here we actually set the values
				// To minimize the overhead we have an optimized method that handles the last dimension in one go
				switch (dimensionSizes.Length)
				{
				case 2:
					ReadLastDimension2D(buffer, ref offset, formatter, (TItem[,])array, index, max);
					break;
				case 3:
					ReadLastDimension3D(buffer, ref offset, formatter, (TItem[,,])array, index, max);
					break;
				case 4:
					ReadLastDimension4D(buffer, ref offset, formatter, (TItem[,,,])array, index, max);
					break;
				case 5:
					ReadLastDimension5D(buffer, ref offset, formatter, (TItem[,,,,])array, index, max);
					break;
				case 6:
					ReadLastDimension6D(buffer, ref offset, formatter, (TItem[,,,,,])array, index, max);
					break;

				default:
					throw new IndexOutOfRangeException("Array rank must be between 2 and 6");
				}
			}
			else
			{
				// Iterate through the lower dimensions
				index[depth] = 0;
				for (int i = 0; i < max; i++)
				{
					ReadArrayEntry(buffer, ref offset, formatter, array, index, dimensionSizes, depth + 1);
					index[depth]++;
				}
			}
		}


		// 2D
		public void Serialize(ref byte[] buffer, ref int offset, TItem[,] ar)
		{
			Array array = ar;
			Serialize(ref buffer, ref offset, array);
		}
		public void Deserialize(byte[] buffer, ref int offset, ref TItem[,] ar)
		{
			Array array = ar;
			Deserialize(buffer, ref offset, ref array);
			ar = (TItem[,])array;
		}

		// 3D
		public void Serialize(ref byte[] buffer, ref int offset, TItem[,,] ar)
		{
			Array array = ar;
			Serialize(ref buffer, ref offset, array);
		}
		public void Deserialize(byte[] buffer, ref int offset, ref TItem[,,] ar)
		{
			Array array = ar;
			Deserialize(buffer, ref offset, ref array);
			ar = (TItem[,,])array;
		}

		// 4D
		public void Serialize(ref byte[] buffer, ref int offset, TItem[,,,] ar)
		{
			Array array = ar;
			Serialize(ref buffer, ref offset, array);
		}
		public void Deserialize(byte[] buffer, ref int offset, ref TItem[,,,] ar)
		{
			Array array = ar;
			Deserialize(buffer, ref offset, ref array);
			ar = (TItem[,,,])array;
		}
		
		// 5D
		public void Serialize(ref byte[] buffer, ref int offset, TItem[,,,,] ar)
		{
			Array array = ar;
			Serialize(ref buffer, ref offset, array);
		}
		public void Deserialize(byte[] buffer, ref int offset, ref TItem[,,,,] ar)
		{
			Array array = ar;
			Deserialize(buffer, ref offset, ref array);
			ar = (TItem[,,,,])array;
		}

		// 6D
		public void Serialize(ref byte[] buffer, ref int offset, TItem[,,,,,] ar)
		{
			Array array = ar;
			Serialize(ref buffer, ref offset, array);
		}
		public void Deserialize(byte[] buffer, ref int offset, ref TItem[,,,,,] ar)
		{
			Array array = ar;
			Deserialize(buffer, ref offset, ref array);
			ar = (TItem[,,,,,])array;
		}
	}


	// ICollection
	// -> Takes advantage of capacity-constructors
	public class CollectionFormatter<TCollection, TItem> : IFormatter<TCollection>
		where TCollection : ICollection<TItem>
	{
		readonly IFormatter<TItem> _itemFormatter;
		readonly uint _maxSize;
		readonly Func<int, TCollection> _capacityConstructor;

		public CollectionFormatter(CerasSerializer serializer)
		{
			var itemType = typeof(TItem);
			_itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatter(itemType);
			_maxSize = serializer.Config.Advanced.SizeLimits.MaxCollectionSize;

			var collectionType = typeof(TCollection);
			if (collectionType.IsGenericType)
			{
				ConstructorInfo ctor = null;

				if (collectionType.GetGenericTypeDefinition() == typeof(List<>))
					// Special case: List<>
					ctor = collectionType.GetConstructor(new Type[] { typeof(int) });
				else if (collectionType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
					// Special case: Dictionary<,>
					ctor = collectionType.GetConstructor(new Type[] { typeof(int) });

				if (ctor != null && serializer.Config.Advanced.AotMode == AotMode.None)
				{
					var sizeArg = Expression.Parameter(typeof(int));
					_capacityConstructor = Expression.Lambda<Func<int, TCollection>>(Expression.New(ctor, sizeArg), sizeArg).Compile();

					CerasSerializer.AddFormatterConstructedType(collectionType);
				}
			}
		}

		public void Serialize(ref byte[] buffer, ref int offset, TCollection value)
		{
			if (value.IsReadOnly)
				ThrowReadonly(value);

			// Write how many items do we have
			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)value.Count);

			// Write each item
			var f = _itemFormatter;
			// Guarantee no boxing
			IEnumerator<TItem> e = value.GetEnumerator();
			try
			{
				while (e.MoveNext())
				{
					f.Serialize(ref buffer, ref offset, e.Current);
				}
			}
			finally
			{
				e.Dispose();
			}
		}

		public void Deserialize(byte[] buffer, ref int offset, ref TCollection value)
		{
			// How many items?
			var itemCount = SerializerBinary.ReadUInt32(buffer, ref offset);

			if (itemCount > _maxSize)
				throw new InvalidOperationException($"The data contains a '{typeof(TCollection)}' with '{itemCount}' entries, which exceeds the allowed limit of '{_maxSize}'");


			if (value == null)
				value = _capacityConstructor((int)itemCount);
			else
			{
				if (value.Count > 0)
					value.Clear();
			}

			if (value.IsReadOnly)
				ThrowReadonly(value);


			var f = _itemFormatter;

			for (int i = 0; i < itemCount; i++)
			{
				TItem item = default;
				f.Deserialize(buffer, ref offset, ref item);
				value.Add(item);
			}
		}

		static void ThrowReadonly(object collection)
		{
			var type = collection.GetType();
			var name = type.FriendlyName();

			if (type.FullName.Contains("System.Collections.Immutable"))
			{
				throw new InvalidOperationException("To serialize types from the 'System.Collections.Immutable' library, please install 'Ceras.ImmutableCollections' from NuGet. " +
													$"The affect type is '{name}'");
			}

			throw new InvalidOperationException($"To serialize readonly collections you must configure a construction mode for the type '{name}'. (It's pretty easy, take a look at the tutorial or open an issue on GitHub)");
		}
	}
}