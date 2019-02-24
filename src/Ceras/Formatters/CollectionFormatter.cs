namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;
	using System.Linq.Expressions;
	using System.Reflection;
	using Helpers;

	// todo: at the moment we refer to the item formatter through an interface field. Would we get a performance improvement if we'd compile a dedicated formatter that has the itemFormatter built-in as a constant instead? (just like the DynamicFormatter already does)? Would we save performance if we'd cache the itemFormatter into a local variable before entering the loops?

	// Idea from a user: at the moment we obtain a generic formatter for the item and then write the entries one after another. But would it be possible somehow (if the type is sealed or value-type) to write the type only once at the start, establishing something like "now X objects of this exact type Y will follow".
	// -> No! We'd still need a reference formatter to ensure references are maintained. And at that point we have saved absolutely nothing because that check already encodes type IF its needed!
	// Which means that we'd not even save a single byte, because there are no bytes to save. We already do not waste any bytes on encoding "yup same type" because that information is packed into the reference-formatters "serialization mode id". We'd have to either write the ID of an existing object or use one byte for "new object" anyway. And the thing is: This unavoidable byte is already used to also encode that...

	sealed class ArrayFormatter<TItem> : IFormatter<TItem[]>
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


	sealed class CollectionFormatter<TCollection, TItem> : IFormatter<TCollection>
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
					ctor = collectionType.GetConstructor(new Type[] { typeof(int) });
				else if (collectionType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
					ctor = collectionType.GetConstructor(new Type[] { typeof(int) });

				if (ctor != null)
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
			foreach (var item in value)
				f.Serialize(ref buffer, ref offset, item);
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