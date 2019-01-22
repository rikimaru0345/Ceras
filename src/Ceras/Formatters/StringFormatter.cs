namespace Ceras.Formatters
{
	sealed class StringFormatter : IFormatter<string>
	{
		public void Serialize(ref byte[] buffer, ref int offset, string value)
		{
			SerializerBinary.WriteString(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref string value)
		{
			value = SerializerBinary.ReadString(buffer, ref offset);
		}
	}
}