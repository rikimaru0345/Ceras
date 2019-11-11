namespace Ceras.Resolvers
{
	using Formatters;
	using Helpers;
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// Creates formatters for array-types and all types that implement ICollection&lt;&gt;
	/// </summary>
	public class CollectionFormatterResolver : IFormatterResolver
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
				int rank = type.GetArrayRank();
				var maxCount = itemType == typeof(byte)
						? _ceras.Config.Advanced.SizeLimits.MaxByteArraySize
						: _ceras.Config.Advanced.SizeLimits.MaxArraySize;


				if (rank == 1 && _ceras.Config.Advanced.UseReinterpretFormatter && ReflectionHelper.IsBlittableType(itemType))
				{
					// ReinterpretArrayFormatter<> (reinterpret)
					var formatterType = typeof(ReinterpretArrayFormatter<>).MakeGenericType(itemType);
					formatter = (IFormatter)Activator.CreateInstance(formatterType, maxCount);
				}
				else
				{
					if (rank == 1)
					{
						// ArrayFormatter<>
						var formatterType = typeof(ArrayFormatter<>).MakeGenericType(itemType);
						formatter = (IFormatter)Activator.CreateInstance(formatterType, _ceras, maxCount);
					}
					else if (rank <= 6)
					{
						// MultiDimensionalArrayFormatter
						var formatterType = typeof(MultiDimensionalArrayFormatter<>).MakeGenericType(itemType);
						formatter = (IFormatter)Activator.CreateInstance(formatterType, _ceras, maxCount);
					}
					else
					{
						ArrayRankTooHigh(rank);
					}
				}

				_formatterInstances[type] = formatter;
				return formatter;
			}


			//
			// Special collection? (Stack, Queue, ReadOnlyDictionary)
			//
			var closedStack = ReflectionHelper.FindClosedType(type, typeof(Stack<>));
			if (closedStack != null)
			{
				var formatterType = typeof(StackFormatter<>).MakeGenericType(closedStack.GetGenericArguments());
				formatter = (IFormatter)Activator.CreateInstance(formatterType);
				_formatterInstances[type] = formatter;
				return formatter;
			}

			var closedQueue = ReflectionHelper.FindClosedType(type, typeof(Queue<>));
			if (closedQueue != null)
			{
				var formatterType = typeof(QueueFormatter<>).MakeGenericType(closedQueue.GetGenericArguments());
				formatter = (IFormatter)Activator.CreateInstance(formatterType);
				_formatterInstances[type] = formatter;
				return formatter;
			}

			var closedRD = ReflectionHelper.FindClosedType(type, typeof(System.Collections.ObjectModel.ReadOnlyDictionary<,>));
			if (closedRD != null)
			{
				var formatterType = typeof(ReadOnlyDictionaryFormatter<,>).MakeGenericType(closedRD.GetGenericArguments());
				formatter = (IFormatter)Activator.CreateInstance(formatterType);
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

				// Use the general case collection formatter
				var formatterType = typeof(CollectionFormatter<,>).MakeGenericType(type, itemType);
				formatter = (IFormatter)Activator.CreateInstance(formatterType, _ceras);

				_formatterInstances[type] = formatter;
				return formatter;
			}

			return null;
		}

		private static void ArrayRankTooHigh(int rank)
		{
			throw new InvalidOperationException("Multi-dimensional array of rank " + rank + " is not yet supported, please open an issue on github");
		}
	}
}