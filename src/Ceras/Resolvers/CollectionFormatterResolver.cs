namespace Ceras.Resolvers
{
	using System;
	using System.Collections.Generic;
	using Formatters;
	using Helpers;

	class CollectionFormatterResolver : IFormatterResolver
	{
		readonly CerasSerializer _ceras;
		Dictionary<Type, IFormatter> _formatterInstances = new Dictionary<Type, IFormatter>();

		public CollectionFormatterResolver(CerasSerializer ceras)
		{
			_ceras = ceras;
		}

		public IFormatter GetFormatter(Type type)
		{
			//
			// Do we already have an array or collection formatter?
			//
			IFormatter formatter;
			if (_formatterInstances.TryGetValue(type, out formatter))
				return formatter;

			//
			// Array?
			//
			if (type.IsArray)
			{
				var itemType = type.GetElementType();

				// reinterpret if allowed
				if (itemType)
				{

				}

				var formatterType = typeof(ArrayFormatter<>).MakeGenericType(itemType);
				formatter = (IFormatter)Activator.CreateInstance(formatterType, _ceras);

				_formatterInstances[type] = formatter;
				return formatter;
			}


			//
			// Collection?
			//
			// If it implements ICollection, we can serialize it!
			var closedCollection = ReflectionHelper.FindClosedType(type, typeof(ICollection<>));
			
			// If the type really implements some kind of ICollection, we can create a CollectionFormatter for it
			if (closedCollection != null)
			{
				// Pre-check for readonly.
				//var isReadonly = ReflectionHelper.FindClosedType(type, typeof(IReadOnlyCollection<>)) != null ||
				//				 ReflectionHelper.FindClosedType(type, typeof(IReadOnlyDictionary<,>)) != null ||
				//				 ReflectionHelper.FindClosedType(type, typeof(IReadOnlyList<>)) != null;
				// todo: all collections implement IReadonlyCollection even if they're writeable
				if(isReadonly)
						// If the type is some sort of readonly collection the following formatters will never be able to handle it correctly
					return null;


				var itemType = closedCollection.GetGenericArguments()[0];


				// Check for specific types first for which we have special implementations
				bool isGenericType = type.IsGenericType;

				if (isGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
				{
					var listFormatterType = typeof(ListFormatter<>).MakeGenericType(itemType);
					formatter = (IFormatter)Activator.CreateInstance(listFormatterType, _ceras);

					_formatterInstances[type] = formatter;
					return formatter;
				}

				if (isGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
				{
					// itemType is KeyValuePair<,> so we need to deconstruct it
					var kvpTypes = itemType.GetGenericArguments();

					var listFormatterType = typeof(DictionaryFormatter<,>).MakeGenericType(kvpTypes);
					formatter = (IFormatter)Activator.CreateInstance(listFormatterType, _ceras);

					_formatterInstances[type] = formatter;
					return formatter;
				}

				// Use the general case collection formatter
				var formatterType = typeof(CollectionFormatter<,>).MakeGenericType(type, itemType);
				formatter = (IFormatter)Activator.CreateInstance(formatterType, _ceras);

				_formatterInstances[type] = formatter;
				return formatter;
			}

			return null;
		}
	}
}