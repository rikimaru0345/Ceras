namespace Ceras.Formatters
{
	using System;


	/*
	 * Important:
	 * For a long time this was wrapped into a ReferenceFormatter, but that had a problem.
	 * Assuming we've added List<> and MyObj to KnownTypes, then List<MyObj> was still not known, which is bad!
	 * The ReferenceFormatter only deals with concrete values and can't know that List<MyObj> can
	 * be assembled from some already existing "primitives" (in our example case: List<> and MyObj).
	 * 
	 * The TypeFormatter is aware of this and deals with it in the most efficient way by splitting each generic
	 * type into its components and serializing them individually, so they can be reconstructed from their individual parts.
	 * 
	 * This saves a ton of space (and thus time!)
	 */


	/*
	 * Todo 1:
	 * 
	 * right now we have checks like this:
	 *		if (_ceras.Config.VersionTolerance == VersionTolerance.AutomaticEmbedded)
	 * 
	 * Would it be possible to remove them, and create a 'SchemaTypeFormatter' which overrides Serialize and Deserialize and adds that to the end?
	 * Would it be faster? Would serialization performance be impacted negatively when not using VersionTolerance because of the virtual methods?
	 */

	sealed class TypeFormatter : IFormatter<Type>
	{
		readonly CerasSerializer _ceras;
		readonly ITypeBinder _typeBinder;

		const int Null = -1;
		const int NewGeneric = -2; // type that is further specified through generic arguments
		const int NewSingle = -3; // normal type that has no generic args

		const int Bias = 3;

		bool _isSealed;

		public TypeFormatter(CerasSerializer ceras)
		{
			_ceras = ceras;
			_typeBinder = ceras.TypeBinder;
		}

		public void Serialize(ref byte[] buffer, ref int offset, Type type)
		{
			// Null
			if (type == null)
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, Null, Bias);
				return;
			}

			var typeCache = _ceras.InstanceData.TypeCache;

			// Existing
			if (typeCache.TryGetExistingObjectId(type, out int id))
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, id, Bias);
				return;
			}

			if (_isSealed)
				ThrowSealed(type, true);

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

			var typeCache = _ceras.InstanceData.TypeCache;

			// Existing
			if (mode >= 0)
			{
				var id = mode;
				type = typeCache.GetExistingObject(id);
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
				var compositeProxy = typeCache.CreateDeserializationProxy();

				type = _typeBinder.GetTypeFromBaseAndArguments(baseType.FullName, genericArgs);
				compositeProxy.Type = type; // make it available for future deserializations

				if (_isSealed)
					ThrowSealed(type, false);
			}
			else
			{
				var proxy = typeCache.CreateDeserializationProxy();

				string baseTypeName = SerializerBinary.ReadString(buffer, ref offset);
				type = _typeBinder.GetTypeFromBase(baseTypeName);

				proxy.Type = type;

				if (_isSealed)
					ThrowSealed(type, false);
			}
		}

		static void ThrowSealed(Type type, bool serializing)
		{
			if (serializing)
			{
				throw new InvalidOperationException($"Serialization Error: The type '{type.FullName}' cannot be added to the TypeCache because the cache is sealed (most likely on purpose to protect against exploits). Check your SerializerConfig (KnownTypes, SealType... ), or open a github issue if you think this is not supposed to happen with your settings.");
			}
			else
			{
				throw new InvalidOperationException($"Deserialization Error: The data contained the type '{type.FullName}', but embedding of types that are not known in advance is not allowed in the current SerializerConfig (most likely on purpose to protect against exploits). Check your SerializerConfig (KnownTypes, SealType... ), or open a github issue if you think this is not supposed to happen with your settings.");
			}
		}


		public void Seal()
		{
			_isSealed = true;
		}

	}
}


/*
 * Explanation:
 * 
 * 	When we're deserializing any nested types (ex: Literal<float>)
	then we're writing Literal`1 as 0, and System.Single as 1
	but while reading, we're recursively descending, so System.Single would get read first (as 0)
	which would throw off everything.
	Solution: 
	Reserve some space (inserting <null>) into the object-cache before actually reading, so all the nested things will just append to the list as intended.
	and then just overwrite the inserted placeholder with the actual value once we're done.
	This only works for types, since generic types obviously can't recursively contain exact copies of themselves.
 * 
 */
