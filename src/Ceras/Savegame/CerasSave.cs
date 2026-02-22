using System;
using System.Buffers.Binary;

namespace Ceras.Savegame
{
    /// <summary>
    /// Public API for the Ceras savegame format.
    ///
    /// Typical usage:
    /// <code>
    /// // One-time setup
    /// var config = new SerializerConfig();
    /// GeneratedFormatters.UseFormatters(config);
    /// CerasSave.ConfigureSavegame(config);
    /// var ceras = new CerasSerializer(config);
    ///
    /// // Save
    /// byte[] data = CerasSave.Save(myPlayer, gameVersion: 1, ceras);
    ///
    /// // Load
    /// var player = CerasSave.Load&lt;Player&gt;(data, currentVersion: 1, ceras);
    /// </code>
    /// </summary>
    public static class CerasSave
    {
        // ── Constants ─────────────────────────────────────────────────────────

        /// <summary>Byte size of the fixed-length file header.</summary>
        public const int HeaderSize = 38; // 4+1+4+8+1+8+8+4

        /// <summary>Current wire-format version. Bumped on breaking changes.</summary>
        public const byte FormatVersion = 1;

        // Header field offsets (internal, used by tests)
        internal const int OffsetMagic            = 0;   // 4 bytes
        internal const int OffsetFormatVersion    = 4;   // 1 byte
        internal const int OffsetGameVersion      = 5;   // 4 bytes
        internal const int OffsetFingerprint      = 9;   // 8 bytes
        internal const int OffsetFlags            = 17;  // 1 byte
        internal const int OffsetDataSection      = 18;  // 8 bytes
        internal const int OffsetStringTable      = 26;  // 8 bytes
        internal const int OffsetChecksum         = 34;  // 4 bytes

        static readonly byte[] MagicBytes = { (byte)'C', (byte)'E', (byte)'R', (byte)'A' };

        // ── Setup ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Configures <paramref name="config"/> for savegame serialization.
        /// Must be called before constructing the <see cref="CerasSerializer"/>.
        ///
        /// Registers <see cref="SavegameStringFormatter"/> so that all
        /// <c>string</c> fields are written as 4-byte string-table indices rather
        /// than inline UTF-8.  Also disables the protocol checksum prefix.
        /// </summary>
        public static void ConfigureSavegame(SerializerConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            config.Advanced.EmbedChecksum = false;
            config.ConfigType<string>().CustomFormatter = new SavegameStringFormatter();
            config.ConfigType<Type>().CustomFormatter   = new SavegameTypeFormatter();
        }

        // ── Save ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Serializes <paramref name="rootObject"/> into the Ceras savegame wire
        /// format and returns the resulting byte array.
        /// </summary>
        /// <typeparam name="T">
        /// The root save type. Must be annotated with
        /// <c>[GenerateFormatter(VersionTolerant = true)]</c> and registered in
        /// <see cref="SavegameSchemaRegistry"/>.
        /// </typeparam>
        /// <param name="rootObject">The object graph to serialize.</param>
        /// <param name="gameVersion">
        /// Monotonically increasing application version number written to the
        /// header. On load, a save whose version exceeds <c>currentGameVersion</c>
        /// is rejected.
        /// </param>
        /// <param name="ceras">
        /// A <see cref="CerasSerializer"/> configured via
        /// <see cref="ConfigureSavegame"/>.
        /// </param>
        public static byte[] Save<T>(T rootObject, uint gameVersion, CerasSerializer ceras)
        {
            if (ceras == null) throw new ArgumentNullException(nameof(ceras));

            var buffer = new byte[4096];
            int offset = 0;

            // 1. Write placeholder header (offsets patched below)
            WriteHeaderPlaceholder(ref buffer, ref offset, gameVersion);
            // offset == HeaderSize

            // 2. Schema section (immediately after header)
            var typeToId = SchemaSection.Write(ref buffer, ref offset);

            // 3. Root type ID + data section
            int dataStart = offset;

            int rootTypeId = GetTypeId(typeof(T));
            SchemaSection.WriteLeb128(ref buffer, ref offset, (uint)rootTypeId);

            var internTable = new StringInternTable();
            SavegameContext.BeginWrite(internTable, typeToId);
            try
            {
                int bytesWritten = ceras.Serialize(rootObject, ref buffer, offset);
                offset += bytesWritten;
            }
            finally
            {
                SavegameContext.End();
            }

            // 4. String table
            int stringTableStart = internTable.Flush(ref buffer, ref offset);

            // 5. Patch header with real offsets + fingerprint + checksum
            PatchHeader(buffer, offset, gameVersion, dataStart, stringTableStart);

            return buffer[..offset];
        }

