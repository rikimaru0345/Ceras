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
    /// <see cref="BuildInfo.ProducerBuildTimestampKnown"/>. Unknown is a deliberate mode for
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

            UtcTicks = DateTime.TryParse(utcIso8601, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed.Ticks
                : 0;
        }
    }

    /// <summary>
    /// Resolves the owning build's producer timestamp (TODO 0.6 item 7). The value comes from the
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

        /// <summary>
        /// The producer build timestamp (UTC ticks) declared by the calling assembly, or 0 when unknown.
        /// </summary>
        public static long ProducerBuildTimestamp => GetProducerBuildTimestamp(Assembly.GetCallingAssembly());

        /// <summary>True when the calling assembly declares a known producer build timestamp.</summary>
        public static bool ProducerBuildTimestampKnown => ProducerBuildTimestamp > 0;
    }
}
