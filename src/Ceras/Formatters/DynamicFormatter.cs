
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

	// todo: override formatters?

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
		static readonly MethodInfo ensureCapacityMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.EnsureCapacity));

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

			if (!schema.IsPrimary)
				throw new InvalidOperationException("Non-Primary Schema requires SchemaFormatter instead of DynamicFormatter!");

			if (schema.Members.Count == 0)
			{
				_serializer = (ref byte[] buffer, ref int offset, T value) => { };
				_deserializer = (byte[] buffer, ref int offset, ref T value) => { };
				return;
			}

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
			var bufferArg = Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var offsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
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

			// Merge Blitting Step
			List<SchemaMember> blittedMembers = null;
			if (!isSchemaFormatter && ceras.Config.Experimental.MergeBlittableCalls)
				blittedMembers = MergeBlittableSerializeCalls(members, typeToFormatter, body, bufferArg, offsetArg, valueArg);


			// Serialize all members
			foreach (var member in members)
			{
				if (member.IsSkip)
					continue;

				if (blittedMembers != null && blittedMembers.Contains(member))
					continue; // already written

				// Get the formatter and its Serialize method
				var formatterExp = typeToFormatter[member.MemberType];
				var formatter = formatterExp.Value;


				// Prepare the actual serialize call
				Expression serializeCall = EmitFormatterCall(
					formatterExp,
					bufferArg, offsetArg,
					member,
					MakeMemberAccess(valueArg, member.MemberInfo),
					true, ceras.Config.Experimental.InlineCalls);


				// Call "Serialize"
				if (!isSchemaFormatter)
				{
					body.Add(serializeCall);
				}
				else
				{
					// remember current position
					// startPos = offset; 
					body.Add(Assign(startPos, offsetArg));

					// reserve space for the length prefix
					// offset += 4;
					body.Add(AddAssign(offsetArg, Constant(FieldSizePrefixBytes)));

					// Serialize(...) write the actual data
					body.Add(serializeCall);

					// calculate the size of what we just wrote
					// size = (offset - startPos) - 4; 
					body.Add(Assign(size, Subtract(Subtract(offsetArg, startPos), Constant(FieldSizePrefixBytes))));

					// go back to where we started and write the size into the reserved space
					// offset = startPos;
					body.Add(Assign(offsetArg, startPos));

					// WriteInt32( size )
					body.Add(Call(
								   method: _sizeWriteMethod,
								   arg0: bufferArg,
								   arg1: offsetArg,
								   arg2: Convert(size, _sizeType)
								  ));

					// continue after the written data
					// offset = startPos + skipOffset; 
					body.Add(Assign(offsetArg, Add(Add(startPos, size), Constant(FieldSizePrefixBytes))));
				}
			}

			var serializeBlock = Block(variables: locals, expressions: body);

			if (isStatic)
				valueArg = Parameter(typeof(T), "value");

			return Lambda<SerializeDelegate<T>>(serializeBlock, bufferArg, offsetArg, valueArg);
		}

		internal static Expression<DeserializeDelegate<T>> GenerateDeserializer(CerasSerializer ceras, Schema schema, bool isSchemaFormatter, bool isStatic)
		{
			bool verifySizes = isSchemaFormatter && ceras.Config.VersionTolerance.VerifySizes;
			var members = schema.Members;
			var typeConfig = ceras.Config.GetTypeConfig(schema.Type, isStatic);
			var tc = typeConfig.TypeConstruction;

			bool callObjectConstructor = tc.HasDataArguments; // Are we responsible for instantiating an object?
			bool weHaveObject = !callObjectConstructor;
			HashSet<ParameterExpression> usedVariables = null; // track what variables the constructor already assigned

			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var offsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = isStatic ? null : Parameter(typeof(T).MakeByRefType(), "value"); // instance is null for static classes/members

			var body = new List<Expression>();
			var locals = new List<ParameterExpression>(schema.Members.Count);


			// BlockSize, OffsetStart (locals to store size of data)
			ParameterExpression blockSize = null, offsetStart = null;
			if (isSchemaFormatter)
			{
				locals.Add(blockSize = Variable(typeof(int), "blockSize"));
				locals.Add(offsetStart = Variable(typeof(int), "offsetStart"));
			}

			// Create local variable for each MemberInfo
			var memberInfoToLocal = new Dictionary<MemberInfo, ParameterExpression>();
			foreach (var m in members)
			{
				if (m.IsSkip)
					continue;

				if(!UseLocal(m.MemberInfo))
					continue;

				var local = Variable(m.MemberType, "local_" + m.MemberName);
				locals.Add(local);
				memberInfoToLocal.Add(m.MemberInfo, local);
			}

			// Type -> Constant(IFormatter<Type>)
			Dictionary<Type, ConstantExpression> typeToFormatter = members
				.Where(m => !m.IsSkip).DistinctBy(m => m.MemberType)
				.ToDictionary(m => m.MemberType, m => Constant(ceras.GetReferenceFormatter(m.MemberType)));


			//
			// 1. Initialize locals (if we have an object)
			foreach (var m in members)
			{
				// We can't pass properties by ref to Deserialize() so:
				// 1. localVar = obj.Prop;
				// 2. Deserialize(buffer, offset, ref localVar);
				// 3. obj.Prop = localVar;
				if (m.IsSkip)
					continue; // Member doesn't exist

				if (!UseLocal(m.MemberInfo))
					continue; // fields can be deserialized by ref

				// Init the local with the current value
				var local = memberInfoToLocal[m.MemberInfo];
				body.Add(Assign(local, MakeMemberAccess(valueArg, m.MemberInfo)));
			}

			//
			// 2. Deserialize into local (or field)
			foreach (var m in members)
			{
				// blockSize = ReadSize();
				if (isSchemaFormatter)
				{
					var readBlockSizeCall = Call(method: _sizeReadMethod, arg0: bufferArg, arg1: offsetArg);
					body.Add(Assign(blockSize, Convert(readBlockSizeCall, typeof(int))));

					if (verifySizes)
					{
						// Store the offset before reading the member so we can compare it later
						body.Add(Assign(offsetStart, offsetArg));
					}

					if (m.IsSkip)
					{
						// Skip over the field
						// offset += blockSize;
						body.Add(AddAssign(offsetArg, blockSize));
						continue;
					}
				}

				if (m.IsSkip && !isSchemaFormatter)
					throw new InvalidOperationException("DynamicFormatter can not skip members in non-schema mode");

				// Where do we serialize into?
				var target = UseLocal(m.MemberInfo)
					? (Expression)memberInfoToLocal[m.MemberInfo] // Local
					: MakeMemberAccess(valueArg, m.MemberInfo);   // value.Field

				// _formatter.Deserialize(...)
				var deserializeCall = EmitFormatterCall(typeToFormatter[m.MemberType],
					bufferArg, offsetArg,
					m, target,
					false, ceras.Config.Experimental.InlineCalls);

				body.Add(deserializeCall);


				// Compare blockSize with how much we've actually read
				if (isSchemaFormatter && verifySizes)
				{
					// if ( offsetStart + blockSize != offset )
					//     ThrowException();

					body.Add(IfThen(test: NotEqual(Add(offsetStart, blockSize), offsetArg),
									ifTrue: Call(instance: null, _offsetMismatchMethod, offsetStart, offsetArg, blockSize)));
				}
			}

			//
			// 3. Create object instance
			if (callObjectConstructor)
			{
				// Create a helper array for the implementing type construction
				var memberParameters = (
						from m in schema.Members
						where !m.IsSkip
						let local = memberInfoToLocal[m.MemberInfo]
						select new MemberParameterPair { LocalVar = local, Member = m.MemberInfo }
				).ToArray();

				usedVariables = new HashSet<ParameterExpression>();
				tc.EmitConstruction(schema, body, valueArg, usedVariables, memberParameters);
			}

			//
			// 4. Assign members (from locals)
			var orderedMembers = OrderMembersForWriteBack(members);
			foreach (var m in orderedMembers)
			{
				if (m.IsSkip)
					continue;

				if (!UseLocal(m.MemberInfo))
					continue; // Field was already deserialized directly

				var local = memberInfoToLocal[m.MemberInfo];

				if (usedVariables != null && usedVariables.Contains(local))
					continue; // Member was already used in the constructor

				if (m.MemberInfo is FieldInfo fieldInfo)
				{
					if (fieldInfo.IsInitOnly)
					{
						// Readonly field
						var memberConfig = typeConfig.Members.First(x => x.Member == m.MemberInfo);
						var rh = memberConfig.ComputeReadonlyHandling();
						DynamicFormatterHelpers.EmitReadonlyWriteBack(m.MemberType, rh, fieldInfo, valueArg, local, body);
					}
					else
					{
						// Normal assignment
						body.Add(Assign(left: Field(valueArg, fieldInfo),
										right: local));
					}
				}
				else
				{
					// Context
					var p = (PropertyInfo)m.MemberInfo;

					var setMethod = p.GetSetMethod(true);
					body.Add(Call(instance: valueArg, setMethod, local));
				}
			}

			//
			// 5. Call "OnAfterDeserialize()"
			EmitOnAfterDeserialize(body, schema, valueArg);



			var bodyBlock = Block(variables: locals, expressions: body);

			if (isStatic)
				valueArg = Parameter(typeof(T).MakeByRefType(), "value");

			return Lambda<DeserializeDelegate<T>>(bodyBlock, bufferArg, offsetArg, valueArg);

			bool UseLocal(MemberInfo memberInfo)
			{
				if (callObjectConstructor)
					return true;
				if (memberInfo is PropertyInfo)
					return true;
				if (memberInfo is FieldInfo field && field.IsInitOnly)
					return true;
				return false;
			}
		}

		static List<SchemaMember> MergeBlittableSerializeCalls(
			List<SchemaMember> members, Dictionary<Type, ConstantExpression> typeToFormatter,
			List<Expression> body,
			ParameterExpression bufferArg, ParameterExpression offsetArg, ParameterExpression valueArg)
		{
			List<SchemaMember> mergeBlitMembers = new List<SchemaMember>();

			//
			// Find what members can be merged
			int sizeSum = 0;
			for (int i = 0; i < members.Count; i++)
			{
				var m = members[i];
				if (!ReflectionHelper.IsBlittableType(m.MemberType))
					break;

				// Ensure we don't get tricked by some formatter
				var formatter = typeToFormatter[m.MemberType].Value;

				bool isValidReplacement = false;
				if (typeof(IIsReinterpretFormatter).IsAssignableFrom(formatter.GetType()))
					isValidReplacement = true;

				if (!isValidReplacement)
					throw new InvalidOperationException("Can not merge-blit formatter: " + formatter.GetType().FriendlyName(true));

				mergeBlitMembers.Add(m);
				sizeSum += ReflectionHelper.GetSize(m.MemberType);
			}

			if (mergeBlitMembers.Count == 0)
				return mergeBlitMembers;

			if (members.Count(m => ReflectionHelper.IsBlittableType(m.MemberType)) != mergeBlitMembers.Count)
				throw new Exception("Found a second group of blittable members, but all blittable members should have been sorted together into one big group!");

			var totalSizeConst = Constant(sizeSum, typeof(int));

			//
			// 1. EnsureCapacity with summed blit-size
			body.Add(Call(ensureCapacityMethod, bufferArg, offsetArg, totalSizeConst));

			//
			// 2. Write each individual member
			int runningOffset = 0;
			foreach (var member in mergeBlitMembers)
			{
				var methodName = nameof(ReinterpretFormatter<int>.Write);

				var writeMethod = typeof(ReinterpretFormatter<>)
					.MakeGenericType(member.MemberType)
					.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
					.First(m => m.Name == methodName);

				body.Add(Call(
					method: writeMethod,
					bufferArg,
					Add(offsetArg, Constant(runningOffset)),
					MakeMemberAccess(valueArg, member.MemberInfo)));

				runningOffset += ReflectionHelper.GetSize(member.MemberType);
			}

			//
			// 3. Add the total offset
			// offset += sizeSum;
			body.Add(AddAssign(offsetArg, totalSizeConst));

			return mergeBlitMembers;
		}


		static Expression EmitFormatterCall(
			ConstantExpression formatterConst,
			ParameterExpression bufferArg, ParameterExpression offsetArg,
			SchemaMember schemaMember,
			Expression target,
			bool isSerialize, bool allowCallInlining)
		{
			var formatter = (IFormatter)formatterConst.Value;

			if (allowCallInlining)
			{
				// Try inlining interface
				if (formatter is ICallInliner callInliner)
				{
					if (isSerialize)
						return callInliner.EmitSerialize(bufferArg, offsetArg, target);
					else
						return callInliner.EmitDeserialize(bufferArg, offsetArg, target);
				}

				// Try inlining blittable
				if (formatter is IIsReinterpretFormatter)
				{
					var methodName = isSerialize
						? nameof(ReinterpretFormatter<int>.Write)
						: nameof(ReinterpretFormatter<int>.Read);

					var method = typeof(ReinterpretFormatter<>)
						.MakeGenericType(schemaMember.MemberType)
						.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
						.First(m => m.Name == methodName);

					var size = ReflectionHelper.GetSize(schemaMember.MemberType);

					if (isSerialize)
					{
						return Block(
							// EnsureCapacity()
							Call(ensureCapacityMethod, bufferArg, offsetArg, Constant(size)),
							// ReinterpretFormatter<T>.Write()
							Call(method, bufferArg, offsetArg, target),
							// offset += sizeof(T)
							AddAssign(offsetArg, Constant(size))
							);
					}
					else
					{
						return Block(
							// ReinterpretFormatter<T>.Read()
							Call(method, bufferArg, offsetArg, target),
							// offset += sizeof(T)
							AddAssign(offsetArg, Constant(size))
							);
					}


				}
			}

			//
			// Normal call
			//
			var formatterMethod = isSerialize
				? formatter.GetType().ResolveSerializeMethod(schemaMember.MemberType)
				: formatter.GetType().ResolveDeserializeMethod(schemaMember.MemberType);

			return Call(
				instance: formatterConst,
				method: formatterMethod,
				bufferArg, offsetArg, target);
		}


		static void EmitOnAfterDeserialize(List<Expression> body, Schema schema, Expression valueArg)
		{
			var type = schema.Type;
			var allMethods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

			MethodInfo method = null;

			foreach (var m in allMethods)
			{
				if (m.GetCustomAttribute<OnAfterDeserializeAttribute>() != null)
				{
					if (m.ReturnType != typeof(void) || m.GetParameters().Length > 0)
						throw new InvalidOperationException($"Method '{m.Name}' is marked as '{nameof(OnAfterDeserializeAttribute)}', but doesn't return void or has parameters");

					if (method == null)
						method = m;
					else
						throw new InvalidOperationException($"Your type '{type.FriendlyName(false)}' has more than one method with the '{nameof(OnAfterDeserializeAttribute)}' attribute. Two methods found: '{m.Name}' and '{method.Name}'");
				}
			}

			if (method != null)
			{
				body.Add(Call(valueArg, method));
			}
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
