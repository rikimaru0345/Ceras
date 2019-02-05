namespace Ceras.Resolvers
{
	using Formatters;
	using Helpers;
	using System;
	using System.Collections.Generic;
	using System.Reflection;

	// todo: Use .IsSubclassOf wherever possible (it's possible when we're only checking for base-type and not interfaces)
	// todo: Ensure we never forget to check .IsGenericType (otherwise we'll get things like issue #24)
	// todo: Maybe implement capacity constructors based on checking for the a ctor that has a 'int capacity' parameter instead of special handling for each concrete type.
	// todo: .. but then we *definitely* need a setting! Because even if the chance is extremely small, capacity might not actually mean what we're assuming!

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

			if (typeof(MulticastDelegate).IsAssignableFrom(type))
			{
				if (_serializer.Config.Advanced.DelegateSerialization == DelegateSerializationMode.Off)
					throw new InvalidOperationException($"The type '{type.FullName}' can not be serialized because it is a delegate; and 'config.Advanced.DelegateSerialization' is turned off.");

				// Every delegate type is created by the formatter, there can't be any exceptions (unless you do some really dangerous stuff)
				CerasSerializer.AddFormatterConstructedType(type);

				var formatterType = typeof(DelegateFormatter<>).MakeGenericType(type);
				var formatter = Activator.CreateInstance(formatterType, args: _serializer);
				return (IFormatter)formatter;
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
			IFormatter formatter;
			if (_formatterInstances.TryGetValue(type, out formatter))
				return formatter;

			//
			// Array?
			//
			if (type.IsArray)
			{
				var itemType = type.GetElementType();

				if (itemType == typeof(byte))
				{
					formatter = new ByteArrayFormatter(_serializer);
					_formatterInstances[type] = formatter;
					return formatter;
				}

				if (itemType == typeof(int))
				{
					formatter = new IntArrayFormatter(_serializer);
					_formatterInstances[type] = formatter;
					return formatter;
				}

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


				// Check for specific types first for which we have special implementations
				bool isGenericType = type.IsGenericType;
				
				if (isGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
				{
					var listFormatterType = typeof(ListFormatter<>).MakeGenericType(itemType);
					formatter = (IFormatter)Activator.CreateInstance(listFormatterType, _serializer);

					_formatterInstances[type] = formatter;
					return formatter;
				}

				if (isGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
				{
					// itemType is KeyValuePair<,> so we need to deconstruct it
					var kvpTypes = itemType.GetGenericArguments();

					var listFormatterType = typeof(DictionaryFormatter<,>).MakeGenericType(kvpTypes);
					formatter = (IFormatter)Activator.CreateInstance(listFormatterType, _serializer);

					_formatterInstances[type] = formatter;
					return formatter;
				}

				// Use the general case collection formatter
				var formatterType = typeof(CollectionFormatter<,>).MakeGenericType(type, itemType);
				formatter = (IFormatter)Activator.CreateInstance(formatterType, _serializer);

				_formatterInstances[type] = formatter;
				return formatter;
			}

			return null;
		}
	}
}