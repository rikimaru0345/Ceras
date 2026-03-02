using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ceras.Format;
using Ceras.Helpers;

namespace Ceras
{
    /// <summary>
    /// Robust serialization for persistent storage (robust formats, databases).
    /// Always includes a schema header for version tolerance and migration.
    /// </summary>
    public class CerasRobustSerializer
    {
        internal const int OffsetMagic = 0;
        public const int OffsetFormatVersion = 4 + OffsetMagic;
        internal const int OffsetGameVersion = 1 + OffsetFormatVersion;
        public const int OffsetFingerprint = 8 + OffsetGameVersion;
        internal const int OffsetFlags = 8 + OffsetFingerprint;
        public const int OffsetDataSection = 1 + OffsetFlags;
        public const int OffsetStringTable = 8 + OffsetDataSection;
        internal const int OffsetChecksum = 8 + OffsetStringTable;
        public const int HeaderSize = 4 + OffsetChecksum;
        public const int FormatVersion = 2;

        readonly CerasSerializer _serializer;
        internal readonly CerasRobustConfig _config;

        public CerasRobustConfig Config => _config;

        public CerasRobustSerializer(Action<CerasRobustConfig> configure = null)
        {
            _config = new CerasRobustConfig();
            _config.Serializer = this;

            InitializeRobustFormatters(_config);

            // Robust format has its own CRC32 checksum in the header, so we don't need the per-object checksum
            _config.EmbedChecksum = false;

            configure?.Invoke(_config);

            _serializer = new CerasSerializer(_config);
        }

        public CerasRobustSerializer(CerasRobustConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.Serializer = this;

            InitializeRobustFormatters(_config);
            _config.EmbedChecksum = false;

            _serializer = new CerasSerializer(_config);
        }

