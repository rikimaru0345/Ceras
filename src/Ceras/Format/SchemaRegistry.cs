using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using Ceras.Formatters;

namespace Ceras.Format
{
    /// <summary>
    /// Runtime registry of all robust-serializable types for one serialization context.
    /// Populated during startup by <c>RobustFormatters.UseRobustFormatters(config, context)</c>, which
    /// registers exactly one context's manifest. The generated manifest is authoritative: the registry
    /// does not discover or build schemas for undeclared types. Only explicit declarations and
    /// structural BCL shapes (arrays and built-in collections) are accepted, and the
    /// baked schema, deterministic IDs, and layout fingerprint are therefore per context.
    /// <para>
    /// One further rule makes those identities usable: within a context, a name belongs to exactly one
    /// entry, whatever produced it. It is stated once, on <see cref="ClaimNames"/>.
    /// </para>
    /// </summary>
    public sealed class SchemaRegistry
    {
        sealed class Entry
        {
            public int            Id;
            public SchemaTypeInfo Info;
            public Type           Type;

            /// <summary>
            /// The descriptor the caller registered, kept so removing an opaque selection can put the
            /// entry back exactly as it was. <see cref="Info"/> is the <em>effective</em> descriptor — the
            /// opaque one while the type has a user custom formatter, this one otherwise — and everything
            /// that describes the bytes (the section encoder, the fingerprint, the dumps) reads that.
            /// </summary>
            public SchemaTypeInfo DeclaredInfo;

            public Entry(int id, SchemaTypeInfo info, Type type)
            {
                Id = id;
                Info = info;
                DeclaredInfo = info;
                Type = type;
            }
        }

        /// <summary>
        /// Which formatter a type's payload belongs to: the name the file records, and whether the value
        /// is written inline. Held per <em>type</em> rather than per entry, because a type can be marked
        /// before the manifest has an entry for it (a caller may install a formatter before the generated
        /// registration runs) and because clearing a selection has to restore the entry that was there
        /// before.
        /// </summary>
        internal readonly struct OpaqueSelection
        {
            public readonly string FormatterName;
            public readonly bool WrittenInline;

            public OpaqueSelection(string formatterName, bool writtenInline)
            {
                FormatterName = formatterName;
                WrittenInline = writtenInline;
            }
        }

        readonly Dictionary<Type,   Entry>  _byType       = new();
        readonly Dictionary<string, Entry>  _byStableName = new();
		int _nextId = WellKnownTypes.UserTypeBase;
		int _sealedWatermark = -1;

        // The types this context writes through a *user* custom formatter, and therefore whose payload is
        // an opaque block rather than a member list. Reconciled from the config's formatter assignments
        // by RobustCustomFormatterPolicy.Reconcile — at context setup and again before every Save/Load,
        // because a serializer seals its config when it is constructed, so the only formatter that can
        // still be installed after that is the one OnConfigNewType puts on the type it is handed. See
        // Format/LAYOUT.md, "A user custom formatter's payload is an opaque block".
        readonly Dictionary<Type, OpaqueSelection> _opaqueSelections = new();

		// The configured type binder, installed once at context setup (CerasRobustConfig.Seal). It is the
		// *only* thing beyond the manifest itself and Type.GetType that may answer a name question, and
		// it is consulted in exactly one place: ResolveDefinitionName, which the canonical-name parser
		// asks for a definition. Null means "this context has no binder", and an unregistered name then
		// resolves to nothing at all - which is the point of this field (see SetTypeBinder).
		ITypeBinder? _typeBinder;

		// ResolveDefinitionName's answers, positive and negative, keyed by the name it was asked about.
		// Memoized because the bake resolves a name per field, element and argument, and a load's id map
		// resolves a name per file entry, while a binder answer is a property of the process and never
		// changes: SimpleTypeBinder.GetTypeFromBase searches assemblies and throws when it finds nothing,
		// so asking it the same name twice costs a search and an exception. A null value is a real answer
		// here, not a missing one, which is why it is memoized too.
		readonly Dictionary<string, Type?> _definitionNames = new(StringComparer.Ordinal);

		// True only while baking: baking walks field/element type names and registers the structural
		// types it discovers, which must be allowed even though the registry is already sealed.
		bool _baking;

		// Root shapes declared through DeclareRoot, kept until the next Seal so their ids do not depend
		// on the order the declarations arrived in. A context has one finite manifest: every allowed root
		// is declared before the first Save/Load, and nothing can join the manifest afterwards.
		readonly SortedDictionary<string, Type> _declaredRoots = new(StringComparer.Ordinal);

		ulong? _fingerprint = null;
		readonly object _lock = new();

		// Frozen schema (baked once, at seal). The happy path uses this directly and never parses
		// the file schema nor decodes schema metadata names. All four are null until the bake and null
		// again after InternalClear, which is exactly what TryGetBakedSchema reports; they are
		// deliberately not each given its own "is it baked" flag.
		byte[]? _bakedSchemaBytes;
		byte[]? _bakedSchemaStringsBytes;
		ulong _bakedFingerprint;
		Dictionary<Type, int>? _bakedTypeToId;
		Dictionary<int, Type>? _bakedIdToType;

		/// <summary>True once the schema has been baked and frozen. A frozen bake never goes stale.</summary>
		public bool HasBakedSchema => IsBaked();

		/// <summary>
		/// Whether the four frozen fields are all present. They are set together by the bake and
		/// cleared together by <see cref="InternalClear"/>, so this is the one answer to "is the
		/// manifest baked" — stated once so a caller cannot read one field and get a different answer
		/// than the accessor hands out. The bake runs under the lock, so a caller already holding it
		/// (or calling one of the accessors) cannot observe a half-written set.
		/// </summary>
		bool IsBaked()
			=> _bakedSchemaBytes != null && _bakedSchemaStringsBytes != null
			   && _bakedTypeToId != null && _bakedIdToType != null;

		/// <summary>
		/// Precondition for resolving a save file's type ids against this registry: the manifest must be
		/// baked. Baking is what registers a shape the generated manifest does not contain - a type
		/// reachable only as a member, a structural shape, a declared root - and with it the
		/// <c>[PreviousTypeName]</c> aliases a file can refer to. Before the bake none of those is in the
		/// manifest, so neither its current name nor its old one is a registry identity; ids resolved too
		/// early are silently incomplete.
		/// </summary>
		internal void RequireBakedManifest()
		{
			lock (_lock)
			{
				if (_bakedSchemaBytes == null)
					throw new Ceras.Exceptions.CerasSchemaException(
						"Cannot resolve a save file's type ids: this context's manifest is not baked yet. Call SchemaRegistry.Seal() first - " +
						"baking is what registers a shape that is only reachable as a member, together with its [PreviousTypeName] aliases.");
			}
		}

		/// <summary>
		/// Returns the frozen schema region: the schema bytes (containing indices into the schema
		/// string table) and the schema string table bytes. Deterministic for a given registered set.
		/// <para>
		/// Every reference is null when this returns false — the context has not baked its manifest
		/// yet — and non-null when it returns true, so the answer is the single thing a caller has to
		/// check. The fingerprint is still reported either way, because it is a value rather than a
		/// reference and comparing it is the point of asking.
		/// </para>
		/// </summary>
		public bool TryGetBakedSchema(
			[NotNullWhen(true)] out byte[]? schemaBytes,
			[NotNullWhen(true)] out byte[]? schemaStrings,
			[NotNullWhen(true)] out Dictionary<Type, int>? typeToId,
			[NotNullWhen(true)] out Dictionary<int, Type>? idToType,
			out ulong fingerprint)
		{
			lock (_lock)
			{
				schemaBytes = _bakedSchemaBytes;
				schemaStrings = _bakedSchemaStringsBytes;
				typeToId = _bakedTypeToId;
				idToType = _bakedIdToType;
				fingerprint = _bakedFingerprint;
				return IsBaked();
			}
		}

        /// <summary>
        /// Clears all registered types. Internal use only.
        /// </summary>
        internal void InternalClear()
        {
            lock (_lock)
            {
                _byType.Clear();
                _byStableName.Clear();
                _opaqueSelections.Clear();
                _definitionNames.Clear();
                _nextId = WellKnownTypes.UserTypeBase;
				_sealedWatermark = -1;
				_declaredRoots.Clear();
				_baking = false;
				_fingerprint = null;
				_bakedSchemaBytes = null;
				_bakedSchemaStringsBytes = null;
				_bakedTypeToId = null;
				_bakedIdToType = null;
				_bakedFingerprint = 0;
            }
        }

