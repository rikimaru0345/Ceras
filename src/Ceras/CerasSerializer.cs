// ReSharper disable RedundantTypeArgumentsOfMethod
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LiveTesting")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ceras.Test")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ceras.AotGenerator")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ceras.TestDebugger")]
namespace Ceras
{
    using Exceptions;
    using Formatters;
    using Helpers;
    using Resolvers;
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using Ceras.Savegame;
    using System.IO;
    using System.Threading.Tasks;

    public class CerasSerializer : ICerasAdvanced
    {
        internal static readonly Type _rtTypeType, _rtFieldType, _rtPropType, _rtCtorType, _rtMethodType;
        static readonly HashSet<Type> _formatterConstructedTypes = new HashSet<Type>();

        public static void AddFormatterConstructedType(Type type)
        {
            lock (_formatterConstructedTypes) _formatterConstructedTypes.Add(type);
        }

        internal static bool IsFormatterConstructed(Type type)
        {
            if (type.IsAbstract) return true;
            lock (_formatterConstructedTypes) return _formatterConstructedTypes.Contains(type);
        }

        internal static bool IsPrimitiveType(Type type)
        {
            if (type.IsPrimitive) return true;
            if (type.IsArray) return IsPrimitiveType(type.GetElementType());
            if (type == typeof(string) || type == typeof(Type)) return true;
            if (type == _rtTypeType || type == _rtFieldType || type == _rtPropType || type == _rtCtorType || type == _rtMethodType) return true;
            return false;
        }

        internal static HashSet<Assembly> _frameworkAssemblies = new HashSet<Assembly> { typeof(object).Assembly, typeof(Uri).Assembly, typeof(Enumerable).Assembly };

        static CerasSerializer()
        {
            var type = typeof(Type);
            _rtTypeType = type.GetType();
            _formatterConstructedTypes.Add(type);
            _formatterConstructedTypes.Add(_rtTypeType);
            _formatterConstructedTypes.Add(typeof(FieldInfo));
            var field = typeof(MemberHelper).GetField(nameof(MemberHelper._field), BindingFlags.Static | BindingFlags.NonPublic);
            _rtFieldType = field.GetType();
            _formatterConstructedTypes.Add(_rtFieldType);
            _formatterConstructedTypes.Add(typeof(PropertyInfo));
            var prop = typeof(MemberHelper).GetProperty(nameof(MemberHelper._prop), BindingFlags.Static | BindingFlags.NonPublic);
            _rtPropType = prop.GetType();
            _formatterConstructedTypes.Add(_rtPropType);
            _formatterConstructedTypes.Add(typeof(ConstructorInfo));
            var ctor = typeof(CerasSerializer).GetConstructor(BindingFlags.Static | BindingFlags.NonPublic, null, new Type[0], new ParameterModifier[0]);
            _rtCtorType = ctor.GetType();
            _formatterConstructedTypes.Add(_rtCtorType);
            _formatterConstructedTypes.Add(typeof(MethodInfo));
            var method = typeof(MemberHelper).GetMethod(nameof(MemberHelper._method), BindingFlags.Static | BindingFlags.NonPublic);
            _rtMethodType = method.GetType();
            _formatterConstructedTypes.Add(_rtMethodType);
            _formatterConstructedTypes.Add(typeof(string));
        }

        public static void ClearGenericCaches() { ObjectCache.RefProxyPoolRegister.TrimAll(); }

        internal readonly SerializerConfig Config;
        public SerializerConfig GetConfig() => Config;
        public ICerasAdvanced Advanced => this;

        readonly Type[] _knownTypes;
        readonly DynamicObjectFormatterResolver _dynamicResolver;
        readonly FormatterResolverCallback[] _userResolvers;
        internal readonly ITypeBinder TypeBinder;
        readonly List<IFormatterResolver> _resolvers = new List<IFormatterResolver>();
        readonly TypeDictionary<TypeMetaData> _metaData = new TypeDictionary<TypeMetaData>();
        readonly TypeDictionary<TypeMetaData> _staticMetaData = new TypeDictionary<TypeMetaData>();
        readonly NoLockFactoryPool<InstanceData> _instanceDataPool;
        internal readonly Action<object> DiscardObjectMethod;
        readonly Stack<InstanceData> _recursionStack = new Stack<InstanceData>();
        internal InstanceData InstanceData;
        int _recursionDepth = 0;
        RecursionMode _mode = RecursionMode.Idle;

