using System;
using System.Collections.Generic;
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
    /// </summary>
    public sealed class SchemaRegistry
    {
        sealed class Entry
        {
            public int            Id;
            public SchemaTypeInfo Info = null!;
            public Type           Type;
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
		// the file schema nor decodes schema metadata names.
		byte[] _bakedSchemaBytes = null!;
		byte[] _bakedSchemaStringsBytes = null!;
		ulong _bakedFingerprint;
		Dictionary<Type, int> _bakedTypeToId;
		Dictionary<int, Type> _bakedIdToType;

		/// <summary>True once the schema has been baked and frozen. A frozen bake never goes stale.</summary>
		public bool HasBakedSchema => _bakedSchemaBytes != null;

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
		/// </summary>
		public bool TryGetBakedSchema(out byte[] schemaBytes, out byte[] schemaStrings, out Dictionary<Type, int> typeToId, out Dictionary<int, Type> idToType, out ulong fingerprint)
		{
			lock (_lock)
			{
				schemaBytes = _bakedSchemaBytes;
				schemaStrings = _bakedSchemaStringsBytes;
				typeToId = _bakedTypeToId;
				idToType = _bakedIdToType;
				fingerprint = _bakedFingerprint;
				return _bakedSchemaBytes != null;
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
				_bakedSchemaBytes = null!;
				_bakedSchemaStringsBytes = null!;
				_bakedTypeToId = null!;
				_bakedIdToType = null!;
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
					// Declared root shapes first, in canonical order, so the id space is deterministic.
					foreach (var declared in _declaredRoots.Values)
					{
						if (_byType.ContainsKey(declared)) continue;
						Register(declared, null!);
					}
					_declaredRoots.Clear();

					// Baking walks field/element type names and may register the structural types it
					// discovers, which is why registrations are allowed for its duration.
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
			_bakedTypeToId = SchemaSection.Write(ref schemaBuf, ref schemaOff, this, schemaStrings);

			byte[] strBuf = new byte[256];
			int strOff = 0;
			schemaStrings.Flush(ref strBuf, ref strOff, StringTableCompression.Disabled);

			_bakedSchemaBytes = new byte[schemaOff];
			Array.Copy(schemaBuf, _bakedSchemaBytes, schemaOff);
			_bakedSchemaStringsBytes = new byte[strOff];
			Array.Copy(strBuf, _bakedSchemaStringsBytes, strOff);
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

		public bool IsSealed => _sealedWatermark != -1;

		// ── Registration ─────────────────────────────────────────────────────

		/// <summary>
		/// Called by the generated <c>RobustFormatters.UseRobustFormatters</c> for every
		/// robust-serializable type of the selected context. Safe to call multiple times with the same
		/// type (idempotent).
		/// A <c>null</c> <paramref name="info"/> declares the type structurally/minimally; it never
		/// walks CLR fields.
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

                var entry = new Entry { Id = _nextId++, Info = info, Type = type };

                _byType[type] = entry;

                _byStableName[info.StableName] = entry;

                foreach (var prev in info.PreviousTypeNames)
                    _byStableName.TryAdd(prev, entry);

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
                for (int i = 0; i < names.Length; i++)
                {
                    // Raw bits, so a ulong value above long.MaxValue is representable.
                    enumValues[i] = new SchemaEnumValueInfo(names[i], EnumBits.ToRawBits(values.GetValue(i), type));
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

                var element = type.GetElementType();
                info = new SchemaTypeInfo(
                    stableName: TypeIdentity.GetCanonicalName(type),
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.GenericInstantiation,
                    fields: Array.Empty<SchemaFieldInfo>(),
                    fixedByteSize: 0,
                    enumValues: null!,
                    underlyingTypeName: TypeIdentity.GetCanonicalName(typeof(Array)),
                    argTypeNames: new[] { TypeIdentity.GetCanonicalName(element) });
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
            // (a List<>/Dictionary<>/Queue<>/Stack<> definition plus the element/key/value identities)
            // instead of emitting an empty ordinary-class schema that cannot describe a single byte.
            if (WellKnownTypes.TryResolveCollectionShape(type, out var shapeDef, out var shapeArgs))
            {
                var argNames = new string[shapeArgs!.Length];
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

            info = null!;
            return false;
        }

        /// <summary>
        /// The actionable error for a multidimensional array, which the robust format cannot currently
        /// represent. </summary>
        internal static Exception UnsupportedMultidimensionalArray(Type type, string context)
            => new Ceras.Exceptions.CerasSchemaException(
                $"The multidimensional array type '{TypeIdentity.GetCanonicalName(type)}' (rank {type.GetArrayRank()}) cannot be used with {context}: " +
                "the robust format currently describes only one-dimensional arrays. Use a jagged array " +
                "(T[][]) or flatten it into a one-dimensional array plus explicit dimension fields.");

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
        /// build resolve to null and are interpreted without being registered.
        /// A caller mapping a whole file's id space calls <c>RequireBakedManifest()</c> first.
        /// </summary>
        public bool TryResolveType(string stableName, out Type type)
        {
			if (string.IsNullOrEmpty(stableName))
			{
				type = null!;
				return false;
			}

            // Steps 1-3 consult the registry's own maps, so they run under the lock. Step 4 walks every
            // loaded assembly, which must not happen under it (0.8.14): the bake holds the same lock and
            // calls this, so a scan here would serialise the whole process behind one name lookup.
            lock (_lock)
            {
                // 1. Registered types are keyed by their canonical stable name. This covers generated
                //    closed generics and (after bake) structural arrays/collections.
                if (_byStableName.TryGetValue(stableName, out var e)) { type = e.Type; return true; }
            }

            // 2. Assembly-qualified CLR names resolve directly. This is the legacy/old-save shape
            //    (Type.FullName for closed generics) and genuine external type names.
            type = Type.GetType(stableName);
            if (type != null) return true;

            // 3. Canonical names of closed generics / arrays / pointers that are not registered yet
            //    (e.g. a structural collection discovered while baking the schema). Definitions are
            //    resolved from the framework or loaded assemblies.
            if (TypeIdentity.TryParseCanonicalName(stableName, ResolveDefinitionName, out type))
                return true;

            // 4. Last resort for genuinely-external/old names: an unqualified, non-canonical
            //    Type.FullName or a definition name. Bounded scan of loaded assemblies, unlocked.
            type = ScanLoadedAssemblies(stableName);
            return type != null;
        }

        /// <summary>
        /// Looks a plain CLR name up in the loaded assemblies. Deliberately lock-free: it does not read
        /// the registry, and a miss is not a correctness problem for the caller (a name that is absent
        /// here is absent from the registry too, and <see cref="TryResolveType"/> is a cold path).
        /// </summary>
        static Type? ScanLoadedAssemblies(string name)
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
        /// so the assembly scan cannot stall the registry (0.8.14).
        /// </summary>
        Type? ResolveDefinitionName(string definitionName)
        {
            if (string.IsNullOrEmpty(definitionName)) return null;

            lock (_lock)
            {
                if (_byStableName.TryGetValue(definitionName, out var e)) return e.Type;
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
