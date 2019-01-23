namespace Ceras.Helpers
{
	using System;
	using System.Collections.Generic;

	class FactoryPool<T>
	{
		readonly Func<FactoryPool<T>, T> _factoryMethod;

		public int Capacity { get; set; }

		Stack<T> _objects = new Stack<T>();

		public FactoryPool(Func<FactoryPool<T>, T> factoryMethod, int startSize = 0)
		{
			_factoryMethod = factoryMethod;

			for (int i = 0; i < startSize; i++)
				ReturnObject(_factoryMethod(this));
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
			}
		}

	}
}