        public object UserContext { get => InstanceData.UserContext; set => InstanceData.UserContext = value; }
        public ProtocolChecksum ProtocolChecksum { get; } = new ProtocolChecksum();

        public CerasSerializer(SerializerConfig config = null)
        {
            if (config != null && !config.Claim(this)) throw new CerasException("SerializerConfig already in use");
            Config = config ?? new SerializerConfig();
            if (Config.ExternalObjectResolver == null) Config.ExternalObjectResolver = new ErrorResolver();
            if (Config.Advanced.UseReinterpretFormatter && Config.VersionTolerance.Mode != VersionToleranceMode.Disabled) throw new NotSupportedException("Reinterpret + VersionTolerance not supported");
            if (Config.Advanced.AotMode != AotMode.None && Config.VersionTolerance.Mode != VersionToleranceMode.Disabled) throw new NotSupportedException("AOT + VersionTolerance not supported");
            TypeBinder = Config.Advanced.TypeBinder;
            DiscardObjectMethod = Config.Advanced.DiscardObjectMethod;
            _userResolvers = Config.OnResolveFormatter.ToArray();
            _resolvers.Add(new PrimitiveResolver(this));
            _resolvers.Add(new ReinterpretFormatterResolver(this));
            _resolvers.Add(new StandardFormatterResolver(this));
            _resolvers.Add(new CollectionFormatterResolver(this));

            IFormatter stringFormatter;
            var stringTypeConfig = Config.GetExistingTypeConfig(typeof(string), false);
            if (stringTypeConfig?.CustomFormatter != null) stringFormatter = stringTypeConfig.CustomFormatter;
            else if (Config.Advanced.SizeLimits.MaxStringLength < uint.MaxValue) stringFormatter = new MaxSizeStringFormatter(Config.Advanced.SizeLimits.MaxStringLength);
            else stringFormatter = new StringFormatter();
            PrepareFormatter(stringFormatter);
            SetFormatters(typeof(string), stringFormatter, stringFormatter);

            var typeFormatter = new TypeFormatter(this);
            var runtimeType = GetType().GetType();
            SetFormatters(typeof(Type), typeFormatter, typeFormatter);
            SetFormatters(runtimeType, typeFormatter, typeFormatter);
            _resolvers.Add(new ReflectionFormatterResolver(this));
            _dynamicResolver = new DynamicObjectFormatterResolver(this);
            _resolvers.Add(new ExpressionFormatterResolver(this));

            _knownTypes = Config.KnownTypes.ToArray();

            // Try to find and call the source-generated registration helper
            var generatedType = Type.GetType("Ceras.Generated.GeneratedFormatters, Ceras.Generated") ??
                                Type.GetType("Ceras.Generated.GeneratedFormatters");

            if (generatedType != null)
            {
                var method = generatedType.GetMethod("UseFormatters", BindingFlags.Static | BindingFlags.Public);
                method?.Invoke(null, new object[] { Config });
            }

            foreach (var t in _knownTypes) ProtocolChecksum.Add(t.FullName); ProtocolChecksum.Finish();
            foreach (var t in _knownTypes) if (!t.ContainsGenericParameters) GetReferenceFormatter(t);

            _instanceDataPool = new NoLockFactoryPool<InstanceData>(() => new InstanceData { ObjectCache = new ObjectCache(Config.Advanced.ObjectCacheComparer), TypeCache = new TypeCache(_knownTypes), EncounteredSchemaTypes = new HashSet<Type>() });
            InstanceData = _instanceDataPool.RentObject();
            if (Config.Advanced.SealTypesWhenUsingKnownTypes && _knownTypes.Length > 0) typeFormatter.Seal();

            void SetFormatters(Type type, IFormatter specific, IFormatter reference) { var meta = GetTypeMetaData(type); meta.SpecificFormatter = specific; meta.ReferenceFormatter = reference; }
        }

