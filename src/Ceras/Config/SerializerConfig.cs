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
		internal readonly RobustConfig _robustConfig = new RobustConfig();

		/// <summary>
		/// The schema registry that tracks type field layouts for version tolerance and migration.
		/// Populated automatically as types are serialized; used during deserialization to detect schema changes.
		/// </summary>
		public SchemaRegistry SchemaRegistry => _robustConfig.SchemaRegistry;

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
			_robustConfig.SchemaRegistry.Seal();
		}
	}

	internal class RobustConfig
	{
		public bool Locked;
		public StringTableCompression StringTableCompression = StringTableCompression.Deflate;
		public readonly SchemaRegistry SchemaRegistry = new SchemaRegistry();
	}
}
