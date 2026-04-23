using System;
using System.Buffers;
using System.Collections.Generic;

namespace Ceras.Formatters
{
    public sealed class StackFormatter<TItem> : FormatterBase<Stack<TItem>>
    {
        IFormatter<int> _intFormatter = default!;
        IFormatter<TItem> _itemFormatter = default!;

        public StackFormatter()
        {
            CerasSerializer.AddFormatterConstructedType(typeof(Stack<TItem>));
        }

        public override void Serialize(IBufferWriter<byte> writer, Stack<TItem> value)
        {
            _intFormatter.Serialize(writer, value.Count);
            foreach (var item in value)
                _itemFormatter.Serialize(writer, item);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Stack<TItem> value)
        {
            int count = 0;
            _intFormatter.Deserialize(ref reader, ref count);

            var ar = new TItem[count];
            for (int i = 0; i < count; i++)
                _itemFormatter.Deserialize(ref reader, ref ar[i]);

            value = new Stack<TItem>(count);
            for (int i = count - 1; i >= 0; i--)
                value.Push(ar[i]);
        }
    }
}
