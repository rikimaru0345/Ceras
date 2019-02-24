
// ReSharper disable ArgumentsStyleOther
// ReSharper disable ArgumentsStyleNamedExpression
namespace Ceras.Formatters
{
	using Helpers;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using static System.Linq.Expressions.Expression;

	/*
	 * This formatter is used for every object-type that Ceras cannot deal with.
	 * It analyzes the members of the class or struct and compiles an optimized formatter for it.
	 * Polymorphism is handled in ReferenceFormatter.
	 */
	// todo: override formatters?
	// todo: merge-blitting ReinterpretFormatter<T>.Write_NoCheckNoAdvance()

	sealed class DynamicFormatter<T> : IFormatter<T>
	{
		// Schema field prefix
		const int FieldSizePrefixBytes = 4;
		static readonly Type _sizeType = typeof(uint);
		static readonly MethodInfo _sizeWriteMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteUInt32Fixed));
		static readonly MethodInfo _sizeReadMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadUInt32Fixed));


		readonly CerasSerializer _ceras;
		
		SerializeDelegate<T> _serializer;
		DeserializeDelegate<T> _deserializer;


		public DynamicFormatter(CerasSerializer serializer)
		{
			_ceras = serializer;

			var type = typeof(T);
			BannedTypes.ThrowIfNonspecific(type);

			var schema = _ceras.GetTypeMetaData(type).PrimarySchema;

			var typeConfig = _ceras.Config.GetTypeConfig(type);
			typeConfig.VerifyConstructionMethod();

			if (!schema.IsPrimary)
				throw new InvalidOperationException("Non-Primary Schema requires SchemaFormatter instead of DynamicFormatter!");

			if (schema.Members.Count == 0)
			{
				_serializer = (ref byte[] buffer, ref int offset, T value) => { };
				_deserializer = (byte[] buffer, ref int offset, ref T value) => { };
				return;
			}

			_serializer = GenerateSerializer(_ceras, schema, false).Compile();
			_deserializer = GenerateDeserializer(_ceras, schema, false).Compile();
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value) => _serializer(ref buffer, ref offset, value);

		public void Deserialize(byte[] buffer, ref int offset, ref T value) => _deserializer(buffer, ref offset, ref value);


		internal static Expression<SerializeDelegate<T>> GenerateSerializer(CerasSerializer ceras, Schema schema, bool isSchemaFormatter)
		{
			var members = schema.Members;
			var refBufferArg = Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Parameter(typeof(T), "value");

			var body = new List<Expression>();
			var locals = new List<ParameterExpression>();

			ParameterExpression startPos = null, size = null;
			if (isSchemaFormatter)
			{
				locals.Add(startPos = Variable(typeof(int), "startPos"));
				locals.Add(size = Variable(typeof(int), "size"));
			}
			
			Dictionary<Type, ConstantExpression> typeToFormatter = new Dictionary<Type, ConstantExpression>();
			foreach (var m in members.Where(m => !m.IsSkip).DistinctBy(m => m.MemberType))
				typeToFormatter.Add(m.MemberType, Constant(ceras.GetReferenceFormatter(m.MemberType)));


			// Serialize all members
			foreach (var member in members)
			{
				if (member.IsSkip)
					continue;

				// Get the formatter and its Serialize method
				var formatterExp = typeToFormatter[member.MemberType];
				var formatter = formatterExp.Value;
				var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
				Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter " + formatter.GetType().FullName);
				
				// Access the field that we want to serialize
				var fieldExp = MakeMemberAccess(valueArg, member.MemberInfo);

				// Call "Serialize"
				if (!isSchemaFormatter)
				{
					var serializeCall = Call(formatterExp, serializeMethod, refBufferArg, refOffsetArg, fieldExp);
					body.Add(serializeCall);
				}
				else
				{
					// remember current position
					// startPos = offset; 
					body.Add(Assign(startPos, refOffsetArg));

					// reserve space for the length prefix
					// offset += 4;
					body.Add(AddAssign(refOffsetArg, Constant(FieldSizePrefixBytes)));

					// Serialize(...) write the actual data
					body.Add(Call(
								   instance: formatterExp,
								   method: serializeMethod,
								   arg0: refBufferArg,
								   arg1: refOffsetArg,
								   arg2: MakeMemberAccess(valueArg, member.MemberInfo)
								  ));

					// calculate the size of what we just wrote
					// size = (offset - startPos) - 4; 
					body.Add(Assign(size, Subtract(Subtract(refOffsetArg, startPos), Constant(FieldSizePrefixBytes))));

					// go back to where we started and write the size into the reserved space
					// offset = startPos;
					body.Add(Assign(refOffsetArg, startPos));

					// WriteInt32( size )
					body.Add(Call(
								   method: _sizeWriteMethod,
								   arg0: refBufferArg,
								   arg1: refOffsetArg,
								   arg2: Convert(size, _sizeType)
								  ));

					// continue after the written data
					// offset = startPos + skipOffset; 
					body.Add(Assign(refOffsetArg, Add(Add(startPos, size), Constant(FieldSizePrefixBytes))));
				}
			}

			var serializeBlock = Block(variables: locals, expressions: body);

			return Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg);
		}

		internal static Expression<DeserializeDelegate<T>> GenerateDeserializer(CerasSerializer ceras, Schema schema, bool isSchemaFormatter)
		{
			var members = schema.Members;
			var typeConfig = ceras.Config.GetTypeConfig(schema.Type);
			var tc = typeConfig.TypeConstruction;

			bool constructObject = tc.HasDataArguments; // Are we responsible for instantiating an object?
			HashSet<ParameterExpression> usedVariables = null;

			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Parameter(typeof(T).MakeByRefType(), "value");

			var body = new List<Expression>();
			var locals = new List<ParameterExpression>(schema.Members.Count);

			ParameterExpression blockSize = null;
			if (isSchemaFormatter)
			{
				blockSize = Variable(typeof(int), "blockSize");
				locals.Add(blockSize);
			}
			

			// MemberInfo -> Variable()
			Dictionary<MemberInfo, ParameterExpression> memberInfoToLocal = new Dictionary<MemberInfo, ParameterExpression>();
			foreach (var m in members)
			{
				if (m.IsSkip)
					continue;

				var local = Variable(m.MemberType, m.MemberName + "_local");
				locals.Add(local);
				memberInfoToLocal.Add(m.MemberInfo, local);
			}

			// Type -> Constant(IFormatter<Type>)
			Dictionary<Type, ConstantExpression> typeToFormatter = new Dictionary<Type, ConstantExpression>();
			foreach (var m in members.Where(m => !m.IsSkip).DistinctBy(m => m.MemberType))
				typeToFormatter.Add(m.MemberType, Constant(ceras.GetReferenceFormatter(m.MemberType)));


			//
			// 1. Read existing values into locals (Why? See explanation at the end of the file)
			foreach (var m in members)
			{
				if (constructObject)
					continue; // Can't read existing data when there is no object yet...

				if (m.IsSkip)
					continue; // Member doesn't exist

				// Init the local with the current value
				var local = memberInfoToLocal[m.MemberInfo];
				body.Add(Assign(local, MakeMemberAccess(refValueArg, m.MemberInfo)));
			}

			//
			// 2. Deserialize into local (faster and more robust than field/prop directly)
			foreach (var m in members)
			{
				if (isSchemaFormatter)
				{
					// Read block size
					// blockSize = ReadSize();
					var readCall = Call(method: _sizeReadMethod, arg0: bufferArg, arg1: refOffsetArg);
					body.Add(Assign(blockSize, Convert(readCall, typeof(int))));

					if (m.IsSkip)
					{
						// Skip over the field
						// offset += blockSize;
						body.Add(AddAssign(refOffsetArg, blockSize));
						continue;
					}
				}

				if (m.IsSkip && !isSchemaFormatter)
					throw new InvalidOperationException("DynamicFormatter can not skip members in non-schema mode");
				

				var formatterExp = typeToFormatter[m.MemberType];
				var formatter = formatterExp.Value;
				var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
				Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter " + formatter.GetType().FullName);
				
				var local = memberInfoToLocal[m.MemberInfo];
				body.Add(Call(formatterExp, deserializeMethod, bufferArg, refOffsetArg, local));

				// todo: In schema-mode, we could check if the expected blockSize matches what we've actually read
			}

			//
			// 3. Create object instance (only when actually needed)
			if (constructObject)
			{
				// Create a helper array for the implementing type construction
				var memberParameters = (
						from m in schema.Members
						where !m.IsSkip
						let local = memberInfoToLocal[m.MemberInfo]
						select new MemberParameterPair {LocalVar = local, Member = m.MemberInfo}
				).ToArray();

				usedVariables = new HashSet<ParameterExpression>();
				tc.EmitConstruction(schema, body, refValueArg, usedVariables, memberParameters);
			}

			//
			// 4. Write back values in one batch
			foreach (var m in members)
			{
				if (m.IsSkip)
					continue;

				var local = memberInfoToLocal[m.MemberInfo];
				var type = m.MemberType;

				if (usedVariables != null && usedVariables.Contains(local))
					// Member was already used in the constructor / factory method, no need to write it again
					continue;

				if (m.IsSkip)
					continue;

				if (m.MemberInfo is FieldInfo fieldInfo)
				{
					if (fieldInfo.IsInitOnly)
					{
						// Readonly field
						var memberConfig = typeConfig.Members.First(x => x.Member == m.MemberInfo);
						var rh = memberConfig.ComputeReadonlyHandling();
						DynamicFormatterHelpers.EmitReadonlyWriteBack(type, rh, fieldInfo, refValueArg, local, body);
					}
					else
					{
						// Normal assignment
						body.Add(Assign(left: Field(refValueArg, fieldInfo),
										right: local));
					}
				}
				else
				{
					// Context
					var p = (PropertyInfo)m.MemberInfo;

					var setMethod = p.GetSetMethod(true);
					body.Add(Call(instance: refValueArg, setMethod, local));
				}
			}


			var bodyBlock = Block(variables: locals, expressions: body);

			return Lambda<DeserializeDelegate<T>>(bodyBlock, bufferArg, refOffsetArg, refValueArg);
		}
	}
}