		/// <summary>
		/// Freezes the current set of registered types as the context's manifest and bakes the schema
		/// bytes. Nothing can be added afterwards, so a context has exactly one id map and one layout
		/// fingerprint for its whole lifetime, and a previously matching save never falls into the
		/// interpreter merely because another root shape was used.
		/// <para>
		/// Root shapes that no manifest can describe — arrays, built-in closed collections,
		/// <c>KeyValuePair</c>, tuples, enums — are declared up front with <see cref="DeclareRoot"/>
		/// (or a context-scoped <c>[CerasKnownType(typeof(T), "context")]</c>, which the generator turns
		/// into the same declaration). They are folded in here in canonical-name order, so the assigned
		/// ids do not depend on the order the declarations were made in.
		/// </para>
		/// </summary>
		public void Seal() {
			lock (_lock) {
				if (IsSealed) return;

				_baking = true;
				try
				{
					// Declared root shapes first, in canonical order, so the id space is deterministic. Every
					// name this context declares is claimed (and so checked) here, before a schema byte
					// exists — the refusal is ClaimNames'.
					foreach (var declared in _declaredRoots.Values)
					{
						if (_byType.ContainsKey(declared)) continue;
						Register(declared, null!);
					}
					_declaredRoots.Clear();

					// Baking walks field/element type names and may register the structural types it
					// discovers, which is why registrations are allowed for its duration. It publishes the
					// frozen region only once it holds all four parts, so a bake refused on the way (a
					// structural shape whose derived name is already taken, a member whose type name
					// resolves to nothing) leaves the registry unsealed and unbaked.
					//
					// Those two facts are the whole of what a refused seal rolls back, and they are what the
					// rollback is for: the four frozen fields are published atomically, so no caller can
					// observe half a bake, and RequireBakedManifest keeps refusing to resolve a file's ids
					// until a bake has really succeeded.
					//
					// What a refused seal does *not* roll back is the registrations that had already
					// happened: the ones the loop above made for the declared roots, and the ones the bake
					// made through SchemaSection.ResolveId -> Register before it reached the bad member.
					// Those entries keep their id, their SchemaTypeInfo and the names they claimed in
					// _byStableName, and _nextId stays advanced past them - so a registry whose seal was
					// refused is NOT the state a registry that was never asked to seal is in, and the
					// precondition RequireBakedManifest states is a fact about the *frozen region* only, not
					// about what the registry holds. (A refusal in the declared-root loop also leaves the
					// pending declarations pending, because _declaredRoots.Clear() is only reached once the
					// whole loop has run: the next Seal refuses on the same declaration, which is what a
					// context with an unfixable root shape wants.)
					//
					// Harmless through Save/Load, which is the only caller: a load seals again (the early
					// return above needs IsSealed, and a refused seal never set the watermark), and
					// BuildIdToType refuses a file's ids until a bake has succeeded. Documented rather than
					// rolled back because unwinding it would mean letting a bake retract an entry that
					// something else may already have observed - a riskier change than the comment it
					// replaces. RefusedSealStateTests pins the state as it is.
					//
					// Asked *before* the bake, and that is what keeps a refused seal free of a published
					// region: both facts it needs are already true here (the declared roots were folded in
					// just above, and every other member of the manifest was registered before the seal), and
					// a bake only ever adds the structural shapes it discovers on the way — each of which is
					// a reserved well-known id, and so answered by the first clause below rather than by the
					// manifest.
					BakeLocked();
				}
				finally
				{
					_baking = false;
				}

				_sealedWatermark = _nextId;
				_fingerprint = null; // force recompute
				_bakedFingerprint = ComputeFingerprintLocked();
			}
		}

		/// <summary>
		/// True when the type is part of the context's manifest: already registered, or a root shape
		/// declared with <see cref="DeclareRoot"/> that the next <see cref="Seal"/> will fold in. Pending
		/// declarations count, because a declaration is made at context setup, before the first seal.
		/// </summary>
		public bool IsDeclared(Type type)
		{
			if (type == null) return false;
			lock (_lock)
			{
				if (_byType.ContainsKey(type)) return true;
				foreach (var declared in _declaredRoots.Values)
					if (declared == type) return true;
				return false;
			}
		}

		/// <summary>
		/// Declares a root shape the context is allowed to serialize, for shapes no generated manifest
		/// describes: a one-dimensional array, a built-in closed collection, a <c>KeyValuePair</c>, a
		/// tuple, or an enum. Must be called before the first <c>Save</c>/<c>Load</c> seals the registry.
		/// Idempotent, and order-independent: the shapes are folded in at the next <see cref="Seal"/> in
		/// canonical-name order, so two serializers that declare the same roots in different orders get the
		/// same ids, the same fingerprint and the same bytes.
		/// </summary>
		public void DeclareRoot(Type rootType)
		{
			if (rootType == null) throw new ArgumentNullException(nameof(rootType));
			if (IsWellKnownType(rootType)) return; // int, string, List<T> definitions, … need no declaration

			lock (_lock)
			{
				if (_byType.ContainsKey(rootType)) return; // already in the manifest
				if (IsSealed)
					throw new Ceras.Exceptions.CerasSchemaException(
                        $"Cannot declare the root shape '{TypeIdentity.GetCanonicalName(rootType)}' now: the context's manifest was already " +
                        "sealed by an earlier Save/Load. A context has one finite manifest, so every allowed root shape has to be declared up " +
                        "front (config.DeclareRoot<T>(), or [assembly: CerasKnownType(typeof(T), \"context\")]).");

				if (!TryBuildStructuralSchema(rootType, out _) && !rootType.IsEnum)
					throw new Ceras.Exceptions.CerasSchemaException(
						$"Cannot declare '{TypeIdentity.GetCanonicalName(rootType)}' as a root shape: only one-dimensional arrays, built-in closed " +
						"collections, KeyValuePair, tuples and enums are declared this way. Any other type must be a [CerasContext] root of the " +
						"context (or reachable from one), or a member of one.");

				_declaredRoots[TypeIdentity.GetCanonicalName(rootType)] = rootType;
			}
		}

		static bool IsWellKnownType(Type type)
			=> WellKnownTypes.IsWellKnown(type)
			|| (type.IsGenericType && type.IsGenericTypeDefinition && WellKnownTypes.TryGetId(type, out _));

		void BakeLocked()
		{
			var schemaStrings = new StringInternTable();

			byte[] schemaBuf = new byte[1024];
			int schemaOff = 0;

			// Every part of the frozen region is produced into a local first and published at the end, in
			// one uninterrupted run, so a bake that raises part-way through — a member whose type name
			// does not resolve, or a structural shape whose name is already claimed — leaves all four
			// fields at their previous value. Half a bake would answer HasBakedSchema/TryGetBakedSchema
			// with "baked" while IsSealed is still false, which is the one combination a caller cannot
			// reason about.
			var typeToId = SchemaSection.Write(ref schemaBuf, ref schemaOff, this, schemaStrings);

			// Asked here, and only here, because this is the first moment both facts are known: the bake is
			// what registers a shape nothing declared (a closed built-in collection reached as a member), so
			// before it a perfectly describable selection would look like one outside the manifest. Still
			// before any of the four frozen fields is published, so a refusal leaves nothing half-baked.
			RequireOpaqueSelectionsAreDescribable();

			byte[] strBuf = new byte[256];
			int strOff = 0;
			schemaStrings.Flush(ref strBuf, ref strOff, StringTableCompression.Disabled);

			// Each array is filled before its field is published, so the frozen region is only ever
			// reachable complete — the bake holds the lock throughout, but a field's own assignment is
			// the point at which it becomes visible at all.
			var bakedSchema = new byte[schemaOff];
			Array.Copy(schemaBuf, bakedSchema, schemaOff);
			var bakedStrings = new byte[strOff];
			Array.Copy(strBuf, bakedStrings, strOff);

			_bakedSchemaBytes = bakedSchema;
			_bakedSchemaStringsBytes = bakedStrings;
			_bakedTypeToId = typeToId;
			_bakedIdToType = BuildIdToTypeLocked();
		}

		Dictionary<int, Type> BuildIdToTypeLocked()
		{
			var map = new Dictionary<int, Type>();
			for (int i = 0; i < 128; i++)
				if (WellKnownTypes.TryGetType(i, out var wk)) map[i] = wk;
			foreach (var e in _byType.Values) map[e.Id] = e.Type;
			return map;
		}

