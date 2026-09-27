namespace Ceras.Formatters
{
    using System;
    using System.Buffers;

    public sealed class TypeFormatter : FormatterBase<Type>
    {
        readonly CerasSerializer _ceras;
        readonly ITypeBinder _typeBinder;

        const int Null = -1;
        const int NewGeneric = -2;
        const int NewSingle = -3;
        const int Bias = 3;

        bool _isSealed;

        internal TypeFormatter(CerasSerializer ceras)
        {
            _ceras = ceras;
            _typeBinder = ceras.TypeBinder;
        }

        public override void Serialize(IBufferWriter<byte> writer, Type type)
        {
            if (type == null)
            {
                SerializerBinary.WriteUInt32Bias(writer, Null, Bias);
                return;
            }

            if (BannedTypes.IsBanned(type))
            {
                // We should only skip it if it is NOT one of the types we actually MUST be able to serialize.
                // Currently Type is not banned, but maybe something else is.
                // If it is banned, we write Null.
                SerializerBinary.WriteUInt32Bias(writer, Null, Bias);
                return;
            }

            var typeCache = _ceras.InstanceData.TypeCache;
            if (typeCache.TryGetExistingObjectId(type, out int id))
            {
                SerializerBinary.WriteUInt32Bias(writer, id, Bias);
                return;
            }

            if (_isSealed) ThrowSealed(type, true);

            if (!type.ContainsGenericParameters && type.IsGenericType)
            {
                SerializerBinary.WriteUInt32Bias(writer, NewGeneric, Bias);
                var baseType = type.GetGenericTypeDefinition();
                Serialize(writer, baseType);
                var genericArgs = type.GetGenericArguments();
                SerializerBinary.WriteByte(writer, (byte)genericArgs.Length);
                for (int i = 0; i < genericArgs.Length; i++)
                    Serialize(writer, genericArgs[i]);
                typeCache.RegisterObject(type);
            }
            else
            {
                SerializerBinary.WriteUInt32Bias(writer, NewSingle, Bias);
                var typeName = _typeBinder.GetBaseName(type);
                SerializerBinary.WriteString(writer, typeName);
                typeCache.RegisterObject(type);
            }
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Type type)
        {
            int mode = SerializerBinary.ReadUInt32Bias(ref reader, Bias);
            if (mode == Null)
            {
                type = null!;
                return;
            }

            var typeCache = _ceras.InstanceData.TypeCache;
            if (mode >= 0)
            {
                type = typeCache.GetExistingObject(mode);
                return;
            }

            if (mode == NewGeneric)
            {
                // The base type is written as the generic type *definition* of a closed type, so it is
                // present and named in every payload Serialize produced. The out parameter of the
                // IFormatter contract is not nullable, hence the seed; a null marker in this slot is
                // corruption and is reported as such below instead of dereferencing null.
                Type baseType = null!;
                Deserialize(ref reader, ref baseType);
                var argCount = SerializerBinary.ReadByte(ref reader);
                Type[] genericArgs = new Type[argCount];
                for (int i = 0; i < argCount; i++)
                    Deserialize(ref reader, ref genericArgs[i]);

                var baseTypeName = baseType?.FullName
                    ?? throw new Ceras.Exceptions.CerasDataCorruptionException(
                        "Corrupt data: a generic type reference has no base type definition (a missing or unnamed Type) to rebuild it from.");

                var compositeProxy = typeCache.CreateDeserializationProxy();
                type = _typeBinder.GetTypeFromBaseAndArguments(baseTypeName, genericArgs);
                compositeProxy.Type = type;
                if (_isSealed) ThrowSealed(type, false);
            }
            else
            {
                var proxy = typeCache.CreateDeserializationProxy();
                // Serialize writes ITypeBinder.GetBaseName(type), which is never null; a null marker here
                // would only come from a payload that does not describe a type.
                var baseTypeName = SerializerBinary.ReadString(ref reader)
                    ?? throw new Ceras.Exceptions.CerasDataCorruptionException(
                        "Corrupt data: a type reference has no name to resolve it from.");
                type = _typeBinder.GetTypeFromBase(baseTypeName);
                proxy.Type = type;
                if (_isSealed) ThrowSealed(type, false);
            }
        }

        static void ThrowSealed(Type type, bool serializing) => throw new InvalidOperationException("Registry sealed");
        public void Seal() => _isSealed = true;
    }
}
