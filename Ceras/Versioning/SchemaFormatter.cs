using System.Collections.Generic;

namespace Ceras.Helpers
{
	using Formatters;
	using System;
	using System.Diagnostics;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using FastExpressionCompiler;
	using static System.Linq.Expressions.Expression;


	class DynamicSchemaFormatter<T> : IFormatter<T>
	{
		CerasSerializer _ceras;

		/*
		 * todo: have a dict that tells us about "known types" and how large they are, or how to skip them easily
		 * Examples:
		 *  int32 -> +4
		 *  string -> read varint as +offset
		 *  
		 */

		public DynamicSchemaFormatter(CerasSerializer ceras)
		{
			_ceras = ceras;
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
		}


		// generate Serializer/Deserializer from schema



		SerializeDelegate<T> GenerateSerializer(Schema schema)
		{
			/*
				Approach:

				- if member is a built-in value-type: write normally and continue.
				- reserve space in front of every member (add 4 bytes)
				- write member
				- write written size at reserved space

				Later, when we are asked to deserialize something again, we first read the schema itself.
				It tells us what members there are, and their order.
				If we recognize that we are missing a member, we will skip over those members, 
				generating code like: skipOffset=ReadInt32();  offset += skipOffset;


				Questions:
				1.) How do we know if a member has changed its type?
				In order to detect this we need to compare the old and the new type, no?
				So we need to know the old and new type...
				That's only possible if we emit type-information for EVERY member.
				We could reuse the written type names later on

				2.) What if we serialize an 'object' field and one goes missing? 
				v1: A,B,C
				v2: A,C
				-> We know how to skip over B because the size is in front of the data.

				3.) What if a type goes missing
				I've investigated and neither Newtonsoft.Json nor MessagePack handle this.
				You just get TypeNotFound exception.

				4.) What about renamed members?
				We'll have an attribute like this: [Name("lvl", oldNames: "level")]
				That solves 2 problems. The name is shorter in the data; and old data can be found again

				5.) What about data where the type changes? From long to int for example
				Just allow the user to adjust the schema. He can customize the reading of the field
				providing a custom formatter to read the field, and convert it to the expected type.
				The formatter needs to return the expected type, but it can just read the data in a different way (forwarding to the actual formatter)
            
			*/


			var refBufferArg = Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Parameter(typeof(T), "value");

			// todo: have a lookup list to directly get the actual 'SerializerBinary' method. There is no reason to actually use objects like "Int32Formatter"

			List<Expression> block = new List<Expression>();

			var writeInt32 = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteInt32Fixed));

			var startPos = Parameter(typeof(int), "startPos");
			var skipOffset = Parameter(typeof(int), "skipOffset");

			foreach (var schemaEntry in schema.Members)
			{
				var member = schemaEntry.Member;
				var type = member.MemberType;

				// Get Serialize method
				var formatter = _ceras.GetGenericFormatter(type);
				var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
				Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter");

				// startPos = offset; 
				block.Add(Assign(startPos, refOffsetArg));

				// offset += 4;
				block.Add(AddAssign(refOffsetArg, Constant(4)));

				// Serialize(...)
				block.Add(Call(
							   instance: Constant(formatter),
							   method: serializeMethod,
							   arg0: refBufferArg,
							   arg1: refOffsetArg,
							   arg2: MakeMemberAccess(valueArg, member.MemberInfo)
						  ));

				// skipOffset = (offset - startPos) - 4;
				block.Add(Assign(skipOffset, Subtract(Subtract(refOffsetArg, startPos), Constant(4))));

				// offset = startPos;
				block.Add(Assign(refOffsetArg, startPos));

				// WriteInt32( skipOffset )
				block.Add(Call(
							   method: writeInt32,
							   arg0: refBufferArg,
							   arg1: refOffsetArg,
							   arg2: skipOffset
							   ));

				// offset = startPos + skipOffset;
				block.Add(Assign(refOffsetArg, startPos));

			}

			var serializeBlock = Block(expressions: block);

#if FAST_EXP
			return Expression.Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg).CompileFast(true);
#else
			return Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg).Compile();
#endif

		}

		DeserializeDelegate<T> GenerateDeserializer(Schema schema)
		{
			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Parameter(typeof(T).MakeByRefType(), "value");

			List<Expression> block = new List<Expression>();

			// Go through all fields and assign them
			//foreach (var member in members)
			//{
			//	// todo: what about Field attributes that tell us to:
			//	// - use a specific formatter? (HalfFloat, Int32Fixed, MyUserFormatter) 
			//	// - assume a type, or exception
			//	// - Force ignore caching (for ref types) (value types cannot be ref-saved)
			//	// - Persistent object caching per type or field

			//	var type = member.MemberType;

			//	IFormatter formatter;

			//	if (type.IsValueType)
			//		formatter = _ceras.GetSpecificFormatter(type);
			//	else
			//		formatter = _ceras.GetGenericFormatter(type);

			//	//var formatter = _ceras.GetFormatter(fieldInfo.FieldType, extraErrorInformation: $"DynamicObjectFormatter ObjectType: {specificType.FullName} FieldType: {fieldInfo.FieldType.FullName}");
			//	var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
			//	Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter");

			//	var fieldExp = MakeMemberAccess(refValueArg, member.MemberInfo);

			//	var serializeCall = Call(Constant(formatter), deserializeMethod, bufferArg, refOffsetArg, fieldExp);
			//	block.Add(serializeCall);
			//}


			var serializeBlock = Block(expressions: block);
#if FAST_EXP
			return Expression.Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).CompileFast(true);
