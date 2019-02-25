using System;
using System.Collections.Generic;
using System.Text;

namespace Ceras.ImmutableCollections
{
	using System.Collections.Immutable;
	using Formatters;
	using Resolvers;

	public sealed class ImmutableCollectionFormatterResolver : IFormatterResolver
	{
		readonly Dictionary<Type, Type> _typeToFormatterType = new Dictionary<Type, Type>
		{
			[typeof(ImmutableArray<>)] = typeof(ImmutableArrayFormatter<>),
			[typeof(ImmutableDictionary<,>)] = typeof(ImmutableDictionaryFormatter<,>),
			[typeof(ImmutableHashSet<>)] = typeof(ImmutableHashSetFormatter<>),
			[typeof(ImmutableList<>)] = typeof(ImmutableListFormatter<>),
			[typeof(ImmutableQueue<>)] = typeof(ImmutableQueueFormatter<>),
			[typeof(ImmutableSortedDictionary<,>)] = typeof(ImmutableSortedDictionaryFormatter<,>),
			[typeof(ImmutableSortedSet<>)] = typeof(ImmutableSortedSetFormatter<>),
			[typeof(ImmutableStack<>)] = typeof(ImmutableStackFormatter<>),
		};

		public IFormatter GetFormatter(Type type)
		{
			if(type.Assembly != typeof(System.Collections.Immutable.ImmutableArray).Assembly)
				return null;

			if(type.IsGenericType)
				if (_typeToFormatterType.TryGetValue(type.GetGenericTypeDefinition(), out var formatterType))
				{
					var genericArgs = type.GetGenericArguments();
					formatterType = formatterType.MakeGenericType(genericArgs);
					return (IFormatter)Activator.CreateInstance(formatterType);
				}

			return null;
		}

		public static void ApplyToConfig(SerializerConfig config)
		{
			var immutableResolver = new ImmutableCollectionFormatterResolver();
			config.OnResolveFormatter.Add((c, t) => immutableResolver.GetFormatter(t));
		}
	}

	public static class CerasImmutableExtension
	{
		public static void UseImmutableFormatters(this SerializerConfig config)
		{
			ImmutableCollectionFormatterResolver.ApplyToConfig(config);
		}
	}
}
