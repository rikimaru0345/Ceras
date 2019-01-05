
// ReSharper disable ArgumentsStyleOther
// ReSharper disable ArgumentsStyleNamedExpression
namespace Ceras.Formatters
{
	using Helpers;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq.Expressions;
	using System.Reflection;

#if FAST_EXP
	using FastExpressionCompiler;
#endif


	// todo: Can we use a static-generic as a cache instead of dict? Is that even possible in our case? Would we even save anything? How much would it be faster?
	class DynamicObjectFormatter<T> : IFormatter<T>
	{
		static readonly MethodInfo SetValue = typeof(FieldInfo).GetMethod(
				name: "SetValue",
				bindingAttr: BindingFlags.Instance | BindingFlags.Public,
				binder: null, 
				types: new Type[] { typeof(object), typeof(object) },
				modifiers: new ParameterModifier[2]);

		CerasSerializer _ceras;
		SerializeDelegate<T> _dynamicSerializer;
		DeserializeDelegate<T> _dynamicDeserializer;

		public DynamicObjectFormatter(CerasSerializer serializer)
		{
			_ceras = serializer;

			var type = typeof(T);

			BannedTypes.ThrowIfBanned(type);
			BannedTypes.ThrowIfNonspecific(type);

			var schema = _ceras.SchemaDb.GetOrCreatePrimarySchema(type);

			if (schema.Members.Count > 0)
			{
				_dynamicSerializer = GenerateSerializer(schema.Members);
				_dynamicDeserializer = GenerateDeserializer(schema.Members);
			}
			else
			{
				_dynamicSerializer = (ref byte[] buffer, ref int offset, T value) => { };
				_dynamicDeserializer = (byte[] buffer, ref int offset, ref T value) => { };
			}
		}


		SerializeDelegate<T> GenerateSerializer(List<SchemaMember> members)
		{
			var refBufferArg = Expression.Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Expression.Parameter(typeof(T), "value");

			List<Expression> block = new List<Expression>();


			foreach (var sMember in members)
			{
				var member = sMember.Member;
				var type = member.MemberType;

				// todo: have a lookup list to directly get the actual 'SerializerBinary' method. There is no reason to actually use objects like "Int32Formatter"
				var formatter = _ceras.GetReferenceFormatter(type);

				// Get the formatter and its Serialize method
				// var formatter = _ceras.GetFormatter(fieldInfo.FieldType, extraErrorInformation: $"DynamicObjectFormatter ObjectType: {specificType.FullName} FieldType: {fieldInfo.FieldType.FullName}");
				var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
				Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter " + formatter.GetType().FullName);

				// Access the field that we want to serialize
				var fieldExp = Expression.MakeMemberAccess(valueArg, member.MemberInfo);

				// Call "Serialize"
				var serializeCall = Expression.Call(Expression.Constant(formatter), serializeMethod, refBufferArg, refOffsetArg, fieldExp);
				block.Add(serializeCall);
			}

			var serializeBlock = Expression.Block(expressions: block);

#if FAST_EXP
			return Expression.Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg).CompileFast(true);
#else
			return Expression.Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg).Compile();
#endif

		}

		DeserializeDelegate<T> GenerateDeserializer(List<SchemaMember> members)
		{
			var bufferArg = Expression.Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Expression.Parameter(typeof(T).MakeByRefType(), "value");

			List<Expression> block = new List<Expression>();

			List<ParameterExpression> locals = new List<ParameterExpression>();

			// Go through all fields and assign them
			foreach (var sMember in members)
			{
				var member = sMember.Member;
				var type = member.MemberType;
				// todo: what about Field attributes that tell us to:
				// - use a specific formatter? (HalfFloat, Int32Fixed, MyUserFormatter) 
				// - assume a type, or exception
				// - Force ignore caching (as in not using the reference formatter)

				IFormatter formatter = _ceras.GetReferenceFormatter(type);

				var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
				Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter " + formatter.GetType().FullName);


				//
				// We have everything we need to read and then assign the member.
				// But what if the member is readonly?

				if (member.MemberInfo is FieldInfo fieldInfo)
					if (fieldInfo.IsInitOnly)
					{
						if (_ceras.Config.ReadonlyFieldHandling == ReadonlyFieldHandling.Off)
							throw new InvalidOperationException($"Error while trying to generate a deserializer for the field '{member.MemberInfo.DeclaringType.FullName}.{member.MemberInfo.Name}' and the field is readonly, but ReadonlyFieldHandling is turned off in the serializer configuration.");

						// We just read the value into a local variable first,
						// for value types we overwrite,
						// for objects we type-check, and if the type matches we populate the fields as normal
						
						// 1. Read the data into a new local variable
						var tempStore = Expression.Variable(type, member.Name + "_tempStoreForReadonly");
						locals.Add(tempStore);

						// 2. Init the local with the current value
						block.Add(Expression.Assign(tempStore, Expression.MakeMemberAccess(refValueArg, member.MemberInfo)));

						// 3. Read the value as usual, but use the temp variable as target
						var tempReadCall = Expression.Call(Expression.Constant(formatter), deserializeMethod, bufferArg, refOffsetArg, tempStore);
						block.Add(tempReadCall);

						// 4. ReferenceTypes and ValueTypes are handled a bit differently (Boxing, Equal-vs-ReferenceEqual, text in exception, ...)
						if (type.IsValueType)
						{
							// Value types are simple.
							// Either they match perfectly -> do nothing
							// Or the values are not the same -> either throw an exception of do a forced overwrite

							Expression onMismatch;
							if (_ceras.Config.ReadonlyFieldHandling == ReadonlyFieldHandling.ForcedOverwrite)
								// field.SetValue(valueArg, tempStore)
								onMismatch = Expression.Call(Expression.Constant(fieldInfo), SetValue, arg0: refValueArg, arg1: Expression.Convert(tempStore, typeof(object))); // Explicit boxing needed
							else
								onMismatch = Expression.Throw(Expression.Constant(new Exception($"The value-type in field '{fieldInfo.Name}' does not match the expected value, but the field is readonly and overwriting is not allowed in the serializer configuration. Fix the value, or make the field writeable, or enable 'ForcedOverwrite' in the serializer settings to allow Ceras to overwrite the readonly-field.")));

							block.Add(Expression.IfThenElse(
								test: Expression.Equal(tempStore, Expression.MakeMemberAccess(refValueArg, member.MemberInfo)),
								ifTrue: Expression.Empty(),
								ifFalse: onMismatch
								));
						}
						else
						{
							// Either we already give the deserializer the existing object as target where it should write to, in which case its fine.
							// Or the deserializer somehow gets its own object instance from somewhere else, in which case we can only proceed with overwriting the field anyway.

							// So the most elegant way to handle this is to first let the deserializer do what it normally does,
							// and then check if it has changed the reference.
							// If it did not, everything is fine; meaning it must have accepted 'null' or whatever object is in there, or fixed its content.
							// If the reference was changed there is potentially some trouble.
							// If we're allowed to change it we use reflection, if not we throw an exception

							
							Expression onReassignment;
							if (_ceras.Config.ReadonlyFieldHandling == ReadonlyFieldHandling.ForcedOverwrite)
								// field.SetValue(valueArg, tempStore)
								onReassignment = Expression.Call(Expression.Constant(fieldInfo), SetValue, arg0: refValueArg, arg1: tempStore);
							else
								onReassignment = Expression.Throw(Expression.Constant(new Exception("The reference in the readonly-field '"+fieldInfo.Name+"' would have to be overwritten, but forced overwriting is not enabled in the serializer settings. Either make the field writeable or enable ForcedOverwrite in the ReadonlyFieldHandling-setting.")));

							// Did the reference change?
							block.Add(Expression.IfThenElse(
								test: Expression.ReferenceEqual(tempStore, Expression.MakeMemberAccess(refValueArg, member.MemberInfo)),
								
								// Still the same. Whatever has happened (and there are a LOT of cases), it seems to be ok.
								// Maybe the existing object's content was overwritten, or the instance reference was already as expected, or...
								ifTrue: Expression.Empty(),

								// Reference changed. Handle it depending on if its allowed or not
								ifFalse: onReassignment
								));

						}

						continue;
					}


				var fieldExp = Expression.MakeMemberAccess(refValueArg, member.MemberInfo);

				var serializeCall = Expression.Call(Expression.Constant(formatter), deserializeMethod, bufferArg, refOffsetArg, fieldExp);
				block.Add(serializeCall);
			}


			var serializeBlock = Expression.Block(variables: locals, expressions: block);
#if FAST_EXP
			return Expression.Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).CompileFast(true);
