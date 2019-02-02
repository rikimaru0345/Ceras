namespace Ceras.Resolvers
{
	using Formatters;
	using System.Linq.Expressions;

	public sealed class EnumFormatter<T> : IFormatter<T>
	{
		delegate void WriteEnum(ref byte[] buffer, ref int offset, T enumVal);
		delegate void ReadEnum(byte[] buffer, ref int offset, out T enumVal);

		WriteEnum _enumWriter;
		ReadEnum _enumReader;

		public EnumFormatter(CerasSerializer serializer)
		{
			var refBuffer = Expression.Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffset = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var value = Expression.Parameter(typeof(T), "value");


			//
			// Generate writer, previously we wrote as int32, but now we use the real underyling type
			var enumBaseType = typeof(T).GetEnumUnderlyingType();
			var formatter = serializer.GetSpecificFormatter(enumBaseType);

			var writeMethod = formatter.GetType().GetMethod("Serialize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

			var converted = Expression.Convert(value, enumBaseType);
			var writeCall = Expression.Call(instance: Expression.Constant(formatter), method: writeMethod, arg0: refBuffer, arg1: refOffset, arg2: converted);

			_enumWriter = Expression.Lambda<WriteEnum>(writeCall, refBuffer, refOffset, value).Compile();


			//
			// Generate reader
			// First figure out what kind of reader we need and then use it (just the exact inverse of writing)

			var buffer = Expression.Parameter(typeof(byte[]), "buffer");
			var refValue = Expression.Parameter(typeof(T).MakeByRefType(), "value");

			// Deserialize(byte[] buffer, ref int offset, ref T value)
			var readMethod = formatter.GetType().GetMethod("Deserialize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

			// We write/read different types. The 'T' we're serializing is (for example) "MyCoolEnum", but we actually write the base type, which is (in this example) "System.Int32"
			// That's why we need a local variable, into which we deserialize, and then we convert and write-back the deserialized value to the refValue parameter
			var tempLocal = Expression.Variable(enumBaseType, "temp");

			var readCall = Expression.Call(instance: Expression.Constant(formatter), method: readMethod, arg0: buffer, arg1: refOffset, arg2: tempLocal);
			var convertBack = Expression.Convert(tempLocal, typeof(T));
			var backAssignment = Expression.Assign(refValue, convertBack);

			BlockExpression deserializationBlock = Expression.Block(
																	variables: new ParameterExpression[] { tempLocal },
																	expressions: new Expression[] { readCall, backAssignment }
																	);

			_enumReader = Expression.Lambda<ReadEnum>(deserializationBlock, buffer, refOffset, refValue).Compile();
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			_enumWriter(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			_enumReader(buffer, ref offset, out value);
		}
	}
}