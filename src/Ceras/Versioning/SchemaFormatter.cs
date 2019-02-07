using System.Collections.Generic;

namespace Ceras.Helpers
{
	using Formatters;
	using System;
	using System.Diagnostics;
	using System.Linq.Expressions;
	using System.Reflection;
	using static System.Linq.Expressions.Expression;

	// Ask community what they prefer:
	// - fixed 4 byte length (best performance)
	// - varInt encoding (slower but most compact)
	// - maybe even fixed 2 byte encoding? (with exception for elements that are too large)
	// What about a setting? But people would have to be very careful that they use the same settings for reading as they did while serializing.

	// todo: when we're adding support for serialization constructors here as well, then it would be a good feature if the user can provide default-values, and maybe even delegate callbacks to create values when they are missing from the data (so the user-factory can still be used!). Or maybe we could have a callback in this scenario: UninitializedObj->Callback->DirectCtor to handle all cases?

	// todo: readjust context for properties

	class SchemaDynamicFormatter<T> : IFormatter<T>, ISchemaTaintedFormatter
	{
		const int FieldSizePrefixBytes = 4;
		static readonly Type _sizeType = typeof(uint);
		static readonly MethodInfo _sizeWriteMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteUInt32Fixed));
		static readonly MethodInfo _sizeReadMethod = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadUInt32Fixed));

		readonly CerasSerializer _ceras;
		readonly Dictionary<Schema, SerializerPair> _generatedSerializerPairs = new Dictionary<Schema, SerializerPair>();

		Schema _currentSchema;

		SerializeDelegate<T> _serializer;
		DeserializeDelegate<T> _deserializer;

		int _deserializationDepth; // recursion tracker for special types of schema-changes (can be removed eventually when we implemented a better solution)


		public SchemaDynamicFormatter(CerasSerializer ceras, Schema schema)
		{
			_ceras = ceras;
			_currentSchema = schema;

			var type = typeof(T);

			BannedTypes.ThrowIfNonspecific(type);
			
			ActivateSchema(_currentSchema);

			RegisterForSchemaChanges();
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			// If this is the first time this type is being written,
			// we need to write the Schema as well.
			if (!_ceras.InstanceData.EncounteredSchemaTypes.Contains(typeof(T)))
			{
				_ceras.InstanceData.EncounteredSchemaTypes.Add(typeof(T));
				CerasSerializer.WriteSchema(ref buffer, ref offset, _currentSchema);
			}

			_serializer(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			// If this is the first time we're reading this type,
			// then we have to read the schema
			var type = typeof(T);
			if (!_ceras.InstanceData.EncounteredSchemaTypes.Contains(type))
			{
				_ceras.InstanceData.EncounteredSchemaTypes.Add(type);

				// Read the schema in which the data was written
				var schema = _ceras.ReadSchema(buffer, ref offset, type);

				_ceras.ActivateSchemaOverride(type, schema);
			}

			try
			{
				_deserializationDepth++;
				_deserializer(buffer, ref offset, ref value);
			}
			finally
			{
				_deserializationDepth--;
			}
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
				var formatter = _ceras.GetReferenceFormatter(type);
				var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
				Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter " + formatter.GetType().FullName);

				// startPos = offset; 
				block.Add(Assign(startPos, refOffsetArg));

				// offset += 4; to reserve space for the length prefix
				block.Add(AddAssign(refOffsetArg, Constant(FieldSizePrefixBytes)));

				// Serialize(...) write the actual data
				block.Add(Call(
							   instance: Constant(formatter),
							   method: serializeMethod,
							   arg0: refBufferArg,
							   arg1: refOffsetArg,
							   arg2: MakeMemberAccess(valueArg, member.MemberInfo)
						  ));

				// size = (offset - startPos) - 4; // calculate the size of what we just wrote
				block.Add(Assign(size, Subtract(Subtract(refOffsetArg, startPos), Constant(FieldSizePrefixBytes))));

				// offset = startPos; // go back to where we started and write the size into the reserved space
				block.Add(Assign(refOffsetArg, startPos));

				// WriteInt32( size )
				block.Add(Call(
							   method: _sizeWriteMethod,
							   arg0: refBufferArg,
							   arg1: refOffsetArg,
							   arg2: Convert(size, _sizeType)
							   ));

				// offset = startPos + skipOffset; // continue serialization where we left off
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
			var members = schema.Members;

			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Parameter(typeof(T).MakeByRefType(), "value");

			List<Expression> block = new List<Expression>();

			List<ParameterExpression> locals = new List<ParameterExpression>();
			var blockSize = Variable(typeof(int), "blockSize");
			locals.Add(blockSize);

			Dictionary<MemberInfo, ParameterExpression> memberInfoToLocal = new Dictionary<MemberInfo, ParameterExpression>();


			// 1. Read existing values into locals
			for (int i = 0; i < schema.Members.Count; i++)
			{
				var member = members[i].Member;

				if (members[i].IsSkip)
					continue; // Don't define local for skipped member

				// Read the data into a new local variable 
				var tempStore = Variable(member.MemberType, member.Name + "_local");
				locals.Add(tempStore);
				memberInfoToLocal.Add(member.MemberInfo, tempStore);

				// Init the local with the current value
				block.Add(Assign(tempStore, MakeMemberAccess(refValueArg, member.MemberInfo)));
			}

			// 2. Deserialize using local
			for (var i = 0; i < members.Count; i++)
			{
				var member = members[i].Member;

				// Read block size: blockSize = ReadSize();
				block.Add(Assign(left: blockSize,
								 right: Convert(Call(method: _sizeReadMethod, arg0: bufferArg, arg1: refOffsetArg), typeof(int))));

				if (members[i].IsSkip)
				{
					// Skip over the field: offset += blockSize;

					block.Add(AddAssign(refOffsetArg, blockSize));
				}
				else
				{
					// Read normally

					// Prepare formatter...
					var formatter = _ceras.GetReferenceFormatter(member.MemberType);
					var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
					Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter " + formatter.GetType().FullName);

					// formatter.Deserialize(buffer, ref offset, ref member_local);
					var tempStore = memberInfoToLocal[member.MemberInfo];
					var tempReadCall = Call(Constant(formatter), deserializeMethod, bufferArg, refOffsetArg, tempStore);
					block.Add(tempReadCall);
					// todo: we could optionally check if the expected blockSize matches what we've actually read.
				}
			}

			// 3. Write back values in one batch
			for (int i = 0; i < members.Count; i++)
			{
				var sMember = members[i];

				if (sMember.IsSkip)
					continue; // Skipped members don't need write-back

				var member = members[i].Member;
				var tempStore = memberInfoToLocal[member.MemberInfo];
				var type = member.MemberType;


				if (member.MemberInfo is FieldInfo fieldInfo && fieldInfo.IsInitOnly)
				{
					// Readonly field
					DynamicFormatterHelpers.EmitReadonlyWriteBack(type, sMember.ReadonlyFieldHandling, fieldInfo, refValueArg, tempStore, block);
				}
				else
				{
					// Normal field or property
					var writeBack = Assign(
										   left: MakeMemberAccess(refValueArg, member.MemberInfo),
										   right: tempStore);

					block.Add(writeBack);
				}
			}

			var serializeBlock = Block(variables: locals, expressions: block);
#if FAST_EXP
			return Expression.Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).CompileFast(true);
#else
			return Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).Compile();
