namespace Ceras
{
	using Ceras.Format;

	/// <summary>
	/// Compression method for the string intern table in the robust save format.
	/// </summary>
	public enum StringTableCompression
	{
		Disabled = 0,
		Deflate = 1,
	}

	/// <summary>
	/// Configuration for <see cref="CerasRobustSerializer"/>: the version-tolerant serializer suitable for
	/// saved games, settings files, and any data that must survive across builds of your application.
	/// <para>
	/// The wire format includes a schema header that describes the fields of each serialized type.
	/// This allows Ceras to migrate data when types change (fields added, removed, or renamed).
	/// </para>
	/// </summary>
	public class CerasRobustConfig : CerasConfig
	{
		public CerasRobustConfig() { }

		internal readonly RobustConfig _robustConfig = new RobustConfig();

		/// <summary>
		/// The schema registry that tracks type field layouts for version tolerance and migration.
		/// Populated automatically as types are serialized; used during deserialization to detect schema changes.
		/// </summary>
		public SchemaRegistry SchemaRegistry => _robustConfig.SchemaRegistry;

		long _buildTimestamp;
		bool _buildTimestampSet;
		System.Reflection.Assembly _owningAssembly;

		/// <summary>
		/// The assembly whose <see cref="CerasBuildTimestampAttribute"/> supplies the default producer
		/// build timestamp (the assembly that created the serializer). Set by the serializer constructor.
		/// </summary>
		internal void SetOwningAssembly(System.Reflection.Assembly assembly) => _owningAssembly = assembly;

		/// <summary>
		/// Producer build timestamp (UTC ticks) embedded once per owning build/context in every robust
		/// save. Loading a save whose timestamp is newer than the current build is rejected before the
		/// payload is decoded, even when layouts match.
		///
		/// <para>
		/// The supported source is the assembly-level <see cref="CerasBuildTimestampAttribute"/>
		/// (see <see cref="BuildInfo"/>): it is baked in once per build, so this property defaults to it
		/// and does <b>not</b> derive a value from serializer construction or save time. Assign it
		/// explicitly only for tests or unusual hosting. A value of 0 means <b>unknown</b>: Ceras writes
		/// 0 and performs no build-ordering check; see <see cref="BuildTimestampKnown"/>.
		/// </para>
		/// </summary>
		public long BuildTimestamp
		{
			get
			{
				if (_buildTimestampSet) return _buildTimestamp;
				return BuildInfo.GetProducerBuildTimestamp(_owningAssembly);
			}
			set { ThrowIfSealed(); _buildTimestamp = value; _buildTimestampSet = true; }
		}

		/// <summary>
		/// True when a build-ordering check is active for this configuration: either an explicit
		/// non-zero <see cref="BuildTimestamp"/> was assigned, or the owning assembly declares a
		/// <see cref="CerasBuildTimestampAttribute"/>. False means the "unknown build" mode, in which a
		/// newer save cannot be detected.
		/// </summary>
		public bool BuildTimestampKnown => BuildTimestamp > 0;

		/// <summary>
		/// Explicitly selects the "unknown build" mode: the header records 0 and no build-ordering check
		/// is performed. Prefer declaring <see cref="CerasBuildTimestampAttribute"/> for real builds; this
		/// exists so the choice is visible and deliberate rather than a silent 0.
		/// </summary>
		public void SetUnknownBuildTimestamp()
		{
			ThrowIfSealed();
			_buildTimestamp = 0;
			_buildTimestampSet = true;
		}

		/// <summary>
		/// Compression applied to the string intern table embedded in each save file.
		/// <para>Default: Deflate</para>
		/// </summary>
		public StringTableCompression StringTableCompression
		{
			get => _robustConfig.StringTableCompression;
			set
			{
				// This setting is deliberately treated as non-layout-affecting: it selects a per-save
				// header/compression flag and the loader reads that flag back from the file, so it does
				// not participate in the layout fingerprint. It is still sealed, for the same reason as
				// BuildTimestamp and the size limits: a serializer instance must not silently change how
				// it writes bytes after construction. The seal check runs before the assignment, so even
				// a same-value no-op throws, matching the other setters' semantics.
				ThrowIfSealed();
				_robustConfig.StringTableCompression = value;
			}
		}

		internal override void Seal()
		{
			base.Seal();
			_robustConfig.Locked = true;
			// The schema registry is deliberately NOT sealed here: sealing bakes the schema and must
			// happen once the declared set (including the root type) is known, at the first use of the
			// robust serializer. Sealing at construction would bake an empty schema (TODO Phase 3.3).
		}
	}

	internal class RobustConfig
	{
		public bool Locked;
		public StringTableCompression StringTableCompression = StringTableCompression.Deflate;
		public readonly SchemaRegistry SchemaRegistry = new SchemaRegistry();
	}
}
