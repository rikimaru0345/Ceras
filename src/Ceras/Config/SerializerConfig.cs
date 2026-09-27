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

		/// <summary>
		/// Producer build timestamp (UTC ticks) embedded once per owning build/context in every robust
		/// save. Loading a save whose timestamp is newer than the current build is rejected before the
		/// payload is decoded, even when layouts match. 0 = unknown (no check).
		/// </summary>
		public long BuildTimestamp
		{
			get => _buildTimestamp;
			set { ThrowIfSealed(); _buildTimestamp = value; }
		}

		/// <summary>
		/// Compression applied to the string intern table embedded in each save file.
		/// <para>Default: Deflate</para>
		/// </summary>
		public StringTableCompression StringTableCompression
		{
			get => _robustConfig.StringTableCompression;
			set => _robustConfig.StringTableCompression = value;
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
