namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;
	using System.Linq.Expressions;
	using System.Reflection;
	using Helpers;

	// todo: Can we optimize the call to IFormater<TItem> ? We cache it into a local already, but but can we do better?

	// Simple formatter for Arrays.
	// Writes elements one by one, not very fast, but ReinterpretArrayFormatter gets used whenever possible.
	public sealed class ArrayFormatter<TItem> : IFormatter<TItem[]>
	{
		readonly IFormatter<TItem> _itemFormatter;
		readonly uint _maxSize;

		public ArrayFormatter(CerasSerializer serializer)
		{
			var itemType = typeof(TItem);
			_itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatter(itemType);

			_maxSize = serializer.Config.Advanced.SizeLimits.MaxArraySize;
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

			if (length > _maxSize)
				throw new InvalidOperationException($"The data contains a '{typeof(TItem)}'-array of size '{length}', which exceeds the allowed limit of '{_maxSize}'");

			if (ar == null || ar.Length != length)
				ar = new TItem[length];

			var f = _itemFormatter; // Cache into local to prevent ram fetches
			for (int i = 0; i < length; i++)
				f.Deserialize(buffer, ref offset, ref ar[i]);
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