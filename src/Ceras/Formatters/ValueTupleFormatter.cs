using System.Buffers;
﻿using System;

namespace Ceras.Formatters
{
#pragma warning disable 0649 // Field is never assigned to (Ceras dependency injection)

    public sealed class ValueTupleFormatter<T1> : IFormatter<ValueTuple<T1>>
    {
        IFormatter<T1> _item1Formatter;

        public void Serialize(IBufferWriter<byte> writer, ValueTuple<T1> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref ValueTuple<T1> value)
        {
            T1 item1 = default;

            _item1Formatter.Deserialize(ref reader, ref item1);

            value = new ValueTuple<T1>(item1);
        }
    }


    public sealed class ValueTupleFormatter<T1, T2> : IFormatter<ValueTuple<T1, T2>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;

        public void Serialize(IBufferWriter<byte> writer, ValueTuple<T1, T2> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref ValueTuple<T1, T2> value)
        {
            T1 item1 = default;
            T2 item2 = default;

            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);

            value = new ValueTuple<T1, T2>(item1, item2);
        }
    }


    public sealed class ValueTupleFormatter<T1, T2, T3> : IFormatter<ValueTuple<T1, T2, T3>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;

        public void Serialize(IBufferWriter<byte> writer, ValueTuple<T1, T2, T3> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref ValueTuple<T1, T2, T3> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;

            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);

            value = new ValueTuple<T1, T2, T3>(item1, item2, item3);
        }
    }


    public sealed class ValueTupleFormatter<T1, T2, T3, T4> : IFormatter<ValueTuple<T1, T2, T3, T4>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;
        IFormatter<T4> _item4Formatter;

        public void Serialize(IBufferWriter<byte> writer, ValueTuple<T1, T2, T3, T4> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
            _item4Formatter.Serialize(writer, value.Item4);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref ValueTuple<T1, T2, T3, T4> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;
            T4 item4 = default;

            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);
            _item4Formatter.Deserialize(ref reader, ref item4);

            value = new ValueTuple<T1, T2, T3, T4>(item1, item2, item3, item4);
        }
    }


    public sealed class ValueTupleFormatter<T1, T2, T3, T4, T5> : IFormatter<ValueTuple<T1, T2, T3, T4, T5>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;
        IFormatter<T4> _item4Formatter;
        IFormatter<T5> _item5Formatter;

        public void Serialize(IBufferWriter<byte> writer, ValueTuple<T1, T2, T3, T4, T5> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
            _item4Formatter.Serialize(writer, value.Item4);
            _item5Formatter.Serialize(writer, value.Item5);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref ValueTuple<T1, T2, T3, T4, T5> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;
            T4 item4 = default;
            T5 item5 = default;

            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);
            _item4Formatter.Deserialize(ref reader, ref item4);
            _item5Formatter.Deserialize(ref reader, ref item5);

            value = new ValueTuple<T1, T2, T3, T4, T5>(item1, item2, item3, item4, item5);
        }
    }


    public sealed class ValueTupleFormatter<T1, T2, T3, T4, T5, T6> : IFormatter<ValueTuple<T1, T2, T3, T4, T5, T6>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;
        IFormatter<T4> _item4Formatter;
        IFormatter<T5> _item5Formatter;
        IFormatter<T6> _item6Formatter;

        public void Serialize(IBufferWriter<byte> writer, ValueTuple<T1, T2, T3, T4, T5, T6> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
            _item4Formatter.Serialize(writer, value.Item4);
            _item5Formatter.Serialize(writer, value.Item5);
            _item6Formatter.Serialize(writer, value.Item6);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref ValueTuple<T1, T2, T3, T4, T5, T6> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;
            T4 item4 = default;
            T5 item5 = default;
            T6 item6 = default;

            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);
            _item4Formatter.Deserialize(ref reader, ref item4);
            _item5Formatter.Deserialize(ref reader, ref item5);
            _item6Formatter.Deserialize(ref reader, ref item6);

            value = new ValueTuple<T1, T2, T3, T4, T5, T6>(item1, item2, item3, item4, item5, item6);
        }
    }


    public sealed class ValueTupleFormatter<T1, T2, T3, T4, T5, T6, T7> : IFormatter<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;
        IFormatter<T4> _item4Formatter;
        IFormatter<T5> _item5Formatter;
        IFormatter<T6> _item6Formatter;
        IFormatter<T7> _item7Formatter;

        public void Serialize(IBufferWriter<byte> writer, ValueTuple<T1, T2, T3, T4, T5, T6, T7> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
            _item4Formatter.Serialize(writer, value.Item4);
            _item5Formatter.Serialize(writer, value.Item5);
            _item6Formatter.Serialize(writer, value.Item6);
            _item7Formatter.Serialize(writer, value.Item7);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref ValueTuple<T1, T2, T3, T4, T5, T6, T7> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;
            T4 item4 = default;
            T5 item5 = default;
            T6 item6 = default;
            T7 item7 = default;

            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);
            _item4Formatter.Deserialize(ref reader, ref item4);
            _item5Formatter.Deserialize(ref reader, ref item5);
            _item6Formatter.Deserialize(ref reader, ref item6);
            _item7Formatter.Deserialize(ref reader, ref item7);

            value = new ValueTuple<T1, T2, T3, T4, T5, T6, T7>(item1, item2, item3, item4, item5, item6, item7);
        }
    }


    public sealed class ValueTupleFormatter<T1, T2, T3, T4, T5, T6, T7, TRest> : IFormatter<ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>> where TRest : struct
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;
        IFormatter<T4> _item4Formatter;
        IFormatter<T5> _item5Formatter;
        IFormatter<T6> _item6Formatter;
        IFormatter<T7> _item7Formatter;
        IFormatter<TRest> _restFormatter;

        public void Serialize(IBufferWriter<byte> writer, ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
            _item4Formatter.Serialize(writer, value.Item4);
            _item5Formatter.Serialize(writer, value.Item5);
            _item6Formatter.Serialize(writer, value.Item6);
            _item7Formatter.Serialize(writer, value.Item7);
            _restFormatter.Serialize(writer, value.Rest);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;
            T4 item4 = default;
            T5 item5 = default;
            T6 item6 = default;
            T7 item7 = default;
            TRest rest = default;

            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);
            _item4Formatter.Deserialize(ref reader, ref item4);
            _item5Formatter.Deserialize(ref reader, ref item5);
            _item6Formatter.Deserialize(ref reader, ref item6);
            _item7Formatter.Deserialize(ref reader, ref item7);
            _restFormatter.Deserialize(ref reader, ref rest);

            value = new ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>(item1, item2, item3, item4, item5, item6, item7, rest);
        }
    }

}
