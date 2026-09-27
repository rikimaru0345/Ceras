using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Ceras.Format;
using Ceras.Helpers;

namespace Ceras
{
    /// <summary>
    /// Robust serialization for persistent storage (robust formats, databases).
    /// Always includes a schema header for version tolerance and migration.
    /// </summary>
    /// <summary>
    /// Explicit codec for the robust file header (TODO Phase 3.3). All multi-byte fields are
    /// little-endian. Layout (Size = 56):
    /// 0 magic "CERA" | 4 format version | 5 flags | 6 reserved (2) | 8 game version |
    /// 12 producer build timestamp | 20 schema string offset | 28 data section offset |
    /// 36 payload string table offset | 44 layout fingerprint | 52 CRC32.
    /// </summary>
    internal static class RobustHeader
    {
        public const int MagicOffset = 0;
        public const int FormatVersionOffset = 4;
        public const int FlagsOffset = 5;
        public const int GameVersionOffset = 8;
        public const int BuildTimestampOffset = 12;
        public const int SchemaStringOffsetOffset = 20;
        public const int DataSectionOffsetOffset = 28;
        public const int StringTableOffsetOffset = 36;
        public const int FingerprintOffset = 44;
        public const int ChecksumOffset = 52;
        public const int ChecksumSize = 4;

        public const int Size = 56;

