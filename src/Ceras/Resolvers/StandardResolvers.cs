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

		public ReflectionTypesFormatterResolver(CerasSerializer serializer)
		{
			_serializer = serializer;
		}

		public IFormatter GetFormatter(Type type)
		{
			if (typeof(MemberInfo).IsAssignableFrom(type))
			{
				var memberInfoFormatterType = typeof(MemberInfoFormatter<>).MakeGenericType(type);
				var memberInfoFormatter = Activator.CreateInstance(memberInfoFormatterType, args: _serializer);
				return (IFormatter)memberInfoFormatter;
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
	
	// todo: Only few collections support a 'capacity'-constructor, but List<> and Dictionar<> do! So we should make special version of the collection formatter for them!
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
			IFormatter formatter;
			if (_formatterInstances.TryGetValue(type, out formatter))
				return formatter;

			//
			// Array?
			//
			if (type.IsArray)
			{
				var itemType = type.GetElementType();

				var formatterType = typeof(ArrayFormatter<>).MakeGenericType(itemType);
				formatter = (IFormatter)Activator.CreateInstance(formatterType, _serializer);

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
				var itemType = closedCollection.GetGenericArguments()[0];

				var formatterType = typeof(CollectionFormatter<,>).MakeGenericType(type, itemType);
				formatter = (IFormatter)Activator.CreateInstance(formatterType, _serializer);

				_formatterInstances[type] = formatter;
				return formatter;
			}

			return null;
		}
	}
}