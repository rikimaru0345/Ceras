namespace Ceras.Formatters
{
    using Ceras.Helpers;
    using Resolvers;
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Reflection;

    public sealed class ReferenceFormatter<T> : IFormatter<T>, ISchemaTaintedFormatter
    where T : class
    {
        const int Null = -1;
        const int NewValue = -2;
        const int NewValueSameType = -3;
        const int ExternalObject = -4;
        const int InlineType = -5;
        const int Bias = 5;

        readonly CerasSerializer _ceras;
        readonly IFormatter<Type> _typeFormatter;
        readonly TypeDictionary<DispatcherEntry> _dispatchers = new TypeDictionary<DispatcherEntry>();
        readonly bool _allowReferences;

        public ReferenceFormatter(CerasSerializer ceras)
        {
            _ceras = ceras;
            if (typeof(T).IsStatic()) throw new InvalidOperationException("static");
            _typeFormatter = ceras.GetFormatter<Type>();
            _allowReferences = _ceras.Config.PreserveReferences;
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value)
        {
            Serialize(writer, (T)value);
        }

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            T value = default;
            if (member is FieldInfo f)
            {
                var existing = f.GetValue(container);
                if (existing is T t) value = t;
                Deserialize(ref reader, ref value);
                f.SetValue(container, value);
            }
            else if (member is PropertyInfo p)
            {
                var existing = p.GetValue(container);
                if (existing is T t) value = t;
                Deserialize(ref reader, ref value);
                p.SetValue(container, value);
            }
        }

        public void Serialize(IBufferWriter<byte> writer, T value)
        {
            if (ReferenceEquals(value, null)) { SerializerBinary.WriteUInt32Bias(writer, Null, Bias); return; }
            var specificType = value.GetType();
            var entry = GetOrCreateEntry(specificType);
            if (entry.IsType) { SerializerBinary.WriteUInt32Bias(writer, InlineType, Bias); _typeFormatter.Serialize(writer, (Type)(object)value); return; }
            if (entry.IsExternalRootObject) { var externalObj = (IExternalRootObject)value; if (!ReferenceEquals(_ceras.InstanceData.CurrentRoot, value)) { SerializerBinary.WriteUInt32Bias(writer, ExternalObject, Bias); SerializerBinary.WriteInt32Fixed(writer, externalObj.GetReferenceId()); _ceras.Config.OnExternalObject?.Invoke(externalObj); return; } }
            if (_allowReferences)
            {
                if (_ceras.InstanceData.ObjectCache.TryGetExistingObjectId(value, out int id)) SerializerBinary.WriteUInt32Bias(writer, id, Bias);
                else { _ceras.InstanceData.ObjectCache.RegisterObject(value); SerializerBinary.WriteUInt32Bias(writer, ReferenceEquals(typeof(T), specificType) ? NewValueSameType : NewValue, Bias); if (!ReferenceEquals(typeof(T), specificType)) _typeFormatter.Serialize(writer, specificType); try { entry.CurrentSerializeDispatcher(writer, value); } catch (Exception e) { throw new Ceras.Exceptions.CerasException($"Error while serializing object of type '{specificType.FullName}': {e.Message}", e); } }
            }
            else { SerializerBinary.WriteUInt32Bias(writer, ReferenceEquals(typeof(T), specificType) ? NewValueSameType : NewValue, Bias); if (!ReferenceEquals(typeof(T), specificType)) _typeFormatter.Serialize(writer, specificType); try { entry.CurrentSerializeDispatcher(writer, value); } catch (Exception e) { throw new Ceras.Exceptions.CerasException($"Error while serializing object of type '{specificType.FullName}': {e.Message}", e); } }
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            var objId = SerializerBinary.ReadUInt32Bias(ref reader, Bias);
            if (objId == Null) { if (value != null) _ceras.DiscardObjectMethod?.Invoke(value); value = default; return; }
            if (objId == InlineType) { Type type = null; _typeFormatter.Deserialize(ref reader, ref type); value = (T)(object)type; return; }
            if (objId >= 0) { value = _ceras.InstanceData.ObjectCache.GetExistingObject<T>(objId); return; }
            if (objId == ExternalObject) { int externalId = SerializerBinary.ReadInt32Fixed(ref reader); _ceras.Config.ExternalObjectResolver.Resolve(externalId, out value); return; }
            Type specificType = (objId == NewValue) ? null : typeof(T);
            if (objId == NewValue) _typeFormatter.Deserialize(ref reader, ref specificType);
            var entry = GetOrCreateEntry(specificType);
            if (!entry.IsValueType) { if (value != null) { if (value.GetType() != specificType) { _ceras.DiscardObjectMethod?.Invoke(value); value = (T)entry.Constructor(); } } else value = (T)entry.Constructor(); }
            if (!_allowReferences) { try { entry.CurrentDeserializeDispatcher(ref reader, ref value); } catch (Exception e) { throw new Ceras.Exceptions.CerasException($"Error while deserializing object of type '{specificType.FullName}': {e.Message}", e); } return; }
            var objectProxy = _ceras.InstanceData.ObjectCache.CreateDeserializationProxy<T>();
            objectProxy.Value = value;
            try { entry.CurrentDeserializeDispatcher(ref reader, ref objectProxy.Value); } catch (Exception e) { throw new Ceras.Exceptions.CerasException($"Error while deserializing object of type '{specificType.FullName}': {e.Message}", e); }
            value = objectProxy.Value;
        }

        DispatcherEntry GetOrCreateEntry(Type type)
        {
            ref var entry = ref _dispatchers.GetOrAddValueRef(type);
            if (entry != null) return entry;
            var meta = _ceras.GetTypeMetaData(type);
            entry = new DispatcherEntry(type, meta.HasSchema, meta.CurrentSchema);
            if (entry.IsType) return entry;
            var formatter = _ceras.GetSpecificFormatter(type);
            if (_ceras.Config.Advanced.AotMode == AotMode.None) { entry.CurrentSerializeDispatcher = CreateSpecificSerializerDispatcher(type, formatter); entry.CurrentDeserializeDispatcher = CreateSpecificDeserializerDispatcher(type, formatter); }
            else { entry.CurrentSerializeDispatcher = CreateSpecificSerializerDispatcher_Aot(type, formatter); entry.CurrentDeserializeDispatcher = CreateSpecificDeserializerDispatcher_Aot(type, formatter); }
            entry.Constructor = CreateObjectConstructor(type);
            if (meta.HasSchema) { var pair = new DispatcherPair(entry.CurrentSerializeDispatcher, entry.CurrentDeserializeDispatcher); entry.SchemaDispatchers[entry.CurrentSchema] = pair; }
            return entry;
        }

        Func<object> CreateObjectConstructor(Type type) { if (type.IsArray) return () => null; if (CerasSerializer.IsFormatterConstructed(type) || type.IsValueType) return () => null; var tc = _ceras.Config.GetTypeConfig(type, false).TypeConstruction; if (tc == null) throw new InvalidOperationException($"No ctor for '{type.FullName}'"); if (tc.HasDataArguments || tc is ConstructNull) return () => null; return tc.GetRefFormatterConstructor(_ceras.Config.Advanced.AotMode == AotMode.None); }
        static SerializeDelegate<T> CreateSpecificSerializerDispatcher(Type type, IFormatter specificFormatter) { var method = specificFormatter.GetType().ResolveSerializeMethod(type); if (type == typeof(T)) return (SerializeDelegate<T>)Delegate.CreateDelegate(typeof(SerializeDelegate<T>), specificFormatter, method); var helper = typeof(ReferenceFormatter<T>).GetMethod(nameof(CreateSerializeDispatcherHelper), BindingFlags.Static | BindingFlags.NonPublic); return (SerializeDelegate<T>)helper.MakeGenericMethod(type).Invoke(null, new object[] { specificFormatter }); }
        static SerializeDelegate<T> CreateSerializeDispatcherHelper<TSpecific>(IFormatter<TSpecific> formatter) => (writer, value) => formatter.Serialize(writer, (TSpecific)(object)value);
        static DeserializeDelegate<T> CreateSpecificDeserializerDispatcher(Type type, IFormatter specificFormatter) { var method = specificFormatter.GetType().ResolveDeserializeMethod(type); if (type == typeof(T)) return (DeserializeDelegate<T>)Delegate.CreateDelegate(typeof(DeserializeDelegate<T>), specificFormatter, method); var helper = typeof(ReferenceFormatter<T>).GetMethod(nameof(CreateDeserializeDispatcherHelper), BindingFlags.Static | BindingFlags.NonPublic); return (DeserializeDelegate<T>)helper.MakeGenericMethod(type).Invoke(null, new object[] { specificFormatter }); }
        static DeserializeDelegate<T> CreateDeserializeDispatcherHelper<TSpecific>(IFormatter<TSpecific> formatter) { return (ref SequenceReader<byte> reader, ref T value) => { TSpecific specific = value != null ? (TSpecific)(object)value : default; formatter.Deserialize(ref reader, ref specific); value = (T)(object)specific; }; }
        static SerializeDelegate<T> CreateSpecificSerializerDispatcher_Aot(Type type, IFormatter specificFormatter)
        {
            var method = specificFormatter.GetType().ResolveSerializeMethod(type);
            if (type == typeof(T))
                return (SerializeDelegate<T>)Delegate.CreateDelegate(typeof(SerializeDelegate<T>), specificFormatter, method);

            return (writer, value) =>
            {
                method.Invoke(specificFormatter, new object[] { writer, value });
            };
        }
        static DeserializeDelegate<T> CreateSpecificDeserializerDispatcher_Aot(Type type, IFormatter specificFormatter) { var method = specificFormatter.GetType().ResolveDeserializeMethod(type); if (type == typeof(T)) return (DeserializeDelegate<T>)Delegate.CreateDelegate(typeof(DeserializeDelegate<T>), specificFormatter, method); return (ref SequenceReader<byte> reader, ref T value) => { throw new NotSupportedException("AOT polymorphic migration requires Source Generator."); }; }
        void ISchemaTaintedFormatter.OnSchemaChanged(TypeMetaData meta) { if (_dispatchers.TryGetValue(meta.Type, out var entry)) { if (entry.SchemaDispatchers.TryGetValue(meta.CurrentSchema, out var pair)) { entry.CurrentSerializeDispatcher = pair.SerializeDispatcher; entry.CurrentDeserializeDispatcher = pair.DeserializeDispatcher; } else { entry.CurrentSerializeDispatcher = null; entry.CurrentDeserializeDispatcher = null; } } }
        class DispatcherEntry { public readonly Type Type; public Func<object> Constructor; public readonly bool IsType, IsExternalRootObject, IsValueType; public Schema CurrentSchema; public SerializeDelegate<T> CurrentSerializeDispatcher; public DeserializeDelegate<T> CurrentDeserializeDispatcher; public readonly Dictionary<Schema, DispatcherPair> SchemaDispatchers; public DispatcherEntry(Type type, bool hasSchema, Schema currentSchema) { Type = type; CurrentSchema = currentSchema; IsType = typeof(Type).IsAssignableFrom(type); IsExternalRootObject = typeof(IExternalRootObject).IsAssignableFrom(type); IsValueType = type.IsValueType; if (hasSchema) SchemaDispatchers = new Dictionary<Schema, DispatcherPair>(); } }
        struct DispatcherPair { public readonly SerializeDelegate<T> SerializeDispatcher; public readonly DeserializeDelegate<T> DeserializeDispatcher; public DispatcherPair(SerializeDelegate<T> serialize, DeserializeDelegate<T> deserialize) { SerializeDispatcher = serialize; DeserializeDispatcher = deserialize; } }
    }

    public sealed class ReferenceFormatter_KnownSealedType<T> : IFormatter<T> where T : class
    {
        const int Null = -1, NewValueSameType = -3, Bias = 5;
        readonly CerasSerializer _ceras; readonly bool _allowReferences; readonly IFormatter<T> _formatter; readonly Func<T> _constructor;
        public ReferenceFormatter_KnownSealedType(CerasSerializer ceras) { _ceras = ceras; if (typeof(T).IsStatic()) throw new InvalidOperationException("static"); if (!typeof(T).IsSealed) throw new InvalidOperationException("Not sealed"); _allowReferences = _ceras.Config.PreserveReferences; _formatter = (IFormatter<T>)ceras.GetSpecificFormatter(typeof(T)); _constructor = (Func<T>)_ceras.Config.GetTypeConfig(typeof(T), false).TypeConstruction.GetRefFormatterConstructor(_ceras.Config.Advanced.AotMode == AotMode.None); }
        
        public void SerializeUntyped(IBufferWriter<byte> writer, object value)
        {
            Serialize(writer, (T)value);
        }

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            T value = default;
            if (member is FieldInfo f)
            {
                var existing = f.GetValue(container);
                if (existing is T t) value = t;
                Deserialize(ref reader, ref value);
                f.SetValue(container, value);
            }
            else if (member is PropertyInfo p)
            {
                var existing = p.GetValue(container);
                if (existing is T t) value = t;
                Deserialize(ref reader, ref value);
                p.SetValue(container, value);
            }
        }
        
        public void Serialize(IBufferWriter<byte> writer, T value) { if (ReferenceEquals(value, null)) { SerializerBinary.WriteUInt32Bias(writer, Null, Bias); return; } if (_allowReferences) { if (_ceras.InstanceData.ObjectCache.TryGetExistingObjectId(value, out int id)) SerializerBinary.WriteUInt32Bias(writer, id, Bias); else { _ceras.InstanceData.ObjectCache.RegisterObject(value); SerializerBinary.WriteUInt32Bias(writer, NewValueSameType, Bias); _formatter.Serialize(writer, value); } } else { SerializerBinary.WriteUInt32Bias(writer, NewValueSameType, Bias); _formatter.Serialize(writer, value); } }
        public void Deserialize(ref SequenceReader<byte> reader, ref T value) { var objId = SerializerBinary.ReadUInt32Bias(ref reader, Bias); if (objId == Null) { if (value != null) _ceras.DiscardObjectMethod?.Invoke(value); value = default; return; } if (objId >= 0) { value = _ceras.InstanceData.ObjectCache.GetExistingObject<T>(objId); return; } if (value == null) value = _constructor(); if (!_allowReferences) { _formatter.Deserialize(ref reader, ref value); return; } var objectProxy = _ceras.InstanceData.ObjectCache.CreateDeserializationProxy<T>(); objectProxy.Value = value; _formatter.Deserialize(ref reader, ref objectProxy.Value); value = objectProxy.Value; }
    }

    static class MemberHelper { internal static byte _field; internal static byte _prop { get; set; } internal static void _method() { } }
}
