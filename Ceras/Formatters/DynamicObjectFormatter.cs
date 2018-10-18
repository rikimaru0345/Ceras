
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

	// todo 2: cache formatters in a static-generic instead of dict? we need to know exactly how much we'd save; the static-ctor can just generate the corrosponding serializers
	public class DynamicObjectFormatter<T> : IFormatter<T>, IDynamicFormatterMarker
	{
		static SchemaMemberComparer _schemaMemberComparer = new SchemaMemberComparer();


		CerasSerializer _ceras;
		SerializeDelegate<T> _dynamicSerializer;
		DeserializeDelegate<T> _dynamicDeserializer;

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

			var schema = GetSerializationSchema(type, _ceras.Config.DefaultTargets, _ceras.Config.ShouldSerializeMember);

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

		static void ThrowIfBanned(Type type)
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
					throw new BannedTypeException($"The type '{type.FullName}' cannot be serialized. Reason: {ban.BanReason}");
			}
		}

		static void ThrowIfNonspecific(Type type)
		{
			if (type.IsAbstract || type.IsInterface)
				throw new InvalidOperationException("Can only generate code for specific types. The type " + type.Name + " is abstract or an interface.");
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


		internal static Schema GetSerializationSchema(Type type, TargetMember defaultTargetMembers, Func<SerializedMember, SerializationOverride> fieldFilter = null)
		{
			// todo: verify that names are valid: letters+numbers, must start with a letter
			// because then we can do a more efficient format 


			Schema schema = new Schema();
			schema.Type = type;

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

				// should we allow users to provide an formater for each old-name (in case newer versions have changed the type of the element?)
				var attrib = m.GetCustomAttribute<MemberAttribute>();

				var overrideFormatter = DetermineOverrideFormatter(m);

				var schemaMember = new SchemaMember
				{
					IsSkip = false,
					Member = serializedMember,
					OverrideFormatter = null, // todo, get from attrib?
					PersistentName = attrib?.Name ?? m.Name,
				};

				VerifyName(schemaMember.PersistentName);

				//
				// 1.) Use filter if there is one
				if (fieldFilter != null)
				{
					var filterResult = fieldFilter(serializedMember);

					if (filterResult == SerializationOverride.ForceInclude)
					{
						schema.Members.Add(schemaMember);
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
					schema.Members.Add(schemaMember);
					continue;
				}

				//
				// 3.) Use class attributes
				if (classConfig != null)
				{
					if (IsMatch(isField, isProp, isPublic, classConfig.TargetMembers))
					{
						schema.Members.Add(schemaMember);
						continue;
					}
				}

				//
				// 4.) Use global defaults
				if (IsMatch(isField, isProp, isPublic, defaultTargetMembers))
				{
					schema.Members.Add(schemaMember);
					continue;
				}
			}

			schema.Members.Sort(_schemaMemberComparer);

			return schema;
		}

		static IFormatter DetermineOverrideFormatter(MemberInfo memberInfo)
		{
			var prevName = memberInfo.GetCustomAttribute<PreviousFormatter>();

		}

		static void VerifyName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new Exception("Member name can not be null/empty");
			if (!char.IsLetter(name[0]))
				throw new Exception("Name must start with a letter");

			for (int i = 1; i < name.Length; i++)
				if (!char.IsLetterOrDigit(name[i]))
					throw new Exception($"The name '{name}' has character '{name[i]}' at index '{i}', which is not allowed. Must be a letter or digit.");
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


		class SchemaMemberComparer : IComparer<SchemaMember>
		{
			static string Prefix(SchemaMember m) => m.Member.IsField ? "f" : "p";

			public int Compare(SchemaMember x, SchemaMember y)
			{
				var name1 = Prefix(x) + x.Member.MemberType.FullName + x.PersistentName;
				var name2 = Prefix(y) + y.Member.MemberType.FullName + y.PersistentName;

				return string.Compare(name1, name2, StringComparison.Ordinal);
			}
		}
	}

	class BannedTypeException : Exception
	{
		public BannedTypeException(string message) : base(message)
		{

		}
	}
}