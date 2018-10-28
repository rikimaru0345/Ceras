using System.Collections.Generic;

namespace Ceras.Helpers
{
	using Formatters;
	using System;
	using System.Diagnostics;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using static System.Linq.Expressions.Expression;

	// todo 1: if we read the same block (containing a schema) twice, we need to recognize that it's the same and re-use the DynamicSchemaFormatter
	//			-> but that does not really happen, does it? every data-block contains only one schema per type.
	//			-> but what about serializing/deserializing multiple times?
	// todo 2: have a dictionary for known namespaces we write directly (without schema bc they never change)

	class SchemaDynamicFormatter<T> : IFormatter<T>
	{
		CerasSerializer _ceras;

		readonly Schema _schema;

		SerializeDelegate<T> _serializer;
		DeserializeDelegate<T> _deserializer;


		const int FieldSizePrefixBytes = 2;
		static readonly Type SizeType = typeof(short);
		static readonly MethodInfo SizeWriteMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteInt16Fixed));
		static readonly MethodInfo SizeReadMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadInt16Fixed));


		public SchemaDynamicFormatter(CerasSerializer ceras, Schema schema)
		{
			_ceras = ceras;
			_schema = schema;

			_serializer = GenerateSerializer(schema);
			_deserializer = GenerateDeserializer(schema);
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			_ceras.InstanceData.WrittenSchemata.Add(_schema);
			_serializer(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			_deserializer(buffer, ref offset, ref value);
		}


		SerializeDelegate<T> GenerateSerializer(Schema schema)
		{
			var refBufferArg = Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Parameter(typeof(T), "value");

			// todo: have a lookup list to directly get the actual 'SerializerBinary' method. There is no reason to actually use objects like "Int32Formatter"

			List<Expression> block = new List<Expression>();
			
			var startPos = Parameter(typeof(int), "startPos");
			var size = Parameter(typeof(int), "size");

			foreach (var schemaEntry in schema.Members)
			{
				if (schemaEntry.IsSkip)
					continue;

				var member = schemaEntry.Member;
				var type = member.MemberType;

				// Get Serialize method
				var formatter = _ceras.GetGenericFormatter(type);
				var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
				Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter");

				// startPos = offset; 
				block.Add(Assign(startPos, refOffsetArg));

				// offset += 4;
				block.Add(AddAssign(refOffsetArg, Constant(FieldSizePrefixBytes)));

				// Serialize(...)
				block.Add(Call(
							   instance: Constant(formatter),
							   method: serializeMethod,
							   arg0: refBufferArg,
							   arg1: refOffsetArg,
							   arg2: MakeMemberAccess(valueArg, member.MemberInfo)
						  ));

				// size = (offset - startPos) - 4;
				block.Add(Assign(size, Subtract(Subtract(refOffsetArg, startPos), Constant(FieldSizePrefixBytes))));

				// offset = startPos;
				block.Add(Assign(refOffsetArg, startPos));

				// WriteInt32( size )
				block.Add(Call(
							   method: SizeWriteMethod,
							   arg0: refBufferArg,
							   arg1: refOffsetArg,
							   arg2: Convert(size, SizeType)
							   ));

				// offset = startPos + skipOffset;
				block.Add(Assign(refOffsetArg, Add(Add(startPos, size), Constant(FieldSizePrefixBytes))));

			}

			var serializeBlock = Block(variables: new[] { startPos, size }, expressions: block);

#if FAST_EXP
			return Expression.Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg).CompileFast(true);
#else
			return Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg).Compile();
#endif

		}

		DeserializeDelegate<T> GenerateDeserializer(Schema schema)
		{
			/*
			 * We got a schema (read from the data), and need to use it to read things in the right order
			 * and skip blocks that we want to skip
			 */

			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Parameter(typeof(T).MakeByRefType(), "value");

			List<Expression> block = new List<Expression>();
			
			var blockSize = Variable(typeof(int), "blockSize");

			foreach (var sMember in schema.Members)
			{
				var member = sMember.Member;

				// 1. Read block size
				block.Add(Assign(left: blockSize,
								 right: Convert(Call(method: SizeReadMethod, arg0: bufferArg, arg1: refOffsetArg), typeof(int))));

				if (sMember.IsSkip)
				{
					// 2. a) Skip over the field
					block.Add(AddAssign(refOffsetArg, blockSize));
				}
				else
				{
					// 2. b) read normally
					var type = member.MemberType;
					IFormatter formatter = _ceras.GetGenericFormatter(type);

					var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
					Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter");

					var fieldExp = MakeMemberAccess(refValueArg, member.MemberInfo);

					var serializeCall = Call(Constant(formatter), deserializeMethod, bufferArg, refOffsetArg, fieldExp);
					block.Add(serializeCall);
				}
			}


			var serializeBlock = Block(variables: new ParameterExpression[] { blockSize }, expressions: block);
#if FAST_EXP
			return Expression.Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).CompileFast(true);
#else
			return Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).Compile();
#endif

		}


	}

}


/*
 * ConstructorFormatter
 * - remove the "CreateInstanceIfNeeded" 
 * - replace all "read into member" with "read into local"
 * - add "call constructor (which could also be a normal static method) from locals"
 * - set remaining members from locals
 */
