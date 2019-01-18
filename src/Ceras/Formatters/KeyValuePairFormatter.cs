namespace Ceras.Formatters
{
	using System.Collections.Generic;

	class KeyValuePairFormatter<TKey, TValue> : IFormatter<KeyValuePair<TKey, TValue>>
	{
		IFormatter<TKey> _keyFormatter;
		IFormatter<TValue> _valueFormatter;

		public KeyValuePairFormatter(CerasSerializer serializer)
		{
			_keyFormatter = (IFormatter<TKey>)serializer.GetFormatter<TKey>();
			_valueFormatter = (IFormatter<TValue>)serializer.GetFormatter<TValue>();
		}


		public void Serialize(ref byte[] buffer, ref int offset, KeyValuePair<TKey, TValue> value)
		{
			_keyFormatter.Serialize(ref buffer, ref offset, value.Key);
			_valueFormatter.Serialize(ref buffer, ref offset, value.Value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref KeyValuePair<TKey, TValue> kvp)
		{
			TKey key = default;
			_keyFormatter.Deserialize(buffer, ref offset, ref key);

			TValue value = default;
			_valueFormatter.Deserialize(buffer, ref offset, ref value);

			kvp = new KeyValuePair<TKey, TValue>(key, value);
		}
	}
}