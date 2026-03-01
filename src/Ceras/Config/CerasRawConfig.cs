namespace Ceras
{
	/// <summary>
	/// Configuration for <see cref="CerasRawSerializer"/>: the fast, transient serializer suitable for
	/// networking, undo stacks, and other short-lived data.
	/// <para>
	/// Wire format is NOT stable across builds (no schema header, no version tolerance).
	/// Both sides must use the same build of your types. Do not use for saved files or databases.
	/// </para>
	/// </summary>
	public class CerasRawConfig : CerasConfig
	{
		/// <summary>
		/// When true, Ceras uses an extremely fast "reinterpret" formatter for blittable types
		/// (plain structs with no managed references). Uses native memory layout; does not respect
		/// endianness and does not support version tolerance.
		/// <para>Default: true</para>
		/// </summary>
		public bool UseReinterpretFormatter { get; set; } = true;
	}
}