        // ── Load ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Deserializes a value from a Ceras savegame byte array.
        ///
        /// On schema match (fast path): uses the generated formatters directly,
        /// with zero migration overhead.
        ///
        /// On schema mismatch (slow path): throws
        /// <see cref="NotSupportedException"/> — migration is implemented in
        /// Phase 5.
        /// </summary>
        /// <param name="data">Raw savegame bytes returned by <see cref="Save{T}"/>.</param>
        /// <param name="currentGameVersion">
        /// The current application version. Load is rejected if the file was
        /// written by a newer version.
        /// </param>
        /// <param name="ceras">
        /// A <see cref="CerasSerializer"/> configured via
        /// <see cref="ConfigureSavegame"/>.
        /// </param>
        public static T Load<T>(byte[] data, uint currentGameVersion, CerasSerializer ceras)
        {
            if (data   == null) throw new ArgumentNullException(nameof(data));
            if (ceras  == null) throw new ArgumentNullException(nameof(ceras));
            if (data.Length < HeaderSize)
                throw new InvalidOperationException("Data is too short to contain a valid savegame header.");

            // 1. Validate magic
            if (data[0] != 'C' || data[1] != 'E' || data[2] != 'R' || data[3] != 'A')
                throw new InvalidOperationException("Invalid savegame magic bytes. Expected 'CERA'.");

            // 2. Format version
            byte formatVersion = data[OffsetFormatVersion];
            if (formatVersion != FormatVersion)
                throw new InvalidOperationException(
                    $"Unsupported savegame format version {formatVersion}. Expected {FormatVersion}.");

            // 3. Game version guard
            uint saveGameVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(OffsetGameVersion));
            if (saveGameVersion > currentGameVersion)
                throw new InvalidOperationException(
                    $"Save file was written by game version {saveGameVersion} but the " +
                    $"current version is {currentGameVersion}. Please update the game.");

