using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
	using System.Collections;

	public sealed class QueueFormatter<TItem> : IFormatter<Queue<TItem>>
	{
		IFormatter<int> _intFormatter;
		IFormatter<TItem> _itemFormatter;


		public QueueFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(Queue<TItem>));
		}

		public void Serialize(ref byte[] buffer, ref int offset, Queue<TItem> value)
		{
			_intFormatter.Serialize(ref buffer, ref offset, value.Count);

			var itemFormatter = _itemFormatter;
			foreach (var item in value)
				itemFormatter.Serialize(ref buffer, ref offset, item);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Queue<TItem> value)
		{
			var itemFormatter = _itemFormatter;

			int count = 0;
			_intFormatter.Deserialize(buffer, ref offset, ref count);

			value = new Queue<TItem>(count);
			
			// Deserialize elements directly into the queue
			for (int i = 0; i < count; i++)
			{
				TItem item = default;
				itemFormatter.Deserialize(buffer, ref offset, ref item);
				value.Enqueue(item);
			}

		}
	}
}
