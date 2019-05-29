using System.Linq.Expressions;

namespace Ceras.Formatters
{
	sealed class StringFormatter : IFormatter<string>, ICallInliner
	{
		public void Serialize(ref byte[] buffer, ref int offset, string value)
		{
			SerializerBinary.WriteString(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref string value)
		{
			value = SerializerBinary.ReadString(buffer, ref offset);
		}

				
		public Expression EmitSerialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteString));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}

		public Expression EmitDeserialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadString));
			return Expression.Assign(target, 
				Expression.Call(method, bufferArg, offsetArg));
		}
	}

	sealed class MaxSizeStringFormatter : IFormatter<string>, ICallInliner
	{
		readonly uint _maxLength;

		public MaxSizeStringFormatter(uint maxLength)
		{
			_maxLength = maxLength;
		}

		public void Serialize(ref byte[] buffer, ref int offset, string value)
		{
			SerializerBinary.WriteString(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref string value)
		{
			value = SerializerBinary.ReadStringLimited(buffer, ref offset, _maxLength);
		}

		
		public Expression EmitSerialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteString));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}
		
		public Expression EmitDeserialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadString));
			return Expression.Assign(target, 
				Expression.Call(method, bufferArg, offsetArg, Expression.Constant(_maxLength)));
		}
	}
}