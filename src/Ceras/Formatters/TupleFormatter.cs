using System;
using System.Buffers;

namespace Ceras.Formatters
{
    public sealed class TupleFormatter<T1> : FormatterBase<Tuple<T1>>
    {
        IFormatter<T1> _item1Formatter;
        public override void Serialize(IBufferWriter<byte> writer, Tuple<T1> value) => _item1Formatter.Serialize(writer, value.Item1);
        public override void Deserialize(ref SequenceReader<byte> reader, ref Tuple<T1> value)
        {
            T1 item1 = default; _item1Formatter.Deserialize(ref reader, ref item1);
            value = new Tuple<T1>(item1);
        }
    }

    public sealed class TupleFormatter<T1, T2> : FormatterBase<Tuple<T1, T2>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        public override void Serialize(IBufferWriter<byte> writer, Tuple<T1, T2> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
        }
        public override void Deserialize(ref SequenceReader<byte> reader, ref Tuple<T1, T2> value)
        {
            T1 item1 = default; T2 item2 = default;
            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            value = new Tuple<T1, T2>(item1, item2);
        }
    }

    public sealed class TupleFormatter<T1, T2, T3> : FormatterBase<Tuple<T1, T2, T3>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;
        public override void Serialize(IBufferWriter<byte> writer, Tuple<T1, T2, T3> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
        }
        public override void Deserialize(ref SequenceReader<byte> reader, ref Tuple<T1, T2, T3> value)
        {
            T1 item1 = default; T2 item2 = default; T3 item3 = default;
            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);
            value = new Tuple<T1, T2, T3>(item1, item2, item3);
        }
    }

    public sealed class TupleFormatter<T1, T2, T3, T4> : FormatterBase<Tuple<T1, T2, T3, T4>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;
        IFormatter<T4> _item4Formatter;
        public override void Serialize(IBufferWriter<byte> writer, Tuple<T1, T2, T3, T4> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
            _item4Formatter.Serialize(writer, value.Item4);
        }
        public override void Deserialize(ref SequenceReader<byte> reader, ref Tuple<T1, T2, T3, T4> value)
        {
            T1 item1 = default; T2 item2 = default; T3 item3 = default; T4 item4 = default;
            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);
            _item4Formatter.Deserialize(ref reader, ref item4);
            value = new Tuple<T1, T2, T3, T4>(item1, item2, item3, item4);
        }
    }

    public sealed class TupleFormatter<T1, T2, T3, T4, T5> : FormatterBase<Tuple<T1, T2, T3, T4, T5>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;
        IFormatter<T4> _item4Formatter;
        IFormatter<T5> _item5Formatter;
        public override void Serialize(IBufferWriter<byte> writer, Tuple<T1, T2, T3, T4, T5> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
            _item4Formatter.Serialize(writer, value.Item4);
            _item5Formatter.Serialize(writer, value.Item5);
        }
        public override void Deserialize(ref SequenceReader<byte> reader, ref Tuple<T1, T2, T3, T4, T5> value)
        {
            T1 item1 = default; T2 item2 = default; T3 item3 = default; T4 item4 = default; T5 item5 = default;
            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);
            _item4Formatter.Deserialize(ref reader, ref item4);
            _item5Formatter.Deserialize(ref reader, ref item5);
            value = new Tuple<T1, T2, T3, T4, T5>(item1, item2, item3, item4, item5);
        }
    }

    public sealed class TupleFormatter<T1, T2, T3, T4, T5, T6> : FormatterBase<Tuple<T1, T2, T3, T4, T5, T6>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;
        IFormatter<T4> _item4Formatter;
        IFormatter<T5> _item5Formatter;
        IFormatter<T6> _item6Formatter;
        public override void Serialize(IBufferWriter<byte> writer, Tuple<T1, T2, T3, T4, T5, T6> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
            _item4Formatter.Serialize(writer, value.Item4);
            _item5Formatter.Serialize(writer, value.Item5);
            _item6Formatter.Serialize(writer, value.Item6);
        }
        public override void Deserialize(ref SequenceReader<byte> reader, ref Tuple<T1, T2, T3, T4, T5, T6> value)
        {
            T1 item1 = default; T2 item2 = default; T3 item3 = default; T4 item4 = default; T5 item5 = default; T6 item6 = default;
            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);
            _item4Formatter.Deserialize(ref reader, ref item4);
            _item5Formatter.Deserialize(ref reader, ref item5);
            _item6Formatter.Deserialize(ref reader, ref item6);
            value = new Tuple<T1, T2, T3, T4, T5, T6>(item1, item2, item3, item4, item5, item6);
        }
    }

    public sealed class TupleFormatter<T1, T2, T3, T4, T5, T6, T7> : FormatterBase<Tuple<T1, T2, T3, T4, T5, T6, T7>>
    {
        IFormatter<T1> _item1Formatter;
        IFormatter<T2> _item2Formatter;
        IFormatter<T3> _item3Formatter;
        IFormatter<T4> _item4Formatter;
        IFormatter<T5> _item5Formatter;
        IFormatter<T6> _item6Formatter;
        IFormatter<T7> _item7Formatter;
        public override void Serialize(IBufferWriter<byte> writer, Tuple<T1, T2, T3, T4, T5, T6, T7> value)
        {
            _item1Formatter.Serialize(writer, value.Item1);
            _item2Formatter.Serialize(writer, value.Item2);
            _item3Formatter.Serialize(writer, value.Item3);
            _item4Formatter.Serialize(writer, value.Item4);
            _item5Formatter.Serialize(writer, value.Item5);
            _item6Formatter.Serialize(writer, value.Item6);
            _item7Formatter.Serialize(writer, value.Item7);
        }
        public override void Deserialize(ref SequenceReader<byte> reader, ref Tuple<T1, T2, T3, T4, T5, T6, T7> value)
        {
            T1 item1 = default; T2 item2 = default; T3 item3 = default; T4 item4 = default; T5 item5 = default; T6 item6 = default; T7 item7 = default;
            _item1Formatter.Deserialize(ref reader, ref item1);
            _item2Formatter.Deserialize(ref reader, ref item2);
            _item3Formatter.Deserialize(ref reader, ref item3);
            _item4Formatter.Deserialize(ref reader, ref item4);
            _item5Formatter.Deserialize(ref reader, ref item5);
            _item6Formatter.Deserialize(ref reader, ref item6);
            _item7Formatter.Deserialize(ref reader, ref item7);
            value = new Tuple<T1, T2, T3, T4, T5, T6, T7>(item1, item2, item3, item4, item5, item6, item7);
        }
    }
}