        public static void WritePlaceholder(ref byte[] buffer, ref int offset, uint formatVersion, uint gameVersion, long buildTimestamp)
        {
            SerializerBinary.EnsureCapacity(ref buffer, offset, Size);
            buffer.AsSpan(offset, Size).Clear();

            buffer[offset + MagicOffset + 0] = (byte)'C';
            buffer[offset + MagicOffset + 1] = (byte)'E';
            buffer[offset + MagicOffset + 2] = (byte)'R';
            buffer[offset + MagicOffset + 3] = (byte)'A';
            buffer[offset + FormatVersionOffset] = (byte)formatVersion;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + GameVersionOffset), gameVersion);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + BuildTimestampOffset), buildTimestamp);
            offset += Size;
        }

        public static void FinalizeHeader(byte[] buffer, int totalLength, byte flags,
            long schemaStringOffset, long dataSectionOffset, long stringTableOffset, ulong fingerprint)
        {
            buffer[FlagsOffset] = flags;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(SchemaStringOffsetOffset), schemaStringOffset);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(DataSectionOffsetOffset), dataSectionOffset);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(StringTableOffsetOffset), stringTableOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(FingerprintOffset), fingerprint);

            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ChecksumOffset), 0);
            uint crc = Ceras.Helpers.HashHelpers.CalculateCrc32WithZeroedRange(
                buffer.AsSpan(0, totalLength), ChecksumOffset, ChecksumSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(ChecksumOffset), crc);
        }

        public static void Read(byte[] data,
            out long buildTimestamp, out byte flags, out long schemaStringOffset,
            out long dataSectionOffset, out long stringTableOffset, out ulong fingerprint)
        {
            buildTimestamp = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(BuildTimestampOffset));
            flags = data[FlagsOffset];
            schemaStringOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(SchemaStringOffsetOffset));
            dataSectionOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(DataSectionOffsetOffset));
            stringTableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(StringTableOffsetOffset));
            fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(FingerprintOffset));
        }
    }

    public sealed class CerasRobustSerializer : CerasSerializer
    {
        // v3: two-region string handling — the schema strings live in their own region so the happy
        // path can skip schema/metadata decoding entirely (TODO Phase 3.3).
        public const int FormatVersion = 3;

        internal readonly CerasRobustConfig _config;

        public new CerasRobustConfig Config => _config;

        public CerasRobustSerializer(Action<CerasRobustConfig> configure = null) : base(MakeConfig(configure, Assembly.GetCallingAssembly()))
        {
            _config = (CerasRobustConfig)base.Config;
        }

        internal CerasRobustSerializer(CerasRobustConfig config) : base(config)
        {
            _config = config;
        }

        static CerasRobustConfig MakeConfig(Action<CerasRobustConfig> configure, Assembly callingAssembly)
        {
            var config = new CerasRobustConfig();
            config.SetOwningAssembly(callingAssembly);
            InitializeRobustFormatters(config);
            configure?.Invoke(config);
            return config;
        }

        static void InitializeRobustFormatters(CerasRobustConfig config)
        {
            // Register the robust string and type formatters required for schema-aware serialization
            var stringConfig = config.ConfigType<string>();
            if (!(stringConfig.CustomFormatter is RobustStringFormatter))
                stringConfig.CustomFormatter = new RobustStringFormatter();

            var typeConfig = config.ConfigType<Type>();
            if (!(typeConfig.CustomFormatter is RobustTypeFormatter))
                typeConfig.CustomFormatter = new RobustTypeFormatter(config.SchemaRegistry);
        }



        public byte[] Save<T>(T rootObject, uint robustVersion)
        {
            var registry = _config.SchemaRegistry;

            // Declare the root (normally via the generated manifest) and seal. After sealing the
            // registry never grows and the schema bytes are the frozen baked schema (TODO 2.6/3.3).
            EnsureRootRegistered(registry, typeof(T));
            registry.Seal();

            if (!registry.TryGetBakedSchema(out var schemaBytes, out var schemaStrings, out var typeToId, out _, out var schemaFingerprint))
                throw new InvalidOperationException("Schema was sealed but not baked.");

            int rootTypeId = GetTypeId(typeof(T), registry);
            if (rootTypeId == WellKnownTypes.Null)
                throw UndeclaredType(typeof(T));

            var buffer = new byte[4096];
            int offset = 0;

            RobustHeader.WritePlaceholder(ref buffer, ref offset, FormatVersion, robustVersion, _config.BuildTimestamp);

            // Schema region: [schema bytes][schema string table]. The schema string table is separate
            // from the payload string table so skipping schema interpretation never decodes metadata names.
            SerializerBinary.EnsureCapacity(ref buffer, offset, schemaBytes.Length);
            schemaBytes.CopyTo(buffer.AsSpan(offset));
            offset += schemaBytes.Length;

            int schemaStringOffset = offset;
            SerializerBinary.EnsureCapacity(ref buffer, offset, schemaStrings.Length);
            schemaStrings.CopyTo(buffer.AsSpan(offset));
            offset += schemaStrings.Length;

            int dataStart = offset;

            var payloadStrings = new StringInternTable();

            RobustContext.BeginWrite(payloadStrings, typeToId);

            if (Ceras.Helpers.ObjectCache.DebugTraceEnabled)
                Ceras.Helpers.ObjectCache.BeginSerializeTrace();

            try
            {
                var writer = new ArrayBufferWriter<byte>();
                SchemaSection.WriteLeb128(writer, (uint)rootTypeId);
                Serialize(writer, rootObject);

                var data = writer.WrittenSpan;
                SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
                data.CopyTo(buffer.AsSpan(offset));
                offset += data.Length;
            }
            finally
            {
                RobustContext.End();
            }

            int stringTableStart = payloadStrings.Flush(ref buffer, ref offset, _config.StringTableCompression);
            RobustHeader.FinalizeHeader(buffer, offset, (byte)_config.StringTableCompression, schemaStringOffset, dataStart, stringTableStart, schemaFingerprint);
            return buffer[..offset];
        }

        public T Load<T>(byte[] data, uint currentRobustVersion)
        {
            T value = default;
            Load(ref value, data, currentRobustVersion);
            return value;
        }

        public void Load<T>(ref T value, byte[] data, uint currentRobustVersion)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < RobustHeader.Size) throw new InvalidOperationException("Header too short");

            var registry = _config.SchemaRegistry;

            // Check magic "CERA"
            if (data[0] != (byte)'C' || data[1] != (byte)'E' || data[2] != (byte)'R' || data[3] != (byte)'A')
                throw new Ceras.Exceptions.CerasDataCorruptionException("Invalid magic. The provided data does not appear to be a Ceras-serialized block.");

            uint version = data[4];
            if (version != FormatVersion)
                throw new Ceras.Exceptions.CerasVersionMismatchException($"Format version mismatch. File version {version} is not compatible with library version {FormatVersion}.", version, FormatVersion);

            uint robustVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(RobustHeader.GameVersionOffset));
            if (robustVersion > currentRobustVersion)
                throw new Ceras.Exceptions.CerasRobustVersionException($"Attempted to load data from a newer application version ({robustVersion}) than the current version ({currentRobustVersion}).", robustVersion, currentRobustVersion);

            RobustHeader.Read(data, out long buildTimestamp, out byte compressionByte,
                out long schemaStringOffsetL, out long dataSectionOffset, out long stringTableOffset, out ulong schemaFingerprint);

            // Reject saves from a newer build before decoding the payload, even when layouts match.
            // The check is active only in a known-build configuration; an explicitly unknown build
            // timestamp records 0 and cannot detect a newer save (TODO 0.6 item 7).
            if (_config.BuildTimestampKnown && buildTimestamp > _config.BuildTimestamp)
                throw new Ceras.Exceptions.CerasException(
                    $"The save was produced by a newer build (timestamp {buildTimestamp}) than the current build " +
                    $"({_config.BuildTimestamp}). Update the application before loading this save.");

            int schemaStringOffset = (int)schemaStringOffsetL;

            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(RobustHeader.ChecksumOffset));
            uint computedCrc = Ceras.Helpers.HashHelpers.CalculateCrc32WithZeroedRange(
                data, RobustHeader.ChecksumOffset, RobustHeader.ChecksumSize);
            if (storedCrc != computedCrc)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"CRC mismatch: the saved data is corrupted or truncated (stored 0x{storedCrc:X8}, computed 0x{computedCrc:X8}).");

            var compression = (StringTableCompression)compressionByte;
            var payloadTable = StringTable.Load(data, (int)stringTableOffset, data.Length - (int)stringTableOffset, compression);

            // Pin the declared set; no post-seal growth.
            EnsureRootRegistered(registry, typeof(T));
            registry.Seal();
            ulong currentFingerprint = registry.ComputeFingerprint();

            if (GetTypeId(typeof(T), registry) == WellKnownTypes.Null)
                throw UndeclaredType(typeof(T));

            var dataSeq = new ReadOnlySequence<byte>(data, (int)dataSectionOffset, (int)stringTableOffset - (int)dataSectionOffset);
            var reader = new SequenceReader<byte>(dataSeq);

            if (Ceras.Helpers.ObjectCache.DebugTraceEnabled)
                Ceras.Helpers.ObjectCache.BeginDeserializeTrace();

            // Happy path: matching layout fingerprint. Use the baked manifest for id -> type
            // resolution and skip parsing the schema and decoding any metadata names entirely.
            if (schemaFingerprint == currentFingerprint)
            {
                if (!registry.TryGetBakedSchema(out _, out _, out _, out var idToType, out _))
                    throw new InvalidOperationException("Schema was sealed but not baked.");

                int storedRootId = (int)SchemaSection.ReadLeb128(ref reader); // root type id
                ValidateStoredRootType(storedRootId, typeof(T), idToType, registry);

                RobustContext.BeginRead(payloadTable, idToType);
                try
                {
                    Deserialize(ref reader, ref value);
                }
                finally
                {
                    RobustContext.End();
                }
                return;
            }

            // Slow path: parse the schema region (schema bytes + its own string table) and interpret.
            if (Ceras.Helpers.ObjectCache.DebugTraceEnabled) Console.WriteLine($"[CerasRobust] Fingerprint mismatch! File: {schemaFingerprint:X16}, Current: {currentFingerprint:X16}. Using SchemaTraversalEngine (slow path).");

            var schemaStrings = StringTable.Load(data, (int)schemaStringOffset,
                (int)dataSectionOffset - (int)schemaStringOffset, StringTableCompression.Disabled);

            int schemaLength = (int)schemaStringOffset - RobustHeader.Size;
            var schemaSeq = new ReadOnlySequence<byte>(data, RobustHeader.Size, schemaLength);
            var schemaReader = new SequenceReader<byte>(schemaSeq);
            var fileSchemas = SchemaSection.Read(ref schemaReader, schemaStrings);

            var slowIdToType = BuildIdToType(fileSchemas, registry, Config.TypeBinder);

            int storedSlowRootId = (int)SchemaSection.ReadLeb128(ref reader); // root type id
            ValidateStoredRootType(storedSlowRootId, typeof(T), slowIdToType, registry);

            RobustContext.BeginRead(payloadTable, slowIdToType);
            try
            {
                var engine = new SchemaTraversalEngine(fileSchemas, payloadTable, robustVersion, this);
                InstanceData.DeserializeCallbacks = null;
                value = engine.ReadRoot<T>(ref reader, value);
                // Deferred [OnAfterDeserialize] callbacks run once the whole graph is populated.
                FlushDeserializeCallbacks();
            }
            finally
            {
                InstanceData.DeserializeCallbacks = null;
                RobustContext.End();
            }
        }

        /// <summary>
        /// Resolves every type referenced by a file's schema section to a CLR <see cref="Type"/>
        /// (used only by the migration/interpreter slow path).
        /// </summary>
        static Dictionary<int, Type> BuildIdToType(Dictionary<string, OldTypeSchema> fileSchemas, SchemaRegistry registry, ITypeBinder typeBinder)
        {
            var idToType = new Dictionary<int, Type>();
            for (int i = 0; i < 128; i++) if (WellKnownTypes.TryGetType(i, out var wkType)) idToType[i] = wkType;

            var schemasById = new Dictionary<int, OldTypeSchema>();
            foreach (var s in fileSchemas.Values) schemasById.TryAdd(s.TypeId, s);

            Type ResolveById(int id)
            {
                if (idToType.TryGetValue(id, out var t)) return t;
                if (!schemasById.TryGetValue(id, out var s)) return null;

                // 1. Registry
                if (registry.TryResolveType(s.StableName, out t))
                {
                    idToType[id] = t;
                    return t;
                }

                // 2. Binder Fallback (for normal types / interfaces / enums)
                if (s.Kind != SchemaTypeKind.GenericInstantiation)
                {
                    t = typeBinder.GetTypeFromBase(s.StableName);
                    if (t != null)
                    {
                        idToType[id] = t;
                        return t;
                    }
                }
                else
                {
                    // 3. Generic Reconstruction
                    var defType = ResolveById(s.DefinitionTypeId);
                    if (defType != null)
                    {
                        var argTypes = new Type[s.ArgTypeIds.Length];
                        bool allResolved = true;
                        for (int i = 0; i < s.ArgTypeIds.Length; i++)
                        {
                            argTypes[i] = ResolveById(s.ArgTypeIds[i]);
                            if (argTypes[i] == null) { allResolved = false; break; }
                        }
                        if (allResolved)
                        {
                            t = defType.MakeGenericType(argTypes);
                            idToType[id] = t;
                            return t;
                        }
                    }
                }

                return null;
            }

            foreach (var s in fileSchemas.Values)
            {
                try
                {
                    ResolveById(s.TypeId);
                }
                catch (Exception e)
                {
                    throw new Ceras.Exceptions.CerasSchemaException($"Error resolving type with stable name '{s.StableName}': {e.Message}", e);
                }
            }

            return idToType;
        }

        static int GetTypeId(Type type, SchemaRegistry registry)
        {
            if (WellKnownTypes.TryGetId(type, out int id)) return id;
            if (registry.TryGetId(type, out id)) return id;
            return WellKnownTypes.Null;
        }

        /// <summary>
        /// The data section starts with the declared root type id. Loading must honor it: the payload
        /// layout is the one written for that declared type, and only the declared type (or its rename
        /// alias) can be read back safely. Base/derived loads cannot be validated from the id alone
        /// (a same-runtime-type root writes no type tag), so they are rejected rather than risking a
        /// silent misparse. Accepted: an exact id match, or a resolved stored type identical to the
        /// requested type (the rename/migration case, where the alias id can differ from the current id).
        /// </summary>
        static void ValidateStoredRootType(int storedRootId, Type requestedType, Dictionary<int, Type> idToType, SchemaRegistry registry)
        {
            // 1. Exact context match: the stored id is this context's id for typeof(T).
            if (storedRootId == GetTypeId(requestedType, registry))
                return;

            Type storedType = null;
            idToType?.TryGetValue(storedRootId, out storedType);

            // 2. Rename/migration: the interpreter resolves a [PreviousTypeName]-renamed root to the
            //    current CLR type. Its id may differ from the old stored id, but the layout is the same.
            if (storedType != null && storedType == requestedType)
                return;

            string storedDescription = storedType != null
                ? $"'{storedType.FullName}'"
                : $"type id {storedRootId} (which is not declared in this context)";

            throw new Ceras.Exceptions.CerasSchemaException(
                $"The saved data's root type {storedDescription} does not match the requested load type " +
                $"'{requestedType.FullName}'. Load the save with its declared root type; loading it as a " +
                $"base, derived, or unrelated type would silently misread the payload.");
        }

        /// <summary>
        /// Declares the root type before sealing. Generated types are already registered by the
        /// manifest; arrays and built-in collections (which the generator skips) are registered
        /// structurally. Undeclared user types are left unregistered and rejected by the caller.
        /// </summary>
        static void EnsureRootRegistered(SchemaRegistry registry, Type type)
        {
            if (registry.TryGetId(type, out _)) return;
            if (WellKnownTypes.IsWellKnown(type)) return;
            if (SchemaRegistry.TryBuildStructuralSchema(type, out var info))
                registry.Register(type, info);
        }

        static Ceras.Exceptions.CerasException UndeclaredType(Type type) =>
            new Ceras.Exceptions.CerasException(
                $"Type '{type.FullName}' is not declared in the selected serialization context. " +
                $"Make it a [CerasContext(\"name\")] root of the context you are using (or reachable from one), " +
                $"or declare it with [assembly: CerasKnownType<...>], so it is part of that context's generated " +
                $"manifest; then configure the serializer with UseGeneratedFormatters(name) for that context.");

        struct NormalizedTypeSchema
        {
            public int TypeId;
            public string StableName;
            public string[] PreviousNames;
            public SchemaTypeKind Kind;
            public (string Name, long Value)[] EnumValues;
            public (string StableName, string TypeName, bool IsMono)[] Fields;
            public string DefinitionName;
            public string[] ArgTypeNames;

            public NormalizedTypeSchema(int id, SchemaTypeInfo info)
            {
                TypeId = id;
                StableName = info.StableName;
                PreviousNames = info.PreviousTypeNames;
                Kind = info.Kind;
                EnumValues = info.EnumValues?.Select(ev => (ev.Name, ev.Value)).ToArray();
                Fields = info.Fields?.Select(f => (f.StableName, f.TypeName, f.IsMonomorphic)).ToArray();
                DefinitionName = info.UnderlyingTypeName;
                ArgTypeNames = info.ArgTypeNames;
            }

            public NormalizedTypeSchema(OldTypeSchema fs, Func<int, string> resolve)
            {
                TypeId = fs.TypeId;
                StableName = fs.StableName;
                PreviousNames = fs.PreviousNames;
                Kind = fs.Kind;
                EnumValues = fs.EnumValues?.Select(ev => (ev.Name, ev.Value)).ToArray();
                Fields = fs.Fields?.Select(f => (f.StableName, resolve(f.TypeId), f.IsMonomorphic)).ToArray();
                DefinitionName = resolve(fs.DefinitionTypeId);
                ArgTypeNames = fs.ArgTypeIds?.Select(resolve).ToArray();
            }
        }

        static void DumpEntry(StringBuilder sb, NormalizedTypeSchema s)
        {
            switch (s.Kind)
            {
                case SchemaTypeKind.Enum:
                    sb.AppendLine($"ENUM  [{s.TypeId}]  {s.StableName}");
                    if (s.EnumValues != null)
                        foreach (var ev in s.EnumValues)
                            sb.AppendLine($"      {ev.Name} = {ev.Value}");
                    break;

                case SchemaTypeKind.GenericInstantiation:
                    sb.AppendLine($"GENERIC  [{s.TypeId}]  {s.StableName}  def={s.DefinitionName}  args=[{string.Join(", ", s.ArgTypeNames ?? Array.Empty<string>())}]");
                    break;

                default:
                    sb.AppendLine($"{s.Kind.ToString().ToUpperInvariant()}  [{s.TypeId}]  {s.StableName}");
                    if (s.Fields != null)
                        foreach (var f in s.Fields)
                            sb.AppendLine($"      [{f.TypeName}]  {(f.IsMono ? "Mono" : "Poly")}  {f.StableName}");
                    break;
            }

            if (s.PreviousNames?.Length > 0)
                sb.AppendLine($"      prev: {string.Join(", ", s.PreviousNames)}");
        }

        // ── Reference-trace debug API ──────────────────────────────────────────────
        public static bool DebugTraceEnabled
        {
            get => ObjectCache.DebugTraceEnabled;
            set => ObjectCache.DebugTraceEnabled = value;
        }

        public static void DumpRefTraceComparison(string path)
            => ObjectCache.DumpComparison(path);
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Dumps the current registry schema to a string so you can inspect it.
        /// </summary>
        public string DumpSchema()
        {
            var registry = _config.SchemaRegistry;
            var sb = new StringBuilder();
            ulong currentFingerprint = registry.ComputeFingerprint();
            sb.AppendLine($"// Current fingerprint: {currentFingerprint:X16}");
            sb.AppendLine($"// Sealed: {registry.IsSealed}");

            var allEntries = registry.All().OrderBy(e => e.info.StableName, StringComparer.Ordinal).ToList();
            sb.AppendLine($"// Type count: {allEntries.Count}");
            sb.AppendLine();

            foreach (var (type, id, info) in allEntries)
            {
                DumpEntry(sb, new NormalizedTypeSchema(id, info));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Dumps the schema from a serialized file or byte array so you can inspect it.
        /// </summary>
        public static string DumpSchema(byte[] data)
        {
            if (data == null || data.Length < RobustHeader.Size)
                return "// Invalid or empty data";

            uint version = data[4];
            if (version != FormatVersion)
                return $"// Format version mismatch. File: {version}, Library: {FormatVersion}";

            RobustHeader.Read(data, out _, out byte flags, out long schemaStringOffsetL,
                out long dataSectionOffset, out long stringTableOffset, out ulong schemaFingerprint);

            int schemaStringOffset = (int)schemaStringOffsetL;

            var schemaStrings = StringTable.Load(data, (int)schemaStringOffset,
                (int)dataSectionOffset - (int)schemaStringOffset, StringTableCompression.Disabled);

            int schemaOffset = RobustHeader.Size;
            var sequence = new System.Buffers.ReadOnlySequence<byte>(data, schemaOffset, (int)schemaStringOffset - schemaOffset);
            var schemaReader = new System.Buffers.SequenceReader<byte>(sequence);
            var fileSchemas = SchemaSection.Read(ref schemaReader, schemaStrings);

            var sb = new StringBuilder();
            sb.AppendLine($"// File fingerprint: {schemaFingerprint:X16}");
            uint gameVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(RobustHeader.GameVersionOffset));
            sb.AppendLine($"// Game version: {gameVersion}");
            sb.AppendLine($"// Type count: {fileSchemas.Count}");
            sb.AppendLine();

            var idToName = new Dictionary<int, string>();
            for (int i = 0; i < 128; i++) if (WellKnownTypes.TryGetType(i, out var wkType)) idToName[i] = wkType.FullName ?? wkType.Name;
            foreach (var s in fileSchemas.Values) idToName[s.TypeId] = s.StableName;

            string Resolve(int id) => idToName.TryGetValue(id, out var name) ? name : $"ID:{id}";

            foreach (var kv in fileSchemas.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var fs = kv.Value;
                if (kv.Key != fs.StableName) continue; // skip alias entries

                DumpEntry(sb, new NormalizedTypeSchema(fs, Resolve));
            }

            return sb.ToString();
        }
    }
}
