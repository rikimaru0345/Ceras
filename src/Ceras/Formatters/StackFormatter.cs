using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
	using System.Collections;

	public sealed class StackFormatter<TItem> : IFormatter<Stack<TItem>>
	{
		IFormatter<int> _intFormatter;
		IFormatter<TItem> _itemFormatter;

		public StackFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(Stack<TItem>));
		}

		public void Serialize(ref byte[] buffer, ref int offset, Stack<TItem> value)
		{
			_intFormatter.Serialize(ref buffer, ref offset, value.Count);

			var itemFormatter = _itemFormatter;
			foreach (var item in value)
				itemFormatter.Serialize(ref buffer, ref offset, item);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Stack<TItem> value)
		{
			var itemFormatter = _itemFormatter;

			int count = 0;
			_intFormatter.Deserialize(buffer, ref offset, ref count);

			value = new Stack<TItem>(count);
			
			// Deserialize into temporary array
			var ar = new TItem[count];
			for (int i = 0; i < count; i++)
				itemFormatter.Deserialize(buffer, ref offset, ref ar[i]);

			// Push in reverse order to restore the original stack
			for (int i = count - 1; i >= 0; i--)
				value.Push(ar[i]);
		}
	}
}
