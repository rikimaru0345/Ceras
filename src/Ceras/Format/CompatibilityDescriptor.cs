using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Ceras.Format
{
    /// <summary>
    /// Which of the two wire modes a compatibility descriptor describes. The two modes share nothing on
    /// the wire — a robust save is self-describing and version tolerant, a transient message is neither
    /// — so a descriptor for one is never a substitute for the other, and the mode is stated rather than
    /// inferred from the type it came from.
    /// </summary>
    public enum CerasCompatibilityMode : byte
    {
        /// <summary><see cref="CerasRobustSerializer"/>: persistent, self-describing saves.</summary>
        RobustPersistent = 0,

        /// <summary><see cref="CerasFastSerializer"/>: transient messages, e.g. a network connection.</summary>
        TransientNetwork = 1,
    }

    /// <summary>
    /// What a <see cref="CerasRobustSerializer"/> context is, for a diagnostic dump or a compatibility
    /// decision taken <em>without</em> producing a save. Obtained from
    /// <see cref="CerasRobustSerializer.RobustCompatibility"/>, which writes no file and seals nothing:
    /// the manifest is still open at that point, which is exactly why <see cref="IsFinal"/> and
    /// <see cref="LayoutFingerprint"/> are one answer and never two independent claims.
    /// <para>
    /// Immutable: get-only properties, no setters, no collection member. A descriptor is a snapshot of
    /// one moment, so a caller cannot hold one and watch it drift into a claim it never made.
    /// </para>
    /// </summary>
    public sealed class RobustCompatibilityDescriptor
    {
        /// <summary>Always <see cref="CerasCompatibilityMode.RobustPersistent"/>.</summary>
        public CerasCompatibilityMode Mode { get; }

        /// <summary>
        /// The serialization context this serializer was configured for, or <b>null</b> when none was
        /// declared. The runtime never learns the generated context name on its own —
        /// <c>UseGeneratedFormatters("Name")</c> registers a manifest but records nothing readable back,
        /// and a manifest's stable names are type names, not the context's — so it is null unless
        /// <see cref="CerasRobustConfig.SetContextName"/> was called. Null
        /// means "not declared": a caller that declared none cannot distinguish that from an empty one,
        /// and a compatibility check must be able to see that it has nothing to compare.
        /// </summary>
        public string? ContextName { get; }

        /// <summary>
        /// The robust wire-format version this build writes and reads
        /// (<see cref="CerasRobustSerializer.FormatVersion"/>). A <em>format</em> version, independent of
        /// the game version a save also records and of <see cref="LayoutFingerprint"/>, which describes
        /// this context's layout within that format.
        /// </summary>
        public int FormatVersion { get; }

        /// <summary>
        /// True once the context's manifest has been sealed and baked, which happens at the first
        /// <c>Save</c> or <c>Load</c> and not before. Only then is <see cref="LayoutFingerprint"/> a number
        /// that means anything: <c>SchemaRegistry.ComputeFingerprint</c> is <em>provisional</em> until the
        /// seal, because it excludes the root shapes still pending in <c>DeclareRoot</c> and more may
        /// still join the manifest. False therefore means "not known yet", never "incompatible".
        /// <para>
        /// Reading a descriptor never seals anything: a context that is still open reports false and
        /// stays open, and the pair flips when the first save or load does the seal.
        /// </para>
        /// </summary>
        public bool IsFinal { get; }

        /// <summary>
        /// The context's layout fingerprint, or <b>0</b> when <see cref="IsFinal"/> is false. Zero is
        /// itself a possible fingerprint value, which is why the two are only ever reported together: a
        /// provisional number would be worse than none, because it would compare equal to another
        /// provisional number. Once final, this is the fingerprint the header of every save this context
        /// writes carries.
        /// </summary>
        public ulong LayoutFingerprint { get; }

        /// <summary>
        /// How many entries the context's manifest holds. Final only together with <see cref="IsFinal"/>:
        /// before the bake a root shape declared with <c>DeclareRoot</c> is still pending and is not
        /// counted.
        /// </summary>
        public int ManifestTypeCount { get; }

        /// <summary>
        /// The producer build timestamp (UTC ticks) every save from this context records, or 0 when
        /// <see cref="ProducerBuildKnown"/> is false. Ordering metadata, deliberately not part of
        /// <see cref="LayoutFingerprint"/>: two builds of the same layout share a fingerprint and are
        /// still told apart by their timestamp.
        /// </summary>
        public long ProducerBuildTimestamp { get; }

        /// <summary>
        /// False when this context is in the explicit "unknown build" mode — no
        /// <c>CerasBuildTimestamp</c> attribute on the owning assembly and no explicit
        /// <c>BuildTimestamp</c>. A save can then not be checked for being newer than this build, and
        /// <see cref="ProducerBuildTimestamp"/> is 0 rather than a guess.
        /// </summary>
        public bool ProducerBuildKnown { get; }

        /// <summary>
        /// How this context compresses the string table in each save. Deliberately not part of
        /// <see cref="LayoutFingerprint"/>: it is a per-save header flag the reader reads back, not a
        /// layout decision.
        /// </summary>
        public StringTableCompression StringTableCompression { get; }

        /// <summary>
        /// The version of the Ceras assembly this descriptor came from. <b>Informational only</b>: it is
        /// not part of the compatibility contract and is not hashed into anything. Two builds of the same
        /// Ceras version can produce different fingerprints, and one fingerprint can be read by a later
        /// Ceras release through the migration path.
        /// </summary>
        public string CerasAssemblyVersion { get; }

        internal RobustCompatibilityDescriptor(CerasCompatibilityMode mode, string? contextName, int formatVersion,
            bool isFinal, ulong layoutFingerprint, int manifestTypeCount, long producerBuildTimestamp,
            bool producerBuildKnown, StringTableCompression stringTableCompression, string cerasAssemblyVersion)
        {
            Mode = mode;
            ContextName = contextName;
            FormatVersion = formatVersion;
            IsFinal = isFinal;
            LayoutFingerprint = layoutFingerprint;
            ManifestTypeCount = manifestTypeCount;
            ProducerBuildTimestamp = producerBuildTimestamp;
            ProducerBuildKnown = producerBuildKnown;
            StringTableCompression = stringTableCompression;
            CerasAssemblyVersion = cerasAssemblyVersion;
        }
    }

    /// <summary>
    /// What a <see cref="CerasFastSerializer"/> is, for a connection-time handshake or a diagnostic.
    /// Obtained from <see cref="CerasFastSerializer.TransientCompatibility"/>.
    /// <para>
    /// It carries <b>no layout fingerprint, and no member that could be mistaken for one</b>. The
    /// transient layout is not hashed, is not stable from one compile of your types to the next and
    /// cannot be compared against anything: the only honest statement is that both ends must be the same
    /// build, and a number here would invite precisely the check this mode cannot support.
    /// </para>
    /// </summary>
    public sealed class TransientCompatibilityDescriptor
    {
        /// <summary>Always <see cref="CerasCompatibilityMode.TransientNetwork"/>.</summary>
        public CerasCompatibilityMode Mode { get; }

        /// <summary>
        /// The serialization context this serializer was configured for, or <b>null</b> when none was
        /// declared (see <see cref="RobustCompatibilityDescriptor.ContextName"/> for what null means).
        /// </summary>
        public string? ContextName { get; }

        /// <summary>
        /// Whether blittable structs are written with the reinterpret (native memory layout) formatter.
        /// Reported because it changes the bytes and carries no version tolerance at all: turning it off
        /// is not a performance knob but a different wire format, so a connection setup has to be able to
        /// see it.
        /// </summary>
        public bool UseReinterpretFormatter { get; }

        /// <summary>
        /// The version of the Ceras assembly this descriptor came from. <b>Informational only</b>, and no
        /// stronger in this mode than in the robust one — see
        /// <see cref="RobustCompatibilityDescriptor.CerasAssemblyVersion"/>.
        /// </summary>
        public string CerasAssemblyVersion { get; }

        internal TransientCompatibilityDescriptor(CerasCompatibilityMode mode, string? contextName,
            bool useReinterpretFormatter, string cerasAssemblyVersion)
        {
            Mode = mode;
            ContextName = contextName;
            UseReinterpretFormatter = useReinterpretFormatter;
            CerasAssemblyVersion = cerasAssemblyVersion;
        }
    }

    /// <summary>
    /// The context-name door for a transient configuration, and why a config has to be told its own
    /// context.
    /// <para>
    /// The runtime never learns the generated context name: <c>UseGeneratedFormatters("Name")</c>
    /// registers a manifest and records nothing Ceras can read back, and a manifest's stable names are
    /// type names rather than the context's. A compatibility descriptor can therefore only report a
    /// context that the application declared, and reports none otherwise.
    /// </para>
    /// </summary>
    public static class CerasCompatibility
    {
        /// <summary>
        /// Declares the context name this configuration reports in its
        /// <see cref="TransientCompatibilityDescriptor.ContextName"/>. The robust counterpart is
        /// <see cref="CerasRobustConfig.SetContextName"/>, which carries the identical contract; this one
        /// is an extension method only because <c>CerasRawConfig</c> is declared in a file this change
        /// does not own, and a <see cref="ConditionalWeakTable{TKey,TValue}"/> keeps the name per config
        /// instance without a field to add. The call site is the same either way.
        /// <para>
        /// Null, empty and whitespace-only all mean "not declared" and are stored as null: a name that
        /// cannot identify a context is the same answer as no name at all, and a handshake has to be able
        /// to see that it has nothing to compare rather than compare an empty string.
        /// </para>
        /// <para>
        /// Sealed like every other setting (see <c>CerasConfig.ThrowIfSealed</c>), so one serializer
        /// instance cannot change which context it claims to be after the fact.
        /// </para>
        /// </summary>
        public static void SetContextName(this CerasRawConfig config, string name)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.ThrowIfSealed();
            RawContextNames.GetValue(config, static _ => new ContextNameBox()).Name = Normalize(name);
        }

        /// <summary>
        /// The declared context name, or null. Asked through the table because a raw config that never
        /// declared one has no box at all, and answering that must not allocate one.
        /// </summary>
        internal static string? DeclaredContextName(this CerasRawConfig config)
            => RawContextNames.TryGetValue(config, out var box) ? box.Name : null;

        /// <summary>Null, empty and whitespace-only are the same answer: not declared.</summary>
        internal static string? Normalize(string name) => string.IsNullOrWhiteSpace(name) ? null : name;

        // One box per raw config, reachable only through the table, so the name is stored per config
        // instance without a field on the type. CerasRawConfig is declared in a file this change does not
        // own, which is why the storage is here rather than beside it.
        sealed class ContextNameBox
        {
            internal string? Name;
        }

        static readonly ConditionalWeakTable<CerasRawConfig, ContextNameBox> RawContextNames = new();
    }

    /// <summary>
    /// Where the two descriptors are built and cached.
    /// <para>
    /// A descriptor is a snapshot, so it cannot be taken at serializer construction: a context that has
    /// not saved yet has no final fingerprint, and a construction-time snapshot would go on reporting
    /// "not final" after the first save. It is built on first read instead, and cached per config — a
    /// config belongs to exactly one serializer (<c>CerasConfig.Claim</c>), so keying the cache on the
    /// config retains nothing past the serializer that owns it.
    /// </para>
    /// </summary>
    static class CompatibilityDescriptors
    {
        /// <summary>
        /// Informational, so resolved once per process rather than per read. <c>GetName().Version</c> is
        /// null only for an assembly with no version at all, which a compiled one does not have, so the
        /// empty string is a defensive answer rather than an expected one.
        /// </summary>
        internal static readonly string CerasVersion =
            typeof(CerasSerializer).Assembly.GetName().Version?.ToString() ?? "";

        static readonly ConditionalWeakTable<CerasRawConfig, Slot<TransientCompatibilityDescriptor>> Transient = new();
        static readonly ConditionalWeakTable<CerasRobustConfig, Slot<RobustCompatibilityDescriptor>> Robust = new();

        /// <summary>A cached snapshot, replaceable — the box exists only to be writable through the table.</summary>
        sealed class Slot<T> where T : class
        {
            internal T? Value;
        }

        /// <summary>
        /// The transient descriptor. A raw config is sealed at serializer construction and owns no
        /// registry, so nothing about it can change afterwards: built once and kept.
        /// </summary>
        internal static TransientCompatibilityDescriptor For(CerasRawConfig config)
        {
            var slot = Transient.GetValue(config, static _ => new Slot<TransientCompatibilityDescriptor>());
            return slot.Value ??= new TransientCompatibilityDescriptor(
                CerasCompatibilityMode.TransientNetwork,
                config.DeclaredContextName(),
                config.UseReinterpretFormatter,
                CerasVersion);
        }

        /// <summary>
        /// The robust descriptor. It becomes final exactly when the manifest is baked, and the bake is
        /// the first <c>Save</c>/<c>Load</c>, so a snapshot taken before that is provisional by
        /// construction and has to be replaced once it is not. The cache is keyed on the finality state
        /// rather than on a separate "has it baked" flag, so a baked registry can never be served a
        /// provisional descriptor, or the other way round.
        /// </summary>
        internal static RobustCompatibilityDescriptor For(CerasRobustConfig config)
        {
            var slot = Robust.GetValue(config, static _ => new Slot<RobustCompatibilityDescriptor>());

            // One call answers both halves. TryGetBakedSchema reports false exactly when the manifest is
            // not baked, and hands back the baked fingerprint when it is. Asking HasBakedSchema and then
            // TryGetBakedSchema would leave room for a bake to land in between, which is how a
            // fingerprint ends up printed next to IsFinal=false: the one combination this descriptor
            // exists to make impossible.
            //
            // Note which fingerprint this is: the *baked* one, never ComputeFingerprint(), which is
            // provisional until the seal. The zero below is belt-and-braces - an unbaked registry's
            // stored fingerprint happens to be 0 already - and it is what makes "no fingerprint before
            // the bake" a property of this code rather than of that coincidence.
            bool isFinal = config.SchemaRegistry.TryGetBakedSchema(out _, out _, out _, out _, out ulong fingerprint);
            if (!isFinal) fingerprint = 0;

            if (slot.Value == null || slot.Value.IsFinal != isFinal)
                slot.Value = Build(config, isFinal, fingerprint);

            return slot.Value;
        }

        static RobustCompatibilityDescriptor Build(CerasRobustConfig config, bool isFinal, ulong fingerprint)
            => new RobustCompatibilityDescriptor(
                CerasCompatibilityMode.RobustPersistent,
                config.ContextName,
                CerasRobustSerializer.FormatVersion,
                isFinal,
                fingerprint,
                config.SchemaRegistry.All().Count(),
                config.BuildTimestamp,
                config.BuildTimestampKnown,
                config.StringTableCompression,
                CerasVersion);
    }
}
