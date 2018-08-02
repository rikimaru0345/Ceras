namespace Ceras.Formatters
{
	using System;

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
			SerializerBinary.WriteInt64Fixed(ref buffer, ref offset, v);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref DateTime value)
		{
			var v = SerializerBinary.ReadInt64Fixed(buffer, ref offset);
			value = DateTime.FromBinary(v);
		}
	}


	public class GuidFormatter : IFormatter<Guid>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Guid value)
		{
			SerializerBinary.WriteString(ref buffer, ref offset, value.ToString("N"));
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Guid value)
		{
			var guidStr = SerializerBinary.ReadString(buffer, ref offset);
			value = Guid.ParseExact(guidStr, "N");
		}
	}
}