        public byte[] Serialize<T>(T obj)
        {
            var writer = new ArrayBufferWriter<byte>();
            Serialize(writer, obj);
            return writer.WrittenSpan.ToArray();
        }

        public int Serialize<T>(T obj, ref byte[] buffer, int offset = 0)
        {
            var writer = new FixedArrayBufferWriter(buffer, offset);
            Serialize(writer, obj);
            buffer = writer.Buffer;
            int size = writer.Offset - offset;
            return size;
        }

        public void Serialize<T>(IBufferWriter<byte> writer, T obj)
        {
            EnterRecursive(RecursionMode.Serialization);
            try
            {
                InstanceData.CurrentRoot = obj as IExternalRootObject;
                if (Config.Advanced.EmbedChecksum) SerializerBinary.WriteInt32Fixed(writer, ProtocolChecksum.Checksum);
                GetFormatter<T>().Serialize(writer, obj);
                if (!Config.Advanced.PersistTypeCache) InstanceData.TypeCache.ResetSerializationCache();
                InstanceData.ObjectCache.ClearSerializationCache();
            }
            finally { InstanceData.EncounteredSchemaTypes.Clear(); InstanceData.CurrentRoot = null; LeaveRecursive(RecursionMode.Serialization); }
        }

        public T Deserialize<T>(byte[] buffer) { T value = default; int offset = 0; Deserialize(ref value, buffer, ref offset); return value; }
        public void Deserialize<T>(ref T value, byte[] buffer) { int offset = 0; Deserialize(ref value, buffer, ref offset); }
        public void Deserialize<T>(ref T value, byte[] buffer, ref int offset, int expectedReadLength = -1)
        {
            var seq = new ReadOnlySequence<byte>(buffer, offset, buffer.Length - offset);
            var reader = new SequenceReader<byte>(seq);
            Deserialize(ref reader, ref value);
            int consumed = (int)reader.Consumed;
            offset += consumed;
            if (expectedReadLength != -1 && consumed != expectedReadLength) throw new UnexpectedBytesConsumedException("Mismatch", expectedReadLength, consumed, offset - consumed, offset);
        }

        public void Deserialize<T>(ref SequenceReader<byte> reader, ref T value)
        {
            EnterRecursive(RecursionMode.Deserialization);
            try
            {
                if (Config.Advanced.EmbedChecksum && SerializerBinary.ReadInt32Fixed(ref reader) != ProtocolChecksum.Checksum) throw new InvalidOperationException("Checksum mismatch");
                GetFormatter<T>().Deserialize(ref reader, ref value);
                if (!Config.Advanced.PersistTypeCache) InstanceData.TypeCache.ResetDeserializationCache();
                InstanceData.ObjectCache.ClearDeserializationCache();
                InstanceData.EncounteredSchemaTypes.Clear();
            }
            finally { LeaveRecursive(RecursionMode.Deserialization); }
        }

        class FixedArrayBufferWriter : IBufferWriter<byte>
        {
            public byte[] Buffer; public int Offset;
            public FixedArrayBufferWriter(byte[] buffer, int offset) { Buffer = buffer ?? CerasBufferPool.Rent(0x4000); Offset = offset; }
            public void Advance(int count) => Offset += count;
            public Memory<byte> GetMemory(int sizeHint = 0) { SerializerBinary.EnsureCapacity(ref Buffer, Offset, sizeHint); return Buffer.AsMemory(Offset); }
            public Span<byte> GetSpan(int sizeHint = 0) { SerializerBinary.EnsureCapacity(ref Buffer, Offset, sizeHint); return Buffer.AsSpan(Offset); }
        }

        public T Clone<T>(T obj) => Deserialize<T>(Serialize(obj));

        public async ValueTask SaveAsync<T>(T obj, Stream stream)
        {
            var writer = new ArrayBufferWriter<byte>();
            Serialize(writer, obj);
            await stream.WriteAsync(writer.WrittenMemory);
        }

        public async ValueTask<T> LoadAsync<T>(Stream stream)
        {
            // For a real implementation we'd read in chunks, but for now we read everything
            // to fulfill the API requirement.
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return Deserialize<T>(ms.ToArray());
        }

