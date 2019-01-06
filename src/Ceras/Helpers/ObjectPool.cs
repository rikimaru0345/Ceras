namespace Ceras.Helpers
{
	using System;
	using System.Collections.Generic;

	public interface IPool<T>
	{
		/// <summary>
		/// How many objects the pool has in total.
		/// The sum of the objects that are not used and still in the pool, plus the objects that are currently in use (rented out)
		/// </summary>
		int Capacity { get; }
		/// <summary>
		/// The number of objects that the pool has still available
		/// </summary>
		int ObjectsAvailableInPool { get; }

		T RentObject();
		void ReturnObject(T objectToReturn);
	}

	class FactoryPool<T> : IPool<T>
	{
		readonly Func<FactoryPool<T>, T> _factoryMethod;

		public int Capacity { get; set; }
		public int ObjectsAvailableInPool { get; set; }


		public FactoryPool(Func<FactoryPool<T>, T> factoryMethod, int startSize = 0)
		{
			_factoryMethod = factoryMethod;

			for (int i = 0; i < startSize; i++)
				ReturnObject(_factoryMethod(this));
		}

		Stack<T> _objects = new Stack<T>();

		public T RentObject()
		{
			lock (_objects)
			{
				if (_objects.Count > 0)
				{
					// Return existing object
					var obj = _objects.Pop();
					ObjectsAvailableInPool--;
					return obj;
				}
				else
				{
					// Create a new one
					var obj = _factoryMethod(this);
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
				ObjectsAvailableInPool++;
			}
		}

	}
}
