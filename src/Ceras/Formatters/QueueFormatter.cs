using System;
using System.Buffers;
using System.Collections.Generic;

namespace Ceras.Formatters
{
    public sealed class QueueFormatter<TItem> : FormatterBase<Queue<TItem>>
    {
        IFormatter<int> _intFormatter;
        IFormatter<TItem> _itemFormatter;

        public QueueFormatter()
        {
            CerasSerializer.AddFormatterConstructedType(typeof(Queue<TItem>));
        }

        public override void Serialize(IBufferWriter<byte> writer, Queue<TItem> value)
        {
            _intFormatter.Serialize(writer, value.Count);
            foreach (var item in value)
                _itemFormatter.Serialize(writer, item);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Queue<TItem> value)
        {
            int count = 0;
            _intFormatter.Deserialize(ref reader, ref count);

            value = new Queue<TItem>(count);
            for (int i = 0; i < count; i++)
            {
                TItem item = default;
                _itemFormatter.Deserialize(ref reader, ref item);
                value.Enqueue(item);
            }
        }
    }
}
