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
        internal const int OffsetFingerprint = 8 + OffsetGameVersion;
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
            if (ceras == null) throw new ArgumentNullException(nameof(ceras));
            
            var registry = ceras.Config.Robust.SchemaRegistry;
            var internTable = new StringInternTable();

			// --- DISCOVERY PASS ---
			// We need to find all types that will be used during serialization.
			// The registry might only contain types explicitly registered or discovered via [CerasSerializable].
			// But the object graph might contain types only reachable via polymorphic fields (object, interfaces).
			// So we do a quick serialization pass to a null writer to trigger registration.
			var oldRobustEnabled = ceras.Config.Robust.Enabled;
			if (!oldRobustEnabled)
				ceras.Config.Robust.Enabled = true; // Enabled so we use the right formatters
            RobustContext.BeginWrite(internTable, new Dictionary<Type, int>());
			try {
				ceras.Serialize(new NullWriter(), rootObject);
			} finally {
				RobustContext.End();
				if (!oldRobustEnabled && !ceras.Config.Robust.Enabled) {
					// We only need to try to set it to false if it's currently false,
					// but wait, if it was false and we set it to true, we want to set it back to false.
					// But if it's locked, we can't.
				}
				
				// Correct logic: Only try to restore 'false' if it was false AND it's not locked.
				if (!oldRobustEnabled) {
					try {
						ceras.Config.Robust.Enabled = false;
					} catch (InvalidOperationException) {
						// Already locked, can't restore to false. That's fine.
					}
				}
			}

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
            var oldRobustEnabled2 = ceras.Config.Robust.Enabled;
            if (!oldRobustEnabled2)
                ceras.Config.Robust.Enabled = true;

            if (Ceras.Helpers.ObjectCache.DebugTraceEnabled)
                Ceras.Helpers.ObjectCache.BeginSerializeTrace();

            try
            {
                var writer = new ArrayBufferWriter<byte>();
                SchemaSection.WriteLeb128(writer, (uint)rootTypeId);
                ceras.Serialize(writer, rootObject);

                var data = writer.WrittenSpan;
                SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
                data.CopyTo(buffer.AsSpan(offset));
                offset += data.Length;
            }
            finally
            {
                RobustContext.End();
                if (!oldRobustEnabled2)
                {
                    try { ceras.Config.Robust.Enabled = false; }
                    catch (InvalidOperationException) { /* locked */ }
                }
            }

            int stringTableStart = internTable.Flush(ref buffer, ref offset);
            PatchHeader(buffer, offset, robustVersion, dataStart, stringTableStart, schemaFingerprint);
            return buffer[..offset];
        }

        public static T Load<T>(byte[] data, uint currentRobustVersion, CerasSerializer ceras) {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < HeaderSize) throw new InvalidOperationException("Header too short");

            var registry = ceras.Config.Robust.SchemaRegistry;

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

            var typeBinder = ceras.Config.Advanced.TypeBinder;
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
            var oldRobustEnabled = ceras.Config.Robust.Enabled;
            if (!oldRobustEnabled)
                ceras.Config.Robust.Enabled = true;
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
            finally {
                if (!oldRobustEnabled)
                {
                    try { ceras.Config.Robust.Enabled = false; }
                    catch (InvalidOperationException) { /* locked */ }
                }
                RobustContext.End();
            }
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

		struct NormalizedTypeSchema {
			public int TypeId;
			public string StableName;
			public string[] PreviousNames;
			public SchemaTypeKind Kind;
			public (string Name, long Value)[] EnumValues;
			public (string StableName, string TypeName, bool IsMono)[] Fields;
			public string DefinitionName;
			public string[] ArgTypeNames;
		}

		static void DumpEntry(StringBuilder sb, NormalizedTypeSchema s) {
			switch (s.Kind) {
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
				var s = new NormalizedTypeSchema {
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
		public static string DumpSchema(byte[] data) {
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

			var idToName = new Dictionary<int, string>();
			for (int i = 0; i < 128; i++) if (WellKnownTypes.TryGetType(i, out var wkType)) idToName[i] = wkType.FullName ?? wkType.Name;
			foreach (var s in fileSchemas.Values) idToName[s.TypeId] = s.StableName;

			string Resolve(int id) => idToName.TryGetValue(id, out var name) ? name : $"ID:{id}";

			foreach (var kv in fileSchemas.OrderBy(x => x.Key, StringComparer.Ordinal)) {
				var fs = kv.Value;
				if (kv.Key != fs.StableName) continue; // skip alias entries

				var s = new NormalizedTypeSchema {
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

		class NullWriter : IBufferWriter<byte> {
			byte[] _buffer = new byte[4096];
			public void Advance(int count) { }
			public Memory<byte> GetMemory(int sizeHint = 0) => _buffer;
			public Span<byte> GetSpan(int sizeHint = 0) => _buffer;
		}
    }
}