        internal CerasRobustSerializer(CerasRobustConfig config, CerasSerializer serializer, bool compatibility)
        {
            _config = config;
            _serializer = serializer;
            _config.Serializer = this;
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

        public byte[] Serialize<T>(T obj) => _serializer.Serialize(obj);
        public void Serialize<T>(IBufferWriter<byte> writer, T obj) => _serializer.Serialize(writer, obj);
        public int Serialize<T>(T obj, ref byte[] buffer, int offset = 0) => _serializer.Serialize(obj, ref buffer, offset);

        public T Deserialize<T>(byte[] buffer) => _serializer.Deserialize<T>(buffer);
        public void Deserialize<T>(ref T obj, byte[] buffer) => _serializer.Deserialize(ref obj, buffer);
        public void Deserialize<T>(ref T obj, byte[] buffer, ref int offset) => _serializer.Deserialize(ref obj, buffer, ref offset);
        public void Deserialize<T>(ref SequenceReader<byte> reader, ref T obj) => _serializer.Deserialize(ref reader, ref obj);

        public byte[] Save<T>(T rootObject, uint robustVersion)
        {
            var registry = _config.SchemaRegistry;
            var internTable = new StringInternTable();

            // Ensure the root type is registered so we can resolve its schema
            registry.TryGetId(typeof(T), out _);

            // Ensure the registry is sealed so that discovered generic instantiations
            // don't affect the fingerprint.
            registry.Seal();

            int rootTypeId = GetTypeId(typeof(T), registry);

            var buffer = new byte[4096];
            int offset = 0;

            WriteHeaderPlaceholder(ref buffer, ref offset, robustVersion);

            ulong schemaFingerprint = registry.ComputeFingerprint();

            var typeToId = SchemaSection.Write(ref buffer, ref offset, registry, internTable);

            int dataStart = offset;

            RobustContext.BeginWrite(internTable, typeToId);

            if (Ceras.Helpers.ObjectCache.DebugTraceEnabled)
                Ceras.Helpers.ObjectCache.BeginSerializeTrace();

            try
            {
                var writer = new ArrayBufferWriter<byte>();
                SchemaSection.WriteLeb128(writer, (uint)rootTypeId);
                _serializer.Serialize(writer, rootObject);

                var data = writer.WrittenSpan;
                SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
                data.CopyTo(buffer.AsSpan(offset));
                offset += data.Length;
            }
            finally
            {
                RobustContext.End();
            }

            int stringTableStart = internTable.Flush(ref buffer, ref offset, _config.StringTableCompression);
            PatchHeader(buffer, offset, robustVersion, dataStart, stringTableStart, schemaFingerprint, (byte)_config.StringTableCompression);
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
            if (data.Length < HeaderSize) throw new InvalidOperationException("Header too short");

            var registry = _config.SchemaRegistry;

            // Ensure the root type is registered so we can resolve its schema
            registry.TryGetId(typeof(T), out _);

            // Ensure the registry is sealed before we start registering anything (like discovered generic instantiations)
            registry.Seal();

            // Check magic "CERA"
            if (data[0] != (byte)'C' || data[1] != (byte)'E' || data[2] != (byte)'R' || data[3] != (byte)'A')
                throw new InvalidOperationException("Invalid magic");

            uint version = data[OffsetFormatVersion];
            if (version != FormatVersion) throw new InvalidOperationException($"Format version mismatch. File: {version}, Library: {FormatVersion}");

            uint robustVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(OffsetGameVersion));
            if (robustVersion > currentRobustVersion) throw new InvalidOperationException("Save from newer version");

            ulong schemaFingerprint = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(OffsetFingerprint));
            byte compressionByte = data[OffsetFlags];
            long dataSectionOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetDataSection));
            long stringTableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetStringTable));

            var compression = (StringTableCompression)compressionByte;
            var table = StringTable.Load(data, (int)stringTableOffset, data.Length - (int)stringTableOffset, compression);
            ulong currentFingerprint = registry.ComputeFingerprint();

            int schemaOffset = HeaderSize;
            var sequence = new ReadOnlySequence<byte>(data, schemaOffset, (int)dataSectionOffset - schemaOffset);
            var schemaReader = new SequenceReader<byte>(sequence);
            var fileSchemas = SchemaSection.Read(ref schemaReader, table);

            var idToType = new Dictionary<int, Type>();
            for (int i = 0; i < 128; i++) if (WellKnownTypes.TryGetType(i, out var wkType)) idToType[i] = wkType;

            var typeBinder = _serializer.Config.TypeBinder;
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
                    throw new Exception($"Error resolving type with stable name '{s.StableName}': {e.Message}", e);
                }
            }

            if (Ceras.Helpers.ObjectCache.DebugTraceEnabled)
                Ceras.Helpers.ObjectCache.BeginDeserializeTrace();

            RobustContext.BeginRead(table, idToType);

            try
            {
                var dataSeq = new ReadOnlySequence<byte>(data, (int)dataSectionOffset, (int)stringTableOffset - (int)dataSectionOffset);
                var reader = new SequenceReader<byte>(dataSeq);
                uint rootTypeId = SchemaSection.ReadLeb128(ref reader);

                if (schemaFingerprint != currentFingerprint)
                {
                    if (Ceras.Helpers.ObjectCache.DebugTraceEnabled) Console.WriteLine($"[CerasRobust] Fingerprint mismatch! File: {schemaFingerprint:X16}, Current: {currentFingerprint:X16}. Using SchemaTraversalEngine (slow path).");
                    var engine = new SchemaTraversalEngine(fileSchemas, table, robustVersion, _serializer);
                    value = engine.ReadRoot<T>(ref reader, value);
                    return;
                }

                if (Ceras.Helpers.ObjectCache.DebugTraceEnabled) Console.WriteLine($"[CerasRobust] Fingerprints match ({currentFingerprint:X16}). Using CerasSerializer (fast path).");
                _serializer.Deserialize(ref reader, ref value);
            }
            finally
            {
                RobustContext.End();
            }
        }

        static void WriteHeaderPlaceholder(ref byte[] buffer, ref int offset, uint robustVersion)
        {
            SerializerBinary.EnsureCapacity(ref buffer, offset, HeaderSize);
            // Clear header space
            buffer.AsSpan(offset, HeaderSize).Clear();

            buffer[offset + 0] = (byte)'C';
            buffer[offset + 1] = (byte)'E';
            buffer[offset + 2] = (byte)'R';
            buffer[offset + 3] = (byte)'A';
            buffer[offset + 4] = (byte)FormatVersion;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 5), robustVersion);
            offset += HeaderSize;
        }

        static void PatchHeader(byte[] buffer, int totalLength, uint robustVersion, long dataSectionOffset, long stringTableOffset, ulong schemaFingerprint, byte flags)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(OffsetFingerprint), schemaFingerprint);
            buffer[OffsetFlags] = flags;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(OffsetDataSection), dataSectionOffset);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(OffsetStringTable), stringTableOffset);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(OffsetChecksum), 0);
            uint crc = Ceras.Helpers.HashHelpers.CalculateCrc32(buffer.AsSpan(0, totalLength));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OffsetChecksum), crc);
        }

        static int GetTypeId(Type type, SchemaRegistry registry)
        {
            if (WellKnownTypes.TryGetId(type, out int id)) return id;
            if (registry.TryGetId(type, out id)) return id;
            return WellKnownTypes.Null;
        }

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
                var s = new NormalizedTypeSchema
                {
                    TypeId = id,
                    StableName = info.StableName,
                    PreviousNames = info.PreviousTypeNames,
                    Kind = info.Kind,
                    EnumValues = info.EnumValues?.Select(ev => (ev.Name, ev.Value)).ToArray(),
                    Fields = info.Fields?.Select(f => (f.StableName, f.TypeName, f.IsMonomorphic)).ToArray(),
                    DefinitionName = info.UnderlyingTypeName,
                    ArgTypeNames = info.ArgTypeNames
                };
                DumpEntry(sb, s);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Dumps the schema from a serialized file or byte array so you can inspect it.
        /// </summary>
        public static string DumpSchema(byte[] data)
        {
            if (data == null || data.Length < HeaderSize)
                return "// Invalid or empty data";

            uint version = data[OffsetFormatVersion];
            if (version != FormatVersion)
                return $"// Format version mismatch. File: {version}, Library: {FormatVersion}";

            ulong schemaFingerprint = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(OffsetFingerprint));
            byte flags = data[OffsetFlags];
            long dataSectionOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetDataSection));
            long stringTableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetStringTable));

            var table = StringTable.Load(data, (int)stringTableOffset, data.Length - (int)stringTableOffset, (StringTableCompression)flags);

            int schemaOffset = HeaderSize;
            var sequence = new System.Buffers.ReadOnlySequence<byte>(data, schemaOffset, (int)dataSectionOffset - schemaOffset);
            var schemaReader = new System.Buffers.SequenceReader<byte>(sequence);
            var fileSchemas = SchemaSection.Read(ref schemaReader, table);

            var sb = new StringBuilder();
            sb.AppendLine($"// File fingerprint: {schemaFingerprint:X16}");
            uint gameVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(OffsetGameVersion));
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

                var s = new NormalizedTypeSchema
                {
                    TypeId = fs.TypeId,
                    StableName = fs.StableName,
                    PreviousNames = fs.PreviousNames,
                    Kind = fs.Kind,
                    EnumValues = fs.EnumValues?.Select(ev => (ev.Name, ev.Value)).ToArray(),
                    Fields = fs.Fields?.Select(f => (f.StableName, Resolve(f.TypeId), f.IsMonomorphic)).ToArray(),
                    DefinitionName = Resolve(fs.DefinitionTypeId),
                    ArgTypeNames = fs.ArgTypeIds?.Select(Resolve).ToArray()
                };
                DumpEntry(sb, s);
            }

            return sb.ToString();
        }
    }
}
