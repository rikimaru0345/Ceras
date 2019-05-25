namespace Ceras.Resolvers
{
	using System;
	using Formatters;
	using System.Linq.Expressions;
    using System.Runtime.CompilerServices;

	/// <summary>
	/// This formatter uses enum.ToString() and Enum.Parse(). It is not used by default, but you can activate through the type config if you want to.
	/// </summary>
	public sealed class EnumAsStringFormatter<T> : IFormatter<T> where T : System.Enum
	{
		ISizeLimitsConfig _sizeLimits;

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			var str = value.ToString();
			SerializerBinary.WriteString(ref buffer, ref offset, str);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			var maxStrLen = _sizeLimits.MaxStringLength;
			var str = SerializerBinary.ReadStringLimited(buffer, ref offset, maxStrLen);

			value = (T)System.Enum.Parse(typeof(T), str);
		}
	}
}