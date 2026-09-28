using System;
using System.Reflection;

namespace Ceras
{
    /// <summary>
    /// Assembly-level declaration of the owning build's producer timestamp (UTC ticks, as produced by
    /// <see cref="DateTime.ToUniversalTime"/>.<see cref="DateTime.Ticks"/> or
    /// <c>DateTimeOffset.UtcNow.UtcTicks</c>).
    ///
    /// <para>
    /// This is the supported way to give Ceras an "owning build" timestamp: it is baked into the
    /// consumer assembly once, by the build, and is therefore identical for every serializer and every
    /// save produced by that build. It is deliberately <b>not</b> read at serializer-construction time
    /// or at save time, so a long-running process (or a save written hours after startup) still records
    /// the build it actually came from. Ceras uses it to reject a save from a <b>newer</b> build before
    /// decoding the payload, even when the layout fingerprints match.
    /// </para>
    /// <para>
    /// A typical MSBuild integration writes one generated file into the compilation, e.g.:
    /// <code>
    /// &lt;ItemGroup&gt;
    ///   &lt;AssemblyAttribute Include="Ceras.CerasBuildTimestampAttribute"&gt;
    ///     &lt;_Parameter1&gt;$(CerasBuildTimestamp)&lt;/_Parameter1&gt;
    ///   &lt;/AssemblyAttribute&gt;
    /// &lt;/ItemGroup&gt;
    /// </code>
    /// where <c>CerasBuildTimestamp</c> is a property set by the build to
    /// <c>$([System.DateTime]::UtcNow.Ticks)</c> (or a fixed value for reproducible builds).
    /// </para>
    /// <para>
    /// Omitting the attribute leaves the timestamp explicitly <b>unknown</b>: Ceras records 0 in the
    /// header and performs no build-ordering check, which is visible via
    /// <see cref="CerasRobustConfig.BuildTimestampKnown"/>. Unknown is a deliberate mode for
    /// experiments/development, not a silent default for release builds.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class CerasBuildTimestampAttribute : Attribute
    {
        /// <summary>The producer build timestamp in UTC ticks. Must be &gt; 0 to be a known build.</summary>
        public long UtcTicks { get; }

        public CerasBuildTimestampAttribute(long utcTicks)
        {
            UtcTicks = utcTicks;
        }

        /// <summary>
        /// Convenience overload for MSBuild/command-line integration that prefers an ISO-8601 UTC string
        /// (e.g. <c>$([System.DateTime]::UtcNow.ToString("o"))</c>). The parser uses the invariant,
        /// round-trip ("o") format and always normalizes to UTC ticks.
        /// </summary>
        public CerasBuildTimestampAttribute(string utcIso8601)
        {
            if (string.IsNullOrWhiteSpace(utcIso8601))
            {
                UtcTicks = 0;
                return;
            }

            if (!DateTime.TryParse(utcIso8601, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                // 0.8.14: this used to map an unparseable value to 0, i.e. "unknown build", so an MSBuild
                // typo (a missing quote, a wrong format) silently disabled the newer-build gate for a
                // release while the attribute was still present. That is a build-configuration mistake,
                // not a runtime condition, so it fails here - at load of the owning assembly - where the
                // cause is still obvious.
                throw new ArgumentException(
                    $"'{utcIso8601}' is not a valid ISO-8601 UTC timestamp, so it cannot be used as a CerasBuildTimestamp. " +
                    "Pass UTC ticks as a number, or an ISO-8601 UTC string such as 2026-09-27T00:00:00.0000000Z " +
                    "(MSBuild: $([System.DateTime]::UtcNow.ToString(\"o\"))). To declare the build as unknown, omit the attribute " +
                    "entirely instead of passing an unparseable value.", nameof(utcIso8601));
            }

            UtcTicks = parsed.Ticks;
        }
    }

    /// <summary>
    /// Resolves the owning build's producer timestamp (see BuildTimestampTests). The value comes from the
    /// <see cref="CerasBuildTimestampAttribute"/> on the assembly, so it is fixed once per build and is
    /// not derived from serializer construction or save time. Kept out of the deterministic layout
    /// fingerprint: it is ordering metadata, not part of the byte layout contract.
    /// </summary>
    public static class BuildInfo
    {
        /// <summary>
        /// The producer build timestamp (UTC ticks) declared by the assembly that owns
        /// <paramref name="assembly"/>, or 0 when the assembly does not declare one (unknown build).
        /// </summary>
        public static long GetProducerBuildTimestamp(Assembly assembly)
        {
            if (assembly == null) return 0;
            var attr = assembly.GetCustomAttribute<CerasBuildTimestampAttribute>();
            return attr != null && attr.UtcTicks > 0 ? attr.UtcTicks : 0;
        }

        // There is deliberately no `ProducerBuildTimestamp` convenience property built on
        // Assembly.GetCallingAssembly(): that call reports the assembly of the method that called the
        // *current* method, so the answer changes if the compiler inlines this accessor into its caller
        // (and it is exactly the frame-skipping problem already recorded against the serializer
        // constructors). A wrapper cannot fix it - the assembly has to be passed explicitly, which is
        // what GetProducerBuildTimestamp(Assembly) does. The old property and its `Known` companion were
        // unused inside the library and are gone rather than left as a trap.
    }
}