		/// <summary>
		/// The manifest's name invariant, stated in the one place that enforces it: <b>a name belongs to
		/// exactly one entry</b>. A stable name or a <c>[PreviousTypeName]</c> alias is how a save file is
		/// matched onto this build, so a name two entries answer for would bind that file to whichever was
		/// registered first — a silent mis-binding on a save that is otherwise perfectly valid, showing up
		/// as a wrong value long after the rename. Two aliases, a stable name and an alias, or a
		/// stable name and a structural entry's derived name are all the same defect, so they are all
		/// refused here rather than by each of the paths that can produce them.
		/// <para>
		/// The one exception is a name several closures of <b>one</b> generic family may claim, because an
		/// <b>open</b> alias names the family rather than one of its members: a file writes the closed
		/// canonical name and the alias is closed with the file's own arguments
		/// (<see cref="AsNamedType"/>), so <c>Legacy.Box`1</c> legitimately stands for
		/// <c>Box&lt;int&gt;</c>, <c>Box&lt;string&gt;</c> and every other closure of <c>Box&lt;&gt;</c>. Two
		/// <i>different</i> families claiming it is a conflict, and that is what is refused.
		/// </para>
		/// <para>
		/// A reserved well-known name is not a manifest name at all (<see cref="WellKnownTypes.TryGetIdByCanonicalName"/>):
		/// ids below <see cref="WellKnownTypes.UserTypeBase"/> are a separate id space which a reader
		/// consults before the manifest, so an entry recorded under such a name could never be referred
		/// to. The check is asked here, at the claim, for the same reason the duplicate check is: a name
		/// the registry could answer for and an id the format already owns must not be one string.
		/// </para>
		/// <para>
		/// And the mirror of that rule, and the one clause of this invariant that ordering cannot settle: a
		/// name Ceras writes for a CLR type is that type's own, whether or not the type has registered yet
		/// (<see cref="RequireOwnDerivedName"/>, which says what is decided syntactically and what
		/// remains). A shape that is only reachable as a member — a structural collection, an array, a
		/// tuple, an enum the generator has no entry for — is registered by the bake at the moment a member
		/// reaches it, so "whoever registers first" would be decided by which member the walk happened to
		/// reach first; and a member whose name an impostor already holds is never reached, so the real
		/// shape never registers and the impostor's id is what gets written.
		/// </para>
		/// <para>
		/// Reported from <see cref="Register"/> rather than from the seal, which is strictly earlier: the
		/// author sees it where they wrote the name, and nothing half-baked has been published. The seal
		/// asks it again for every shape it discovers on the way (<c>SchemaSection.ResolveId</c> registers
		/// a structural type through the same door), so the invariant covers the whole finite manifest and
		/// not only what was declared up front.
		/// </para>
		/// </summary>
		void ClaimNames(Entry entry)
		{
			var info = entry.Info;

			// Two passes on purpose: every name is required before any of them is indexed, so a refused
			// registration cannot leave one of its aliases pointing at an entry that never joined the
			// manifest. Registering is a context-setup step, so the second walk costs nothing. First claim
			// wins, which is what resolving an open name already assumes (see AsNamedType): only a family
			// that is already allowed to share a name reaches the part where an index entry is a duplicate.
			RequireClaimableName(entry, info.StableName, isStableName: true);
			foreach (var alias in info.PreviousTypeNames)
				RequireClaimableName(entry, alias, isStableName: false);

			_byStableName.TryAdd(info.StableName, entry);
			foreach (var alias in info.PreviousTypeNames)
				if (IsClaimable(alias))
					_byStableName.TryAdd(alias, entry);
		}

