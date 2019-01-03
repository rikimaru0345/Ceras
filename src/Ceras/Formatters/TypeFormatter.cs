namespace Ceras.Formatters
{
	using Ceras.Helpers;
	using System;


	/*
	 * Important:
	 * For a long time this was wrapped into a CacheFormatter, but that had a problem.
	 * Assuming we've added List<> and MyObj to KnownTypes, then List<MyObj> was still not known, which is bad!
	 * The cache formatter only deals with concrete values and can't know that List<MyObj> can be built from
	 * two already existing "primitives" (List<> and MyObj).
	 * The type formatter is aware of this and deals with it in the most efficient way by splitting each generic
	 * type into its components and serializing them individually, so they can be reconstructed from their individual parts.
	 * This saves a ton of space (and thus time!)
	 */
	
	
	/*
	 * Todo 1:
	 * 
	 * right now we have checks like this:
	 *		if (_serializer.Config.VersionTolerance == VersionTolerance.AutomaticEmbedded)
	 * 
	 * Would it be possible to remove them, and create a 'SchemaTypeFormatter' which overrides Serialize and Deserialize and adds that to the end?
	 * Would it be faster? Would serialization performance be impacted negatively when not using VersionTolerance because of the virtual methods?
	 */

	class TypeFormatter : IFormatter<Type>
	{
		readonly CerasSerializer _serializer;
		readonly ITypeBinder _typeBinder;

		const int Bias = 3;
		const int Null = -1;
		const int NewGeneric = -2; // type that is further specified through generic arguments
		const int NewSingle = -3; // normal type that has no generic args

		public TypeFormatter(CerasSerializer serializer)
		{
			_serializer = serializer;
			_typeBinder = serializer.TypeBinder;
		}

		public void Serialize(ref byte[] buffer, ref int offset, Type type)
		{
			// Null
			if (type == null)
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, Null, Bias);
				return;
			}

			var typeCache = _serializer.InstanceData.TypeCache;

			// Existing
			if (typeCache.TryGetExistingObjectId(type, out int id))
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, id, Bias);
				return;
			}


			//
			// From here on we know it's a new type
			// Now, is it a composite type that we have to split into its parts? (aka any generic)
			bool isClosed = !type.ContainsGenericParameters;

			if (isClosed && type.IsGenericType)
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, NewGeneric, Bias);

				// Split and write

				// Base
				var baseType = type.GetGenericTypeDefinition();
				Serialize(ref buffer, ref offset, baseType);

				// Args
				var genericArgs = type.GetGenericArguments();

				SerializerBinary.WriteByte(ref buffer, ref offset, (byte)(genericArgs.Length)); // We need count. Ex: Action<T1> and Action<T1, T2> share the name
				for (int i = 0; i < genericArgs.Length; i++)
					Serialize(ref buffer, ref offset, genericArgs[i]);

				// Register composite type
				typeCache.RegisterObject(type);
			}
			else
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, NewSingle, Bias);

				// Open generic, something that can be serialized alone
				var typeName = _typeBinder.GetBaseName(type);

				// Name
				SerializerBinary.WriteString(ref buffer, ref offset, typeName);

				// Register single type
				typeCache.RegisterObject(type);
			}

			if (_serializer.Config.VersionTolerance == VersionTolerance.AutomaticEmbedded)
				if (!CerasSerializer.FrameworkAssemblies.Contains(type.Assembly))
				{
					// Get Schema
					var schema = _serializer.SchemaDb.GetOrCreatePrimarySchema(type);

					// Write it (if not done yet)

					if(!_serializer.InstanceData.WrittenSchemata.Contains(schema))
					{
						_serializer.InstanceData.WrittenSchemataList.Add(schema); // need to enter into the list as well so we know in what order the schemata are used
						_serializer.SchemaDb.WriteSchema(ref buffer, ref offset, schema);
					}

					// Make the formatter available, if we're called from TypeFormatter then this will be the next thing
					_serializer.ActivateSchemaOverride(type, schema);


					// todo: collect all schemata, embed them in front, add hash and skip-over thing, ...
					// todo: while reading the schema block, all specific serializers have to be created first before any reference formatters are allowed to be created.
					// todo: how? how is this solved right now? If a DynamicFormatter has a field reference to itself, how does that work
					//			- For ValueTypes: they cannot contain fields as themselves in anyway (would be infinite recursion)
					//			- ReferenceTypes: when a DynamicFormatter<T> is created, and T has a field of type T inside it as well, it obtains a ReferenceFormatter<T> instead.
					//							  that way the actual specific formatter is resolved later (when the first DynamicFormatter<T> has completed its construction)
					// Even when constructing a dynamicFormatter and it contains a reference to some object, it will always ask for a referenceFormatter first.
					// Problem: If an object contains a custom value-type it will not use the reference formatter.
					// Solution:
					// -> We must always construct formatters for value-types first.
					throw new Exception("version tolerance is currently being reworked");
				}
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Type type)
		{
			int mode = SerializerBinary.ReadUInt32Bias(buffer, ref offset, Bias);

			// Null
			if (mode == Null)
			{
				type = null;
				return;
			}

			var typeCache = _serializer.InstanceData.TypeCache;

			// Existing
			if (mode >= 0)
			{
				var id = mode;
				type = typeCache.GetExistingObject<Type>(id);
				return;
			}


			bool isComposite = mode == NewGeneric;

			if (isComposite) // composite aka "closed generic"
			{
				// Read base type first (example: Dictionary<T1, T2>)
				Type baseType = type;
				Deserialize(buffer, ref offset, ref baseType);


				// Read count (example: 2)
				var argCount = SerializerBinary.ReadByte(buffer, ref offset);
				Type[] genericArgs = new Type[argCount];

				// Read all inner type definitions (in our example: 'string' and 'object)
				for (int i = 0; i < argCount; i++)
					Deserialize(buffer, ref offset, ref genericArgs[i]);


				// Read construct full composite (example: Dictionary<string, object>)
				var compositeProxy = typeCache.CreateDeserializationProxy<Type>();

				type = _typeBinder.GetTypeFromBaseAndAgruments(baseType.FullName, genericArgs);
				compositeProxy.Value = type; // make it available for future deserializations
			}
			else
			{
				var proxy = typeCache.CreateDeserializationProxy<Type>();

				string baseTypeName = SerializerBinary.ReadString(buffer, ref offset);
				type = _typeBinder.GetTypeFromBase(baseTypeName);

				proxy.Value = type;
			}


			if (_serializer.Config.VersionTolerance == VersionTolerance.AutomaticEmbedded)
				if (!CerasSerializer.FrameworkAssemblies.Contains(type.Assembly))
				{
					var schema = _serializer.SchemaDb.ReadSchema(buffer, ref offset, type);

					_serializer.ActivateSchemaOverride(type, schema);
				}

		}
	}
}
