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

            public Entry(int id, SchemaTypeInfo info, Type type)
            {
                Id = id;
                Info = info;
                Type = type;
            }
        }

        readonly Dictionary<Type,   Entry>  _byType       = new();
        readonly Dictionary<string, Entry>  _byStableName = new();
		int _nextId = WellKnownTypes.UserTypeBase;
		int _sealedWatermark = -1;

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
		/// <c>[PreviousTypeName]</c> aliases a file can refer to. Before the bake a renamed member-only
		/// type resolves its current name through the loaded-assembly scan but not its old one, so ids
		/// resolved too early are silently incomplete.
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
					// frozen region only once it holds all four parts, so a claim refused on the way (a
					// structural shape whose derived name is already taken) leaves the registry unsealed and
					// unbaked, exactly as if it had never been asked to seal.
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
		/// non-empty where it has to be, must not be a reserved well-known name, and must not already be
		/// claimed by a different entry (or by a different generic family). Answers without mutating
		/// anything, which is what lets the caller require every name before indexing any of them.
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

		public bool IsSealed => _sealedWatermark != -1;

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
		/// that collides with another entry's name, with a reserved well-known name, or with the name a
		/// structural shape derives from the CLR type is refused here and nothing joins the manifest. See
		/// <see cref="ClaimNames"/>, which states it.
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
        /// </summary>
        public bool TryResolveType(string stableName, [MaybeNullWhen(false)] out Type type)
        {
            if (string.IsNullOrEmpty(stableName))
            {
                type = null;
                return false;
            }

            // Steps 1-3 consult the registry's own maps, so they run under the lock. Step 4 walks every
            // loaded assembly, which must not happen under it (0.8.14): the bake holds the same lock and
            // calls this, so a scan here would serialise the whole process behind one name lookup.
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
            //    (e.g. a structural collection discovered while baking the schema). Definitions are
            //    resolved from the framework or loaded assemblies.
            if (TypeIdentity.TryParseCanonicalName(stableName, ResolveDefinitionName, out var parsed))
            {
                type = parsed;
                return true;
            }

            // 4. Last resort for genuinely-external/old names: an unqualified, non-canonical
            //    Type.FullName or a definition name. Bounded scan of loaded assemblies, unlocked.
            type = ScanLoadedAssemblies(stableName);
            return type != null;
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
        /// </summary>
        static Type AsNamedType(Type registered, string? name)
        {
            if (!registered.IsGenericType || registered.IsGenericTypeDefinition) return registered;
            if (name != null && name.IndexOf('<') >= 0) return registered;

            return registered.GetGenericTypeDefinition();
        }

        /// <summary>
        /// Looks a plain CLR name up in the loaded assemblies. Deliberately lock-free: it does not read
        /// the registry, and a miss is not a correctness problem for the caller (a name that is absent
        /// here is absent from the registry too, and <see cref="TryResolveType"/> is a cold path).
        /// </summary>
        static Type? ScanLoadedAssemblies(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(name);
                if (type != null) return type;
            }

            return null;
        }

        /// <summary>
        /// Resolves a generic/array element *definition* name (no arguments) to a <see cref="Type"/>.
        /// Used only by canonical-name parsing on cold paths, and called without <see cref="_lock"/> held
        /// so the assembly scan cannot stall the registry (0.8.14). It answers null for a name this build
        /// does not have, which is the contract <c>TypeIdentity.TryParseCanonicalName</c> is given.
        /// </summary>
        Type? ResolveDefinitionName(string? definitionName)
        {
            if (string.IsNullOrEmpty(definitionName)) return null;

            lock (_lock)
            {
                if (_byStableName.TryGetValue(definitionName, out var e)) return AsNamedType(e.Type, definitionName);
            }

            var type = Type.GetType(definitionName);
            if (type != null) return type;

            return ScanLoadedAssemblies(definitionName);
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
