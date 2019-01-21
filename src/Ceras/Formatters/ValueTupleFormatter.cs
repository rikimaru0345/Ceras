
#if NETSTANDARD

using System;

namespace Ceras.Formatters
{

    sealed class ValueTupleFormatter<T1> : IFormatter<ValueTuple<T1>>
    {
		public IFormatter<T1> Item1Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, ValueTuple<T1> value)
        {
            Item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref ValueTuple<T1> value)
        {
            T1 Item1 = default;

            Item1Formatter.Deserialize(buffer, ref offset, ref Item1);

			value = new ValueTuple<T1>(Item1);
        }
    }


    sealed class ValueTupleFormatter<T1, T2> : IFormatter<ValueTuple<T1, T2>>
    {
		public IFormatter<T1> Item1Formatter;
		public IFormatter<T2> Item2Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, ValueTuple<T1, T2> value)
        {
            Item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            Item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref ValueTuple<T1, T2> value)
        {
            T1 Item1 = default;
            T2 Item2 = default;

            Item1Formatter.Deserialize(buffer, ref offset, ref Item1);
            Item2Formatter.Deserialize(buffer, ref offset, ref Item2);

			value = new ValueTuple<T1, T2>(Item1, Item2);
        }
    }


    sealed class ValueTupleFormatter<T1, T2, T3> : IFormatter<ValueTuple<T1, T2, T3>>
    {
		public IFormatter<T1> Item1Formatter;
		public IFormatter<T2> Item2Formatter;
		public IFormatter<T3> Item3Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, ValueTuple<T1, T2, T3> value)
        {
            Item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            Item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            Item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref ValueTuple<T1, T2, T3> value)
        {
            T1 Item1 = default;
            T2 Item2 = default;
            T3 Item3 = default;

            Item1Formatter.Deserialize(buffer, ref offset, ref Item1);
            Item2Formatter.Deserialize(buffer, ref offset, ref Item2);
            Item3Formatter.Deserialize(buffer, ref offset, ref Item3);

			value = new ValueTuple<T1, T2, T3>(Item1, Item2, Item3);
        }
    }


    sealed class ValueTupleFormatter<T1, T2, T3, T4> : IFormatter<ValueTuple<T1, T2, T3, T4>>
    {
		public IFormatter<T1> Item1Formatter;
		public IFormatter<T2> Item2Formatter;
		public IFormatter<T3> Item3Formatter;
		public IFormatter<T4> Item4Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, ValueTuple<T1, T2, T3, T4> value)
        {
            Item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            Item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            Item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
            Item4Formatter.Serialize(ref buffer, ref offset, value.Item4);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref ValueTuple<T1, T2, T3, T4> value)
        {
            T1 Item1 = default;
            T2 Item2 = default;
            T3 Item3 = default;
            T4 Item4 = default;

            Item1Formatter.Deserialize(buffer, ref offset, ref Item1);
            Item2Formatter.Deserialize(buffer, ref offset, ref Item2);
            Item3Formatter.Deserialize(buffer, ref offset, ref Item3);
            Item4Formatter.Deserialize(buffer, ref offset, ref Item4);

			value = new ValueTuple<T1, T2, T3, T4>(Item1, Item2, Item3, Item4);
        }
    }


    sealed class ValueTupleFormatter<T1, T2, T3, T4, T5> : IFormatter<ValueTuple<T1, T2, T3, T4, T5>>
    {
		public IFormatter<T1> Item1Formatter;
		public IFormatter<T2> Item2Formatter;
		public IFormatter<T3> Item3Formatter;
		public IFormatter<T4> Item4Formatter;
		public IFormatter<T5> Item5Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, ValueTuple<T1, T2, T3, T4, T5> value)
        {
            Item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            Item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            Item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
            Item4Formatter.Serialize(ref buffer, ref offset, value.Item4);
            Item5Formatter.Serialize(ref buffer, ref offset, value.Item5);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref ValueTuple<T1, T2, T3, T4, T5> value)
        {
            T1 Item1 = default;
            T2 Item2 = default;
            T3 Item3 = default;
            T4 Item4 = default;
            T5 Item5 = default;

            Item1Formatter.Deserialize(buffer, ref offset, ref Item1);
            Item2Formatter.Deserialize(buffer, ref offset, ref Item2);
            Item3Formatter.Deserialize(buffer, ref offset, ref Item3);
            Item4Formatter.Deserialize(buffer, ref offset, ref Item4);
            Item5Formatter.Deserialize(buffer, ref offset, ref Item5);

			value = new ValueTuple<T1, T2, T3, T4, T5>(Item1, Item2, Item3, Item4, Item5);
        }
    }


    sealed class ValueTupleFormatter<T1, T2, T3, T4, T5, T6> : IFormatter<ValueTuple<T1, T2, T3, T4, T5, T6>>
    {
		public IFormatter<T1> Item1Formatter;
		public IFormatter<T2> Item2Formatter;
		public IFormatter<T3> Item3Formatter;
		public IFormatter<T4> Item4Formatter;
		public IFormatter<T5> Item5Formatter;
		public IFormatter<T6> Item6Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, ValueTuple<T1, T2, T3, T4, T5, T6> value)
        {
            Item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            Item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            Item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
            Item4Formatter.Serialize(ref buffer, ref offset, value.Item4);
            Item5Formatter.Serialize(ref buffer, ref offset, value.Item5);
            Item6Formatter.Serialize(ref buffer, ref offset, value.Item6);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref ValueTuple<T1, T2, T3, T4, T5, T6> value)
        {
            T1 Item1 = default;
            T2 Item2 = default;
            T3 Item3 = default;
            T4 Item4 = default;
            T5 Item5 = default;
            T6 Item6 = default;

            Item1Formatter.Deserialize(buffer, ref offset, ref Item1);
            Item2Formatter.Deserialize(buffer, ref offset, ref Item2);
            Item3Formatter.Deserialize(buffer, ref offset, ref Item3);
            Item4Formatter.Deserialize(buffer, ref offset, ref Item4);
            Item5Formatter.Deserialize(buffer, ref offset, ref Item5);
            Item6Formatter.Deserialize(buffer, ref offset, ref Item6);

			value = new ValueTuple<T1, T2, T3, T4, T5, T6>(Item1, Item2, Item3, Item4, Item5, Item6);
        }
    }


    sealed class ValueTupleFormatter<T1, T2, T3, T4, T5, T6, T7> : IFormatter<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>
    {
		public IFormatter<T1> Item1Formatter;
		public IFormatter<T2> Item2Formatter;
		public IFormatter<T3> Item3Formatter;
		public IFormatter<T4> Item4Formatter;
		public IFormatter<T5> Item5Formatter;
		public IFormatter<T6> Item6Formatter;
		public IFormatter<T7> Item7Formatter;

        public void Serialize(ref byte[] buffer, ref int offset, ValueTuple<T1, T2, T3, T4, T5, T6, T7> value)
        {
            Item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            Item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            Item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
            Item4Formatter.Serialize(ref buffer, ref offset, value.Item4);
            Item5Formatter.Serialize(ref buffer, ref offset, value.Item5);
            Item6Formatter.Serialize(ref buffer, ref offset, value.Item6);
            Item7Formatter.Serialize(ref buffer, ref offset, value.Item7);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref ValueTuple<T1, T2, T3, T4, T5, T6, T7> value)
        {
            T1 Item1 = default;
            T2 Item2 = default;
            T3 Item3 = default;
            T4 Item4 = default;
            T5 Item5 = default;
            T6 Item6 = default;
            T7 Item7 = default;

            Item1Formatter.Deserialize(buffer, ref offset, ref Item1);
            Item2Formatter.Deserialize(buffer, ref offset, ref Item2);
            Item3Formatter.Deserialize(buffer, ref offset, ref Item3);
            Item4Formatter.Deserialize(buffer, ref offset, ref Item4);
            Item5Formatter.Deserialize(buffer, ref offset, ref Item5);
            Item6Formatter.Deserialize(buffer, ref offset, ref Item6);
            Item7Formatter.Deserialize(buffer, ref offset, ref Item7);

			value = new ValueTuple<T1, T2, T3, T4, T5, T6, T7>(Item1, Item2, Item3, Item4, Item5, Item6, Item7);
        }
    }


    sealed class ValueTupleFormatter<T1, T2, T3, T4, T5, T6, T7, TRest> : IFormatter<ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>> where TRest : struct
    {
		public IFormatter<T1> Item1Formatter;
		public IFormatter<T2> Item2Formatter;
		public IFormatter<T3> Item3Formatter;
		public IFormatter<T4> Item4Formatter;
		public IFormatter<T5> Item5Formatter;
		public IFormatter<T6> Item6Formatter;
		public IFormatter<T7> Item7Formatter;
		public IFormatter<TRest> RestFormatter;

        public void Serialize(ref byte[] buffer, ref int offset, ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> value)
        {
            Item1Formatter.Serialize(ref buffer, ref offset, value.Item1);
            Item2Formatter.Serialize(ref buffer, ref offset, value.Item2);
            Item3Formatter.Serialize(ref buffer, ref offset, value.Item3);
            Item4Formatter.Serialize(ref buffer, ref offset, value.Item4);
            Item5Formatter.Serialize(ref buffer, ref offset, value.Item5);
            Item6Formatter.Serialize(ref buffer, ref offset, value.Item6);
            Item7Formatter.Serialize(ref buffer, ref offset, value.Item7);
            RestFormatter.Serialize(ref buffer, ref offset, value.Rest);
        }

		public void Deserialize(byte[] buffer, ref int offset, ref ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> value)
        {
            T1 Item1 = default;
            T2 Item2 = default;
            T3 Item3 = default;
            T4 Item4 = default;
            T5 Item5 = default;
            T6 Item6 = default;
            T7 Item7 = default;
            TRest Rest = default;

            Item1Formatter.Deserialize(buffer, ref offset, ref Item1);
            Item2Formatter.Deserialize(buffer, ref offset, ref Item2);
            Item3Formatter.Deserialize(buffer, ref offset, ref Item3);
            Item4Formatter.Deserialize(buffer, ref offset, ref Item4);
            Item5Formatter.Deserialize(buffer, ref offset, ref Item5);
            Item6Formatter.Deserialize(buffer, ref offset, ref Item6);
            Item7Formatter.Deserialize(buffer, ref offset, ref Item7);
            RestFormatter.Deserialize(buffer, ref offset, ref Rest);

			value = new ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>(Item1, Item2, Item3, Item4, Item5, Item6, Item7, Rest);
        }
    }

}

#endif