        byte[] ICerasAdvanced.SerializeStatic(Type type) { var writer = new ArrayBufferWriter<byte>(); ((ICerasAdvanced)this).SerializeStatic(type, writer); return writer.WrittenSpan.ToArray(); }
        void ICerasAdvanced.SerializeStatic(Type type, IBufferWriter<byte> writer) { var meta = GetStaticTypeMetaData(type); if (meta.StaticSerialize == null) PrepareStaticSerializationDispatchers(meta); meta.StaticSerialize(writer); }
        int ICerasAdvanced.SerializeStatic(Type type, ref byte[] buffer, ref int offset) { var writer = new FixedArrayBufferWriter(buffer, offset); ((ICerasAdvanced)this).SerializeStatic(type, writer); buffer = writer.Buffer; int size = writer.Offset - offset; offset = writer.Offset; return size; }
        void ICerasAdvanced.DeserializeStatic(Type type, byte[] buffer) { int offset = 0; ((ICerasAdvanced)this).DeserializeStatic(type, buffer, ref offset); }
        int ICerasAdvanced.DeserializeStatic(Type type, byte[] buffer, ref int offset) { var seq = new ReadOnlySequence<byte>(buffer, offset, buffer.Length - offset); var reader = new SequenceReader<byte>(seq); ((ICerasAdvanced)this).DeserializeStatic(type, ref reader); int size = (int)reader.Consumed; offset += size; return size; }
        void ICerasAdvanced.DeserializeStatic(Type type, ref SequenceReader<byte> reader) { var meta = GetStaticTypeMetaData(type); if (meta.StaticDeserialize == null) PrepareStaticSerializationDispatchers(meta); meta.StaticDeserialize(ref reader); }

