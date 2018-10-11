namespace Ceras.Formatters
{
	using System;
	using Helpers;

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
	class TypeFormatter : IFormatter<Type>
	{
		readonly ObjectCache _typeCache;
		readonly ITypeBinder _typeBinder;

		public TypeFormatter(CerasSerializer serializer, ObjectCache typeCache)
		{
			_typeCache = typeCache;
			_typeBinder = serializer.TypeBinder;
		}
		
		public void Serialize(ref byte[] buffer, ref int offset, Type type)
		{
			var typeName = _typeBinder.GetBaseName(type);
			var genericArgs = type.GetGenericArguments();

			// Name
			SerializerBinary.WriteString(ref buffer, ref offset, typeName);
			
			// Number of generic args
			SerializerBinary.WriteInt32(ref buffer, ref offset, genericArgs.Length);

			// Generic Args (possibly recursive)
			for (int i = 0; i < genericArgs.Length; i++)
				Serialize(ref buffer, ref offset, genericArgs[i]);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Type value)
		{
			string baseTypeName = SerializerBinary.ReadString(buffer, ref offset);

			// Get generic args to the type
			int numGenericArgs = SerializerBinary.ReadInt32(buffer, ref offset);

			if (numGenericArgs == 0)
			{
				// Return type as it is
				value = _typeBinder.GetTypeFromBase(baseTypeName);
				return;
			}

			Type[] genericArgs = new Type[numGenericArgs];
			for (int i = 0; i < numGenericArgs; i++)
			{
				Type genericArgument = null;
				Deserialize(buffer, ref offset, ref genericArgument);
				genericArgs[i] = genericArgument;
			}

			// Get base type
			value = _typeBinder.GetTypeFromBaseAndAgruments(baseTypeName, genericArgs);
		}
	}
}