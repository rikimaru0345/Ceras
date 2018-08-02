namespace Ceras.Resolvers
{
	using System;
	using System.Collections.Generic;
	using System.Reflection;
	using Formatters;
	using Helpers;

	class ReflectionTypesFormatterResolver : IFormatterResolver
	{
		readonly CerasSerializer _serializer;
		readonly Dictionary<Type, IFormatter> _memberInfoFormatters = new Dictionary<Type, IFormatter>();

		public ReflectionTypesFormatterResolver(CerasSerializer serializer)
		{
			_serializer = serializer;
		}

		public IFormatter GetFormatter(Type type)
		{
			if (typeof(MemberInfo).IsAssignableFrom(type))
			{
				IFormatter memberInfoFormatter;
				if (!_memberInfoFormatters.TryGetValue(type, out memberInfoFormatter))
				{
					var formatterType = typeof(MemberInfoFormatter<>).MakeGenericType(type);
					memberInfoFormatter = (IFormatter)Activator.CreateInstance(formatterType, _serializer);
					_memberInfoFormatters[type] = memberInfoFormatter;
				}

				return memberInfoFormatter;
			}

			return null;
		}
	}

	class KeyValuePairFormatterResolver : IFormatterResolver
	{
		readonly CerasSerializer _serializer;

		public KeyValuePairFormatterResolver(CerasSerializer serializer)
		{
			_serializer = serializer;
		}

		public IFormatter GetFormatter(Type type)
		{
			// KeyValuePair
			var closedKeyValuePair = ReflectionHelper.FindClosedType(type, typeof(KeyValuePair<,>));
			if (closedKeyValuePair != null)
			{
				var genericArgs = closedKeyValuePair.GetGenericArguments();
				var tKey = genericArgs[0];
				var tVal = genericArgs[1];

				var formatterType = typeof(KeyValuePairFormatter<,>).MakeGenericType(tKey, tVal);

				return (IFormatter)Activator.CreateInstance(formatterType, _serializer);
			}

			return null;
		}
	}
	
	class CollectionFormatterResolver : IFormatterResolver
	{
		readonly CerasSerializer _serializer;
		Dictionary<Type, IFormatter> _formatterInstances = new Dictionary<Type, IFormatter>();

		public CollectionFormatterResolver(CerasSerializer serializer)
		{
			_serializer = serializer;
		}

		public IFormatter GetFormatter(Type type)
		{
			//
			// Do we already have an array or collection formatter?
			//
			IFormatter existingInstance;
			if (_formatterInstances.TryGetValue(type, out existingInstance))
				return existingInstance;

			//
			// Array?
			//
			if (type.IsArray)
			{
				var itemType = type.GetElementType();

				var formatterType = typeof(ArrayFormatter<>).MakeGenericType(itemType);

				existingInstance = (IFormatter)Activator.CreateInstance(formatterType, _serializer);
				_formatterInstances[type] = existingInstance;
				return existingInstance;
			}


			//
			// Collection?
			//
			// If it implements ICollection, we can serialize it!
			// We need to know what type item the collection contains
			var closedCollection = ReflectionHelper.FindClosedType(type, typeof(ICollection<>));

			// If the type really implements some kind of ICollection, we can create a CollectionFormatter for it
			if (closedCollection != null)
			{
				var itemType = closedCollection.GetGenericArguments()[0];

				var formatterType = typeof(CollectionFormatter<,>).MakeGenericType(type, itemType);

				existingInstance = (IFormatter)Activator.CreateInstance(formatterType, _serializer);
				_formatterInstances[type] = existingInstance;
				return existingInstance;
			}

			return null;
		}
	}
}