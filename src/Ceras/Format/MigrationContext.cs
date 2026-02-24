using System;
using System.Collections.Generic;

namespace Ceras.Format
{
    /// <summary>
    /// Passed to <c>[OnAfterDeserialize]</c> methods when an object was loaded from a save
    /// file whose schema differs from the current schema (i.e. the game was updated).
    /// On the fast path (no migration needed) <see cref="WasMigrated"/> is false and
    /// all collections are empty — the method should return immediately in that case.
    /// </summary>
    public sealed class MigrationContext
    {
        /// <summary>Singleton for the no-migration fast path — avoids allocation.</summary>
        public static readonly MigrationContext None = new MigrationContext(false, 0, null, null);

        /// <summary>True when the schema of this type differed from the save file.</summary>
        public bool WasMigrated { get; }

        /// <summary>The game version number stored in the save file header.</summary>
        public uint OldGameVersion { get; }

        /// <summary>
        /// Names of fields present in the old save that had no matching field in the
        /// current type (removed or unrecognized fields whose data was skipped).
        /// </summary>
        public IReadOnlySet<string> SkippedFields { get; }

        /// <summary>
        /// Names of fields present in the current type that were absent in the old save
        /// and therefore defaulted (null / 0 / false / etc.).
        /// </summary>
        public IReadOnlySet<string> AddedFields { get; }

        internal MigrationContext(
            bool                wasMigrated,
            uint                oldGameVersion,
            IReadOnlySet<string> skippedFields,
            IReadOnlySet<string> addedFields)
        {
            WasMigrated    = wasMigrated;
            OldGameVersion = oldGameVersion;
            SkippedFields  = skippedFields ?? EmptySet;
            AddedFields    = addedFields   ?? EmptySet;
        }

        static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();
    }
}
