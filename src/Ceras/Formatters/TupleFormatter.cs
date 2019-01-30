using System;

namespace Ceras.Formatters
{

    sealed class TupleFormatter<T1> : IFormatter<Tuple<T1>>
    {
		IFormatter<T1> _item1Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, Tuple<T1> value)
        {
            _item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref Tuple<T1> value)
        {
            T1 item1 = default;

            _item1Formatter.Deserialize(buffer, ref offset, ref item1);

			value = new Tuple<T1>(item1);
        }
    }


    sealed class TupleFormatter<T1, T2> : IFormatter<Tuple<T1, T2>>
    {
		IFormatter<T1> _item1Formatter;
		IFormatter<T2> _item2Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, Tuple<T1, T2> value)
        {
            _item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            _item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref Tuple<T1, T2> value)
        {
            T1 item1 = default;
            T2 item2 = default;

            _item1Formatter.Deserialize(buffer, ref offset, ref item1);
            _item2Formatter.Deserialize(buffer, ref offset, ref item2);

			value = new Tuple<T1, T2>(item1, item2);
        }
    }


    sealed class TupleFormatter<T1, T2, T3> : IFormatter<Tuple<T1, T2, T3>>
    {
		IFormatter<T1> _item1Formatter;
		IFormatter<T2> _item2Formatter;
		IFormatter<T3> _item3Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, Tuple<T1, T2, T3> value)
        {
            _item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            _item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            _item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref Tuple<T1, T2, T3> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;

            _item1Formatter.Deserialize(buffer, ref offset, ref item1);
            _item2Formatter.Deserialize(buffer, ref offset, ref item2);
            _item3Formatter.Deserialize(buffer, ref offset, ref item3);

			value = new Tuple<T1, T2, T3>(item1, item2, item3);
        }
    }


    sealed class TupleFormatter<T1, T2, T3, T4> : IFormatter<Tuple<T1, T2, T3, T4>>
    {
		IFormatter<T1> _item1Formatter;
		IFormatter<T2> _item2Formatter;
		IFormatter<T3> _item3Formatter;
		IFormatter<T4> _item4Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, Tuple<T1, T2, T3, T4> value)
        {
            _item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            _item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            _item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
            _item4Formatter.Serialize(ref buffer, ref offset, value.Item4);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref Tuple<T1, T2, T3, T4> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;
            T4 item4 = default;

            _item1Formatter.Deserialize(buffer, ref offset, ref item1);
            _item2Formatter.Deserialize(buffer, ref offset, ref item2);
            _item3Formatter.Deserialize(buffer, ref offset, ref item3);
            _item4Formatter.Deserialize(buffer, ref offset, ref item4);

			value = new Tuple<T1, T2, T3, T4>(item1, item2, item3, item4);
        }
    }


    sealed class TupleFormatter<T1, T2, T3, T4, T5> : IFormatter<Tuple<T1, T2, T3, T4, T5>>
    {
		IFormatter<T1> _item1Formatter;
		IFormatter<T2> _item2Formatter;
		IFormatter<T3> _item3Formatter;
		IFormatter<T4> _item4Formatter;
		IFormatter<T5> _item5Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, Tuple<T1, T2, T3, T4, T5> value)
        {
            _item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            _item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            _item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
            _item4Formatter.Serialize(ref buffer, ref offset, value.Item4);
            _item5Formatter.Serialize(ref buffer, ref offset, value.Item5);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref Tuple<T1, T2, T3, T4, T5> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;
            T4 item4 = default;
            T5 item5 = default;

            _item1Formatter.Deserialize(buffer, ref offset, ref item1);
            _item2Formatter.Deserialize(buffer, ref offset, ref item2);
            _item3Formatter.Deserialize(buffer, ref offset, ref item3);
            _item4Formatter.Deserialize(buffer, ref offset, ref item4);
            _item5Formatter.Deserialize(buffer, ref offset, ref item5);

			value = new Tuple<T1, T2, T3, T4, T5>(item1, item2, item3, item4, item5);
        }
    }


    sealed class TupleFormatter<T1, T2, T3, T4, T5, T6> : IFormatter<Tuple<T1, T2, T3, T4, T5, T6>>
    {
		IFormatter<T1> _item1Formatter;
		IFormatter<T2> _item2Formatter;
		IFormatter<T3> _item3Formatter;
		IFormatter<T4> _item4Formatter;
		IFormatter<T5> _item5Formatter;
		IFormatter<T6> _item6Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, Tuple<T1, T2, T3, T4, T5, T6> value)
        {
            _item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            _item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            _item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
            _item4Formatter.Serialize(ref buffer, ref offset, value.Item4);
            _item5Formatter.Serialize(ref buffer, ref offset, value.Item5);
            _item6Formatter.Serialize(ref buffer, ref offset, value.Item6);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref Tuple<T1, T2, T3, T4, T5, T6> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;
            T4 item4 = default;
            T5 item5 = default;
            T6 item6 = default;

            _item1Formatter.Deserialize(buffer, ref offset, ref item1);
            _item2Formatter.Deserialize(buffer, ref offset, ref item2);
            _item3Formatter.Deserialize(buffer, ref offset, ref item3);
            _item4Formatter.Deserialize(buffer, ref offset, ref item4);
            _item5Formatter.Deserialize(buffer, ref offset, ref item5);
            _item6Formatter.Deserialize(buffer, ref offset, ref item6);

			value = new Tuple<T1, T2, T3, T4, T5, T6>(item1, item2, item3, item4, item5, item6);
        }
    }


    sealed class TupleFormatter<T1, T2, T3, T4, T5, T6, T7> : IFormatter<Tuple<T1, T2, T3, T4, T5, T6, T7>>
    {
		IFormatter<T1> _item1Formatter;
		IFormatter<T2> _item2Formatter;
		IFormatter<T3> _item3Formatter;
		IFormatter<T4> _item4Formatter;
		IFormatter<T5> _item5Formatter;
		IFormatter<T6> _item6Formatter;
		IFormatter<T7> _item7Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, Tuple<T1, T2, T3, T4, T5, T6, T7> value)
        {
            _item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            _item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            _item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
            _item4Formatter.Serialize(ref buffer, ref offset, value.Item4);
            _item5Formatter.Serialize(ref buffer, ref offset, value.Item5);
            _item6Formatter.Serialize(ref buffer, ref offset, value.Item6);
            _item7Formatter.Serialize(ref buffer, ref offset, value.Item7);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref Tuple<T1, T2, T3, T4, T5, T6, T7> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;
            T4 item4 = default;
            T5 item5 = default;
            T6 item6 = default;
            T7 item7 = default;

            _item1Formatter.Deserialize(buffer, ref offset, ref item1);
            _item2Formatter.Deserialize(buffer, ref offset, ref item2);
            _item3Formatter.Deserialize(buffer, ref offset, ref item3);
            _item4Formatter.Deserialize(buffer, ref offset, ref item4);
            _item5Formatter.Deserialize(buffer, ref offset, ref item5);
            _item6Formatter.Deserialize(buffer, ref offset, ref item6);
            _item7Formatter.Deserialize(buffer, ref offset, ref item7);

			value = new Tuple<T1, T2, T3, T4, T5, T6, T7>(item1, item2, item3, item4, item5, item6, item7);
        }
    }


    sealed class TupleFormatter<T1, T2, T3, T4, T5, T6, T7, TRest> : IFormatter<Tuple<T1, T2, T3, T4, T5, T6, T7, TRest>> where TRest : struct
    {
		IFormatter<T1> _item1Formatter;
		IFormatter<T2> _item2Formatter;
		IFormatter<T3> _item3Formatter;
		IFormatter<T4> _item4Formatter;
		IFormatter<T5> _item5Formatter;
		IFormatter<T6> _item6Formatter;
		IFormatter<T7> _item7Formatter;
		IFormatter<TRest> _restFormatter;

        public void Serialize(ref byte[] buffer, ref int offset, Tuple<T1, T2, T3, T4, T5, T6, T7, TRest> value)
        {
            _item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            _item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            _item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
            _item4Formatter.Serialize(ref buffer, ref offset, value.Item4);
            _item5Formatter.Serialize(ref buffer, ref offset, value.Item5);
            _item6Formatter.Serialize(ref buffer, ref offset, value.Item6);
            _item7Formatter.Serialize(ref buffer, ref offset, value.Item7);
            _restFormatter.Serialize(ref buffer, ref offset, value.Rest);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref Tuple<T1, T2, T3, T4, T5, T6, T7, TRest> value)
        {
            T1 item1 = default;
            T2 item2 = default;
            T3 item3 = default;
            T4 item4 = default;
            T5 item5 = default;
            T6 item6 = default;
            T7 item7 = default;
            TRest rest = default;

            _item1Formatter.Deserialize(buffer, ref offset, ref item1);
            _item2Formatter.Deserialize(buffer, ref offset, ref item2);
            _item3Formatter.Deserialize(buffer, ref offset, ref item3);
            _item4Formatter.Deserialize(buffer, ref offset, ref item4);
            _item5Formatter.Deserialize(buffer, ref offset, ref item5);
            _item6Formatter.Deserialize(buffer, ref offset, ref item6);
            _item7Formatter.Deserialize(buffer, ref offset, ref item7);
            _restFormatter.Deserialize(buffer, ref offset, ref rest);

			value = new Tuple<T1, T2, T3, T4, T5, T6, T7, TRest>(item1, item2, item3, item4, item5, item6, item7, rest);
        }
    }

}
