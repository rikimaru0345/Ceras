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
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Pack = 1, Size = 42)]
    internal struct RobustHeader
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public uint Magic;
        [System.Runtime.InteropServices.FieldOffset(4)]
        public byte FormatVersion;
        [System.Runtime.InteropServices.FieldOffset(5)]
        public uint GameVersion;
        [System.Runtime.InteropServices.FieldOffset(13)]
        public ulong Fingerprint;
        [System.Runtime.InteropServices.FieldOffset(21)]
        public byte Flags;
        [System.Runtime.InteropServices.FieldOffset(22)]
        public long DataSectionOffset;
        [System.Runtime.InteropServices.FieldOffset(30)]
        public long StringTableOffset;
        [System.Runtime.InteropServices.FieldOffset(38)]
        public uint Checksum;

        public const int Size = 42;
    }

    public class CerasRobustSerializer
    {
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

            configure?.Invoke(_config);

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
            if (data.Length < RobustHeader.Size) throw new InvalidOperationException("Header too short");

            var registry = _config.SchemaRegistry;

            // Ensure the root type is registered so we can resolve its schema
            registry.TryGetId(typeof(T), out _);

            // Ensure the registry is sealed before we start registering anything (like discovered generic instantiations)
            registry.Seal();

            // Check magic "CERA"
            if (data[0] != (byte)'C' || data[1] != (byte)'E' || data[2] != (byte)'R' || data[3] != (byte)'A')
                throw new InvalidOperationException("Invalid magic");

            uint version = data[4];
            if (version != FormatVersion) throw new InvalidOperationException($"Format version mismatch. File: {version}, Library: {FormatVersion}");

            uint robustVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(5));
            if (robustVersion > currentRobustVersion) throw new InvalidOperationException("Save from newer version");

            ulong schemaFingerprint = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(13));
            byte compressionByte = data[21];
            long dataSectionOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(22));
            long stringTableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(30));

            var compression = (StringTableCompression)compressionByte;
            var table = StringTable.Load(data, (int)stringTableOffset, data.Length - (int)stringTableOffset, compression);
            ulong currentFingerprint = registry.ComputeFingerprint();

            int schemaOffset = RobustHeader.Size;
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
            SerializerBinary.EnsureCapacity(ref buffer, offset, RobustHeader.Size);
            // Clear header space
            buffer.AsSpan(offset, RobustHeader.Size).Clear();

            buffer[offset + 0] = (byte)'C';
            buffer[offset + 1] = (byte)'E';
            buffer[offset + 2] = (byte)'R';
            buffer[offset + 3] = (byte)'A';
            buffer[offset + 4] = (byte)FormatVersion;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 5), robustVersion);
            offset += RobustHeader.Size;
        }

        static void PatchHeader(byte[] buffer, int totalLength, uint robustVersion, long dataSectionOffset, long stringTableOffset, ulong schemaFingerprint, byte flags)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(13), schemaFingerprint);
            buffer[21] = flags;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(22), dataSectionOffset);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(30), stringTableOffset);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(38), 0);
            uint crc = Ceras.Helpers.HashHelpers.CalculateCrc32(buffer.AsSpan(0, totalLength));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(38), crc);
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

            ulong schemaFingerprint = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(13));
            byte flags = data[21];
            long dataSectionOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(22));
            long stringTableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(30));

            var table = StringTable.Load(data, (int)stringTableOffset, data.Length - (int)stringTableOffset, (StringTableCompression)flags);

            int schemaOffset = RobustHeader.Size;
            var sequence = new System.Buffers.ReadOnlySequence<byte>(data, schemaOffset, (int)dataSectionOffset - schemaOffset);
            var schemaReader = new System.Buffers.SequenceReader<byte>(sequence);
            var fileSchemas = SchemaSection.Read(ref schemaReader, table);

            var sb = new StringBuilder();
            sb.AppendLine($"// File fingerprint: {schemaFingerprint:X16}");
            uint gameVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(5));
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
