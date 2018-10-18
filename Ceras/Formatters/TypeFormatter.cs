namespace Ceras.Formatters
{
	using Helpers;
	using System;

	/*
	 * The idea here is that we have a map of "known types" that we use when possible to encode a type as just one number
	 * If the type is not known, we format the type + all its generic paramters recursively
	 *
	 * Should we maybe allow the user to provide a set of "known types"? What would that even accomplish if we cache type-serialization anyway?
	 * Advantage: Even the first types will likely be efficiently written as an ID, only rarely falling back to formatting as a string.
	 * Disadvantage: Opens a huge door for mistakes, since if the user changes the known types, or forgets to initialize it exactly the same way, stuff will break horribly with no way
	 *				 for us to fix it anymore. Once the "configuration" of known types is lost, all data serialized by this serializer will be very hard to recover.
	 * Only really a problem if writing to files. Not a problem when dealing with networking (since messages are not saved and discarded after reading and processing)
	 */
	/*
	 * Important:
	 * For a long time this was wrapped into a CacheFormatter, but that had a problem.
	 * Assuming we've added List<> and MyObj to KnownTypes, then List<MyObj> was still not known, which is bad!
	 * The cache formatter only deals with concrete values and can't know that List<MyObj> can be built from two already existing "primitives".
	 * That's why we changed it so now TypeFormatter does its own caching.
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
				
				SerializerBinary.WriteByte(ref buffer, ref offset, (byte)(genericArgs.Length)); // We need count. Ex: Action<T1> and Action<T1, T2> share the name.
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
				// Read main type
				var compositeProxy = typeCache.CreateDeserializationProxy<Type>();

				Type baseType = value;
				Deserialize(buffer, ref offset, ref baseType);

				
				// Read count
				var argCount = SerializerBinary.ReadByte(buffer, ref offset);
				Type[] genericArgs = new Type[argCount];
				for (int i = 0; i < argCount; i++)
				{
					var genericArgProxy = typeCache.CreateDeserializationProxy<Type>();

					Deserialize(buffer, ref offset, ref genericArgProxy.Value);

					genericArgs[i] = genericArgProxy.Value;
				}

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
		}
	}
}