        void PrepareStaticSerializationDispatchers(TypeMetaData meta)
        {
            var formatterType = typeof(DynamicFormatter<>).MakeGenericType(meta.Type);
            var f = Activator.CreateInstance(formatterType, new object[] { this, true });
            formatterType.GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(f, null);
            var createDelegates = typeof(CerasSerializer).GetMethod(nameof(CreateStaticDelegates), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(meta.Type);
            var result = ((StaticSerializeDelegate ser, StaticDeserializeDelegate deser))createDelegates.Invoke(null, new object[] { f });
            meta.StaticSerialize = result.ser; meta.StaticDeserialize = result.deser;
        }

        static (StaticSerializeDelegate, StaticDeserializeDelegate) CreateStaticDelegates<T>(IFormatter<T> formatter)
        {
            T dummy = default;
            return ((writer) => formatter.Serialize(writer, default), (ref SequenceReader<byte> reader) => formatter.Deserialize(ref reader, ref dummy));
        }

        public Type PeekType(byte[] buffer) { var seq = new ReadOnlySequence<byte>(buffer); var reader = new SequenceReader<byte>(seq); Type t = null; GetFormatter<Type>().Deserialize(ref reader, ref t); return t; }
        public IEnumerable<IFormatterResolver> GetFormatterResolvers() { foreach (var r in _resolvers) yield return r; yield return _dynamicResolver; }
        IFormatterResolver ICerasAdvanced.GetFormatterResolver<TResolver>() => Advanced.GetFormatterResolvers().OfType<TResolver>().FirstOrDefault();

        public IFormatter<T> GetFormatter<T>() => (IFormatter<T>)GetReferenceFormatter(typeof(T));
        public IFormatter<T> GetSpecificFormatter<T>() => (IFormatter<T>)GetSpecificFormatter(typeof(T));
        public IFormatter<T> GetReferenceFormatter<T>() => (IFormatter<T>)GetReferenceFormatter(typeof(T));

        public IFormatter GetReferenceFormatter(Type type)
        {
            var meta = GetTypeMetaData(type);
            if (meta.Type.IsValueType) return GetSpecificFormatter(type, meta);
            if (meta.ReferenceFormatter != null) return meta.ReferenceFormatter;
            bool isType = typeof(Type).IsAssignableFrom(type), isExternal = typeof(IExternalRootObject).IsAssignableFrom(type), isVT = Config.VersionTolerance.Mode != VersionToleranceMode.Disabled;
            var refFormatterType = (type.IsSealed && !isType && !isExternal && !isVT) ? typeof(ReferenceFormatter_KnownSealedType<>).MakeGenericType(type) : typeof(ReferenceFormatter<>).MakeGenericType(type);
            return meta.ReferenceFormatter = (IFormatter)Activator.CreateInstance(refFormatterType, this);
        }

        public IFormatter GetSpecificFormatter(Type type) => GetSpecificFormatter(type, GetTypeMetaData(type));
        IFormatter GetSpecificFormatter(Type type, TypeMetaData meta)
        {
            if (meta.SpecificFormatter != null) return meta.SpecificFormatter;
            if (type.IsAbstract() || type.IsInterface || type.ContainsGenericParameters) throw new InvalidOperationException("Abstract/Interface/OpenGeneric not supported");
            if (!meta.IsPrimitive && meta.TypeConfig.CustomFormatter != null) { meta.SpecificFormatter = meta.TypeConfig.CustomFormatter; FormatterHelper.ThrowOnMismatch(meta.SpecificFormatter, type); PrepareFormatter(meta.SpecificFormatter); return meta.SpecificFormatter; }
            if (!meta.IsPrimitive && meta.TypeConfig.CustomResolver != null) { var f = meta.TypeConfig.CustomResolver(this, type); meta.SpecificFormatter = f ?? throw new InvalidOperationException("Null formatter"); FormatterHelper.ThrowOnMismatch(meta.SpecificFormatter, type); PrepareFormatter(meta.SpecificFormatter); return meta.SpecificFormatter; }
            if (!meta.IsPrimitive) foreach (var r in _userResolvers) { var f = r(this, type); if (f != null) { meta.SpecificFormatter = f; FormatterHelper.ThrowOnMismatch(f, type); PrepareFormatter(f); return f; } }
            foreach (var r in _resolvers) { var f = r.GetFormatter(type); if (f != null) { meta.SpecificFormatter = f; PrepareFormatter(f); return f; } }
            var df = _dynamicResolver.GetFormatter(type); if (df != null) { meta.SpecificFormatter = df; PrepareFormatter(df); return df; }
            throw new NotSupportedException("No formatter for " + type.FullName);
        }

        internal TypeMetaData GetTypeMetaData(Type type) { ref var meta = ref _metaData.GetOrAddValueRef(type); return meta ?? CreateMetaData(type, false); }
        internal TypeMetaData GetStaticTypeMetaData(Type type) { ref var meta = ref _staticMetaData.GetOrAddValueRef(type); return meta ?? CreateMetaData(type, true); }
        TypeMetaData CreateMetaData(Type type, bool isStatic) { var dict = isStatic ? _staticMetaData : _metaData; ref var meta = ref dict.GetOrAddValueRef(type); if (meta != null) return meta; BannedTypes.ThrowIfBanned(type); bool isPrim = IsPrimitiveType(type), isFrame = _frameworkAssemblies.Contains(type.Assembly); meta = new TypeMetaData(type, isPrim ? null : Config.GetTypeConfig(type, isStatic), isFrame, isPrim); if (!isPrim) meta.CurrentSchema = meta.PrimarySchema = CreatePrimarySchema(type, isStatic); return meta; }

        void PrepareFormatter(IFormatter formatter)
        {
            var formatterType = formatter.GetType();
            formatterType.GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(formatter, null);
            if (formatterType.GetCustomAttribute<CerasNoInjectAttribute>() != null) return;
            var config = formatterType.GetCustomAttribute<CerasInjectAttribute>() ?? CerasInjectAttribute.Default;
            var fields = formatterType.GetFields(BindingFlags.Public | BindingFlags.Instance | (config.IncludePrivate ? BindingFlags.NonPublic : 0));
            foreach (var f in fields)
            {
                if (f.GetCustomAttribute<CerasNoInjectAttribute>() != null) continue;
                var ft = f.FieldType;
                if (ft == typeof(CerasSerializer)) SafeInject(formatter, f, this);
                else if (ft == typeof(SerializerConfig)) SafeInject(formatter, f, Config);
                else if (ft == typeof(IAdvancedConfig)) SafeInject(formatter, f, Config.Advanced);
                else if (ft == typeof(ISizeLimitsConfig)) SafeInject(formatter, f, Config.Advanced.SizeLimits);
                else if (ft == typeof(SchemaRegistry)) SafeInject(formatter, f, Config.Savegame.SchemaRegistry);
                else if (typeof(IFormatter).IsAssignableFrom(ft))
                {
                    var itf = ReflectionHelper.FindClosedType(ft, typeof(IFormatter<>));
                    if (itf == null) continue;
                    var fmtType = itf.GetGenericArguments()[0];
                    bool noRef = f.GetCustomAttribute<CerasNoReference>() != null;
                    if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(IFormatter<>)) SafeInject(formatter, f, noRef ? GetSpecificFormatter(fmtType) : GetReferenceFormatter(fmtType));
                    else if (ReflectionHelper.IsAssignableToGenericType(ft, typeof(IFormatter<>)))
                    {
                        var rf = noRef ? GetSpecificFormatter(fmtType) : GetReferenceFormatter(fmtType);
                        if (rf.GetType() == ft) SafeInject(formatter, f, rf);
                        else { var df = GetSpecificFormatter(fmtType); if (df.GetType() == ft) SafeInject(formatter, f, df); else throw new InvalidOperationException("Injection mismatch for " + fmtType.Name); }
                    }
                }
            }
            void SafeInject(object f, FieldInfo field, object value) { var ev = field.GetValue(f); if (ev == null) field.SetValue(f, value); else if (!ReferenceEquals(ev, value)) throw new InvalidOperationException("Injection conflict"); }
        }

