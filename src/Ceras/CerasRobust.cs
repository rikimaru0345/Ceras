using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Ceras
{
    using Ceras.Format;
    using Ceras.Helpers;

    /// <summary>
    /// Robust serialization for persistent storage (robust formats, databases).
    /// Always includes a schema header for version tolerance and migration.
    /// </summary>
    public static class CerasRobust
    {
        internal const int OffsetMagic = 0;
        internal const int OffsetFormatVersion = 4 + OffsetMagic;
        internal const int OffsetGameVersion = 1 + OffsetFormatVersion;
        internal const int OffsetFingerprint = 4 + OffsetGameVersion;
        internal const int OffsetFlags = 8 + OffsetFingerprint;
        internal const int OffsetDataSection = 1 + OffsetFlags;
        internal const int OffsetStringTable = 8 + OffsetDataSection;
        internal const int OffsetChecksum = 8 + OffsetStringTable;
        public const int HeaderSize = 4 + OffsetChecksum;
        public const int FormatVersion = 1;

        public static void Configure(SerializerConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Advanced.EmbedChecksum = false;
            
            // Core robust formatters
            config.ConfigType<string>().CustomFormatter = new RobustStringFormatter();
            config.ConfigType<Type>().CustomFormatter = new RobustTypeFormatter(config.Robust.SchemaRegistry);

            // Ensure all primitives are handled robustly (non-raw) if they are used
            // in a version-tolerant context.
            // Note: In a real implementation we might want a RobustPrimitiveResolver.
        }

        public static byte[] Save<T>(T rootObject, uint robustVersion, CerasSerializer ceras)
        {
            var registry = ceras.Config.Robust.SchemaRegistry;
            var buffer = new byte[4096];
            int offset = 0;

            WriteHeaderPlaceholder(ref buffer, ref offset, robustVersion);
            int rootTypeId = GetTypeId(typeof(T), registry);
            var typeToId = SchemaSection.Write(ref buffer, ref offset, registry);

            int dataStart = offset;
            
            var internTable = new StringInternTable();
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
            PatchHeader(buffer, offset, robustVersion, dataStart, stringTableStart, registry);
            return buffer[..offset];
        }

        public static T Load<T>(byte[] data, uint currentRobustVersion, CerasSerializer ceras)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < HeaderSize) throw new InvalidOperationException("Header too short");
            
            // Check magic "CERA"
            if (data[0] != (byte)'C' || data[1] != (byte)'E' || data[2] != (byte)'R' || data[3] != (byte)'A')
                throw new InvalidOperationException("Invalid magic");

            var registry = ceras.Config.Robust.SchemaRegistry;
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
            var fileSchemas = SchemaSection.Read(ref schemaReader);

            var idToType = new Dictionary<int, Type>();
            for (int i = 0; i < 128; i++) if (WellKnownTypes.TryGetType(i, out var wkType)) idToType[i] = wkType;
            foreach (var s in fileSchemas.Values) if (registry.TryResolveType(s.StableName, out var type)) idToType[s.TypeId] = type;

            RobustContext.BeginRead(table, idToType);
            try
            {
                var dataSeq = new ReadOnlySequence<byte>(data, (int)dataSectionOffset, (int)stringTableOffset - (int)dataSectionOffset);
                var reader = new SequenceReader<byte>(dataSeq);
                uint rootTypeId = SchemaSection.ReadLeb128(ref reader);

                if (schemaFingerprint != currentFingerprint)
                {
                    var engine = new SchemaTraversalEngine(fileSchemas, table, robustVersion, ceras);
                    return engine.ReadRoot<T>(ref reader);
                }

                T value = default;
                ceras.Deserialize(ref reader, ref value);
                return value;
            }
            finally { RobustContext.End(); }
        }

        static void WriteHeaderPlaceholder(ref byte[] buffer, ref int offset, uint robustVersion)
        {
            SerializerBinary.EnsureCapacity(ref buffer, offset, HeaderSize);
            buffer[offset + 0] = (byte)'C';
            buffer[offset + 1] = (byte)'E';
            buffer[offset + 2] = (byte)'R';
            buffer[offset + 3] = (byte)'A';
            buffer[offset + 4] = (byte)FormatVersion;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 5), robustVersion);
            offset += HeaderSize;
        }

        static void PatchHeader(byte[] buffer, int totalLength, uint robustVersion, long dataSectionOffset, long stringTableOffset, SchemaRegistry registry)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(OffsetFingerprint), registry.ComputeFingerprint());
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
    }
}
