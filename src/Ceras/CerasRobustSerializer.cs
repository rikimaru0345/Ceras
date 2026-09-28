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
    /// Explicit codec for the robust file header, specified in Format/LAYOUT.md. All multi-byte
    /// fields are little-endian. Layout (Size = 56):
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

        /// <summary>
        /// Validates the header's section offsets before any of them is used to index or allocate
        /// (TODO 0.6 item 8). The only legal envelope is:
        /// <c>Size &lt;= schemaStringOffset &lt;= dataSectionOffset &lt;= stringTableOffset &lt;= data.Length</c>.
        /// A negative, overflowing, out-of-order or out-of-range offset is corruption, not a decoder
        /// defect, and must be rejected with a clear error. (The schema string region may be empty, so
        /// <c>schemaStringOffset == dataSectionOffset</c> is allowed; the payload region may be empty too.)
        /// </summary>
        public static void Validate(byte[] data)
        {
            if (data.Length < Size)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"The robust header is truncated: expected at least {Size} bytes, got {data.Length}.");

            long schemaStringOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(SchemaStringOffsetOffset));
            long dataSectionOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(DataSectionOffsetOffset));
            long stringTableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(StringTableOffsetOffset));

            if (schemaStringOffset < Size)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt robust header: schema-string-table offset {schemaStringOffset} is before the end of the header ({Size}).");

            if (dataSectionOffset < schemaStringOffset)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt robust header: data-section offset {dataSectionOffset} is before the schema-string-table offset {schemaStringOffset}.");

            if (stringTableOffset < dataSectionOffset)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt robust header: payload-string-table offset {stringTableOffset} is before the data-section offset {dataSectionOffset}.");

            if (stringTableOffset > data.Length)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt robust header: payload-string-table offset {stringTableOffset} is beyond the end of the data ({data.Length} bytes).");
        }

        /// <summary>Rejects the header unless it looks like a Ceras robust block (magic + format version).</summary>
        public static void ValidateMagicAndVersion(byte[] data, uint expectedFormatVersion)
        {
            if (data.Length < Size)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"The robust header is truncated: expected at least {Size} bytes, got {data.Length}.");

            if (data[0] != (byte)'C' || data[1] != (byte)'E' || data[2] != (byte)'R' || data[3] != (byte)'A')
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    "Invalid magic. The provided data does not appear to be a Ceras-serialized block.");

            uint version = data[FormatVersionOffset];
            if (version == expectedFormatVersion)
                return;

            // An older version is a *known* file that this build deliberately no longer reads, so it gets
            // the reason rather than a bare mismatch: the experimental format carried no upgrade promise,
            // and v3 in particular could hold a user custom formatter's opaque bytes under a schema (and a
            // matching layout fingerprint) that described generated fields, which is exactly the state
            // that cannot be told apart from a good file.
            if (version < expectedFormatVersion)
                throw new Ceras.Exceptions.CerasVersionMismatchException(
                    $"Unsupported robust format version {version}: this build reads version {expectedFormatVersion} only. " +
                    $"Format version {version} is an older experimental version and is no longer accepted; experimental saves are not " +
                    "upgradeable and must be recreated. Version 3 is rejected in particular because it could be written with a user " +
                    "custom formatter, whose opaque bytes were invisible to both the file's schema section and the layout fingerprint, " +
                    "so such a file cannot be validated. See RobustCustomFormatterPolicy.",
                    version, expectedFormatVersion);

            throw new Ceras.Exceptions.CerasVersionMismatchException(
                $"Format version mismatch. File version {version} is not compatible with library version {expectedFormatVersion}.",
                version, expectedFormatVersion);
        }

        /// <summary>Returns true when <paramref name="flags"/> is a known string-table compression flag.</summary>
        public static bool IsValidCompressionFlag(byte flags)
            => flags == (byte)StringTableCompression.Disabled || flags == (byte)StringTableCompression.Deflate;
    }

    public sealed class CerasRobustSerializer : CerasSerializer
    {
        // v4: a robust context no longer accepts a user custom formatter (see
        // RobustCustomFormatterPolicy). v3 could be written with one — the schema section still
        // described the *generated* fields while the payload held opaque custom bytes, and the layout
        // fingerprint matched anyway, so such a file could not be distinguished from a good one. The
        // experimental format therefore advances rather than keeping a false promise: every v3 file is
        // rejected, including the ones that happen to be fine.
        public const int FormatVersion = 4;

        internal readonly CerasRobustConfig _config;

        public new CerasRobustConfig Config => _config;

        public CerasRobustSerializer(Action<CerasRobustConfig>? configure = null) : base(MakeConfig(configure, Assembly.GetCallingAssembly()))
        {
            _config = (CerasRobustConfig)base.Config;
        }

        internal CerasRobustSerializer(CerasRobustConfig config) : base(config)
        {
            _config = config;
            // The owning assembly has to be captured here, in the constructor itself:
            // Assembly.GetCallingAssembly() reports the assembly of the method that called the *current*
            // method, and the runtime skips a constructor's own frame, so evaluating it inside the
            // constructor yields the assembly that constructed the serializer. Moving it into a helper
            // (even a [MethodImpl(NoInlining)] one) would report Ceras' own assembly instead, so it stays
            // here on purpose. Both constructors hand it to the same place, so no construction path can
            // silently end up in the unknown-build mode.
            _config.SetOwningAssembly(Assembly.GetCallingAssembly());
        }

        static CerasRobustConfig MakeConfig(Action<CerasRobustConfig> configure, Assembly owningAssembly)
        {
            var config = new CerasRobustConfig();
            config.SetOwningAssembly(owningAssembly);
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

            // Re-run the context check: a type config is sealed lazily, so a user custom formatter can
            // still be installed after the serializer was constructed (the setup-time check in
            // CerasRobustConfig.Seal only sees what was configured before it). Nothing with opaque bytes
            // may enter a robust file.
            RobustCustomFormatterPolicy.VerifyNoUserCustomFormatters(_config);

            // Declare the root (normally via the generated manifest) and seal. A context has one
            // finite manifest: a root shape no manifest can describe (an array, a built-in closed
            // collection, a tuple, an enum) has to be declared up front with DeclareRoot /
            // [CerasKnownType], and this throws if it was not — nothing is added on first use.
            // Seal() folds the pending declarations in and bakes; it is a no-op once sealed, and a
            // later Register throws rather than re-baking. See Format/LAYOUT.md, "Envelope".
            EnsureRootDeclared(registry, typeof(T));
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

            // Scoped: a nested operation (a callback that saves) gets its own context and restores this
            // one on the way out, instead of overwriting it.
            using var writeScope = RobustContext.BeginWrite(payloadStrings, typeToId);

            if (Ceras.Helpers.ObjectCache.DebugTraceEnabled)
                Ceras.Helpers.ObjectCache.BeginSerializeTrace();

            // The payload is written straight into `buffer` rather than into a scratch writer that
            // is copied in afterwards. Both produce the same bytes in the same order, but the scratch
            // writer has to grow to the payload's size first, and it grows by doubling from 256
            // bytes, so a multi-kilobyte payload costs that whole ladder plus a full memcpy of the
            // payload, all of it discarded as soon as the copy lands. `FixedArrayBufferWriter` is
            // the same contract the scratch writer satisfied (an IBufferWriter<byte> that grows in
            // place through the shared EnsureCapacity/ExpandBuffer path), so nothing below it
            // changes. `offset` is the fill level of `buffer` and is exactly where the data section
            // starts (dataStart above), so the writer continues the file rather than starting a
            // new one.
            var writer = new FixedArrayBufferWriter(buffer, offset);
            SchemaSection.WriteLeb128(writer, (uint)rootTypeId);
            Serialize(writer, rootObject);

            // Growth replaces the array, so the local has to take it back before the string table
            // and the header are written into it.
            buffer = writer.Buffer;
            offset = writer.Offset;

            int stringTableStart = payloadStrings.Flush(ref buffer, ref offset, _config.StringTableCompression);
            RobustHeader.FinalizeHeader(buffer, offset, (byte)_config.StringTableCompression, schemaStringOffset, dataStart, stringTableStart, schemaFingerprint);
            return buffer[..offset];
        }

        public T Load<T>(byte[] data, uint currentRobustVersion)
        {
            T value = default!;
            Load(ref value, data, currentRobustVersion);
            return value;
        }

        public void Load<T>(ref T value, byte[] data, uint currentRobustVersion)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            var registry = _config.SchemaRegistry;

            // Same gate as Save: a formatter installed after construction must not be able to decode
            // this file either (see RobustCustomFormatterPolicy).
            RobustCustomFormatterPolicy.VerifyNoUserCustomFormatters(_config);

            // Validate magic/version and the full section-offset envelope before any offset is used to
            // index or allocate (TODO 0.6 item 8).
            RobustHeader.ValidateMagicAndVersion(data, FormatVersion);
            RobustHeader.Validate(data);

            uint robustVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(RobustHeader.GameVersionOffset));
            if (robustVersion > currentRobustVersion)
                throw new Ceras.Exceptions.CerasRobustVersionException($"Attempted to load data from a newer application version ({robustVersion}) than the current version ({currentRobustVersion}).", robustVersion, currentRobustVersion);

            // Integrity first. The CRC covers the whole block, so it has to be verified before any field it
            // protects is interpreted: a corrupted build-timestamp byte must be reported as corruption
            // rather than as "produced by a newer build", and a corrupted flags byte as corruption rather
            // than as "unknown compression flag".
            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(RobustHeader.ChecksumOffset));
            uint computedCrc = Ceras.Helpers.HashHelpers.CalculateCrc32WithZeroedRange(
                data, RobustHeader.ChecksumOffset, RobustHeader.ChecksumSize);
            if (storedCrc != computedCrc)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"CRC mismatch: the saved data is corrupted or truncated (stored 0x{storedCrc:X8}, computed 0x{computedCrc:X8}). " +
                    "The header fields cannot be trusted until the checksum matches.");

            RobustHeader.Read(data, out long buildTimestamp, out byte compressionByte,
                out long schemaStringOffsetL, out long dataSectionOffset, out long stringTableOffset, out ulong schemaFingerprint);

            if (!RobustHeader.IsValidCompressionFlag(compressionByte))
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt robust header: unknown string-table compression flag {compressionByte}.");

            // Reject saves from a newer build before decoding the payload, even when layouts match.
            // The check is active only in a known-build configuration; an explicitly unknown build
            // timestamp records 0 and cannot detect a newer save.
            if (_config.BuildTimestampKnown && buildTimestamp > _config.BuildTimestamp)
                throw new Ceras.Exceptions.CerasException(
                    $"The save was produced by a newer build (timestamp {buildTimestamp}) than the current build " +
                    $"({_config.BuildTimestamp}). Update the application before loading this save.");

            int schemaStringOffset = (int)schemaStringOffsetL;

            var compression = (StringTableCompression)compressionByte;

            // Both regions are bounded by the configured limits *before* they are decoded: an entry
            // length cannot pick an allocation, and a compressed region cannot expand without limit.
            var payloadTable = StringTable.Load(data, (int)stringTableOffset, data.Length - (int)stringTableOffset,
                compression, _config.SizeLimits.MaxStringLength, _config.SizeLimits.MaxStringTableSize);

            // Pin the declared set: the manifest is finite, so this can only verify. Seal() also bakes the
            // manifest, which the fast path's baked id map and BuildIdToType's guard both rely on.
            EnsureRootDeclared(registry, typeof(T));
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
                ValidateStoredRootType(storedRootId, typeof(T), idToType, registry, fingerprintMatched: true);

                using (RobustContext.BeginRead(payloadTable, idToType, _config.SizeLimits.MaxStringLength))
                {
                    Deserialize(ref reader, ref value);
                }

                // The same detector the interpreter gets, and it stays a detector here: the reader is
                // already bounded to the declared data section, so knowing what it should have consumed
                // needs no schema parse, no string-table decode and no allocation. (The generated path
                // flushes its deferred [OnAfterDeserialize] callbacks inside Deserialize, so unlike the
                // interpreter below they have already run by the time this can fire; that is inherent to
                // reusing Deserialize rather than re-implementing it, and it only matters for a load that
                // is about to be rejected anyway.)
                VerifyDataSectionConsumedExactly(ref reader, dataSectionOffset, stringTableOffset,
                    fingerprintMatched: true, typeof(T));
                return;
            }

            // Slow path: parse the schema region (schema bytes + its own string table) and interpret.
            if (Ceras.Helpers.ObjectCache.DebugTraceEnabled) Console.WriteLine($"[CerasRobust] Fingerprint mismatch! File: {schemaFingerprint:X16}, Current: {currentFingerprint:X16}. Using SchemaTraversalEngine (slow path).");

            var schemaStrings = StringTable.Load(data, (int)schemaStringOffset,
                (int)dataSectionOffset - (int)schemaStringOffset, StringTableCompression.Disabled,
                _config.SizeLimits.MaxStringLength, _config.SizeLimits.MaxStringTableSize);

            int schemaLength = (int)schemaStringOffset - RobustHeader.Size;
            var schemaSeq = new ReadOnlySequence<byte>(data, RobustHeader.Size, schemaLength);
            var schemaReader = new SequenceReader<byte>(schemaSeq);
            var fileSchemas = SchemaSection.Read(ref schemaReader, schemaStrings);

            var slowIdToType = BuildIdToType(fileSchemas, registry, Config.TypeBinder);

            // Validate the file's schema against the current model *before* any payload byte is
            // interpreted - not even the root type id has been read yet. Reading the section is where the
            // file's own description is first checked for self-consistency (SchemaSection.Read, via
            // StructuralShape: a collection/array/tuple entry that carries no readable definition and
            // arguments describes no payload at all), and building the old-to-current plans is what refuses
            // a member the two sides write incompatibly (a field whose declared type changed in a way
            // Ceras cannot convert, a member whose reference marker moved because it changed between a
            // concrete array and a polymorphic slot) and an entry whose identities the file does not
            // describe. Those are facts about the schema alone, so deciding them needs no payload; asking
            // after the root type id would only mean that a load which cannot succeed had already started
            // interpreting one, and could already have fired an [OnBeforeDeserialize] callback against a
            // half-read graph.
            var slowPlans = MigrationPlanBuilder.BuildAll(fileSchemas, registry);

            int storedSlowRootId = (int)SchemaSection.ReadLeb128(ref reader); // root type id
            ValidateStoredRootType(storedSlowRootId, typeof(T), slowIdToType, registry, fingerprintMatched: false);

            try
            {
                using (RobustContext.BeginRead(payloadTable, slowIdToType, _config.SizeLimits.MaxStringLength))
                {
                    var engine = new SchemaTraversalEngine(fileSchemas, payloadTable, robustVersion, this, slowPlans);
                    InstanceData.DeserializeCallbacks = null!;
                    value = engine.ReadRoot<T>(ref reader, value);

                    // Before the deferred callbacks, and that ordering is the point: a load that did not
                    // read the whole declared data section is not a successful load, and [OnAfterDeserialize]
                    // would otherwise run on a graph the file only half described.
                    VerifyDataSectionConsumedExactly(ref reader, dataSectionOffset, stringTableOffset,
                        fingerprintMatched: false, typeof(T));

                    // Deferred [OnAfterDeserialize] callbacks run once the whole graph is populated.
                    FlushDeserializeCallbacks();
                }
            }
            finally
            {
                // A failed or abandoned load must not leave callbacks queued for the next one.
                InstanceData.DeserializeCallbacks = null!;
            }
        }

        /// <summary>
        /// The declared data section must be consumed **exactly** (TODO P0, "Detect trailing migration
        /// payload bytes"). By construction it holds nothing but the declared root type id followed by
        /// the root's payload: <see cref="Save"/> starts the payload string table at the very byte after
        /// the last payload byte and never pads the region, so a reader that stopped earlier left part of
        /// the save unread, and the object graph it returns is only as trustworthy as the part it did read.
        /// A schema mismatch used to reach this point silently - the interpreter skips a field it cannot
        /// map, skips a payload its grammar does not describe, and returns - so the check turns one whole
        /// class of "your save loaded, but half of it is missing" into a typed error naming the offsets.
        /// <para>
        /// <b>What this is not.</b> It is a detector, not evidence that the format or the schema was
        /// truthful. A desync that happens to land exactly on the end of the data section passes here and
        /// still decodes wrong values, and a deliberately lying <c>SchemaRegistry.Register</c>ed schema
        /// matches its own fingerprint just the same (see <c>Format/LAYOUT.md</c>). It cannot stand in for
        /// truthful custom-payload metadata either: it only says the reader covered the whole declared
        /// region, which is a necessary - not sufficient - condition for a correct load.
        /// </para>
        /// <para>
        /// <b>Why the exception type depends on the path.</b> The interpreter ran because the stored
        /// fingerprint did not match this context's, so the file's own schema was mapped onto the current
        /// model and the two disagreeing is a schema/layout problem the caller can act on
        /// (<see cref="Ceras.Exceptions.CerasSchemaException"/>). The generated reader ran because the
        /// fingerprint <em>did</em> match, so the file declares this build's own layout and no layout
        /// disagreement can explain a leftover: what is left are bytes that are not a Ceras save of this
        /// layout (<see cref="Ceras.Exceptions.CerasDataCorruptionException"/>), whether from a
        /// hand-edited block, a foreign block, or a decoder defect.
        /// </para>
        /// <para>
        /// The happy path is a single integer compare with no allocation, and the reader is already
        /// bounded to the declared slice, so this costs the same O(1) on the generated path (which
        /// deliberately parses no schema) as on the interpreter path.
        /// </para>
        /// </summary>
        static void VerifyDataSectionConsumedExactly(ref SequenceReader<byte> reader, long dataSectionOffset,
            long stringTableOffset, bool fingerprintMatched, Type rootType)
        {
            // Taken from the header rather than from reader.Sequence.Length (which holds the same value):
            // the report has to talk about the region the file *declares*, because that declaration is
            // what a writer and a reader disagree about.
            long declaredLength = stringTableOffset - dataSectionOffset;
            long consumed = reader.Consumed;
            if (consumed == declaredLength)
                return;

            // The reader is constructed from exactly this slice, so it cannot have consumed more than the
            // region holds; the leftover count is therefore a real "bytes no reader ever looked at".
            long unread = declaredLength - consumed;
            long stoppedAt = dataSectionOffset + consumed;

            string readerDescription = fingerprintMatched
                ? "the generated (matching-layout) reader"
                : "the migration interpreter, running because the layout fingerprint did not match";
            string because = fingerprintMatched
                ? "a matching fingerprint means the file declares this build's own layout, so no layout disagreement can explain the leftover"
                : "the file's own schema section was mapped onto this build's model, and the two do not describe the same payload";

            string message =
                $"The save's declared data section is [{dataSectionOffset}, {stringTableOffset}) " +
                $"({declaredLength} bytes), but reading the root '{TypeIdentity.GetCanonicalNameOrUnknown(rootType)}' consumed only " +
                $"{consumed} of them and stopped at file offset {stoppedAt}: {unread} byte(s) of the data section were never read. " +
                $"The reader was {readerDescription}, and {because}. A robust save holds nothing after the root's payload " +
                "(see Format/LAYOUT.md, \"Envelope\"), so a leftover means this build and the file do not agree on the layout of the " +
                "data section - typically a member the file wrote that this build does not describe, a member whose type changed shape, " +
                "or a payload written by a formatter no schema accounts for. Load the save with the build that wrote it, or start the save " +
                "again. Note this is a consumption check only: it says the reader covered the whole declared region, not that the schema " +
                "described those bytes correctly, and it cannot detect a mismatch that happens to land exactly on the end of the region.";

            if (fingerprintMatched)
                throw new Ceras.Exceptions.CerasDataCorruptionException(message);

            throw new Ceras.Exceptions.CerasSchemaException(message);
        }

        /// <summary>
        /// Resolves every type referenced by a file's schema section to a CLR <see cref="Type"/>
        /// (used only by the migration/interpreter slow path).
        /// <para>
        /// Requires a baked manifest: a shape that is only reachable as a member is registered by the
        /// bake, and its <c>[PreviousTypeName]</c> alias with it, so ids resolved before the bake silently
        /// miss a renamed member-only type. The registry enforces that here rather than leaving it to the
        /// caller's ordering.
        /// </para>
        /// </summary>
        internal static Dictionary<int, Type> BuildIdToType(Dictionary<string, OldTypeSchema> fileSchemas, SchemaRegistry registry, ITypeBinder typeBinder)
        {
            registry.RequireBakedManifest();

            var idToType = new Dictionary<int, Type>();
            for (int i = 0; i < 128; i++) if (WellKnownTypes.TryGetType(i, out var wkType)) idToType[i] = wkType;

            var schemasById = new Dictionary<int, OldTypeSchema>();
            foreach (var s in fileSchemas.Values) schemasById.TryAdd(s.TypeId, s);

            Type? ResolveById(int id)
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
                            argTypes[i] = ResolveById(s.ArgTypeIds[i])!;
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

                // A type this build does not have stays unmapped: an old file may describe a type that has
                // since been removed, and the engine skips such a graph structurally instead of failing
                // the load. Only the *root* is identity-critical, and ValidateStoredRootType rejects a
                // stored root that is not the requested one.
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
        /// silent misparse.
        /// <para>
        /// A numeric id is only meaningful within the id space that produced it, so
        /// <paramref name="fingerprintMatched"/> decides how much it proves. With a matching layout
        /// fingerprint the id space is this context's own, so numeric equality is authoritative. With a
        /// mismatching fingerprint the file may come from a different context whose ids collide — two
        /// one-root contexts hand both their roots the same id — so the stored identity has to be
        /// resolved from the <em>file</em> schema and matched by type (including a
        /// <c>[PreviousTypeName]</c> rename). Trusting the number there made a wrong-root load return
        /// <c>null</c> instead of failing.
        /// </para>
        /// </summary>
        static void ValidateStoredRootType(int storedRootId, Type requestedType, Dictionary<int, Type> idToType,
            SchemaRegistry registry, bool fingerprintMatched)
        {
            // 1. Same id space (the layout fingerprint matches): the stored id IS this context's id for
            //    typeof(T), so numeric equality is the whole answer.
            if (fingerprintMatched && storedRootId == GetTypeId(requestedType, registry))
                return;

            Type? storedType = null;
            idToType?.TryGetValue(storedRootId, out storedType);

            // 2. Identity: the file's stored root resolves to exactly the requested CLR type. This also
            //    covers the rename/migration case, where a [PreviousTypeName]-renamed root resolves to the
            //    current type and the old stored id differs from the current one.
            if (storedType != null && storedType == requestedType)
                return;

            string storedDescription = storedType != null
                ? $"'{TypeIdentity.GetCanonicalName(storedType)}'"
                : $"type id {storedRootId} (which is not declared in this context)";

            string because = fingerprintMatched
                ? "the layout fingerprint matched but the root type is not the declared one"
                : "the save was written by a context whose type-id space can collide with this one's, so the root identity was resolved by name";

            throw new Ceras.Exceptions.CerasSchemaException(
                $"The saved data's root type {storedDescription} does not match the requested load type " +
                $"'{TypeIdentity.GetCanonicalName(requestedType)}' ({because}). Load the save with its declared root type; " +
                "loading it as a base, derived, or unrelated type would silently misread the payload.");
        }

        /// <summary>
        /// Checks that the root type belongs to the context's manifest. A context has one finite
        /// manifest, so nothing is registered here: generated types are already in it, and a structural
        /// root shape (an array, a built-in closed collection, a tuple, an enum) has to have been declared
        /// up front with <c>DeclareRoot&lt;T&gt;()</c> or a context-scoped
        /// <c>[CerasKnownType(typeof(T), "context")]</c>. Anything else is an undeclared type and fails with
        /// the declaration message.
        /// </summary>
        static void EnsureRootDeclared(SchemaRegistry registry, Type type)
        {
            if (registry.IsDeclared(type)) return;   // in the manifest, or a declared root shape
            if (WellKnownTypes.IsWellKnown(type)) return;

            if (SchemaRegistry.TryBuildStructuralSchema(type, out _) || type.IsEnum)
            {
                string canonical = TypeIdentity.GetCanonicalName(type);
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"The root shape '{canonical}' is not part of the context's manifest. A context has one finite manifest, so every " +
                    $"allowed root shape has to be declared before the first Save/Load: call config.DeclareRoot<{canonical}>() " +
                    $"(or config.DeclareRoots(typeof({canonical}), …)), or declare it at compile time with " +
                    $"[assembly: CerasKnownType(typeof({canonical}), \"yourContext\")].");
            }

            // Left to the caller's UndeclaredType check, which carries the general message.
        }

        /// <summary>
        /// A root that is not part of the selected context's manifest. This is a declaration bug, exactly
        /// like the bake-time errors in <see cref="SchemaSection"/> and <see cref="WellKnownTypes"/>, so
        /// it is reported as a <see cref="Ceras.Exceptions.CerasSchemaException"/> and a caller can catch
        /// "you forgot to declare a type / the layout is wrong" without matching on message text.
        /// </summary>
        static Ceras.Exceptions.CerasSchemaException UndeclaredType(Type type) =>
            new Ceras.Exceptions.CerasSchemaException(
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
            public (string Name, ulong Value)[] EnumValues;
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
                    {
                        // Values are raw bits; show them signed only when the underlying type is signed.
                        bool isUnsigned = EnumBits.IsUnsignedUnderlyingName(s.DefinitionName);
                        foreach (var ev in s.EnumValues)
                            sb.AppendLine($"      {ev.Name} = {EnumBits.Format(ev.Value, isUnsigned)}");
                    }
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
            sb.AppendLine(_config.BuildTimestampKnown
                ? $"// Producer build timestamp (known): {_config.BuildTimestamp} (UTC ticks)"
                : "// Producer build timestamp: UNKNOWN (0; no newer-build gate)");

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

            // Validate the header like a load does, instead of only reading the version byte. Without
            // this a corrupt buffer produced a raw ArgumentOutOfRangeException from the long -> int
            // truncations below, while every neighbouring API reports a clean message (0.8.14). The
            // version mismatch stays a message rather than an exception, because that is the one case
            // a caller legitimately wants to *see* rather than have thrown at it.
            RobustHeader.Validate(data);
            uint version = data[RobustHeader.FormatVersionOffset];
            if (version != FormatVersion)
                return $"// Format version mismatch. File: {version}, Library: {FormatVersion}";

            RobustHeader.Read(data, out _, out byte flags, out long schemaStringOffsetL,
                out long dataSectionOffset, out long stringTableOffset, out ulong schemaFingerprint);

            // Validate() has bounded both offsets by the data length and ordered them, so the narrowing
            // conversions below cannot overflow.
            int schemaStringOffset = (int)schemaStringOffsetL;
            int dataSectionOffsetI = (int)dataSectionOffset;

            // No config here (this is a static diagnostic), so the table itself is read unbounded; the
            // per-entry and truncation checks inside StringTable.Load still apply.
            var schemaStrings = StringTable.Load(data, schemaStringOffset,
                dataSectionOffsetI - schemaStringOffset, StringTableCompression.Disabled);

            int schemaOffset = RobustHeader.Size;
            var sequence = new System.Buffers.ReadOnlySequence<byte>(data, schemaOffset, (int)schemaStringOffset - schemaOffset);
            var schemaReader = new System.Buffers.SequenceReader<byte>(sequence);

            // The raw parse, deliberately: a *diagnostic* has to be able to show a section the load path
            // refuses, which is exactly when it is most needed. The structural invariant is not skipped —
            // it is reported, one comment line per defective entry, below.
            var parsedEntries = new List<OldTypeSchema>();
            var fileSchemas = SchemaSection.ReadUnvalidated(ref schemaReader, schemaStrings, parsedEntries);

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

            // A section whose entries contradict the format is refused by Load, with the same reason. A
            // dump that hid that would be useless on exactly the file the caller is trying to understand,
            // so the defect is stated here rather than thrown.
            var defects = new List<string>();
            var describedIds = new HashSet<int>();
            foreach (var entry in parsedEntries) describedIds.Add(entry.TypeId);
            foreach (var entry in parsedEntries)
            {
                var verdict = StructuralShape.InspectShape(entry);
                if (!verdict.IsDefect) verdict = StructuralShape.InspectDefinition(entry, describedIds.Contains);
                if (verdict.IsDefect)
                    defects.Add("// REFUSED BY LOAD: " + StructuralShape.Describe(entry, verdict));
            }
            foreach (var defect in defects) sb.AppendLine(defect);
            if (defects.Count > 0)
                sb.AppendLine($"// ({defects.Count} entr{(defects.Count == 1 ? "y" : "ies")} a Load would refuse; see the reason above.)");

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
