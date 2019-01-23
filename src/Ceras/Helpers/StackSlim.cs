using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
	class StackSlim<T>
	{
		Entry[] _array;

		public int Count { get; private set; }

		struct Entry
		{
			public T Item;

			public static implicit operator T(Entry e) => e.Item;
			public static implicit operator Entry(T t) => new Entry { Item = t };
		}


		public StackSlim(int capacity = 4)
		{
			if (capacity < 4)
				capacity = 4;

			capacity = HashHelpers.PowerOf2(capacity);

			_array = new Entry[capacity];
		}

		public void Push(T item)
		{
			if (Count == _array.Length)
			{
				// Resize
				var newCapacity = _array.Length * 2;
				Entry[] newArray = new Entry[newCapacity];

				Array.Copy(_array, newArray, _array.Length);

				_array = newArray;
			}

			_array[Count] = item;
			Count++;
		}
		
		public T Pop()
		{
			// Not needed, we have an automatic check below + most likely a check in the caller
			//if (Count == 0)
			//	throw new InvalidOperationException("Stack is empty");

			var ar = _array;

			var index = --Count;

			var item = ar[index].Item;
			ar[index] = new Entry();

			return item;
		}

		public void Clear()
		{
			Array.Clear(_array, 0, Count);
			Count = 0;
		}
	}
}