#endif

		}


		void RegisterForSchemaChanges()
		{
			if (_ceras.Config.VersionTolerance == VersionTolerance.Disabled)
				return;


			// We want to know when the schema of this type changes!
			_ceras.GetTypeMetaData(typeof(T)).OnSchemaChangeTargets.Add(this);

			// We also need to know about changes to value-type schemata.
			// But we have to ensure that we're recording ALL changes, not just the ones of the current schema (which might be missing entries!)
			var meta = _ceras.GetTypeMetaData(typeof(T));
			var primarySchema = meta.PrimarySchema;

			foreach (var member in primarySchema.Members)
			{
				var memberType = member.Member.MemberType;

				// Only value-types are important, ref-types are handled somewhere else (ref-formatter)
				if (!memberType.IsValueType)
					continue;

				var memberMetaData = _ceras.GetTypeMetaData(member.Member.MemberType);
				memberMetaData.OnSchemaChangeTargets.Add(this);
			}



			// todo: this is for later, but probably we can avoid all this anyway if we select a better solution to the "inline problem"

			/*
			// What Schema changes do we want to know about?
			// When the schema of our own type or the schema of one of our members changes
			// 1.) Collect all types of whos schema we (so that when it changes, we know that we should update ourselves)
			_tempHashSet.Clear();

			_tempHashSet.Add(type);
			foreach (var m in schema.Members)
				_tempHashSet.Add(m.Member.MemberType);

			List<Schema> currentSchemata = new List<Schema>(_tempHashSet.Count);

			// 2.) Enter ourselves into the "interested" lists so that we get notified
			foreach (var t in _tempHashSet)
			{
				var meta = _ceras.GetTypeMetaData(t);
				meta.OnSchemaChangeTargets.Add(this);
				currentSchemata.Add(meta.CurrentSchema);
			}
			_tempHashSet.Clear();

			// 3.) Create a schema complex that represents the sum of all schemata we're currently using
			var currentSchemaComplex = new SchemaComplex(currentSchemata);
			*/

		}


		public void OnSchemaChanged(TypeMetaData meta)
		{
			// We're given the full metadata, but we only need the schema itself here
			// That simplifies the code because we can reuse the function for the constructor
			var schema = meta.CurrentSchema;
			ActivateSchema(schema);
		}

		void ActivateSchema(Schema schema)
		{
			// What schema changes are relevant to us?
			// - Schema of own type
			// - Schema of value-types inside us (dispatches for ref types are handled by RefFormatter anyway)


			// For now we only adapt to change to the current type schema.
			// Do we have serializers prepared for this schema already?


			// Important sanity check, if this happens the user should definitely know about it!
			if (_deserializationDepth > 0)
				if (schema.Type.IsValueType)
					throw new InvalidOperationException("Schema of a value-type has changed while an object-type is being deserialized. This is feature is WIP, check out GitHub for more information.");



			if (_generatedSerializerPairs.TryGetValue(schema, out var pair))
			{
				// Use already generated serializers 
				_serializer = pair.Serializer;
				_deserializer = pair.Deserializer;

				_currentSchema = schema;
				return;
			}

			// We have to make a new serializer pair
			if (schema.Members.Count == 0)
			{
				_serializer = (ref byte[] buffer, ref int offset, T value) => { };
				_deserializer = (byte[] buffer, ref int offset, ref T value) => { };
				return;
			}

			if (schema.IsPrimary)
			{
				_serializer = GenerateSerializer(schema);
				_deserializer = GenerateDeserializer(schema);
			}
			else
			{
				// No serializer! Writing data in some old format is not supported (yet, maybe in the future).
				// In theory we could do it. But it's not implemented because there would have to be some way for the user to specify what Schema to use.
				// And we get into all sorts of troubles with type-conversion (not implemented yet, but it will probably arrive earlier than this...)
				// This also protects us against bugs!
				_serializer = ErrorSerializer;
				_deserializer = GenerateDeserializer(schema);
			}

			_currentSchema = schema;

			_generatedSerializerPairs.Add(schema, new SerializerPair(_serializer, _deserializer));


			// todo: later we want to include the "schema complex" as well, which is the combination of multiple schemata.
			//       because that's the actual key here.
			//       but it makes things more difficult, it would most likely be better to chose one of the solutions below (in point #3)


			// 1) Mutate the current SchemaComplex and create a new SchemaComplex that represents the current one
			//    todo: how do we do this efficiently? We can't create a new SchemaComplex class and List<> just to (maybe) realize that we've already got one of those!


			// 2) After updating the current SchemaComplex, we check if we already got a serializer-pair that handles this, if not create a new one
			//    Then assign it.

			// 3) In case we're already deserializing while a schema change for a value-type appears, we've got a problem.
			//    First: Why do we not have a problem when its a reference-type?
			//    That's simply because we never call the formatter for those directly, we use the ReferenceFormatter, which will switch to the new formatter in time.
			//    Now for value-types that's a problem as the reference to the specific formatter is compiled as a constant into the formatter-delegate.
			//    
			//    -> Exception: All we can do is throw an exception here, warning the user that they're dealing with a very very strange situation.
			//    
			//    -> Force Ref: Force reference formatter in-between even for value-types; only to ensure that specific-formatters can be hot-swapped.
			//
			//    -> Virtualized Serialization: What we could do is have a setting that enforces "virtualized" deserialization, where nothing gets compiled and formatters are looked up constantly.
			//    Which is obviously extremely slow (on par with BinaryFormatter I guess), but the only solution. Should be fine, I can't imagine a real scenario where that isn't avoidable.
			//
			//    -> Cached Lookup: Instead of compiling it in as a constant, we could have an external "holder" object which we can change from the outside, and the delegate would always
			//    look into this object to get the refernce to the formatter. That way we could swap out the formatter even while we're already running it.
			//

		}


		static void ErrorSerializer(ref byte[] buffer, ref int offset, T value)
		{
			throw new InvalidOperationException("Trying to write using a non-primary ObjectSchema. This should never happen and is a bug, please report it on GitHub!");
		}


		struct SerializerPair
		{
			public readonly SerializeDelegate<T> Serializer;
			public readonly DeserializeDelegate<T> Deserializer;

			public SerializerPair(SerializeDelegate<T> serializer, DeserializeDelegate<T> deserializer)
			{
				Serializer = serializer;
				Deserializer = deserializer;
			}
		}
	}
}
