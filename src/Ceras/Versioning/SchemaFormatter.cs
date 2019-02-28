using System.Collections.Generic;

namespace Ceras.Helpers
{
	using Formatters;
	using System;
	using System.Diagnostics;
	using System.Linq;
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

			// No members?
			if (schema.Members.Count == 0)
			{
				_serializer = (ref byte[] buffer, ref int offset, T value) => { };
				_deserializer = (byte[] buffer, ref int offset, ref T value) => { };
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