		/// <summary>
		/// What a claim by <paramref name="type"/> names: a generic's <i>definition</i>, because an open
		/// name reaches the family and every closure of it answers for it (<see cref="AsNamedType"/>).
		/// Two claims are the same claim when this answers the same type for both — the exception
		/// <see cref="ClaimNames"/> documents.
		/// </summary>
		static Type ClaimTargetOf(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition() : type;

		/// <summary>
		/// A name an entry answers for at all. An empty alias names nothing, so it is skipped rather than
		/// indexed (and rather than made to collide with the next entry's empty alias); an empty
		/// <em>stable</em> name is a defect instead, and <see cref="RequireClaimableName"/> refuses it.
		/// </summary>
		static bool IsClaimable(string name) => !string.IsNullOrEmpty(name);

		static string NameRole(bool isStableName)
			=> isStableName ? "stable name" : "[PreviousTypeName] alias";

		/// <summary>
		/// The single check <see cref="ClaimNames"/> makes for one of its names: the name must be
		/// non-empty where it has to be, must not be a reserved well-known name, must be a name this
		/// entry's own type may answer for, and must not already be claimed by a different entry (or by a
		/// different generic family). Answers without mutating anything, which is what lets the caller
		/// require every name before indexing any of them.
		/// </summary>
		void RequireClaimableName(Entry entry, string name, bool isStableName)
		{
			if (!IsClaimable(name))
			{
				if (isStableName)
					throw new Ceras.Exceptions.CerasSchemaException(
						$"Cannot register '{TypeIdentity.GetCanonicalName(entry.Type)}': the descriptor's stable name is empty. " +
						"That name is the entry's identity on the wire — the schema string table records it and a save file is " +
						"matched by it — so it has to name something. Give the type a [CerasSerializable(\"Name\")] (or a " +
						"descriptor whose stable name is the type's canonical name), then rebuild.");

				return;   // an empty alias names nothing
			}

			if (WellKnownTypes.TryGetIdByCanonicalName(name, out int wellKnownId))
				throw new Ceras.Exceptions.CerasSchemaException(
					$"Cannot register '{TypeIdentity.GetCanonicalName(entry.Type)}' under the name '{name}': that name is " +
					$"reserved, and already stands for well-known type id {wellKnownId} without consulting any manifest. " +
					$"Type ids below {WellKnownTypes.UserTypeBase} and this context's own names are separate id spaces, so an " +
					$"entry recorded under '{name}' could never be resolved to — a reader takes the well-known id first. Rename " +
					"the type (or drop the [PreviousTypeName] alias that reuses the name), then rebuild.");

			RequireOwnDerivedName(entry, name, isStableName);

			if (!_byStableName.TryGetValue(name, out var owner))
				return;

			// The one claim that is not a conflict: the same entry, or another closure of the same family.
			if (ClaimTargetOf(owner.Type) == ClaimTargetOf(entry.Type))
				return;

			throw new Ceras.Exceptions.CerasSchemaException(
				$"The name '{name}' is claimed by two different types in this context's manifest: " +
				$"'{TypeIdentity.GetCanonicalName(entry.Type)}' claims it as its {NameRole(isStableName)}, and " +
				$"'{TypeIdentity.GetCanonicalName(owner.Type)}' already claims it as its " +
				$"{NameRole(owner.Info.StableName == name)}. A manifest name identifies exactly one type — a save file is " +
				"matched to this build by it, so two claims would bind it to whichever was registered first, silently. " +
				"Rename one of the two types, or declare the [PreviousTypeName] alias on only one of them, then rebuild.");
		}

		/// <summary>
		/// The mirror of the reserved-name rule: a name Ceras writes for a CLR type belongs to that type.
		/// The name is not invented here — it is read off the type being described
		/// (<see cref="TryBuildStructuralSchema"/> and <see cref="BuildDeclaredSchema"/> both write
		/// <c>TypeIdentity.GetCanonicalName(type)</c> as a derived entry's stable name, the generator
		/// writes the same string for a declared one), and a member records that same string as its type
		/// name. So such a name already has an owner, whoever is asking: the CLR type it spells.
		/// <para>
		/// <b>Why that has to be a rule and not a coincidence of registration order.</b> A member's type
		/// name is resolved by <c>SchemaSection.ResolveId</c>, which asks the reserved table, then
		/// <see cref="TryResolveType"/>, and registers the real shape only if the name is not already
		/// claimed. So an entry that claims a member's name <em>first</em> is what that member resolves
		/// to: <c>Register(typeof(List&lt;int&gt;), null)</c> is never reached, the member is written with
		/// the impostor's id, and the load reports success. On the migration path the member then reads
		/// through <c>SchemaTraversalEngine.ReadGenericInstantiation</c> with the impostor as its current
		/// type, which is not a collection shape, so <c>ReadUnknownCollectionPayload</c> consumes the
		/// count, skips the elements and answers <b>null</b> — a member silently gone, with the stream
		/// still aligned and no exception anywhere. Nothing downstream can catch that: the file's entry
		/// records the definition the real type would have recorded, so
		/// <c>MigrationPlanBuilder.TypeIdsCompatible</c> agrees with itself. This is the one hole in
		/// <see cref="ClaimNames"/> that ordering cannot close, which is why it is closed here instead —
		/// at the claim, before the name is in the index at all.
		/// </para>
		/// <para>
		/// What makes it so easy to miss is that the corruption needs the impostor in the context that
		/// <em>reads</em>: a reader whose manifest does not carry it registers the real shape under that
		/// name while it bakes, and the same bytes come back whole there. Measured on the code before this
		/// rule, for a member declared <c>List&lt;int&gt;</c> whose name a descriptor had claimed: the
		/// generated reader returned the list, the migration interpreter in the context that carried the
		/// descriptor returned <c>null</c>, and the context without it returned the list again. (See
		/// <c>DerivedNameBindingTests</c>.)
		/// </para>
		/// <para>
		/// <b>What is decided syntactically, and why it has to be.</b> Whether some string is the
		/// canonical name of a CLR type is a question about the CLR, and the only way to answer it inside a
		/// registry is <see cref="ResolveDefinitionName"/> — the configured <see cref="ITypeBinder"/>, which
		/// is user code and is documented as never running under the lock this method runs under
		/// (<see cref="Register"/> holds it). <see cref="WellKnownTypes.IsDerivedCanonicalName"/> therefore
		/// recognizes the <em>form</em> of a closed canonical name instead, which needs no type, no
		/// assembly and no binder, and which covers every such name because Ceras only ever writes
		/// <c>TypeIdentity.GetCanonicalName</c>. A type claiming its own canonical name is the one
		/// overlap, and it is what every entry in a manifest does, so no manifest is refused by this.
		/// </para>
		/// <para>
		/// <b>The residual gap, stated rather than papered over:</b> a name with <em>no</em> arguments is
		/// written for a CLR type just as much — an enum (<see cref="BuildDeclaredSchema"/>) and a
		/// non-generic collection subclass (<see cref="TryBuildStructuralSchema"/>) both derive a plain
		/// canonical name — and <b>nothing syntactic can tell those from an arbitrary alias</b>, because
		/// both are ordinary names that merely look like a type. The duplicate check below covers the
		/// order that keeps them safe: the real type registers (as a manifest entry, a declared root, or
		/// through a member of one) and the later claim is refused, naming both. What survives is the
		/// window in which the impostor registers first and the real type is only reached as a member, so
		/// its name resolves to the impostor and it never registers at all — the same silent binding, for
		/// an enum or a non-generic collection subclass. Closing that would mean resolving the name at
		/// claim time, i.e. the binder, under the lock: not available here, and not worth making the
		/// invariant conditional on a user callback. A descriptor claiming a name of <em>another</em>
		/// type's is the only shape of it that this gap applies to.
		/// </para>
		/// </summary>
		void RequireOwnDerivedName(Entry entry, string name, bool isStableName)
		{
			if (!WellKnownTypes.IsDerivedCanonicalName(name))
				return;

			// The one overlap, and not a narrow one: this is what the bake itself does for every shape it
			// discovers, and what a generated manifest does for every closed generic and array in it.
			string ownName = TypeIdentity.GetCanonicalName(entry.Type);
			if (string.Equals(name, ownName, StringComparison.Ordinal))
				return;

			throw new Ceras.Exceptions.CerasSchemaException(
				$"Cannot register '{ownName}' under the name '{name}': that name is the canonical name of a CLR type, and Ceras " +
				$"writes it for that type — the bake and the generator both spell an entry's stable name as " +
				$"TypeIdentity.GetCanonicalName(type), and a member records the same string as its type name. '{name}' therefore " +
				$"already belongs to the type it spells, and a member declared as that type resolves its name through this " +
				$"context's name index ({NameRole(isStableName)}), so the claim would bind that member to '{ownName}' instead of " +
				$"to the type it is: the real shape is then never registered, the member's payload is written with the wrong " +
				"type id, and a migrated load reads it back as null with the stream still aligned and no error raised. Only the " +
				$"type a name spells may claim it — which is why this entry's own canonical name ('{ownName}') is the name it may " +
				"use. Give the type its canonical name, and describe a renamed generic with an OPEN [PreviousTypeName] alias of " +
				"its definition ('Legacy.Box`1'), which a reader closes with the file's own arguments, then rebuild.");
		}

		public bool IsSealed => _sealedWatermark != -1;

		// ── Opaque selections (a user custom formatter) ─────────────────────────────────────

		/// <summary>
		/// Records that <paramref name="type"/> is written by a user custom formatter, so its manifest
		/// entry becomes an <see cref="SchemaTypeKind.Opaque"/> one: no member list, the formatter's
		/// identity, and whether a reference marker stands in front of the frame.
		/// <para>
		/// May be called before or after the type joins the manifest — a caller may install a formatter
		/// before the generated registration runs, and the registration is what puts the type in the
		/// manifest — and either order ends with the same effective entry. Idempotent for one selection,
		/// and refused once the manifest is baked, because a frozen schema section and a frozen
		/// fingerprint cannot describe a value whose formatter changed under them.
		/// </para>
		/// </summary>
		internal void ApplyOpaqueSelection(Type type, string formatterName, bool writtenInline)
		{
			if (type == null) throw new ArgumentNullException(nameof(type));
			if (formatterName == null) throw new ArgumentNullException(nameof(formatterName));

			lock (_lock)
			{
				if (_opaqueSelections.TryGetValue(type, out var existing)
					&& existing.FormatterName == formatterName
					&& existing.WrittenInline == writtenInline)
					return;

				if (IsSealed)
					throw new Ceras.Exceptions.CerasSchemaException(
						$"Cannot change the custom formatter for '{TypeIdentity.GetCanonicalName(type)}' now: this context's manifest was already " +
						"sealed and baked by an earlier Save/Load. The schema section a save carries and the layout fingerprint that chooses the " +
						"reader are both computed once, from the manifest as it stood at that moment, so a formatter selected after it cannot be " +
						"described by either. Configure the custom formatter before the first Save/Load - in the serializer's configure callback, or " +
						"for a type Ceras only discovers at runtime from OnConfigNewType, which is the one door that still takes an assignment after the " +
						"serializer was constructed - and rebuild.");

				_opaqueSelections[type] = new OpaqueSelection(formatterName, writtenInline);
				if (_byType.TryGetValue(type, out var entry))
					entry.Info = OpaqueInfoFor(entry.DeclaredInfo, formatterName, writtenInline);

				_fingerprint = null;   // the formatter is part of the layout, so the fingerprint moved
			}
		}

		/// <summary>
		/// Undoes <see cref="ApplyOpaqueSelection"/>: the entry goes back to the descriptor it was
		/// registered with, so the type is described by its member list again. Refused once the manifest
		/// is baked, for the same reason an application is refused.
		/// </summary>
		internal void ClearOpaqueSelection(Type type)
		{
			if (type == null) throw new ArgumentNullException(nameof(type));

			lock (_lock)
			{
				if (!_opaqueSelections.ContainsKey(type)) return;

				if (IsSealed)
					throw new Ceras.Exceptions.CerasSchemaException(
						$"Cannot remove the custom formatter for '{TypeIdentity.GetCanonicalName(type)}' now: this context's manifest was already " +
						"sealed and baked by an earlier Save/Load, and the schema section and layout fingerprint a save carries were both computed " +
						"with that formatter in place. Configure the formatter before the first Save/Load, and rebuild.");

				_opaqueSelections.Remove(type);
				if (_byType.TryGetValue(type, out var entry))
					entry.Info = entry.DeclaredInfo;

				_fingerprint = null;
			}
		}

		/// <summary>Whether this context writes <paramref name="type"/> through a user custom formatter.</summary>
		public bool IsOpaqueSelection(Type type)
		{
			if (type == null) return false;
			lock (_lock)
				return _opaqueSelections.ContainsKey(type);
		}

		/// <summary>
		/// The formatter selection recorded for <paramref name="type"/>, for the migration interpreter:
		/// the name the file recorded and the name this build would use are compared through it, because
		/// a different formatter's bytes are a different payload.
		/// </summary>
		internal bool TryGetOpaqueSelection(Type? type, out OpaqueSelection selection)
		{
			selection = default;
			if (type == null) return false;
			lock (_lock)
				return _opaqueSelections.TryGetValue(type, out selection);
		}

		/// <summary>
		/// The opaque descriptor derived from a registered one. Everything that identifies the type is
		/// copied — stable name and its <c>[PreviousTypeName]</c> aliases — because a name is how a save is
		/// matched onto this build and an opaque payload does not rename a type. Everything that describes
		/// the bytes is replaced: no members, no enum values, no arguments, no fixed size.
		/// </summary>
		static SchemaTypeInfo OpaqueInfoFor(SchemaTypeInfo declared, string formatterName, bool writtenInline)
			=> new SchemaTypeInfo(
				stableName: declared.StableName,
				previousTypeNames: declared.PreviousTypeNames,
				kind: SchemaTypeKind.Opaque,
				fields: Array.Empty<SchemaFieldInfo>(),
				fixedByteSize: 0,
				opaqueFormatterName: formatterName,
				opaqueValueWrittenInline: writtenInline);

		/// <summary>
		/// Every selection this context made must be able to reach the file, or a value would be written
		/// whose formatter the schema section cannot record. Called from <c>BakeLocked</c>, after the section
		/// has been encoded and before any of the frozen region is published — so a refusal leaves a context
		/// that has neither sealed nor baked, and a second refusal on the next call.
		/// <para>
		/// Both clauses restate the original defect rather than adding a new one. A member of a type whose
		/// schema identity is a <em>reserved</em> name is recorded under that name's fixed one-byte id, which
		/// has nowhere to record which formatter wrote the value; a type outside the manifest has no entry to
		/// record it in at all, and the bake refuses such a member anyway ("not part of the selected
		/// serialization context's manifest"). Either way the payload would be invisible to both the schema
		/// section and the fingerprint that chooses the reader — which is the whole thing this mechanism
		/// exists to remove.
		/// </para>
		/// </summary>
		internal void RequireOpaqueSelectionsAreDescribable() => RequireOpaqueSelections(checkIdentityToo: true);

		/// <summary>
		/// The half that needs no bake: a selection for a type the <em>reserved table</em> answers by name
		/// can never reach the file, whatever the manifest holds, so it is refused as early as the caller's
		/// own call stack allows — at context setup, and again before every Save/Load. The other half, a
		/// type outside the manifest, is asked after the bake, because a bake is what registers a shape
		/// nothing declared (see <see cref="BakeLocked"/>).
		/// </summary>
		internal void RequireOpaqueSelectionsHaveARecordableIdentity() => RequireOpaqueSelections(checkIdentityToo: false);

		void RequireOpaqueSelections(bool checkIdentityToo)
		{
			lock (_lock)
			{
				foreach (var selection in _opaqueSelections)
				{
					string name = TypeIdentity.GetCanonicalName(selection.Key);
					string token = selection.Value.FormatterName;

					if (_byType.ContainsKey(selection.Key))
						continue;

					// The same door the bake resolves a member's name through, and deliberately the *name*
					// table: a closed name such as `List<int>` is a manifest entry of its own (so an opaque
					// entry can carry the selection), while a name the reserved table owns is a fixed
					// one-byte id with nowhere to put it.
					if (WellKnownTypes.TryGetIdByCanonicalName(name, out int wellKnownId))
						throw new Ceras.Exceptions.CerasSchemaException(
							$"Cannot use the custom formatter '{token}' for '{name}' in a robust context: Ceras addresses this type by the " +
							$"reserved well-known id {wellKnownId} rather than by a manifest entry, and a reserved id has nowhere to record which " +
							"formatter wrote the value, so the payload would be invisible to both the file's schema section and the layout fingerprint " +
							"that chooses the reader. Store a serializable form of the value instead (an id or name you resolve in your own loading " +
							"code, or a [CerasSerializable] wrapper type the generator emits a schema for), and use a raw/transient serializer for the " +
							"custom encoding of the wrapper's contents.");

					if (!checkIdentityToo) continue;   // a bake may still register this one; asked again after it

					throw new Ceras.Exceptions.CerasSchemaException(
						$"Cannot use the custom formatter '{token}' for '{name}' in a robust context: the type is not part of the selected " +
						"serialization context's manifest, so there is no schema entry to record that its payload is a custom formatter's opaque " +
						"block, and a value of it cannot be described in a save at all. Declare the type with [CerasSerializable]/[CerasContext(\"name\")] " +
						"so the context's generated manifest holds it, make it reachable from a context root, or declare it with " +
						"[assembly: CerasKnownType<...>], then rebuild.");
				}
			}
		}

		// ── Registration ─────────────────────────────────────────────────────

		/// <summary>
		/// Called by the generated <c>RobustFormatters.UseRobustFormatters</c> for every
		/// robust-serializable type of the selected context. Safe to call multiple times with the same
		/// type (idempotent).
		/// A <c>null</c> <paramref name="info"/> declares the type structurally/minimally; it never
		/// walks CLR fields.
		/// <para>
		/// With an explicit <paramref name="info"/> this is also the documented public forge: it is how a
		/// historical file's shape is described by hand. Such a descriptor is held to the structural
		/// invariant — a <c>GenericInstantiation</c> of one of the format's own definitions has to record
		/// exactly that definition's argument count — so a malformed one is refused here rather than
		/// reaching a reader that could not honour it. See <see cref="StructuralShape"/>.
		/// </para>
		/// <para>
		/// It is also the one place the manifest's <em>name</em> invariant is enforced, so a descriptor
		/// that collides with another entry's name, with a reserved well-known name, or with the closed
		/// canonical name a CLR type is <em>derived</em> from is refused here and nothing joins the
		/// manifest. See <see cref="ClaimNames"/>, which states it, and
		/// <see cref="RequireOwnDerivedName"/>, which states the third rule and the gap it leaves.
		/// </para>
		/// </summary>
		public void Register(Type type, SchemaTypeInfo info)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            lock (_lock)
            {
                if (_byType.ContainsKey(type))
                    return; // idempotent

                if (IsSealed && !_baking)
                {
                    // A context has one finite manifest: nothing may join it after the seal, which keeps
                    // the id map and the layout fingerprint stable for the context's whole lifetime.
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"Cannot register type '{TypeIdentity.GetCanonicalName(type)}' after the schema has been sealed by an earlier Save/Load. " +
                        (TryBuildStructuralSchema(type, out _) || type.IsEnum
                            ? $"Declare it as an allowed root shape before the first Save/Load: config.DeclareRoot<{TypeIdentity.GetCanonicalName(type)}>(), " +
                              $"or [assembly: CerasKnownType(typeof({TypeIdentity.GetCanonicalName(type)}), \"context\")], and rebuild."
                            : $"Every other type must be part of the selected context's generated manifest: declare '{TypeIdentity.GetCanonicalName(type)}' " +
                              "with [CerasContext(\"name\")] (or make it reachable from one) and rebuild."));
                }

                if (info == null)
                {
                    if (WellKnownTypes.IsWellKnown(type)) return;
                    info = BuildDeclaredSchema(type);
                }

                // The authoring half of the structural invariant (StructuralShape): an explicit descriptor
                // is the one door a caller can open by hand, so a malformed one is refused here, where the
                // author can still see it, rather than surfacing as a mis-decode or a desync on a later load.
                // The derived descriptors above cannot fail this: TryBuildStructuralSchema builds the entry
                // from the CLR type, so the argument count is whatever the definition takes. The invariant
                // itself is stated in one place and asked again at the section parser, the migration plan
                // builder and the Type-value grammar; this is only the earliest door.
                StructuralShape.RequireDescribableDescriptor(type, info);

                var entry = new Entry(_nextId, info, type);

                // A selection recorded before the type joined the manifest applies now, so that the two
                // orders a caller can install a formatter in — before or after the generated registration
                // that registers the type — end with the same effective entry (ApplyOpaqueSelection).
                if (_opaqueSelections.TryGetValue(type, out var pendingSelection))
                    entry.Info = OpaqueInfoFor(info, pendingSelection.FormatterName, pendingSelection.WrittenInline);

                // The manifest's name invariant, before the entry becomes part of it: every name this entry
                // answers for is claimed (or the registration is refused) while the registry still looks
                // exactly as it did, so a rejected entry costs no id and leaves no name behind pointing at
                // an entry that is not in the manifest.
                ClaimNames(entry);

                _nextId++;
                _byType[type] = entry;

                _fingerprint = null; // invalidate
            }
        }

