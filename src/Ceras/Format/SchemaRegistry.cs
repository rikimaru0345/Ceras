using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Ceras.Formatters;

namespace Ceras.Format
{
    /// <summary>
    /// Runtime registry of all types registered via <c>[CerasSerializable(VersionTolerant = true)]</c>.
    /// Populated during startup by <c>RobustFormatters.UseRobustFormatters(config)</c>.
    /// </summary>
    public sealed class SchemaRegistry
    {
        sealed class Entry
        {
            public int            Id;
            public SchemaTypeInfo Info;
            public Type           Type;
        }

        readonly Dictionary<Type,   Entry>  _byType       = new();
        readonly Dictionary<string, Entry>  _byStableName = new();
		int _nextId = WellKnownTypes.UserTypeBase;
		int _sealedWatermark = -1;
		ulong? _fingerprint = null;
		readonly object _lock = new();

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
				_fingerprint = null;
            }
        }

		/// <summary>
		/// Freezes the current set of registered types as the "declared" schema.
		/// Only types registered before this call contribute to the fingerprint.
		/// Subsequent registrations (e.g. discovered generic instantiations) are
		/// included in the schema block but do not change the fingerprint.
		/// </summary>
		public void Seal() {
			lock (_lock) {
				if (IsSealed) return;
				_sealedWatermark = _nextId;
				_fingerprint = null; // force recompute
			}
		}

		public bool IsSealed => _sealedWatermark != -1;

		// ── Registration ─────────────────────────────────────────────────────

		/// <summary>
		/// Called by the generated <c>RobustFormatters.UseRobustFormatters</c> for every
		/// <c>[CerasSerializable(VersionTolerant = true)]</c> type.
		/// Safe to call multiple times with the same type (idempotent).
		/// </summary>
		public void Register(Type type, SchemaTypeInfo info)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            lock (_lock)
            {
                if (_byType.ContainsKey(type))
                    return; // idempotent

                if (info == null)
                {
                    if (WellKnownTypes.IsWellKnown(type)) return;
                    info = CreateSchemaForUserType(type);
                }

                var entry = new Entry { Id = _nextId++, Info = info, Type = type };

                _byType[type] = entry;

                _byStableName[info.StableName] = entry;

                foreach (var prev in info.PreviousTypeNames)
                    _byStableName.TryAdd(prev, entry);

                _fingerprint = null; // invalidate
            }
        }

        // ── Lookup ────────────────────────────────────────────────────────────

        public bool TryGetId(Type type, out int id)
        {
            lock (_lock)
            {
                if (_byType.TryGetValue(type, out var e)) { id = e.Id; return true; }

				// Automatic registration for types encountered at runtime (e.g. generic instantiations)
				if (type != null && !WellKnownTypes.IsWellKnown(type))
                {
                    EnsureRegisteredRecursive(type);
                    if (_byType.TryGetValue(type, out e)) { id = e.Id; return true; }
                }

                id = -1;
                return false;
            }
        }

        void EnsureRegisteredRecursive(Type type)
        {
            if (type == null || WellKnownTypes.IsWellKnown(type) || _byType.ContainsKey(type))
                return;

            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                // Register arguments first
                foreach (var arg in type.GetGenericArguments())
                    EnsureRegisteredRecursive(arg);

                // Create and register schema for this instantiation
                var info = CreateSchemaForGeneric(type);
                Register(type, info);
            }
            else
            {
                // Non-generic types must be pre-registered by the source-generated UseRobustFormatters()
                // before SchemaSection.Write runs. If we reach here after Seal(), the type was
                // never registered at startup — its ID won't be in the schema section, so the
                // stream would be unreadable. Throw now (at save time) with a clear message
                // rather than silently writing corrupt data that fails to load.
                //
                // Exception: open generic type definitions (e.g. IEnumerable<>, IDictionary<,>)
                // are only referenced for schema field-type metadata by SchemaSection.ResolveId,
                // never as actual values in the data stream — so dynamic registration is fine.
                if (IsSealed && !type.IsGenericTypeDefinition)
                {
                    // If we're already sealed, we can't add new entries to the "declared" schema (the fingerprint part).
                    // But we can still register them as "runtime discovered" types.
                }

                Register(type, null);
            }
        }

        static SchemaTypeInfo CreateSchemaForUserType(Type type)
        {
            string typeFullName = type.FullName ?? type.Name;

            if (type.IsEnum)
            {
                var names = Enum.GetNames(type);
                var values = Enum.GetValues(type);
                var enumValues = new SchemaEnumValueInfo[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    var val = values.GetValue(i);
                    long longVal = Convert.ToInt64(val);
                    enumValues[i] = new SchemaEnumValueInfo(names[i], longVal);
                }

                return new SchemaTypeInfo(
                    stableName: typeFullName,
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.Enum,
                    fields: null,
                    fixedByteSize: 0,
                    enumValues: enumValues,
                    underlyingTypeName: Enum.GetUnderlyingType(type).FullName);
            }

            // Simple description for types that weren't source-generated but need to be robustly handled.
            // We use the full name as the stable name to avoid collisions.
            var fields = new List<SchemaFieldInfo>();
            foreach (var f in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (f.GetCustomAttribute<ExcludeAttribute>() != null) continue;
                if (f.GetCustomAttribute<NonSerializedAttribute>() != null) continue;

                if (BannedTypes.IsBanned(f.FieldType))
                {
                    if (f.GetCustomAttribute<IncludeAttribute>() == null)
                    {
                        continue;
                    }
                }

                var sa = f.GetCustomAttribute<CerasStableNameAttribute>();
                string stableName = sa?.Name ?? f.Name;
                if (sa == null && stableName.StartsWith("<") && stableName.EndsWith(">k__BackingField"))
                    stableName = stableName.Substring(1, stableName.Length - 17);

                string fieldTypeName = f.FieldType.FullName ?? f.FieldType.Name;

				bool isMonomorphic = f.FieldType.IsValueType || f.FieldType.IsSealed;
                if (f.GetCustomAttribute<MonomorphicAttribute>() != null) isMonomorphic = true;
                if (f.GetCustomAttribute<PolymorphicAttribute>() != null) isMonomorphic = false;

                fields.Add(new SchemaFieldInfo(stableName, new string[0], fieldTypeName, isMonomorphic));
            }

            return new SchemaTypeInfo(
                stableName: typeFullName,
                previousTypeNames: Array.Empty<string>(),
                kind: type.IsValueType ? SchemaTypeKind.Struct : SchemaTypeKind.Class,
                fields: fields.ToArray(),
                fixedByteSize: 0);
        }

        static SchemaTypeInfo CreateSchemaForGeneric(Type type)
        {
            string typeFullName = type.FullName ?? type.Name;
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            var argNames = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                argNames[i] = args[i].FullName ?? args[i].Name;
            }

            var fields = new List<SchemaFieldInfo>();
            foreach (var f in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (f.GetCustomAttribute<ExcludeAttribute>() != null) continue;
                if (f.GetCustomAttribute<NonSerializedAttribute>() != null) continue;

                if (BannedTypes.IsBanned(f.FieldType))
                {
                    if (f.GetCustomAttribute<IncludeAttribute>() == null) continue;
                }

                var sa = f.GetCustomAttribute<CerasStableNameAttribute>();
                string stableName = sa?.Name ?? f.Name;
                if (sa == null && stableName.StartsWith("<") && stableName.EndsWith(">k__BackingField"))
                    stableName = stableName.Substring(1, stableName.Length - 17);

                string fieldTypeName = f.FieldType.FullName ?? f.FieldType.Name;

                bool isMonomorphic = f.FieldType.IsValueType || f.FieldType.IsSealed;
                if (f.GetCustomAttribute<MonomorphicAttribute>() != null) isMonomorphic = true;
                if (f.GetCustomAttribute<PolymorphicAttribute>() != null) isMonomorphic = false;

                fields.Add(new SchemaFieldInfo(stableName, new string[0], fieldTypeName, isMonomorphic));
            }

            return new SchemaTypeInfo(
                stableName: typeFullName,
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.GenericInstantiation,
                fields: fields.ToArray(),
                fixedByteSize: 0,
                enumValues: null,
                underlyingTypeName: def.FullName ?? def.Name,
                argTypeNames: argNames);
        }

        public bool TryGetInfo(Type type, out SchemaTypeInfo info)
        {
            lock (_lock)
            {
                if (_byType.TryGetValue(type, out var e)) { info = e.Info; return true; }
                info = null;
                return false;
            }
        }

        /// <summary>
        /// Resolves a stable name (or previous type name) to the current registered <see cref="Type"/>.
        /// Used by the migration engine when reading old schema sections.
        /// </summary>
        public bool TryResolveType(string stableName, out Type type)
        {
			if (string.IsNullOrEmpty(stableName))
			{
				type = null;
				return false;
			}

            lock (_lock)
            {
                if (_byStableName.TryGetValue(stableName, out var e)) { type = e.Type; return true; }

				type = Type.GetType(stableName);
				if (type != null) return true;

				// Fallback: search all assemblies for a type with this name.
				// This is slow, but only happens once per unknown type during schema write/read.
				// TODO: can we find a better way? maybe while iterating save all types? hmm, no that'd take even longer
				foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
					type = assembly.GetType(stableName);
					if (type != null) return true;
				}

				type = null;
                return false;
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
		/// Computes a stable fingerprint over all registered schemas.
		/// Cached after the first call. Invalidated whenever a new type is registered.
		/// If <see cref="Seal()"/> has been called, only types registered before the seal
		/// contribute to the fingerprint.
		/// </summary>
		public ulong ComputeFingerprint()
        {
            lock (_lock)
            {
                if (_fingerprint.HasValue)
                    return _fingerprint.Value;

                var sorted = new List<Entry>(_byType.Count);
                foreach (var e in _byType.Values) {
					// If sealed, only fingerprint entries below the watermark.
					// This way we exclude discovered generic instantiations.
					if (_sealedWatermark != -1 && e.Id >= _sealedWatermark)
						continue;

					sorted.Add(e);
				}

				sorted.Sort((a, b) => string.Compare(
                    a.Info.StableName, b.Info.StableName, StringComparison.Ordinal));

                uint h1 = 0, h2 = 0x9e3779b9u;
                foreach (var entry in sorted)
                {
                    var info = entry.Info;
                    h1 = Combine(h1, Hash(info.StableName));
                    h2 = Combine(h2, (uint)info.Kind);

					if (info.Fields != null)
                    {
                        foreach (var f in info.Fields)
                        {
                            h1 = Combine(h1, Hash(f.StableName));
                            h2 = Combine(h2, Hash(f.TypeName));
                        }
                    }

                    if (info.EnumValues != null)
                    {
                        foreach (var ev in info.EnumValues)
                        {
                            h1 = Combine(h1, Hash(ev.Name));
                            h2 = Combine(h2, (uint)ev.Value);
                        }
                    }
                }

                _fingerprint = ((ulong)h1 << 32) | h2;
                return _fingerprint.Value;
			}
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
