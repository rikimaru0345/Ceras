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
		/// How many formatter assignments the last <c>RobustCustomFormatterPolicy</c> reconciliation
		/// already covered, so a context that configures no user custom formatter — the overwhelmingly
		/// common case — pays one integer comparison per <c>Save</c>/<c>Load</c> instead of a walk of
		/// every configured type. See <see cref="CerasConfig.FormatterAssignmentCount"/>.
		/// </summary>
		internal int VerifiedFormatterAssignmentCount { get; set; } = -1;

		/// <summary>
		/// Whether a migration may <b>drop</b> a value the file recorded as a custom formatter's opaque
		/// payload when this build has no formatter that can read it. Default <c>false</c>.
		/// <para>
		/// A custom payload's frame is a declared length and nothing more: it says how many bytes there
		/// are, never what they mean. So a value this build cannot decode has exactly two honest
		/// outcomes — refuse the load, or drop the value and say so. Which of the two is right is the
		/// application's decision, not Ceras': a value behind an id the application can resolve from
		/// elsewhere is worth keeping, and one that was a cache or a derived value is not. Ceras refuses
		/// by default, because a dropped payload is data loss that the load would otherwise report as
		/// success.
		/// </para>
		/// <para>
		/// This covers <b>every</b> position a framed payload can be met at, and one of them is easy to
		/// misread as an exception: a member the application has since <em>removed</em> is not free.
		/// There is nowhere to put that value either, so it is refused on the same terms as one this build
		/// cannot decode — the declared length accounts for its bytes exactly, which is what keeps the rest
		/// of the save aligned, but the value is lost and losing it is the caller's decision. The contrast
		/// is with an ordinary <b>described</b> type in the next paragraph, not with a kept member here.
		/// </para>
		/// <para>
		/// What this does <em>not</em> cover: a member of an ordinary <b>described</b> type the application
		/// has since removed. That is still a plain skip, reported through
		/// <see cref="MigrationContext.SkippedFields"/> and never refused — the file's schema says what the
		/// value was, so the loss is something the application can reason about. A custom payload is
		/// narrower on purpose: the file records only that <em>a formatter</em> wrote some bytes, so losing
		/// it is asked about rather than assumed.
		/// </para>
		/// <para>
		/// Set it before the first <c>Load</c>, like every other setting on this config.
		/// </para>
		/// </summary>
		public bool AllowDroppingCustomPayloads
		{
			get => _robustConfig.AllowDroppingCustomPayloads;
			set
			{
				ThrowIfSealed();
				_robustConfig.AllowDroppingCustomPayloads = value;
			}
		}

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

		/// <summary>
		/// True once a caller has named a build for this config itself, through
		/// <see cref="BuildTimestamp"/> or <see cref="SetUnknownBuildTimestamp"/>. An explicitly
		/// assigned timestamp outranks any <see cref="OwningAssembly"/>: the caller that states a
		/// build is stating it deliberately, whichever of the two is set first.
		/// </summary>
		bool _buildTimestampAssigned;

		/// <summary>
		/// The assembly that owns this context's build, or null when none is known. A real stored
		/// value rather than "unset": null <em>is</em> the unknown-build mode, so nothing has to tell
		/// it apart from never having been asked. See <see cref="OwningAssembly"/>.
		/// </summary>
		System.Reflection.Assembly? _owningAssembly;

		/// <summary>
		/// The assembly whose <see cref="CerasBuildTimestampAttribute"/> supplies the default producer
		/// build timestamp, or null when none is known.
		/// <para>
		/// <b>Where this has to be set.</b> <c>new CerasRobustSerializer(configure)</c> finds the
		/// calling assembly on every runtime that can walk a stack to do it, so the common case needs
		/// nothing here. NativeAOT cannot - there is no stack to walk, and the call throws
		/// <see cref="PlatformNotSupportedException"/> - so on that runtime this stays null unless it
		/// is set, and the build is reported honestly <b>unknown</b>: see
		/// <see cref="BuildTimestampKnown"/>. Under NativeAOT, set this property, or pass the assembly
		/// to <c>new CerasRobustSerializer(owningAssembly, configure)</c>, to get the timestamp there.
		/// </para>
		/// <para>
		/// Assigning null is the explicit spelling of the same answer: no owning assembly, unknown
		/// build. So is assigning an assembly that declares no
		/// <see cref="CerasBuildTimestampAttribute"/> - that is the state the attribute's own absence
		/// has always meant, and it is reported that way rather than guessed at.
		/// </para>
		/// <para>
		/// Assigning an explicit <see cref="BuildTimestamp"/> outranks this property whatever the order.
		/// Sealed like every other setting (<see cref="CerasConfig.ThrowIfSealed"/>), so a serializer
		/// that already exists cannot change which build it claims to be.
		/// </para>
		/// </summary>
		public System.Reflection.Assembly? OwningAssembly
		{
			get => _owningAssembly;
			set
			{
				ThrowIfSealed();
				_owningAssembly = value;
				// Resolved here rather than on every read of BuildTimestamp, and only when nothing
				// more specific has already said what the build is.
				if (_buildTimestampAssigned) return;
				_buildTimestamp = value == null ? 0 : BuildInfo.GetProducerBuildTimestamp(value);
			}
		}

		/// <summary>
		/// Producer build timestamp (UTC ticks) embedded once per owning build/context in every robust
		/// save. Loading a save whose timestamp is newer than the current build is rejected before the
		/// payload is decoded, even when layouts match.
		///
		/// <para>
		/// The supported source is the assembly-level <see cref="CerasBuildTimestampAttribute"/>
		/// (see <see cref="BuildInfo"/>) on the <see cref="OwningAssembly"/>: it is baked in once per
		/// build, so this property defaults to it and does <b>not</b> derive a value from serializer
		/// construction or save time. Assigning this explicitly outranks the owning assembly, whichever
		/// order the two are set in. A value of 0 means <b>unknown</b>: Ceras writes 0 and performs no
		/// build-ordering check; see <see cref="BuildTimestampKnown"/>.
		/// </para>
		/// </summary>
		public long BuildTimestamp
		{
			get => _buildTimestamp;
			set { ThrowIfSealed(); _buildTimestamp = value; _buildTimestampAssigned = true; }
		}

		/// <summary>
		/// True when a build-ordering check is active for this configuration: either an explicit
		/// non-zero <see cref="BuildTimestamp"/> was assigned, or the <see cref="OwningAssembly"/>
		/// declares a <see cref="CerasBuildTimestampAttribute"/>. False means the "unknown build" mode,
		/// in which a newer save cannot be detected — and a save from a newer build is then accepted
		/// rather than misread, so this reports exactly what the gate will do.
		/// <para>
		/// False is a normal answer, not a fault: no attribute, an explicit
		/// <see cref="SetUnknownBuildTimestamp"/>, and a serializer built where the owning assembly
		/// could not be discovered (NativeAOT, which has no stack to walk) without being told one all
		/// land here. See <see cref="OwningAssembly"/> for the last of those.
		/// </para>
		/// </summary>
		public bool BuildTimestampKnown => BuildTimestamp > 0;

		/// <summary>
		/// Explicitly selects the "unknown build" mode: the header records 0 and no build-ordering check
		/// is performed. Prefer declaring <see cref="CerasBuildTimestampAttribute"/> for real builds; this
		/// exists so the choice is visible and deliberate rather than a silent 0. It also outranks an
		/// <see cref="OwningAssembly"/>: a config that says "unknown build" says it after the assembly
		/// has been handed over.
		/// </summary>
		public void SetUnknownBuildTimestamp()
		{
			ThrowIfSealed();
			_buildTimestamp = 0;
			_buildTimestampAssigned = true;
		}

		// The context name the application declared, or null while none was declared. The runtime never
		// learns the generated context name on its own (UseGeneratedFormatters registers a manifest and
		// records nothing readable back, and a manifest's stable names are type names), so the
		// compatibility descriptor reports exactly what SetContextName was told and nothing more.
		//
		// `null` is a real stored value, not "unset": SetContextName stores null for a name that cannot
		// identify a context (null, empty or whitespace), which is the same answer as never calling it -
		// so there is one state here and the descriptor needs no way to tell two apart.
		string? _contextName;

		/// <summary>
		/// The declared context name, or null when none was declared. Read by
		/// <c>CerasRobustSerializer.RobustCompatibility</c> to fill
		/// <see cref="Ceras.Format.RobustCompatibilityDescriptor.ContextName"/>; see
		/// <see cref="SetContextName"/> for the contract.
		/// </summary>
		internal string? ContextName => _contextName;

		/// <summary>
		/// Declares the name of the serialization context this configuration is for, which the
		/// compatibility descriptor reports as
		/// <see cref="Ceras.Format.RobustCompatibilityDescriptor.ContextName"/>.
		/// <para>
		/// The runtime never learns the generated context name: <c>UseGeneratedFormatters("Name")</c>
		/// registers a manifest and records nothing Ceras can read back, and a manifest's stable names
		/// are type names rather than the context's. Pass the same string you passed to
		/// <c>UseGeneratedFormatters</c>; a descriptor that has not been told reports null rather than
		/// guessing one from the manifest.
		/// </para>
		/// <para>
		/// Null, empty and whitespace-only all mean "not declared" and are stored as null, so a name that
		/// cannot identify a context is reported the same way as no name at all. A compatibility check
		/// must be able to see that it has nothing to compare instead of comparing an empty string.
		/// </para>
		/// <para>
		/// Sealed like every other setting (<see cref="CerasConfig.ThrowIfSealed"/>), so one serializer
		/// instance cannot change which context it claims to be after the fact.
		/// </para>
		/// </summary>
		public CerasRobustConfig SetContextName(string name)
		{
			ThrowIfSealed();
			_contextName = CerasCompatibility.Normalize(name);
			return this;
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
			// Bring the manifest into agreement with the formatters this context has, as early as possible
			// — at context setup, before a save can exist — instead of only at the first Save/Load. A user
			// custom formatter is supported here: it makes the type's manifest entry opaque and its payload
			// a framed block, and it is hashed into the layout fingerprint. What is refused is the
			// remainder, where the format has nowhere to record the selection
			// (RobustCustomFormatterPolicy).
			RobustCustomFormatterPolicy.Reconcile(this);

			// The manifest resolves a name only for what it holds and for what Type.GetType knows; the
			// configured binder is the one way outside that, and the seal is where it becomes known to the
			// registry (SchemaRegistry.SetTypeBinder). Doing it here rather than at first use means the
			// binder the writer, the baked manifest and the migration interpreter all consult is settled
			// before a save can exist, and that a binder name shadowing a *different* manifest entry is
			// reported at context setup — where the author can still fix it — instead of silently binding
			// a save file to whichever of the two answers first
			// (SchemaRegistry.VerifyBinderNamesDoNotShadowManifest).
			SchemaRegistry.SetTypeBinder(TypeBinder);
			SchemaRegistry.VerifyBinderNamesDoNotShadowManifest();
			// (This used to also set RobustConfig.Locked, which nothing ever read: either it was meant to
			// guard something that does not exist, or it is dead. The sealed state that *is* consulted is
			// SizeLimits/CerasConfig's own, set by base.Seal().)
			// The schema registry is deliberately NOT sealed here: sealing bakes the schema and must
			// happen once the declared set (including the root type) is known, at the first use of the
			// robust serializer. Sealing at construction would bake an empty schema.
		}
	}

	internal class RobustConfig
	{
		public StringTableCompression StringTableCompression = StringTableCompression.Deflate;
		public readonly SchemaRegistry SchemaRegistry = new SchemaRegistry();

		/// <summary>
		/// See <see cref="CerasRobustConfig.AllowDroppingCustomPayloads"/>. On the internal holder rather
		/// than on the public config class because the public property is what a caller sets, and this is
		/// what the migration interpreter reads.
		/// </summary>
		public bool AllowDroppingCustomPayloads;
	}
}