        /// <summary>
        /// Builds the schema for an explicitly declared type that has no generated schema. Structural
        /// for arrays and built-in collections; enum metadata for enums; an empty declaration
        /// otherwise. Never enumerates CLR fields.
        /// </summary>
        static SchemaTypeInfo BuildDeclaredSchema(Type type)
        {
            if (TryBuildStructuralSchema(type, out var structural))
                return structural;

            string typeName = TypeIdentity.GetCanonicalName(type);

            if (type.IsEnum)
            {
                var names = Enum.GetNames(type);
                var values = Enum.GetValues(type);
                var enumValues = new SchemaEnumValueInfo[names.Length];
                var wireNameOwner = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < names.Length; i++)
                {
                    // The wire name, not the C# member name: an enum member is recorded and matched under
                    // [CerasStableName] when it has one, and the generated schema says the same thing
                    // (RobustCodeEmitter.BuildSchemaExpression). Recording the C# name here instead would
                    // make this path the one that cannot read its own files after a member rename.
                    string wireName = EnumWireName.Of(type, names[i]);

                    if (wireNameOwner.TryGetValue(wireName, out var other))
                        throw new Ceras.Exceptions.CerasSchemaException(
                            $"Cannot describe the enum '{TypeIdentity.GetCanonicalName(type)}': its members '{other}' and " +
                            $"'{names[i]}' would both be recorded under the name '{wireName}'. An enum member is identified by " +
                            "that name, so it has to be unique - give one of them a different [CerasStableName].");

                    wireNameOwner[wireName] = names[i];

                    // Raw bits, so a ulong value above long.MaxValue is representable.
                    enumValues[i] = new SchemaEnumValueInfo(wireName, EnumBits.ToRawBits(values.GetValue(i), type));
                }

                // An enum is a named schema entry, so it carries its rename aliases like any other type:
                // without them a renamed enum cannot be resolved from an old save.
                var previousNames = new List<string>();
                foreach (var prev in type.GetCustomAttributes<PreviousTypeNameAttribute>(inherit: false))
                    if (prev.Name != null) previousNames.Add(prev.Name);

                return new SchemaTypeInfo(
                    stableName: typeName,
                    previousTypeNames: previousNames.ToArray(),
                    kind: SchemaTypeKind.Enum,
                    fields: null!,
                    fixedByteSize: 0,
                    enumValues: enumValues,
                    underlyingTypeName: TypeIdentity.GetCanonicalName(Enum.GetUnderlyingType(type)));
            }

