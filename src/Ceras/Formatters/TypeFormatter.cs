namespace Ceras.Formatters
{
    using Ceras.Helpers;
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

        public override void Serialize(IBufferWriter<byte> writer, Type type) => Serialize(writer, type, null);

        /// <summary>
        /// <paramref name="argumentOf"/> is the closed generic type this one is being written as a
        /// type argument of, or null at the top level. It only makes the error message name the
        /// value the user actually wrote, which is the one they can recognize.
        /// </summary>
        void Serialize(IBufferWriter<byte> writer, Type type, Type? argumentOf)
        {
            if (type == null)
            {
                // A null Type is a real value here: a null member, or a member without a declaring
                // type. It is written as the null marker and read back as null.
                SerializerBinary.WriteUInt32Bias(writer, Null, Bias);
                return;
            }

            if (BannedTypes.TryGetBanned(type, out var banReason))
            {
                // Only reachable for a Type that *describes* a banned shape, e.g. typeof(Action) or
                // typeof(List<Func<int>>): a type reference is normally just a name, but the banned
                // shapes are banned as a whole, so the reference is rejected too. It used to be
                // written as a null marker, which for a generic argument made the reader rebuild
                // the type with a null argument and leak a bare ArgumentNullException out of
                // MakeGenericType. Reporting the rejected value here is the diagnosis that is
                // actually useful.
                string where = argumentOf == null
                    ? "A type reference"
                    : $"The type argument '{type.FriendlyName(true)}' of the type reference '{argumentOf.FriendlyName(true)}'";

                throw new BannedTypeException(
                    $"{where} describes the banned type '{type.FriendlyName(true)}', which Ceras cannot serialize, and it will not be written as null: " +
                    "a null here silently changes what the file means. Remove the reference, or point it at a type Ceras can serialize. " +
                    $"Reason this type is banned: \"{banReason}\".");
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
                Serialize(writer, baseType, null);
                var genericArgs = type.GetGenericArguments();
                SerializerBinary.WriteByte(writer, (byte)genericArgs.Length);
                for (int i = 0; i < genericArgs.Length; i++)
                    Serialize(writer, genericArgs[i], type);
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
                {
                    Deserialize(ref reader, ref genericArgs[i]);
                    // Serialize never writes a null argument: a type that cannot be serialized is
                    // rejected there, by name. A null here is a payload that lost the argument (an
                    // older writer that wrote a null marker for it, or a damaged file), and
                    // MakeGenericType would answer it with a bare "Value cannot be null" that names
                    // neither the type nor the argument.
                    if (genericArgs[i] == null)
                        throw new Ceras.Exceptions.CerasDataCorruptionException(
                            $"Corrupt data: the type reference for '{baseType?.FullName ?? "an unnamed type"}' has no type for generic argument {i}, so it cannot be rebuilt.");
                }

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
