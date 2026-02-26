using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ceras {
    using Ceras.Format;
    using Ceras.Helpers;

    /// <summary>
    /// Robust serialization for persistent storage (robust formats, databases).
    /// Always includes a schema header for version tolerance and migration.
    /// </summary>
    public static class CerasRobust {
        internal const int OffsetMagic = 0;
        internal const int OffsetFormatVersion = 4 + OffsetMagic;
        internal const int OffsetGameVersion = 1 + OffsetFormatVersion;
        internal const int OffsetFingerprint = 4 + OffsetGameVersion;
        internal const int OffsetFlags = 8 + OffsetFingerprint;
        internal const int OffsetDataSection = 1 + OffsetFlags;
        internal const int OffsetStringTable = 8 + OffsetDataSection;
        internal const int OffsetChecksum = 8 + OffsetStringTable;
        public const int HeaderSize = 4 + OffsetChecksum;
        public const int FormatVersion = 2;

        public static void Configure(SerializerConfig config) {
            if (config == null) throw new ArgumentNullException(nameof(config));

            config.Advanced.EmbedChecksum = false;
            config.Advanced.AllowReflectionFallback = false;

            // Core robust formatters
            config.ConfigType<string>().CustomFormatter = new RobustStringFormatter();
            config.ConfigType<Type>().CustomFormatter = new RobustTypeFormatter(config.Robust.SchemaRegistry);

            // todo: maybe put a RobustPrimitiveResolver here?
        }

        public static byte[] Save<T>(T rootObject, uint robustVersion, CerasSerializer ceras) {
            var registry = ceras.Config.Robust.SchemaRegistry;
            var buffer = new byte[4096];
            int offset = 0;

            WriteHeaderPlaceholder(ref buffer, ref offset, robustVersion);

			// Ensure the registry is sealed so that discovered generic instantiations
			// don't affect the fingerprint.
			registry.Seal();

			int rootTypeId = GetTypeId(typeof(T), registry);

			// Snapshot the fingerprint BEFORE SchemaSection.Write — Write calls ResolveId which
			// may call TryGetId for field-referenced types, triggering EnsureRegisteredRecursive
			// and registering new generic instantiations into the registry.  The load path also
			// computes the fingerprint before any such resolution, so both sides must use the
			// same pre-Write baseline (the state after UseFormatters).
			ulong schemaFingerprint = registry.ComputeFingerprint();

            var internTable = new StringInternTable();
            var typeToId = SchemaSection.Write(ref buffer, ref offset, registry, internTable);

            int dataStart = offset;

            RobustContext.BeginWrite(internTable, typeToId);

            var writer = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(writer, (uint)rootTypeId);
            try { ceras.Serialize(writer, rootObject); }
            finally { RobustContext.End(); }

            var data = writer.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;

            int stringTableStart = internTable.Flush(ref buffer, ref offset);
            PatchHeader(buffer, offset, robustVersion, dataStart, stringTableStart, schemaFingerprint);
            return buffer[..offset];
        }

        public static T Load<T>(byte[] data, uint currentRobustVersion, CerasSerializer ceras) {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < HeaderSize) throw new InvalidOperationException("Header too short");

            var registry = ceras.Config.Robust.SchemaRegistry;

			// Ensure the registry is sealed before we start registering anything (like root type)
			registry.Seal();

			// Ensure the root type is registered so we can resolve its schema
			registry.TryGetId(typeof(T), out _);

			// Check magic "CERA"
			if (data[0] != (byte)'C' || data[1] != (byte)'E' || data[2] != (byte)'R' || data[3] != (byte)'A')
                throw new InvalidOperationException("Invalid magic");

            uint version = data[OffsetFormatVersion];
            if (version != FormatVersion) throw new InvalidOperationException($"Format version mismatch. File: {version}, Library: {FormatVersion}");

            uint robustVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(OffsetGameVersion));
            if (robustVersion > currentRobustVersion) throw new InvalidOperationException("Save from newer version");

            ulong schemaFingerprint = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(OffsetFingerprint));
            long dataSectionOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetDataSection));
            long stringTableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetStringTable));

            var table = StringTable.Load(data, (int)stringTableOffset);
            ulong currentFingerprint = registry.ComputeFingerprint();

            int schemaOffset = HeaderSize;
            var sequence = new ReadOnlySequence<byte>(data, schemaOffset, (int)dataSectionOffset - schemaOffset);
            var schemaReader = new SequenceReader<byte>(sequence);
            var fileSchemas = SchemaSection.Read(ref schemaReader, table);

            var idToType = new Dictionary<int, Type>();
            for (int i = 0; i < 128; i++) if (WellKnownTypes.TryGetType(i, out var wkType)) idToType[i] = wkType;
            foreach (var s in fileSchemas.Values) {
                try {
                    if (registry.TryResolveType(s.StableName, out var type)) {
                        if (type != null) idToType[s.TypeId] = type;
                    }
                }
                catch (Exception e) {
                    throw new Exception($"Error resolving type with stable name '{s.StableName}': {e.Message}", e);
                }
            }

            RobustContext.BeginRead(table, idToType);
            try {
                var dataSeq = new ReadOnlySequence<byte>(data, (int)dataSectionOffset, (int)stringTableOffset - (int)dataSectionOffset);
                var reader = new SequenceReader<byte>(dataSeq);
                uint rootTypeId = SchemaSection.ReadLeb128(ref reader);

                if (schemaFingerprint != currentFingerprint) {
                    var engine = new SchemaTraversalEngine(fileSchemas, table, robustVersion, ceras);
                    return engine.ReadRoot<T>(ref reader);
                }

                T value = default;
                ceras.Deserialize(ref reader, ref value);
                return value;
            }
            finally { RobustContext.End(); }
        }

        static void WriteHeaderPlaceholder(ref byte[] buffer, ref int offset, uint robustVersion) {
            SerializerBinary.EnsureCapacity(ref buffer, offset, HeaderSize);
            buffer[offset + 0] = (byte)'C';
            buffer[offset + 1] = (byte)'E';
            buffer[offset + 2] = (byte)'R';
            buffer[offset + 3] = (byte)'A';
            buffer[offset + 4] = (byte)FormatVersion;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 5), robustVersion);
            offset += HeaderSize;
        }

        static void PatchHeader(byte[] buffer, int totalLength, uint robustVersion, long dataSectionOffset, long stringTableOffset, ulong schemaFingerprint) {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(OffsetFingerprint), schemaFingerprint);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(OffsetDataSection), dataSectionOffset);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(OffsetStringTable), stringTableOffset);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(OffsetChecksum), 0);
            uint crc = Ceras.Helpers.HashHelpers.CalculateCrc32(buffer.AsSpan(0, totalLength));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OffsetChecksum), crc);
        }

        static int GetTypeId(Type type, SchemaRegistry registry) {
            if (WellKnownTypes.TryGetId(type, out int id)) return id;
            if (registry.TryGetId(type, out id)) return id;
            return WellKnownTypes.Null;
        }

        /// <summary>
        /// Dumps the current registry schema to a string so you can inspect it.
        /// </summary>
        public static string DumpSchema(SchemaRegistry registry)
        {
            var sb = new StringBuilder();
            ulong currentFingerprint = registry.ComputeFingerprint();
            sb.AppendLine($"// Current fingerprint: {currentFingerprint:X16}");
			sb.AppendLine($"// Sealed: {registry.IsSealed}");

			var allEntries = registry.All().OrderBy(e => e.info.StableName, StringComparer.Ordinal).ToList();
			sb.AppendLine($"// Type count: {allEntries.Count}");
            sb.AppendLine();

            foreach (var (type, id, info) in allEntries)
            {
                switch (info.Kind)
                {
                    case SchemaTypeKind.Enum:
                        sb.AppendLine($"ENUM  [{id}]  {info.StableName}");
                        if (info.EnumValues != null)
                            foreach (var ev in info.EnumValues)
                                sb.AppendLine($"      {ev.Name} = {ev.Value}");
                        break;

                    case SchemaTypeKind.GenericInstantiation:
                        sb.AppendLine($"GENERIC  [{id}]  {info.StableName}  def={info.UnderlyingTypeName}  args=[{string.Join(", ", info.ArgTypeNames ?? Array.Empty<string>())}]");
                        break;

                    default:
                        sb.AppendLine($"{info.Kind.ToString().ToUpperInvariant()}  [{id}]  {info.StableName}");
                        if (info.Fields != null)
                            foreach (var f in info.Fields)
								sb.AppendLine($"      [{f.TypeName}]  {(f.IsMonomorphic ? "Mono" : "Poly")}  {f.StableName}");
						break;
				}

                if (info.PreviousTypeNames?.Length > 0)
                    sb.AppendLine($"      prev: {string.Join(", ", info.PreviousTypeNames)}");
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
			long dataSectionOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetDataSection));
                    long stringTableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetStringTable));

			var table = StringTable.Load(data, (int)stringTableOffset);

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
                    foreach (var kv in fileSchemas.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var s = kv.Value;
                if (kv.Key != s.StableName) continue; // skip alias entries

                switch (s.Kind)
                {
                    case SchemaTypeKind.Enum:
                        sb.AppendLine($"ENUM  [{s.TypeId}]  {s.StableName}");
                        foreach (var ev in s.EnumValues)
                            sb.AppendLine($"      {ev.Name} = {ev.Value}");
                        break;

                    case SchemaTypeKind.GenericInstantiation:
                        sb.AppendLine($"GENERIC  [{s.TypeId}]  {s.StableName}  def={s.DefinitionTypeId}  args=[{string.Join(", ", s.ArgTypeIds)}]");
                        break;

                    default:
                        sb.AppendLine($"{s.Kind.ToString().ToUpperInvariant()}  [{s.TypeId}]  {s.StableName}");
                        foreach (var f in s.Fields)
							sb.AppendLine($"      [{f.TypeId}]  {(f.IsMonomorphic ? "Mono" : "Poly")}  {f.StableName}");
						break;
                }

                if (s.PreviousNames.Length > 0)
                    sb.AppendLine($"      prev: {string.Join(", ", s.PreviousNames)}");
            }

            return sb.ToString();
        }
    }
}