#else
			return Expression.Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).Compile();
#endif

		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			_dynamicSerializer(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			_dynamicDeserializer(buffer, ref offset, ref value);
		}
	}

	// Some types are banned from serialization
	// and instead of throwing crazy errors that don't help the user at all, we give an explanation
	static class BannedTypes
	{
		struct BannedType
		{
			public readonly Type Type;
			public readonly string BanReason;
			public readonly bool AlsoCheckInherit;

			public BannedType(Type type, string banReason, bool alsoCheckInherit)
			{
				Type = type;
				BanReason = banReason;
				AlsoCheckInherit = alsoCheckInherit;
			}
		}

		static List<BannedType> _bannedTypes = new List<BannedType>
		{
				new BannedType(typeof(System.Collections.IEnumerator), "Enumerators are potentially infinite, and also most likely have no way to be instantiated at deserialization-time. If you think this is a mistake, report it as a github issue or provide a custom IFormatter for this case.", true),

				new BannedType(typeof(System.Delegate), "Delegates cannot be serialized easily because they often drag in a lot of unintended objects. Support for delegates to static methods is on the todo list though! If you want to know why this is complicated then check this out: https://github.com/rikimaru0345/Ceras/issues/11", true),
		};


		internal static void ThrowIfBanned(Type type)
		{
			for (var i = 0; i < _bannedTypes.Count; i++)
			{
				var ban = _bannedTypes[i];

				bool isBanned = false;
				if (ban.AlsoCheckInherit)
				{
					if (ban.Type.IsAssignableFrom(type))
						isBanned = true;
				}
				else
				{
					if (type == ban.Type)
						isBanned = true;
				}

				if (isBanned)
					throw new BannedTypeException($"The type '{type.FullName}' cannot be serialized, please mark the field/property with the [Ignore] attribute or filter it out using the 'ShouldSerialize' callback. Reason: {ban.BanReason}");
			}
		}

		internal static void ThrowIfNonspecific(Type type)
		{
			if (type.IsAbstract || type.IsInterface)
				throw new InvalidOperationException("Can only generate code for specific types. The type " + type.Name + " is abstract or an interface.");
		}

	}

	class BannedTypeException : Exception
	{
		public BannedTypeException(string message) : base(message)
		{

		}
	}
}
