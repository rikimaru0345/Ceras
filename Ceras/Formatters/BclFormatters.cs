namespace Ceras.Formatters
{
	using System;
	using static SerializerBinary;

	public class BclFormatterResolver : Resolvers.IFormatterResolver
	{
		readonly DateTimeFormatter _dtf = new DateTimeFormatter();
		readonly GuidFormatter _gf = new GuidFormatter();

		public IFormatter GetFormatter(Type type)
		{
			if (type == typeof(DateTime))
				return _dtf;

			if (type == typeof(Guid))
				return _gf;

			return null;
		}
	}


	public class DateTimeFormatter : IFormatter<DateTime>
	{
		public void Serialize(ref byte[] buffer, ref int offset, DateTime value)
		{
			var v = value.ToBinary();
			WriteInt64Fixed(ref buffer, ref offset, v);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref DateTime value)
		{
			var v = ReadInt64Fixed(buffer, ref offset);
			value = DateTime.FromBinary(v);
		}
	}


	public class GuidFormatter : IFormatter<Guid>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Guid value)
		{
			WriteGuid(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Guid value)
		{
			value = ReadGuid(buffer, ref offset);
		}
	}
}