#else
			return Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).Compile();
#endif

		}


	}

	/*
	 * Open questions:
	 * - Can we read without schema? Can we try reading from the automatic schema?
	 * - Is version tolerant format compatible with brittle?
	 *
	 * - Schema then type?
	 * - Or type then schema?
	 *
	 * - Do we want that to be able to read both?
	 *
	 * - Should there be a setting per-object?
	 *
	 * - What if we save something, and later add a thing?
	 *   The saved data does not have the needed information for us to ever decode it again.
	 *
	 * - An option to set members to default if they are missing?
	 *
	 * - What types need a schema? Do we have a blacklist for .NET dlls?
	 *
	 * - '.IncludeSchema' setting per serialization?
	 * - '.VersionTolerance =
	 *			- Disabled, // as is right now
	 *			- EmbededDescription,
	 *			- Manual, // call GetSchemaDescriptions() to get type + hash + byte[] description. 
	 *    The last option is especially interesting for DB stuff.
	 *	  A game would use the 3 things like this:
	 *    First check if the game already has a schema with that hash saved somewhere already.
	 *    If yes, nothing needs to be done, if no, the game needs to save the description blob.
	 *    When wanting to load the data, Ceras will ask for a description block with the given hash.
	 *    The game then has to provide the matching description block. Pretty similar to IExternalRootObject.
	 *    Advantage: Individual object size stays really small
	 *
	 *
	 */


	// I'd like to just use the dynamic formatter :), but manually writing it gives more control
	class SchemaFormatter : IFormatter<Schema>
	{
		// todo: how to de-duplicate object-cache instances?
		// if two objects are different, but share their definition by chance, we want to exploit that fact
		// ObjectSchema can implement GetHashCode(), but then we have to ensure that it, and its collection, and the values inside, never change! (which is trivial but boring)
		// todo for another day...
		ObjectCache _cache;
		IFormatter<Type> _typeFormatter;

		const int Bias = 1;
		const int NewSchema = -1;

		public SchemaFormatter(CerasSerializer ceras)
		{
			_cache = ceras.InstanceData.ObjectCache;
			_typeFormatter = ceras.GetFormatter<Type>();
		}

		public void Serialize(ref byte[] buffer, ref int offset, Schema value)
		{
			if (_cache.TryGetExistingObjectId(value, out int existingId))
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, existingId, Bias);
				return;
			}
			SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, NewSchema, Bias);
			_cache.RegisterObject(value);

			_typeFormatter.Serialize(ref buffer, ref offset, value.Type);

			var members = value.Members;

			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)members.Count);

			for (int i = 0; i < members.Count; i++)
			{
				var member = members[i];
				SerializerBinary.WriteString(ref buffer, ref offset, member.PersistentName);
			}
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Schema schema)
		{
			int id = SerializerBinary.ReadUInt32Bias(buffer, ref offset, Bias);
			if (id >= 0)
			{
				schema = _cache.GetExistingObject<Schema>(id);
				return;
			}

			Type type = null;
			_typeFormatter.Deserialize(buffer, ref offset, ref type);
			schema.Type = type;


			var count = SerializerBinary.ReadUInt32(buffer, ref offset);
			for (int i = 0; i < count; i++)
			{
				var name = SerializerBinary.ReadString(buffer, ref offset);

				var schemaMember = new SchemaMember();
				schema.Members.Add(schemaMember);

				var member = FindMember(type, name);
				if (member == null)
				{
					schemaMember.PersistentName = name;
					schemaMember.IsSkip = true;
				}
				else
				{
					schemaMember.PersistentName = name;
					schemaMember.IsSkip = false;
					schemaMember.Member = new SerializedMember(member);
				}
			}
		}

		static MemberInfo FindMember(Type type, string name)
		{
			foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
			{
				if (member is FieldInfo f)
				{
					if (IsMatch(f, name))
						return f;
				}
				else if (member is PropertyInfo p)
				{
					if (IsMatch(p, name))
						return p;
				}
			}

			return null;
		}

		static bool IsMatch(MemberInfo member, string name)
		{
			if (member.Name == name)
				return true;

			var attrib = member.GetCustomAttribute<MemberAttribute>();
			if (attrib != null)
			{
				if (attrib.AlternativeNames.Any(n => n == name))
					return true;
			}

			return false;
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
