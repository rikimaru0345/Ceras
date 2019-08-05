namespace Ceras.Formatters
{
	using Ceras.Helpers;
	using System;
	using System.Collections.Generic;


	/*
	 * What does TypeFormatter do?
	 * 
	 * Assuming we have 'List<>' and 'MyObj' in KnownTypes. And now we want to serialize 'List<MyObj>', but that type isn't known!
	 * ReferenceFormatter (where this functionality was originally located) only deals with actual, concrete, values and can't
	 * know that List<MyObj> can be assembled from some already existing "primitives" (in our example case: List<> and MyObj).
	 * 
	 * TypeFormatter however is aware of this.
	 * It splits each generic type into its components and serializes them individually, 
	 * so they can be reconstructed from their individual parts.
	 * 
	 * This saves a ton of space (and time!)
	 */

	sealed class TypeFormatter : IFormatter<Type>
	{
		static readonly TypeDictionary<uint> _commonTypeToId = new TypeDictionary<uint>();
		static readonly Dictionary<uint, Type> _idToCommonType = new Dictionary<uint, Type>();

		static TypeFormatter()
		{
			_idToCommonType.Add(0, typeof(int));
			_idToCommonType.Add(1, typeof(uint));
			_idToCommonType.Add(2, typeof(short));
			_idToCommonType.Add(3, typeof(ushort));
			_idToCommonType.Add(4, typeof(byte));
			_idToCommonType.Add(5, typeof(char));
			_idToCommonType.Add(6, typeof(float));
			_idToCommonType.Add(7, typeof(double));
			_idToCommonType.Add(8, typeof(List<>));
			_idToCommonType.Add(9, typeof(Dictionary<,>));
			_idToCommonType.Add(10, typeof(int[]));
			_idToCommonType.Add(11, typeof(byte[]));
			_idToCommonType.Add(12, typeof(float[]));
			_idToCommonType.Add(13, typeof(string));
			_idToCommonType.Add(14, typeof(System.Collections.ArrayList));
			_idToCommonType.Add(15, typeof(System.Collections.Hashtable));
			_idToCommonType.Add(16, typeof(object));

			foreach (var kvp in _idToCommonType)
				_commonTypeToId.GetOrAddValueRef(kvp.Value) = kvp.Key;
		}


		const int Null = -1;
		const int NewGeneric = -2; // type that is further specified through generic arguments
		const int NewSingle = -3; // normal type that has no generic args
		const int SingleCommon = -4; // one of the types that are so common that they get a dedicated ID to save space

		const int Bias = 4;

		readonly CerasSerializer _ceras;
		readonly ITypeBinder _typeBinder;


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


			// From here on we know it's a new type
			// Now, is it a composite type that we have to split into its parts? (aka any generic)
			bool isClosed = !type.ContainsGenericParameters;

			if (isClosed && type.IsGenericType)
			{
				// Generic Type

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
				// Single Type

				if (_commonTypeToId.TryGetValue(type, out uint commonId))
				{
					// Common Type

					SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, SingleCommon, Bias);
					SerializerBinary.WriteUInt32(ref buffer, ref offset, commonId);
				}
				else
				{
					// New Type: write name

					SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, NewSingle, Bias);

					var typeName = _typeBinder.GetBaseName(type);
					SerializerBinary.WriteString(ref buffer, ref offset, typeName);

					typeCache.RegisterObject(type);
				}
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


			bool isComposite = mode == NewGeneric; // composite aka "closed generic"

			if (isComposite)
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
				if (mode == SingleCommon)
				{
					// Common Type

					var commonId = SerializerBinary.ReadUInt32(buffer, ref offset);
					if (!_idToCommonType.TryGetValue(commonId, out type))
						ThrowNoSuchCommonType(commonId);
				}
				else
				{
					// New Single Type

					var proxy = typeCache.CreateDeserializationProxy();

					string baseTypeName = SerializerBinary.ReadStringLimited(buffer, ref offset, 350);
					type = _typeBinder.GetTypeFromBase(baseTypeName);

					proxy.Type = type;

					if (_isSealed)
						ThrowSealed(type, false);
				}
			}
		}

		
		public void Seal()
		{
			_isSealed = true;
		}

		static void ThrowSealed(Type type, bool serializing)
		{
			if (serializing)
			{
				throw new InvalidOperationException($"Serialization Error: The type '{type.FriendlyName(true)}' cannot be added to the TypeCache because the cache is sealed (most likely on purpose to protect against exploits). Check your SerializerConfig (KnownTypes, SealType... ), or open a github issue if you think this is not supposed to happen with your settings.");
			}
			else
			{
				throw new InvalidOperationException($"Deserialization Error: The data contained the type '{type.FriendlyName(true)}', but embedding of types that are not known in advance is not allowed in the current SerializerConfig (most likely on purpose to protect against exploits). Check your SerializerConfig (KnownTypes, SealType... ), or open a github issue if you think this is not supposed to happen with your settings.");
			}
		}

		static void ThrowNoSuchCommonType(uint commonId)
		{
			throw new Exceptions.CerasException($"Read common type ID '{commonId}', but no such type exists. Maybe the data is corrupted, was serialized with different settings, or a different version.");
		}
	}
}
