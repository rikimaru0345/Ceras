
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
	using System.Runtime.CompilerServices;

#if FAST_EXP
	using FastExpressionCompiler;
#endif

	interface IDynamicFormatterMarker { }

	// todo 1: check if FastExpressionCompiler works without crashing now (a lot of work has been put into it recently)
	// todo 2: cache formatters in a static-generic instead of dict? we need to know exactly how much we'd save; the static-ctor can just generate the corrosponding serializers
	public class DynamicObjectFormatter<T> : IFormatter<T>, IDynamicFormatterMarker
	{
		static MemberComparer _memberComparer = new MemberComparer();

		delegate void DynamicSerializer(ref byte[] buffer, ref int offset, T value);
		delegate void DynamicDeserializer(byte[] buffer, ref int offset, ref T value);

		CerasSerializer _ceras;
		DynamicSerializer _dynamicSerializer;
		DynamicDeserializer _dynamicDeserializer;

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
		};

		public DynamicObjectFormatter(CerasSerializer serializer)
		{
			_ceras = serializer;

			var type = typeof(T);

			ThrowIfBanned(type);
			ThrowIfNonspecific(type);

			var members = GetSerializableMembers(type, _ceras.Config.DefaultTargets, _ceras.Config.ShouldSerializeMember);

			if (members.Count > 0)
			{
				_dynamicSerializer = GenerateSerializer(members);
				_dynamicDeserializer = GenerateDeserializer(members);
			}
			else
			{
				_dynamicSerializer = (ref byte[] buffer, ref int offset, T value) => { };
				_dynamicDeserializer = (byte[] buffer, ref int offset, ref T value) => { };
			}
		}

		static void ThrowIfBanned(Type type)
		{
			// todo: in order to be REALLY useful we need to catch this further up (reference serializer) and wrap it in another exception so we can include what field/prop caused this and where that was defined.

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
					throw new Exception($"The type '{type.FullName}' cannot be serialized. Reason: {ban.BanReason}");
			}
		}


		DynamicSerializer GenerateSerializer(List<SerializedMember> members)
		{
			var refBufferArg = Expression.Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Expression.Parameter(typeof(T), "value");

			List<Expression> block = new List<Expression>();


			foreach (var member in members)
			{
				IFormatter formatter;

				var type = member.MemberType;

				if (type.IsValueType)
					formatter = _ceras.GetSpecificFormatter(type);
				else
					formatter = _ceras.GetGenericFormatter(type);

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
			return Expression.Lambda<DynamicSerializer>(serializeBlock, refBufferArg, refOffsetArg, valueArg).CompileFast(true);
#else
			return Expression.Lambda<DynamicSerializer>(serializeBlock, refBufferArg, refOffsetArg, valueArg).Compile();
#endif

		}

		DynamicDeserializer GenerateDeserializer(List<SerializedMember> members)
		{
			var bufferArg = Expression.Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Expression.Parameter(typeof(T).MakeByRefType(), "value");

			List<Expression> block = new List<Expression>();

			// Go through all fields and assign them
			foreach (var member in members)
			{
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
			return Expression.Lambda<DynamicDeserializer>(serializeBlock, bufferArg, refOffsetArg, refValueArg).CompileFast(true);
#else
			return Expression.Lambda<DynamicDeserializer>(serializeBlock, bufferArg, refOffsetArg, refValueArg).Compile();
#endif

		}

		void ThrowIfNonspecific(Type type)
		{
			if (type.IsAbstract || type.IsInterface)
				throw new InvalidOperationException("Can only generate code for specific types. The type " + type.Name + " is abstract or an interface.");
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			_dynamicSerializer(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			_dynamicDeserializer(buffer, ref offset, ref value);
		}


		internal static List<SerializedMember> GetSerializableMembers(Type type, TargetMember defaultTargetMembers, Func<SerializedMember, SerializationOverride> fieldFilter = null)
		{
			List<SerializedMember> members = new List<SerializedMember>();

			var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

			var classConfig = type.GetCustomAttribute<MemberConfig>();

			foreach (var m in type.GetFields(flags).Cast<MemberInfo>().Concat(type.GetProperties(flags)))
			{
				bool isPublic;
				bool isField = false, isProp = false;

				if (m is FieldInfo f)
				{
					// Skip readonly
					if (f.IsInitOnly)
						continue;

					// Skip property backing fields
					if (f.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
						continue;

					isPublic = f.IsPublic;
					isField = true;
				}
				else if (m is PropertyInfo p)
				{
					if (!p.CanRead || !p.CanWrite)
						continue;
					if (p.GetIndexParameters().Length != 0)
						continue;

					isPublic = p.GetMethod.IsPublic;
					isProp = true;
				}
				else
					continue;

				var serializedMember = FieldOrProp.Create(m);


				//
				// 1.) Use filter if there is one
				if (fieldFilter != null)
				{
					var filterResult = fieldFilter(serializedMember);

					if (filterResult == SerializationOverride.ForceInclude)
					{
						members.Add(serializedMember);
						continue;
					}
					else if (filterResult == SerializationOverride.ForceSkip)
					{
						continue;
					}
				}

				//
				// 2.) Use attribute
				var ignore = m.GetCustomAttribute<Ignore>(true) != null;
				var include = m.GetCustomAttribute<Include>(true) != null;

				if (ignore && include)
					throw new Exception($"Member '{m.Name}' on type '{type.Name}' has both [Ignore] and [Include]!");

				if (ignore)
				{
					continue;
				}

				if (include)
				{
					members.Add(serializedMember);
					continue;
				}

				//
				// 3.) Use class attributes
				if (classConfig != null)
				{
					if (IsMatch(isField, isProp, isPublic, classConfig.TargetMembers))
					{
						members.Add(serializedMember);
						continue;
					}
				}

				//
				// 4.) Use global defaults
				if (IsMatch(isField, isProp, isPublic, defaultTargetMembers))
				{
					members.Add(serializedMember);
					continue;
				}
			}

			members.Sort(_memberComparer);

			return members;
		}

		static bool IsMatch(bool isField, bool isProp, bool isPublic, TargetMember targetMembers)
		{
			if (isField)
			{
				if (isPublic)
				{
					if ((targetMembers & TargetMember.PublicFields) != 0)
						return true;
				}
				else
				{
					if ((targetMembers & TargetMember.PrivateFields) != 0)
						return true;
				}
			}

			if (isProp)
			{
				if (isPublic)
				{
					if ((targetMembers & TargetMember.PublicProperties) != 0)
						return true;
				}
				else
				{
					if ((targetMembers & TargetMember.PrivateProperties) != 0)
						return true;
				}
			}

			return false;
		}


		class MemberComparer : IComparer<SerializedMember>
		{
			static string Prefix(SerializedMember m) => m.IsField ? "f" : "p";

			public int Compare(SerializedMember x, SerializedMember y)
			{
				var name1 = Prefix(x) + x.MemberType.FullName + x.Name;
				var name2 = Prefix(y) + y.MemberType.FullName + y.Name;

				return string.Compare(name1, name2, StringComparison.Ordinal);
			}
		}
	}
}