            // 4. Validate checksum (if present)
            byte flags = data[OffsetFlags];
            if ((flags & 1) != 0)
            {
                uint savedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(OffsetChecksum));
                // Temporarily zero out checksum for calculation
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(OffsetChecksum), 0);
                uint actualCrc = Helpers.HashHelpers.CalculateCrc32(data);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(OffsetChecksum), savedCrc); // restore

                if (savedCrc != actualCrc)
                    throw new InvalidOperationException("Savegame checksum validation failed. The file may be corrupted.");
            }

            // 5. Read offsets from header
            ulong schemaFingerprint  = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(OffsetFingerprint));
            long  dataSectionOffset  = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetDataSection));
            long  stringTableOffset  = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetStringTable));

            // 6. Load string table (needed by both fast and slow paths)
            var table = StringTable.Load(data, (int)stringTableOffset);

            // 6. Fast path vs slow path
            ulong currentFingerprint = SavegameSchemaRegistry.ComputeFingerprint();
            
            // Build IdToType map from schema in file (always needed for SavegameTypeFormatter)
            int schemaOffset = HeaderSize;
            var fileSchemas = SchemaSection.Read(data, ref schemaOffset);
            var idToType = new System.Collections.Generic.Dictionary<int, Type>();
            
            // 1. Well-known types
            for (int i = 0; i < 128; i++)
            {
                // We need a helper to get well-known Type by ID
                Type wkType = GetWellKnownType(i);
                if (wkType != null) idToType[i] = wkType;
            }

            // 2. File types
            foreach (var s in fileSchemas.Values)
            {
                if (SavegameSchemaRegistry.TryResolveType(s.StableName, out var type))
                    idToType[s.TypeId] = type;
            }

            SavegameContext.BeginRead(table, idToType);
            try
            {
                if (schemaFingerprint != currentFingerprint)
                {
                    // Slow path: schema changed — use migration engine
                    int dataOffset = (int)dataSectionOffset;
                    // Skip root type ID
                    SchemaSection.ReadLeb128(data, ref dataOffset);

                    var engine = new SchemaTraversalEngine(fileSchemas, table, saveGameVersion, ceras);
                    return engine.ReadRoot<T>(data, ref dataOffset);
                }

                // Fast path: skip to data section, deserialize
                int offset = (int)dataSectionOffset;

                // Skip root type ID (written by Save; on fast path we don't need it)
                SchemaSection.ReadLeb128(data, ref offset);

                T value = default;
                ceras.Deserialize(ref value, data, ref offset);
                return value;
            }
            finally
            {
                SavegameContext.End();
            }
        }

        // ── Header helpers ────────────────────────────────────────────────────

        static void WriteHeaderPlaceholder(ref byte[] buffer, ref int offset, uint gameVersion)
        {
            EnsureCapacity(ref buffer, offset + HeaderSize);

            // Magic "CERA"
            buffer[offset++] = (byte)'C';
            buffer[offset++] = (byte)'E';
            buffer[offset++] = (byte)'R';
            buffer[offset++] = (byte)'A';

            // Format version
            buffer[offset++] = FormatVersion;

            // Game version
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), gameVersion);
            offset += 4;

            // Fingerprint — zero placeholder (patched in PatchHeader)
            offset += 8;

            // Flags — bit 0: has checksum (always true for now)
            buffer[offset++] = 1;

            // Data section offset — zero placeholder
            offset += 8;

            // String table offset — zero placeholder
            offset += 8;

            // Checksum — zero placeholder
            offset += 4;

            // Assert offset == HeaderSize
        }

        static void PatchHeader(byte[] buffer, int totalLength, uint gameVersion, long dataSectionOffset, long stringTableOffset)
        {
            // Fingerprint
            ulong fp = SavegameSchemaRegistry.ComputeFingerprint();
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(OffsetFingerprint), fp);

            // Data section offset
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(OffsetDataSection), dataSectionOffset);

            // String table offset
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(OffsetStringTable), stringTableOffset);

            // Checksum (calculated with checksum field = 0)
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(OffsetChecksum), 0);
            uint crc = Helpers.HashHelpers.CalculateCrc32(buffer.AsSpan(0, totalLength));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OffsetChecksum), crc);
        }

        // ── Type-ID resolution ────────────────────────────────────────────────

        static int GetTypeId(Type type)
        {
            if (WellKnownTypes.TryGetId(type, out int id))
                return id;
            if (SavegameSchemaRegistry.TryGetId(type, out id))
                return id;
            return WellKnownTypes.Null;
        }

        static Type GetWellKnownType(int id)
        {
            return id switch
            {
                WellKnownTypes.Bool     => typeof(bool),
                WellKnownTypes.Byte     => typeof(byte),
                WellKnownTypes.SByte    => typeof(sbyte),
                WellKnownTypes.Char     => typeof(char),
                WellKnownTypes.Int16    => typeof(short),
                WellKnownTypes.UInt16   => typeof(ushort),
                WellKnownTypes.Int32    => typeof(int),
                WellKnownTypes.UInt32   => typeof(uint),
                WellKnownTypes.Single   => typeof(float),
                WellKnownTypes.Int64    => typeof(long),
                WellKnownTypes.UInt64   => typeof(ulong),
                WellKnownTypes.Double   => typeof(double),
                WellKnownTypes.Decimal  => typeof(decimal),
                WellKnownTypes.String   => typeof(string),
                WellKnownTypes.Object   => typeof(object),
                WellKnownTypes.DateTime => typeof(DateTime),
                WellKnownTypes.TimeSpan => typeof(TimeSpan),
                WellKnownTypes.Guid     => typeof(Guid),
                _                       => null,
            };
        }

        // ── Buffer growth ─────────────────────────────────────────────────────

        static void EnsureCapacity(ref byte[] buffer, int required)
        {
            if (required <= buffer.Length) return;
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, required + 64));
        }
    }
}
