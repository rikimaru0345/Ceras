namespace Ceras.Formatters
{
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


			// Mode: New


			// Is it a composite type that we have to split into its parts? (aka any generic)
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

			// todo: do we put this only in the if or else part? or is it ok here? it should be ok, since we want to embed the schema of every type
			if (_serializer.Config.VersionTolerance == VersionTolerance.AutomaticEmbedded)
				if (!CerasSerializer.FrameworkAssemblies.Contains(type.Assembly))
					_serializer.WriteSchemaForType(ref buffer, ref offset, type);

		}

		public void Deserialize(byte[] buffer, ref int offset, ref Type value)
		{
			int mode = SerializerBinary.ReadUInt32Bias(buffer, ref offset, Bias);

			// Null
			if (mode == Null)
			{
				value = null;
				return;
			}

			var typeCache = _serializer.InstanceData.TypeCache;

			// Existing
			if (mode >= 0)
			{
				var id = mode;
				value = typeCache.GetExistingObject<Type>(id);
				return;
			}


			bool isComposite = mode == NewGeneric;

			if (isComposite) // composite aka "closed generic"
			{
				// Read base type first (example: Dictionary<T1, T2>)
				Type baseType = value;
				Deserialize(buffer, ref offset, ref baseType);


				// Read count (example: 2)
				var argCount = SerializerBinary.ReadByte(buffer, ref offset);
				Type[] genericArgs = new Type[argCount];

				// Read all inner type definitions (in our example: 'string' and 'object)
				for (int i = 0; i < argCount; i++)
					Deserialize(buffer, ref offset, ref genericArgs[i]);
				

				// Read construct full composite (example: Dictionary<string, object>)
				var compositeProxy = typeCache.CreateDeserializationProxy<Type>();

				value = _typeBinder.GetTypeFromBaseAndAgruments(baseType.FullName, genericArgs);
				compositeProxy.Value = value; // make it available for future deserializations
			}
			else
			{
				var proxy = typeCache.CreateDeserializationProxy<Type>();

				string baseTypeName = SerializerBinary.ReadString(buffer, ref offset);
				value = _typeBinder.GetTypeFromBase(baseTypeName);

				proxy.Value = value;
			}

			// todo: what to do when the type is not written because it is the same already?
			// a) force writing the type when embedding version info
			// b) just write schema, assuming the type

			if (_serializer.Config.VersionTolerance == VersionTolerance.AutomaticEmbedded)
				if (!CerasSerializer.FrameworkAssemblies.Contains(value.Assembly))
					_serializer.ReadSchemaForType(buffer, ref offset, value);

		}
	}
}
