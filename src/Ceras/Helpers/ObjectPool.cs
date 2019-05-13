namespace Ceras.Helpers
{
	using System;
	using System.Collections.Generic;

	// Simple thread-safe object pool.
	class FactoryPool<T> : IFactoryPool
	{
		readonly Func<T> _factoryMethod;
		readonly Stack<T> _objects = new Stack<T>();

		/// <summary>
		/// The initial pool size (number of elements the pool created while constructing the pool)
		/// </summary>
		public int StartSize { get; }

		/// <summary>
		/// Total number of elements of the pool (sum of both currently available + currently rented out)
		/// </summary>
		public int Capacity { get; set; }

		/// <summary>
		/// Number of objects the pool can still give out before having to use its 'factoryMethod'
		/// </summary>
		public int Available
		{
			get
			{
				lock (_objects)
					return _objects.Count;
			}
		}

		public Type ElementType => typeof(T);

		
		public FactoryPool(Func<T> factoryMethod, int startSize = 0)
		{
			_factoryMethod = factoryMethod;
			StartSize = startSize;
			for (int i = 0; i < startSize; i++)
				ReturnObject(factoryMethod());
		}

		public T RentObject()
		{
			lock (_objects)
			{
				if (_objects.Count > 0)
				{
					// Return existing object
					var obj = _objects.Pop();
					return obj;
				}
				else
				{
					// Create a new one
					var obj = _factoryMethod();
					Capacity++;
					return obj;
				}
			}
		}

		public void ReturnObject(T objectToReturn)
		{
			lock (_objects)
			{
				_objects.Push(objectToReturn);
			}
		}

		// Reduce the pool back down to 'startSize'
		public void TrimPool()
		{
			lock (_objects)
			{
				while (_objects.Count > StartSize)
				{
					_objects.Pop();
					Capacity--;
				}
			}
		}
	}

	interface IFactoryPool
	{
		int StartSize { get; }
		int Capacity {get;}
		int Available {get;}

		Type ElementType {get;}

		void TrimPool();
	}
}
