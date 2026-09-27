namespace Ceras
{
	using System;
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
		/// How many formatter assignments the last <c>RobustCustomFormatterPolicy</c> scan already
		/// covered, so a context that configures no user custom formatter — the only kind a robust
		/// context accepts — pays one integer comparison per <c>Save</c>/<c>Load</c> instead of a walk of
		/// every configured type. See <see cref="CerasConfig.FormatterAssignmentCount"/>.
		/// </summary>
		internal int VerifiedFormatterAssignmentCount { get; set; } = -1;

		/// <summary>
		/// Declares a root shape this context may serialize, for the shapes no generated manifest can
		/// describe: a one-dimensional array, a built-in closed collection (<c>List&lt;T&gt;</c>,
		/// <c>Dictionary&lt;K,V&gt;</c>, <c>HashSet&lt;T&gt;</c>, <c>Queue&lt;T&gt;</c>, <c>Stack&lt;T&gt;</c>),
		/// a <c>KeyValuePair</c>, a tuple, or an enum. Call it before the first <c>Save</c>/<c>Load</c>:
		/// a context has one finite manifest, so nothing may join it afterwards.
		/// <para>
		/// The equivalent compile-time declaration is a context-scoped
		/// <c>[assembly: CerasKnownType(typeof(T), "context")]</c>, which the generator turns into the
		/// same registration. Both are order-independent: the shapes are folded in at the first seal in
		/// canonical-name order, so two serializers that declare the same roots in different order produce
		/// the same ids, fingerprint and bytes.
		/// </para>
		/// </summary>
		public CerasRobustConfig DeclareRoot(Type rootType)
		{
			SchemaRegistry.DeclareRoot(rootType);
			return this;
		}

		/// <summary>Declares several allowed root shapes (see <see cref="DeclareRoot(Type)"/>).</summary>
		public CerasRobustConfig DeclareRoots(params Type[] rootTypes)
		{
			if (rootTypes == null) return this;
			foreach (var type in rootTypes) DeclareRoot(type);
			return this;
		}

		/// <summary>Declares an allowed root shape (see <see cref="DeclareRoot(Type)"/>).</summary>
		public CerasRobustConfig DeclareRoot<T>() => DeclareRoot(typeof(T));

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
		/// build timestamp (the assembly that constructed the serializer). Every constructor hands the
		/// assembly here, so no construction path can silently degrade to the unknown-build mode, and the
		/// timestamp is resolved once instead of on every read.
		/// </summary>
		internal void SetOwningAssembly(System.Reflection.Assembly assembly)
		{
			_owningAssembly = assembly;
			if (_buildTimestampSet) return;
			_buildTimestamp = BuildInfo.GetProducerBuildTimestamp(assembly);
			_buildTimestampSet = true;
		}

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
			get => _buildTimestamp;
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
			// A user custom formatter writes bytes the schema section cannot describe, and the layout
			// fingerprint cannot see that it was selected. Reject it as early as possible — at context
			// setup, before a save can exist — instead of only at the first Save/Load
			// (RobustCustomFormatterPolicy).
			RobustCustomFormatterPolicy.VerifyNoUserCustomFormatters(this);
			// (This used to also set RobustConfig.Locked, which nothing ever read: either it was meant to
			// guard something that does not exist, or it is dead. The sealed state that *is* consulted is
			// SizeLimits/CerasConfig's own, set by base.Seal().)
			// The schema registry is deliberately NOT sealed here: sealing bakes the schema and must
			// happen once the declared set (including the root type) is known, at the first use of the
			// robust serializer. Sealing at construction would bake an empty schema (TODO Phase 3.3).
		}
	}

	internal class RobustConfig
	{
		public StringTableCompression StringTableCompression = StringTableCompression.Deflate;
		public readonly SchemaRegistry SchemaRegistry = new SchemaRegistry();
	}
}
