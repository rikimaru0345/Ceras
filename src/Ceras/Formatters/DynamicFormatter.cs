
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
	 * This is Ceras' "main" formatter, used for every complex type.
	 * Given a "Schema" it compiles an optimized formatter for it.
	 */

	abstract class DynamicFormatter
	{
		internal abstract void Initialize();
	}

	sealed class DynamicFormatter<T> : DynamicFormatter, IFormatter<T>
	{
		// Schema field prefix
		const int FieldSizePrefixBytes = 4;
		static readonly Type _sizeType = typeof(uint);
		static readonly MethodInfo _sizeWriteMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteUInt32Fixed));
		static readonly MethodInfo _sizeReadMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadUInt32Fixed));
		static readonly MethodInfo _offsetMismatchMethod = ReflectionHelper.GetMethod(() => ThrowOffsetMismatch(0, 0, 0));

		readonly CerasSerializer _ceras;

		SerializeDelegate<T> _serializer;
		DeserializeDelegate<T> _deserializer;

		readonly bool _isStatic;
		readonly Schema _schema;

		public DynamicFormatter(CerasSerializer serializer, bool isStatic)
		{
			_ceras = serializer;

			var type = typeof(T);
			BannedTypes.ThrowIfNonspecific(type);

			var schema = isStatic
					? _ceras.GetStaticTypeMetaData(type).PrimarySchema
					: _ceras.GetTypeMetaData(type).PrimarySchema;

			var typeConfig = _ceras.Config.GetTypeConfig(type, isStatic);
			typeConfig.VerifyConstructionMethod();

			if (!schema.IsPrimary)
				throw new InvalidOperationException("Non-Primary Schema requires SchemaFormatter instead of DynamicFormatter!");

			_isStatic = isStatic;
			_schema = schema;
		}

		internal override void Initialize()
		{
			if (_serializer != null)
				return;

			// If we are getting constructed by a ReferenceFormatter, and one of our members
			// depends on that same ReferenceFormatter we'll end up with a StackOverflowException.
			// To solve this we just delay the compile step until after the constructor is done.
			_serializer = GenerateSerializer(_ceras, _schema, false, _isStatic).Compile();
			_deserializer = GenerateDeserializer(_ceras, _schema, false, _isStatic).Compile();
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value) => _serializer(ref buffer, ref offset, value);

		public void Deserialize(byte[] buffer, ref int offset, ref T value) => _deserializer(buffer, ref offset, ref value);


		internal static Expression<SerializeDelegate<T>> GenerateSerializer(CerasSerializer ceras, Schema schema, bool isSchemaFormatter, bool isStatic)
		{
			var members = schema.Members;
			var refBufferArg = Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Parameter(typeof(T), "value");

			if (isStatic)
				valueArg = null;

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

			/* 
			 * Temporary disabled until v5 is released
			 * 
			// Merge Blitting Step
			if (!isSchemaFormatter)
				MergeBlittableSerializeCalls(members, typeToFormatter);
			*/

			if (!isStatic)
				EmitCallToAttribute(ceras, body, valueArg, schema.Type, typeof(OnBeforeSerializeAttribute), false);

			// Serialize all members
			foreach (var member in members)
			{
				if (member.IsSkip)
					continue;

				// Get the formatter and its Serialize method
				var formatterExp = typeToFormatter[member.MemberType];
				var formatter = formatterExp.Value;
				var serializeMethod = formatter.GetType().ResolveSerializeMethod(member.MemberType);

				// Prepare the actual serialize call
				var serializeCall = Call(formatterExp, serializeMethod, refBufferArg, refOffsetArg, MakeMemberAccess(valueArg, member.MemberInfo));


				// Call "Serialize"
				if (!isSchemaFormatter)
				{
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
					body.Add(serializeCall);

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


			if (!isStatic)
				EmitCallToAttribute(ceras, body, valueArg, schema.Type, typeof(OnAfterSerializeAttribute), false);

			if(body.Count == 0)
				body.Add(Empty());

			var serializeBlock = Block(variables: locals, expressions: body);

			if (isStatic)
				valueArg = Parameter(typeof(T), "value");

			return Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg);
		}

		static void MergeBlittableSerializeCalls(List<SchemaMember> members, Dictionary<Type, ConstantExpression> typeToFormatter)
		{
			List<SchemaMember> mergeBlitMembers = new List<SchemaMember>();

			for (int i = 0; i < members.Count; i++)
			{
				var m = members[i];
				if (!ReflectionHelper.IsBlittableType(m.MemberType))
					break;

				mergeBlitMembers.Add(m);
			}

#if DEBUG
			if (members.Count(m => ReflectionHelper.IsBlittableType(m.MemberType)) != mergeBlitMembers.Count)
				throw new Exception("Found a second group of blittable members, but all blittable members should have been sorted together into one big group!");
#endif


			for (int i = 0; i < mergeBlitMembers.Count; i++)
			{
				var m = mergeBlitMembers[i];
				var formatter = (IInlineEmitter)typeToFormatter[m.MemberType].Value;

			}

		}

		internal static Expression<DeserializeDelegate<T>> GenerateDeserializer(CerasSerializer ceras, Schema schema, bool isSchemaFormatter, bool isStatic)
		{
			bool verifySizes = isSchemaFormatter && ceras.Config.VersionTolerance.VerifySizes;
			var members = schema.Members;
			var typeConfig = ceras.Config.GetTypeConfig(schema.Type, isStatic);
			var tc = typeConfig.TypeConstruction;

			bool constructObject = tc.HasDataArguments; // Are we responsible for instantiating an object?
			HashSet<ParameterExpression> usedVariables = null;

			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Parameter(typeof(T).MakeByRefType(), "value");
			if (isStatic)
				refValueArg = null;

			var body = new List<Expression>();
			var locals = new List<ParameterExpression>(schema.Members.Count);

			ParameterExpression blockSize = null, offsetStart = null;
			if (isSchemaFormatter)
			{
				locals.Add(blockSize = Variable(typeof(int), "blockSize"));
				locals.Add(offsetStart = Variable(typeof(int), "offsetStart"));
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
			// 1. OnBeforeDeserialize
			if (!isStatic)
				EmitCallToAttribute(ceras, body, refValueArg, schema.Type, typeof(OnBeforeDeserializeAttribute), true);

			//
			// 2. Read existing values into locals (Why? See explanation at the end of the file)
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
			// 3. Deserialize into local (faster and more robust than field/prop directly)
			foreach (var m in members)
			{
				if (isSchemaFormatter)
				{
					// Read block size
					// blockSize = ReadSize();
					var readCall = Call(method: _sizeReadMethod, arg0: bufferArg, arg1: refOffsetArg);
					body.Add(Assign(blockSize, Convert(readCall, typeof(int))));

					if (verifySizes)
					{
						// Store the offset before reading the member so we can compare it later
						body.Add(Assign(offsetStart, refOffsetArg));
					}


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
				var deserializeMethod = formatter.GetType().ResolveDeserializeMethod(m.MemberType);

				var local = memberInfoToLocal[m.MemberInfo];
				body.Add(Call(formatterExp, deserializeMethod, bufferArg, refOffsetArg, local));

				if (isSchemaFormatter && verifySizes)
				{
					// Compare blockSize with how much we've actually read
					// if ( offsetStart + blockSize != offset )
					//     ThrowException();

					body.Add(IfThen(test: NotEqual(Add(offsetStart, blockSize), refOffsetArg),
									ifTrue: Call(instance: null, _offsetMismatchMethod, offsetStart, refOffsetArg, blockSize)));
				}
			}

			//
			// 4. Create object instance (only when actually needed)
			if (constructObject)
			{
				// Create a helper array for the implementing type construction
				var memberParameters = (
						from m in schema.Members
						where !m.IsSkip
						let local = memberInfoToLocal[m.MemberInfo]
						select new MemberParameterPair { LocalVar = local, Member = m.MemberInfo }
				).ToArray();

				usedVariables = new HashSet<ParameterExpression>();
				tc.EmitConstruction(schema, body, refValueArg, usedVariables, memberParameters);
			}

			//
			// 5. Write back values in one batch
			var orderedMembers = OrderMembersForWriteBack(members);
			foreach (var m in orderedMembers)
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
						var memberConfig = typeConfig._allMembers.First(x => x.Member == m.MemberInfo);
						var rh = memberConfig.ComputeReadonlyHandling();
						DynamicFormatterHelpers.EmitReadonlyWriteBack(type, rh, fieldInfo, refValueArg, local, body, locals);
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

			//
			// 6. OnAfterDeserialize
			if (!isStatic)
				EmitCallToAttribute(ceras, body, refValueArg, schema.Type, typeof(OnAfterDeserializeAttribute), false);

			
			if(body.Count == 0)
				body.Add(Empty());

			var bodyBlock = Block(variables: locals, expressions: body);


			if (isStatic)
				refValueArg = Parameter(typeof(T).MakeByRefType(), "value");

			return Lambda<DeserializeDelegate<T>>(bodyBlock, bufferArg, refOffsetArg, refValueArg);
		}


		static void EmitCallToAttribute(CerasSerializer ceras, List<Expression> block, Expression value, Type objectType, Type attributeType, bool checkIfObjectIsNull)
		{
			var method = GetMethodByAttribute(objectType, attributeType);
			if (method == null)
				return;

			if (objectType.IsValueType)
				checkIfObjectIsNull = false;

			MethodCallExpression call;
			if (method.GetParameters().Length == 0)
				call = Call(value, method);
			else
				call = Call(value, method, Constant(ceras));

			if (checkIfObjectIsNull)
			{
				var valueIsNotNull = Not(ReferenceEqual(value, Constant(null, typeof(object))));
				var checkAndCall = IfThen(valueIsNotNull, call);
				block.Add(checkAndCall);
			}
			else
			{
				block.Add(call);
			}
		}

		static MethodInfo GetMethodByAttribute(Type objectType, Type attributeType)
		{
			var allMethods = objectType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

			MethodInfo method = null;

			foreach (var m in allMethods)
			{
				if (m.GetCustomAttribute(attributeType) == null)
					continue;

				if (m.ReturnType != typeof(void))
					throw new InvalidOperationException($"Method '{objectType.Name}.{m.Name}' is marked as '{attributeType.Name}' so it must return 'void'. But it currently returns '{m.ReturnType.FriendlyName(true)}'. ");

				var parameters = m.GetParameters();
				if ((parameters.Length == 1 && parameters[0].ParameterType != typeof(CerasSerializer))
					|| parameters.Length >= 2)
					throw new InvalidOperationException($"Method '{objectType.Name}.{m.Name}' must either take a '{nameof(CerasSerializer)}' or zero arguments.");

				if (method == null)
					method = m;
				else
					throw new InvalidOperationException($"Your type '{objectType.FriendlyName(false)}' has more than one method with the '{attributeType.Name}' attribute. Two methods found: '{m.Name}' and '{method.Name}'");
			}

			return method;
		}

		static IEnumerable<SchemaMember> OrderMembersForWriteBack(List<SchemaMember> members)
		{
			return from m in members
				   orderby m.WriteBackOrder ascending, members.IndexOf(m)
				   select m;
		}

		static void ThrowOffsetMismatch(int startOffset, int offset, int blockSize)
		{
			throw new InvalidOperationException($"The data being read is corrupted. The amount of data read did not match the expected block-size! BlockStart:{startOffset} BlockSize:{blockSize} CurrentOffset:{offset}");
		}
	}
}
