using System;
using System.Buffers;
using System.Collections.Generic;

namespace Ceras.Formatters
{
    public sealed class StackFormatter<TItem> : FormatterBase<Stack<TItem>>
    {
        IFormatter<int> _intFormatter = default!;
        IFormatter<TItem> _itemFormatter = default!;

        /// <summary>
        /// Injected by <c>CerasSerializer.PrepareFormatter</c>, which matches on the field type, so the
        /// constructor cannot set it.
        /// </summary>
        SizeLimits _sizeLimits = default!;

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
            // The count is the injected Int32Formatter: a fixed 4-byte little-endian element count with no
            // null marker, so every negative int in it is corrupt data rather than a null stack. Refused
            // before the allocation below.
            int count = 0;
            _intFormatter.Deserialize(ref reader, ref count);
            CollectionCountLimit.Require(count, CollectionCountLimit.MaxCount(_sizeLimits), typeof(Stack<TItem>), countHasNoNullMarker: true);

            // The staging array is the allocation the count sizes. Reading into it forward and pushing
            // back-to-front restores the writer's top-first order; the guard has to precede this line.
            var ar = new TItem[count];
            for (int i = 0; i < count; i++)
                _itemFormatter.Deserialize(ref reader, ref ar[i]);

            value = new Stack<TItem>(count);
            for (int i = count - 1; i >= 0; i--)
                value.Push(ar[i]);
        }
    }
}