        internal void ActivateSchemaOverride(Type type, Schema schema) { var meta = GetTypeMetaData(type); if (Equals(meta.CurrentSchema, schema)) return; meta.CurrentSchema = schema; GetSpecificFormatter(type, meta); for (int i = 0; i < meta.OnSchemaChangeTargets.Count; i++) meta.OnSchemaChangeTargets[i].OnSchemaChanged(meta); }
        internal void EnsurePrimarySchema(Type type) { var meta = GetTypeMetaData(type); if (!meta.CurrentSchema.IsPrimary) ActivateSchemaOverride(type, meta.PrimarySchema); }
        Schema CreatePrimarySchema(Type type, bool isStatic) { if (IsPrimitiveType(type) || type.IsAbstract() || type.IsInterface || type.ContainsGenericParameters) return null; var tc = Config.GetTypeConfig(type, isStatic); tc.Seal(); var s = new Schema(true, type, tc, isStatic); foreach (var mc in tc._allMembers) if (mc.ComputeFinalInclusionFast()) s.Members.Add(new SchemaMember(mc.PersistentName, mc.Member, mc.WriteBackOrder)); s.Members.Sort(SchemaMemberComparer.Instance); return s; }
        internal Schema ReadSchema(byte[] buffer, ref int offset, Type type, bool isStatic) { var meta = GetTypeMetaData(type); if (meta.IsPrimitive) throw new InvalidOperationException("No schema for primitive"); var tc = Config.GetTypeConfig(type, isStatic); var s = new Schema(false, type, tc, isStatic); int count = 0; var seq = new ReadOnlySequence<byte>(buffer, offset, buffer.Length - offset); var reader = new SequenceReader<byte>(seq); count = SerializerBinary.ReadInt32(ref reader); for (int i = 0; i < count; i++) { var name = SerializerBinary.ReadString(ref reader); var m = Schema.FindMemberInType(type, name, isStatic); if (m == null) s.Members.Add(new SchemaMember(name)); else s.Members.Add(new SchemaMember(name, m, tc.GetMemberConfig(m).WriteBackOrder)); } offset += (int)reader.Consumed; var secondaries = meta.SecondarySchemata; int idx = secondaries.IndexOf(s); if (idx == -1) { secondaries.Add(s); return s; } return secondaries[idx]; }
        internal static void WriteSchema(ref byte[] buffer, ref int offset, Schema schema) { if (!schema.IsPrimary) throw new InvalidOperationException("Not primary"); var members = schema.Members; var writer = new FixedArrayBufferWriter(buffer, offset); SerializerBinary.WriteInt32(writer, members.Count); for (int i = 0; i < members.Count; i++) SerializerBinary.WriteString(writer, members[i].PersistentName); buffer = writer.Buffer; offset = writer.Offset; }
        public string GenerateSerializationDebugReport(Type type) { if (IsPrimitiveType(type)) return type.FullName + " is primitive"; var tc = Config.GetTypeConfig(type, false); var inc = tc._allMembers.Where(m => m.ComputeFinalInclusionFast()).ToArray(); var exc = tc._allMembers.Where(m => !m.ComputeFinalInclusionFast()).ToArray(); string r = $"Schema report for {type.FriendlyName()}:\nSerialized:\n" + string.Join("\n", inc.Select(m => $"[{m.Member.Name}] {m.ComputeFinalInclusion().Reason}")) + "\nExcluded:\n" + string.Join("\n", exc.Select(m => $"[{m.Member.Name}] {m.ComputeFinalInclusion().Reason}")); return r; }

