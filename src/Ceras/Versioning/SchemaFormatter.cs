namespace Ceras.Helpers
{
	using System;
	using System.Collections.Generic;
	using Formatters;

	class SchemaDynamicFormatter<T> : IFormatter<T>, ISchemaTaintedFormatter
	{
		readonly CerasSerializer _ceras;
		readonly Dictionary<Schema, SerializerPair> _generatedSerializerPairs = new Dictionary<Schema, SerializerPair>();
		readonly bool _isStatic;

		Schema _currentSchema;

		SerializeDelegate<T> _serializer;
		DeserializeDelegate<T> _deserializer;

		int _deserializationDepth; // recursion tracker for special types of schema-changes (can be removed eventually when we implemented a better solution)


		public SchemaDynamicFormatter(CerasSerializer ceras, Schema schema, bool isStatic)
		{
			_ceras = ceras;
			_currentSchema = schema;
			_isStatic = isStatic;

			var type = typeof(T);

			BannedTypes.ThrowIfNonspecific(type);
			
			var typeConfig = _ceras.Config.GetTypeConfig(type, isStatic);
			typeConfig.VerifyConstructionMethod();

			ActivateSchema(_currentSchema);

			RegisterForSchemaChanges();
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			// If this is the first time (in the current Serialization) that this type is being written?
			// -> Ensure we're on the primary schema
			// -> Write the Schema into the binary
			if (!_ceras.InstanceData.EncounteredSchemaTypes.Contains(typeof(T)))
			{
				_ceras.EnsurePrimarySchema(typeof(T));
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
				var schema = _ceras.ReadSchema(buffer, ref offset, type, _isStatic);

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


		
		void ISchemaTaintedFormatter.OnSchemaChanged(TypeMetaData meta) => ActivateSchema(meta.CurrentSchema);

		void RegisterForSchemaChanges()
		{
			// We want to know when the schema of this type changes!
			_ceras.GetTypeMetaData(typeof(T)).OnSchemaChangeTargets.Add(this);

			// We also need to know about changes to value-type schemata.
			// But we have to ensure that we're recording ALL changes, not just the ones of the current schema (which might be missing entries!)
			var meta = _ceras.GetTypeMetaData(typeof(T));
			var primarySchema = meta.PrimarySchema;

			foreach (var member in primarySchema.Members)
			{
				var memberType = member.MemberType;

				// Only value-types are important, ref-types are handled somewhere else (ref-formatter)
				if (!memberType.IsValueType)
					continue;

				var memberMetaData = _ceras.GetTypeMetaData(member.MemberType);
				memberMetaData.OnSchemaChangeTargets.Add(this);
			}
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


			// Use already compiled serializers
			if (_generatedSerializerPairs.TryGetValue(schema, out var pair))
			{
				_serializer = pair.Serializer;
				_deserializer = pair.Deserializer;

				_currentSchema = schema;
				return;
			}

			bool isStatic = schema.IsStatic;

			// Generate
			if (schema.IsPrimary)
			{
				_serializer = DynamicFormatter<T>.GenerateSerializer(_ceras, schema, true, isStatic).Compile();
				_deserializer = DynamicFormatter<T>.GenerateDeserializer(_ceras, schema, true, isStatic).Compile();
			}
			else
			{
				// Different Schema -> generate no serializer!
				// Writing data in some old format is not supported (yet, maybe in the future).
				// In theory we could do it. But it's not implemented because there would have to be some way for the user to specify what Schema to use.
				// And we get into all sorts of troubles with type-conversion (not implemented yet, but it will probably arrive earlier than this...)
				// This also protects us against bugs!
				_serializer = ErrorSerializer;
				_deserializer = DynamicFormatter<T>.GenerateDeserializer(_ceras, schema, true, isStatic).Compile();
			}

			_currentSchema = schema;

			_generatedSerializerPairs.Add(schema, new SerializerPair(_serializer, _deserializer));
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