            return new SchemaTypeInfo(
                stableName: typeName,
                previousTypeNames: Array.Empty<string>(),
                kind: type.IsValueType ? SchemaTypeKind.Struct : SchemaTypeKind.Class,
                fields: Array.Empty<SchemaFieldInfo>(),
                fixedByteSize: 0);
        }

        /// <summary>
        /// Structural schema for arrays and built-in collections (definition + ordered argument type
        /// names). These are valid as roots/fields even though the generator skips them. Returns false
        /// for every other type (which must be declared via the generated manifest).
        /// <para>
        /// A shape the format cannot represent at all — or one the owner has decided Ceras does not
        /// support — is <b>rejected</b> rather than described: a multidimensional array, a
        /// <c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> subclass, and the collection shapes
        /// <see cref="UnsupportedCollectionPolicy"/> refuses. This is the bake's fail-loud entry point
        /// (see <c>Format/LAYOUT.md</c>), so the error is raised here rather than returned — unlike
        /// <see cref="WellKnownTypes.TryResolveCollectionShape"/>, which is a pure classifier every caller
        /// may probe, and which supplies the reason this method reports.
        /// </para>
        /// <para>
        /// Only the <em>derived</em> structural schema is refused this way. <see cref="Register(Type, SchemaTypeInfo)"/>
        /// with an explicit descriptor is the documented public forge (it is how a historical file is
        /// described by hand), and it stays available; the automatic paths — <see cref="DeclareRoot"/> and
        /// <c>Register(type, null)</c>, which both derive the descriptor from the CLR type — reach this
        /// method and therefore cannot admit a refused shape.
        /// </para>
        /// </summary>
        public static bool TryBuildStructuralSchema(Type type, out SchemaTypeInfo info)
        {
            if (type.IsArray)
            {
                if (type.GetArrayRank() != 1)
                {
                    // Multidimensional arrays are written by MultiDimensionalArrayFormatter as
                    // rank + per-dimension lengths + flattened elements, which this structural
                    // (definition=Array, one element arg) description cannot express. Rejected loudly
                    // rather than described with a payload the interpreter cannot decode.
                    throw UnsupportedMultidimensionalArray(type, "schema");
                }

                info = new SchemaTypeInfo(
                    stableName: TypeIdentity.GetCanonicalName(type),
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.GenericInstantiation,
                    fields: Array.Empty<SchemaFieldInfo>(),
                    fixedByteSize: 0,
                    enumValues: null!,
                    underlyingTypeName: TypeIdentity.GetCanonicalName(typeof(Array)),
                    argTypeNames: new[] { TypeIdentity.GetCanonicalName(TypeIdentity.ElementTypeOf(type)) });
                return true;
            }

            if (WellKnownTypes.IsWellKnownCollectionDefinition(type) || WellKnownTypes.IsWellKnownStructuralValueType(type))
            {
                var def = type.GetGenericTypeDefinition();
                var args = type.GetGenericArguments();
                var argNames = new string[args.Length];
                for (int i = 0; i < args.Length; i++) argNames[i] = TypeIdentity.GetCanonicalName(args[i]);

                info = new SchemaTypeInfo(
                    stableName: TypeIdentity.GetCanonicalName(type),
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.GenericInstantiation,
                    fields: Array.Empty<SchemaFieldInfo>(),
                    fixedByteSize: 0,
                    enumValues: null!,
                    underlyingTypeName: TypeIdentity.GetCanonicalName(def),
                    argTypeNames: argNames);
                return true;
            }

            // Collection subclasses (e.g. MyList<T> : List<T>, MyDict : Dictionary<K,V>) are written by
            // the built-in CollectionFormatter with an explicit element count; describe that payload
            // (a List<>/Dictionary<>/HashSet<> definition plus the element/key/value identities)
            // instead of emitting an empty ordinary-class schema that cannot describe a single byte.
            if (WellKnownTypes.TryResolveCollectionShape(type, out var shapeDef, out var shapeArgs, out var shapeRejection))
            {
                // A shape is a (definition, ordered arguments) pair, and the classifier reports one as
                // a pair: answering true while leaving either half out would produce a schema that
                // describes an argument list the file cannot have written. It is a Ceras-side
                // invariant rather than a property of the model, so it is reported as such and not
                // papered over with a null-forgiving operator on the argument array.
                if (shapeDef == null || shapeArgs == null)
                    throw new InvalidOperationException(
                        $"The collection classifier resolved '{TypeIdentity.GetCanonicalName(type)}' to a shape with no definition " +
                        "or no arguments, so no structural schema can describe it. This is a defect in the classifier, not in the model.");

                var argNames = new string[shapeArgs.Length];
                for (int i = 0; i < shapeArgs.Length; i++) argNames[i] = TypeIdentity.GetCanonicalName(shapeArgs[i]);

                info = new SchemaTypeInfo(
                    stableName: TypeIdentity.GetCanonicalName(type),
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.GenericInstantiation,
                    fields: Array.Empty<SchemaFieldInfo>(),
                    fixedByteSize: 0,
                    enumValues: null!,
                    underlyingTypeName: TypeIdentity.GetCanonicalName(shapeDef),
                    argTypeNames: argNames);
                return true;
            }

            // A Queue<>/Stack<> subclass, or one of the collection shapes the owner has decided Ceras does
            // not support (ReadOnlyCollection<>/ReadOnlyDictionary<,> and their subclasses, and every
            // List<>/Dictionary<,> subclass): no Ceras formatter writes these in a way that round-trips
            // (see each rejection's own documentation), so there is no payload to describe. Refused
            // loudly, with the same actionable reason the migration decoder reports, rather than admitted
            // as an empty class — which is what would otherwise produce a save that loads back empty.
            if (shapeRejection is CollectionShapeRejection.UnsupportedQueueOrStack
                or CollectionShapeRejection.UnsupportedCollectionPolicy)
                throw new Ceras.Exceptions.CerasSchemaException(
                    WellKnownTypes.DescribeCollectionShapeRejection(type, shapeRejection));

            // Anything else is simply not a structural shape; the caller decides whether that is a
            // declaration problem (the common case) or one of the other rejections.
            info = null!;
            return false;
        }

        /// <summary>
        /// The actionable error for a multidimensional array, which the robust format cannot currently
        /// represent. Shares its message with the polymorphic-slot refusal (which has a different reason
        /// to give) through <see cref="MultidimensionalArrayGuard"/>, so the actionable half is one
        /// sentence at every site.
        /// </summary>
        internal static Exception UnsupportedMultidimensionalArray(Type type, string context)
            => MultidimensionalArrayGuard.Reject(type, context,
                "the robust format currently describes only one-dimensional arrays");

        // ── Lookup ────────────────────────────────────────────────────────────

        public bool TryGetId(Type type, out int id)
        {
            lock (_lock)
            {
                if (_byType.TryGetValue(type, out var e)) { id = e.Id; return true; }

                // No runtime auto-registration: the generated manifest is authoritative.
                // Undeclared types are rejected by the caller with guidance.
                id = -1;
                return false;
            }
        }

        public bool TryGetInfo(Type type, out SchemaTypeInfo info)
        {
            lock (_lock)
            {
                if (_byType.TryGetValue(type, out var e)) { info = e.Info; return true; }
                info = null!;
                return false;
            }
        }

        /// <summary>
        /// Resolves a stable name (or previous type name) to the current registered <see cref="Type"/>.
        /// Used by the migration engine when reading old schema sections. Types absent from the current
        /// build resolve to null and are interpreted without being registered — so the out parameter is
        /// null exactly when this returns false, and every caller has to treat the two as one fact.
        /// A caller mapping a whole file's id space calls <c>RequireBakedManifest()</c> first.
        /// <para>
        /// Three identities answer, in this order, and nothing else does:
        /// </para>
        /// <list type="number">
        /// <item>the manifest's own names — each registered entry's stable name and its
        /// <c>[PreviousTypeName]</c> aliases (see <see cref="ClaimNames"/>);</item>
        /// <item><c>Type.GetType</c>, for the framework and assembly-qualified spellings;</item>
        /// <item>canonical names, whose definitions are resolved by <see cref="ResolveDefinitionName"/> —
        /// and that is where the context's configured binder is consulted, and the only place.</item>
        /// </list>
        /// <para>
        /// A name none of the three answers resolves to <b>nothing</b>. There is deliberately no fallback
        /// that walks the loaded assemblies: which types a name stands for must be a fact about this
        /// context, not about whatever happens to be loaded when the name is read. A save written on a
        /// host with a plugin loaded would otherwise describe a different graph from the same bytes read
        /// on a host without it.
        /// </para>
        /// </summary>
        public bool TryResolveType(string stableName, [MaybeNullWhen(false)] out Type type)
        {
            if (string.IsNullOrEmpty(stableName))
            {
                type = null;
                return false;
            }

            // Step 1 is the registry's own map, so it runs under the lock; steps 2 and 3 do not read it
            // directly (ResolveDefinitionName takes the lock itself), so they stay outside - a binder call
            // is user code and must not run under the lock this bake already holds.
            lock (_lock)
            {
                // 1. Registered types are keyed by their canonical stable name. This covers generated
                //    closed generics and (after bake) structural arrays/collections.
                if (_byStableName.TryGetValue(stableName, out var e)) { type = AsNamedType(e.Type, stableName); return true; }
            }

            // 2. Assembly-qualified CLR names resolve directly. This is the legacy/old-save shape
            //    (Type.FullName for closed generics) and genuine external type names.
            type = Type.GetType(stableName);
            if (type != null) return true;

            // 3. Canonical names of closed generics / arrays / pointers that are not registered yet
            //    (e.g. a structural collection discovered while baking the schema). Their definitions are
            //    resolved from the manifest, from Type.GetType, or through the configured binder.
            if (TypeIdentity.TryParseCanonicalName(stableName, ResolveDefinitionName, out var parsed))
            {
                type = parsed;
                return true;
            }

            // 4. Nothing else. A name this context's manifest does not describe, that Type.GetType does
            //    not know, and that the configured binder cannot bind, names no type here.
            type = null;
            return false;
        }

        /// <summary>
        /// What a name found in <c>_byStableName</c> stands for.
        /// <para>
        /// The map is keyed by each entry's own canonical name - which always carries its type
        /// arguments, <c>Ns.Box`1&lt;System.Int32&gt;</c> - and by its <c>[PreviousTypeName]</c>
        /// aliases, which are whatever the author wrote. A constructed type can therefore only be
        /// reached under author text, and only an <b>open</b> alias can reach one: an open name such as
        /// <c>Old.Box`1</c> is indexed by every closure of the generic, so the entry it finds is
        /// whichever closure was registered first. The name asks for the family, not for that
        /// arbitrary member of it, so the definition is what it resolves to - which is also what a
        /// later <c>MakeGenericType</c> needs in order to close it with the file's own arguments.
        /// </para>
        /// <para>
        /// The test is the name's own shape and nothing else: a canonical closed name (and the legacy
        /// <c>Type.FullName</c> shape, and a hand-written closed alias) contains <c>&lt;</c>, so it
        /// keeps the exact closure it was indexed with. Only a name that carries no arguments at all
        /// is an alias for the family, so no other type can be picked up by accident.
        /// </para>
        /// <para>
        /// A canonical closed name is also the <b>only</b> entry it can reach: it is the name the bake
        /// derives for one CLR type, so <see cref="RequireOwnDerivedName"/> refuses every other entry's
        /// claim on it and the entry this finds is that type. The branch below is therefore the answer
        /// for a name that is not closed, and the closed one is answered by the same string the bake wrote.
        /// </para>
        /// </summary>
        static Type AsNamedType(Type registered, string? name)
        {
            if (!registered.IsGenericType || registered.IsGenericTypeDefinition) return registered;
            if (name != null && name.IndexOf('<') >= 0) return registered;

            return registered.GetGenericTypeDefinition();
        }

        /// <summary>
        /// Installs the binder that may answer definition names this context's manifest does not hold.
        /// <para>
        /// This is the whole of Ceras' "reach outside the manifest" affordance, and it is explicit on
        /// purpose: what a name stands for is the manifest's business, so a name the manifest does not
        /// describe resolves to nothing unless the owner configured a binder that can bind it. A context
        /// with no binder (the <c>internal</c> default, and every hand-built <c>SchemaRegistry</c> in a
        /// test) resolves nothing outside itself.
        /// </para>
        /// <para>
        /// Called once, from <c>CerasRobustConfig.Seal()</c> - the same setup-time door the robust custom
        /// formatter policy check uses, so the binder that the writer and the interpreter both consult is
        /// known before any save can exist. Installing a different one drops the memo: it was computed
        /// from the previous binder's answers.
        /// </para>
        /// </summary>
        internal void SetTypeBinder(ITypeBinder? binder)
        {
            lock (_lock)
            {
                _typeBinder = binder;
                _definitionNames.Clear();
            }
        }

        /// <summary>
        /// The manifest's name invariant applied to the <b>binder's</b> names as well (see
        /// <see cref="ClaimNames"/>, which states it for the manifest's own half).
        /// <para>
        /// A save file reaches the current model through two doors, and both have to agree: the manifest
        /// (<see cref="TryResolveType"/> step 1) and the configured binder (what
        /// <c>CerasRobustSerializer.BuildIdToType</c> asks when the manifest has no answer, and what
        /// <see cref="SchemaTraversalEngine"/> resolves a stored <see cref="Type"/> value through). If the
        /// binder names a type under a string that belongs to a *different* manifest entry, then the same
        /// name in a save file binds to one type on one path and to another on the other - a silent
        /// mis-binding, showing up as a wrong value long after the rename that caused it.
        /// </para>
        /// <para>
        /// Asked once, at the seal, for every entry: cheap because it is one string per registered type
        /// and it happens once per context, before any save or load. <see cref="SchemaTypeKind.GenericInstantiation"/>
        /// is skipped because a binder names such an entry by its <em>definition</em>
        /// (<c>Ns.Box`1</c>), which names the family rather than the closure — and that definition is an
        /// ordinary entry whose own name is checked. A binder that cannot name a type at all (returns null,
        /// or throws, which <see cref="SimpleTypeBinder.GetBaseName"/> does for a generic type parameter)
        /// claims nothing and is skipped, exactly as <c>SchemaTraversalEngine.ResolveFullName</c>
        /// normalizes a binder's "no such type" to null.
        /// </para>
        /// </summary>
        internal void VerifyBinderNamesDoNotShadowManifest()
        {
            lock (_lock)
            {
                if (_typeBinder == null) return;

                foreach (var entry in _byType.Values)
                {
                    if (entry.Info.Kind == SchemaTypeKind.GenericInstantiation) continue;

                    var binderName = TryGetBinderName(_typeBinder, entry.Type);
                    if (string.IsNullOrEmpty(binderName)) continue;
                    if (!_byStableName.TryGetValue(binderName!, out var owner)) continue;

                    // The one overlap that is not a conflict: the same entry, or another closure of the
                    // same family (ClaimNames).
                    if (ClaimTargetOf(owner.Type) == ClaimTargetOf(entry.Type)) continue;

                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"The configured type binder names '{TypeIdentity.GetCanonicalName(entry.Type)}' as '{binderName}', but that name " +
                        $"already belongs to '{TypeIdentity.GetCanonicalName(owner.Type)}' in this context's manifest. A save file that says " +
                        $"'{binderName}' would be matched to '{TypeIdentity.GetCanonicalName(owner.Type)}' through the manifest and to " +
                        $"'{TypeIdentity.GetCanonicalName(entry.Type)}' through the binder, depending on which of the two reads it - a silent " +
                        "mis-binding of an otherwise valid save, visible only as a wrong value long after the rename that caused it. " +
                        "Give the binder a name that stands for exactly one type (Config.TypeBinder), or rename one of the two types, then rebuild.");
                }
            }
        }

        /// <summary>
        /// What the binder calls <paramref name="type"/>, or null when the binder cannot say: a binder
        /// reports an unnameable type either by returning null or by throwing
        /// (<see cref="SimpleTypeBinder.GetBaseName"/> throws for a generic type parameter), and both are
        /// the same answer here - this type claims no name, so nothing can be in conflict with it.
        /// </summary>
        static string? TryGetBinderName(ITypeBinder binder, Type type)
        {
            try
            {
                return binder.GetBaseName(type);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves a generic/array element *definition* name (no arguments) to a <see cref="Type"/>.
        /// Used only by canonical-name parsing on cold paths (the bake and the interpreter's plan builder),
        /// and called without <see cref="_lock"/> held, so a binder call cannot stall the registry. It
        /// answers null for a name this build does not have, which is the contract
        /// <c>TypeIdentity.TryParseCanonicalName</c> is given.
        /// <para>
        /// Three sources, in this order: the manifest's own names, <c>Type.GetType</c> for the
        /// framework spellings, and the configured <see cref="ITypeBinder"/> for everything else. The
        /// last is the only one that is a choice somebody made rather than a fact of this context, which
        /// is why it is reached last and memoized (<see cref="_definitionNames"/>): a binder is asked once
        /// per name per context, hit or miss.
        /// </para>
        /// </summary>
        Type? ResolveDefinitionName(string? definitionName)
        {
            if (string.IsNullOrEmpty(definitionName)) return null;

            ITypeBinder? binder;
            lock (_lock)
            {
                // The manifest is consulted first, and before the memo: the bake registers entries while
                // it resolves names, so a memoized miss must not shadow an entry that has since joined.
                if (_byStableName.TryGetValue(definitionName, out var e)) return AsNamedType(e.Type, definitionName);
                if (_definitionNames.TryGetValue(definitionName, out var memoized)) return memoized;
                binder = _typeBinder;
            }

            // Type.GetType is a fact about the runtime, not a choice: a framework definition resolves
            // whether or not this context configured a binder. Only the binder below is the choice, and it
            // is the last thing asked.
            var resolved = Type.GetType(definitionName)
                           ?? (binder != null ? ResolveThroughBinder(binder, definitionName) : null);

            lock (_lock)
                _definitionNames[definitionName] = resolved;

            return resolved;
        }

        /// <summary>
        /// The binder's answer for a name, with "no such type" normalized to null. A binder reports it
        /// either by returning null or by throwing - <see cref="SimpleTypeBinder.GetTypeFromBase"/> throws
        /// after searching every loaded assembly - and both are a miss here, not a failure of the load.
        /// </summary>
        static Type? ResolveThroughBinder(ITypeBinder binder, string definitionName)
        {
            try
            {
                return binder.GetTypeFromBase(definitionName);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                return null;
            }
        }

        /// <summary>Enumerates all registered entries in registration order.</summary>
        public IEnumerable<(Type type, int id, SchemaTypeInfo info)> All()
        {
            lock (_lock)
            {
                var copy = new List<(Type type, int id, SchemaTypeInfo info)>(_byType.Count);
                foreach (var kv in _byType)
                    copy.Add((kv.Key, kv.Value.Id, kv.Value.Info));
                
                copy.Sort((a, b) => a.id.CompareTo(b.id));
                return copy;
            }
        }

		// ── Fingerprint ───────────────────────────────────────────────────────

		/// <summary>
		/// Computes a stable fingerprint over all registered schemas. Cached after the first call;
		/// invalidated whenever a new type is registered.
		/// </summary>
		public ulong ComputeFingerprint()
        {
            lock (_lock)
                return ComputeFingerprintLocked();
        }

        ulong ComputeFingerprintLocked()
        {
                if (_fingerprint.HasValue)
                    return _fingerprint.Value;

                var sorted = new List<Entry>(_byType.Count);
                foreach (var e in _byType.Values) {
					// If sealed, only fingerprint entries below the watermark.
					if (_sealedWatermark != -1 && e.Id >= _sealedWatermark)
						continue;

					sorted.Add(e);
				}

				sorted.Sort((a, b) => string.Compare(
                    a.Info.StableName, b.Info.StableName, StringComparison.Ordinal));

                uint h1 = 0, h2 = 0x9e3779b9u;

                // The wire format version is part of the contract: identical logical layouts must not
                // fingerprint the same across incompatible formats.
                h1 = Combine(h1, (uint)CerasRobustSerializer.FormatVersion);

                foreach (var entry in sorted)
                {
                    var info = entry.Info;

                    // The *final* type-ID assignment is part of the on-wire contract: the schema bytes
                    // and every payload type tag embed these IDs. Two contexts that describe the same
                    // logical types but assign different IDs are therefore NOT byte-compatible and must
                    // not share a fingerprint. We hash the assignment rather than canonicalizing IDs
                    // because IDs are already baked into the emitted bytes; reorder/canonicalization
                    // would silently rebind them, whereas a fingerprint difference correctly routes the
                    // load through the identity-preserving interpreter.
                    h1 = Combine(h1, (uint)entry.Id);
                    h1 = Combine(h1, Hash(info.StableName));
                    h2 = Combine(h2, (uint)info.Kind);

                    // Which formatter wrote a custom payload, and whether a reference marker stands in
                    // front of its frame. Formatter selection is part of the layout, and it used to be in
                    // neither this hash nor the file's schema section: a build that added, removed or
                    // replaced a type's custom formatter therefore saw a *matching* fingerprint and took
                    // the generated reader, over a payload the generated reader does not read. Hashing the
                    // kind alone would already separate an opaque entry from a described one; the
                    // formatter's own name is what separates two *different* opaque payloads, and the
                    // inline flag is what separates the two framings of one.
                    if (info.Kind == SchemaTypeKind.Opaque)
                    {
                        h1 = Combine(h1, Hash(info.OpaqueFormatterName));
                        h2 = Combine(h2, info.OpaqueValueWrittenInline ? 1u : 0u);
                    }

                    // Enum underlying type / generic definition. Part of the value representation.
                    h1 = Combine(h1, Hash(info.UnderlyingTypeName));

                    // Generic instantiations encode their ordered argument identities explicitly.
                    if (info.ArgTypeNames != null)
                    {
                        foreach (var argTypeName in info.ArgTypeNames)
                            h1 = Combine(h1, Hash(argTypeName));
                    }

                    // Fixed vs variable size changes how the payload is read and skipped.
                    h2 = Combine(h2, (uint)info.FixedByteSize);

					if (info.Fields != null)
                    {
                        foreach (var f in info.Fields)
                        {
                            h1 = Combine(h1, Hash(f.StableName));
                            h2 = Combine(h2, Hash(f.TypeName));
                            // Monomorphism decides whether per-element type tags are present.
                            h2 = Combine(h2, f.IsMonomorphic ? 1u : 0u);
                        }
                    }

                    if (info.EnumValues != null)
                    {
                        foreach (var ev in info.EnumValues)
                        {
                            h1 = Combine(h1, Hash(ev.Name));
                            // Hash all 64 bits: truncating to uint made e.g. 1 and 4294967297 collide.
                            h2 = Combine(h2, (uint)ev.Value);
                            h2 = Combine(h2, (uint)(ev.Value >> 32));
                        }
                    }
                }

                _fingerprint = ((ulong)h1 << 32) | h2;
                return _fingerprint.Value;
        }

        static uint Hash(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            return xxHash.CalculateHash(bytes);
        }

        static uint Combine(uint h, uint v)
        {
            h ^= v;
            h = (h << 13) | (h >> 19);  // rotate left 13
            h += h << 3;
            h ^= h >> 17;
            return h;
        }
    }
}