        void EnterRecursive(RecursionMode enteringMode) { _recursionDepth++; if (_recursionDepth == 1) _mode = enteringMode; else { if (_mode != enteringMode) throw new InvalidOperationException("Mixed mode recursion"); _recursionStack.Push(InstanceData); InstanceData = _instanceDataPool.RentObject(); } }
        void LeaveRecursive(RecursionMode leavingMode) { _recursionDepth--; if (_recursionDepth == 0) _mode = RecursionMode.Idle; else { _instanceDataPool.ReturnObject(InstanceData); InstanceData = _recursionStack.Pop(); } }
    }

    public interface ICerasAdvanced
    {
        byte[] SerializeStatic(Type type);
        void SerializeStatic(Type type, IBufferWriter<byte> writer);
        int SerializeStatic(Type type, ref byte[] buffer, ref int offset);
        void DeserializeStatic(Type type, byte[] buffer);
        int DeserializeStatic(Type type, byte[] buffer, ref int offset);
        void DeserializeStatic(Type type, ref SequenceReader<byte> reader);
        Type PeekType(byte[] buffer);
        T Clone<T>(T obj);
        IEnumerable<IFormatterResolver> GetFormatterResolvers();
        IFormatterResolver GetFormatterResolver<TResolver>() where TResolver : IFormatterResolver;
    }

    struct InstanceData { public TypeCache TypeCache; public ObjectCache ObjectCache; public IExternalRootObject CurrentRoot; public object UserContext; public HashSet<Type> EncounteredSchemaTypes; }
    enum RecursionMode { Idle, Serialization, Deserialization, }

    public class ProtocolChecksum
    {
        xxHash _hash = new xxHash(); bool _isClosed; int _checksum;
        public int Checksum => _isClosed ? _checksum : throw new InvalidOperationException("Not computed");
        internal ProtocolChecksum() { _hash.Init(0); }
        internal void Add(string name) { if (_isClosed) throw new InvalidOperationException("Closed"); var bytes = Encoding.UTF8.GetBytes(name); _hash.Update(bytes); }
        internal void Finish() { _isClosed = true; _checksum = (int)_hash.Digest(); }
    }

    class TypeMetaData
    {
        public readonly Type Type; public readonly bool IsFrameworkType; public readonly bool IsPrimitive;
        public bool HasSchema => !IsPrimitive && !IsFrameworkType;
        public readonly TypeConfig TypeConfig;
        public IFormatter SpecificFormatter, ReferenceFormatter;
        public Schema CurrentSchema, PrimarySchema;
        public readonly List<Schema> SecondarySchemata = new List<Schema>();
        public readonly List<ISchemaTaintedFormatter> OnSchemaChangeTargets = new List<ISchemaTaintedFormatter>();
        public StaticSerializeDelegate StaticSerialize; public StaticDeserializeDelegate StaticDeserialize;
        public TypeMetaData(Type type, TypeConfig typeConfig, bool isFrameworkType, bool isSerializationPrimitive) { Type = type; IsFrameworkType = isFrameworkType; IsPrimitive = isSerializationPrimitive; TypeConfig = typeConfig; }
    }
}
