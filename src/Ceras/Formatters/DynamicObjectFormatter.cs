
// ReSharper disable ArgumentsStyleOther
// ReSharper disable ArgumentsStyleNamedExpression
namespace Ceras.Formatters
{
	using Helpers;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq.Expressions;

#if FAST_EXP
	using FastExpressionCompiler;
#endif


	// todo: Can we use a static-generic as a cache instead of dict? Is that even possible in our case? Would we even save anything? How much would it be faster?
	public class DynamicObjectFormatter<T> : IFormatter<T>
	{
		CerasSerializer _ceras;
		SerializeDelegate<T> _dynamicSerializer;
		DeserializeDelegate<T> _dynamicDeserializer;

		public DynamicObjectFormatter(CerasSerializer serializer)
		{
			_ceras = serializer;

			var type = typeof(T);

			BannedTypes.ThrowIfBanned(type);
			BannedTypes.ThrowIfNonspecific(type);

			var schema = serializer.GetSerializationSchema(type, _ceras.Config.DefaultTargets, _ceras.Config.SkipCompilerGeneratedFields, _ceras.Config.ShouldSerializeMember);

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
				var formatter = _ceras.GetGenericFormatter(type);

				// Get the formatter and its Serialize method
				// var formatter = _ceras.GetFormatter(fieldInfo.FieldType, extraErrorInformation: $"DynamicObjectFormatter ObjectType: {specificType.FullName} FieldType: {fieldInfo.FieldType.FullName}");
				var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
				Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter");

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

			// Go through all fields and assign them
			foreach (var sMember in members)
			{
				var member = sMember.Member;
				// todo: what about Field attributes that tell us to:
				// - use a specific formatter? (HalfFloat, Int32Fixed, MyUserFormatter) 
				// - assume a type, or exception
				// - Force ignore caching (for ref types) (value types cannot be ref-saved)
				// - Persistent object caching per type or field

				var type = member.MemberType;

				IFormatter formatter;

				if (type.IsValueType)
					formatter = _ceras.GetSpecificFormatter(type);
				else
					formatter = _ceras.GetGenericFormatter(type);

				//var formatter = _ceras.GetFormatter(fieldInfo.FieldType, extraErrorInformation: $"DynamicObjectFormatter ObjectType: {specificType.FullName} FieldType: {fieldInfo.FieldType.FullName}");
				var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
				Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter");

				var fieldExp = Expression.MakeMemberAccess(refValueArg, member.MemberInfo);

				var serializeCall = Expression.Call(Expression.Constant(formatter), deserializeMethod, bufferArg, refOffsetArg, fieldExp);
				block.Add(serializeCall);
			}


			var serializeBlock = Expression.Block(expressions: block);
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
