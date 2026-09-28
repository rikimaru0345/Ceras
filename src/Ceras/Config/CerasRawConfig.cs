namespace Ceras
{
	/// <summary>
	/// Configuration for <see cref="CerasFastSerializer"/>: the fast, transient serializer suitable for
	/// networking, undo stacks, and other short-lived data.
	/// <para>
	/// Wire format is NOT stable across builds (no schema header, no version tolerance).
	/// Both sides must use the same build of your types. Do not use for saved files or databases.
	/// </para>
	/// </summary>
	public class CerasRawConfig : CerasConfig
	{
		public CerasRawConfig() { }

		/// <summary>
		/// When true, Ceras uses an extremely fast "reinterpret" formatter for the types whose memory
		/// image already <em>is</em> the member-by-member encoding: the same members, in the same order,
		/// at the same width with no padding, so that the bytes do not depend on which of the two ways
		/// the value was written. A type with a field the member list leaves out, a member order that
		/// differs from the layout order, or padding is written member by member instead - so the rule
		/// is narrower than "a struct with no references in it", and a type is not silently moved
		/// between the two encodings when a formatter is generated for it.
		/// <para>
		/// The rule is <see cref="Helpers.ReflectionHelper.IsBlittableType"/>, specified in
		/// <c>Format/LAYOUT.md</c>, "Which types the raw path may reinterpret" - which is where the
		/// member-order and no-padding checks, and the <c>Guid</c> and <c>fixed</c>-buffer exemptions,
		/// are described.
		/// </para>
		/// <para>
		/// Uses native memory layout; does not respect endianness and does not support version tolerance.
		/// Default: true.
		/// </para>
		/// </summary>
		bool _useReinterpretFormatter = true;
		public bool UseReinterpretFormatter { 
			get => _useReinterpretFormatter; 
			set { ThrowIfSealed(); _useReinterpretFormatter = value; } 
		}
	}
}
