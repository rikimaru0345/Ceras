namespace Ceras
{
	using Ceras.Formatters;
	using Ceras.Helpers;
	using Ceras.Resolvers;
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using IFormatter = Formatters.IFormatter;

	/// <summary>
	/// Callback used to provide a custom formatter for a specific type.
	/// Return null to fall through to the next resolver.
	/// </summary>
	public delegate IFormatter FormatterResolverCallback(CerasSerializer ceras, Type typeToBeFormatted);

	/// <summary>
	/// Size limits used to protect against malicious or corrupt input during deserialization.
	/// </summary>
	public class SizeLimits
	{
		bool _isSealed;
		internal void Seal() => _isSealed = true;
		void ThrowIfSealed() { if (_isSealed) throw new InvalidOperationException("Sealed"); }

		uint _maxStringLength = uint.MaxValue;
		public uint MaxStringLength { get => _maxStringLength; set { ThrowIfSealed(); _maxStringLength = value; } }

		uint _maxByteArraySize = uint.MaxValue;
		public uint MaxByteArraySize { get => _maxByteArraySize; set { ThrowIfSealed(); _maxByteArraySize = value; } }

		uint _maxArraySize = uint.MaxValue;
		public uint MaxArraySize { get => _maxArraySize; set { ThrowIfSealed(); _maxArraySize = value; } }

		uint _maxCollectionSize = uint.MaxValue;
		public uint MaxCollectionSize { get => _maxCollectionSize; set { ThrowIfSealed(); _maxCollectionSize = value; } }

		long _maxStringTableSize = long.MaxValue;

		/// <summary>
		/// The largest decoded string-table region a load accepts, in bytes. Applies to the region as it
		/// is read, so a <see cref="StringTableCompression.Deflate"/> region is bounded by its
		/// <em>decompressed</em> size: a small compressed payload cannot expand without limit. Defaults
		/// to <see cref="long.MaxValue"/>, i.e. the previous unbounded behaviour. Set it when
		/// deserializing untrusted data.
		/// </summary>
		public long MaxStringTableSize
		{
			get => _maxStringTableSize;
			set { ThrowIfSealed(); if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _maxStringTableSize = value; }
		}
	}

	/// <summary>
	/// Base configuration shared by <see cref="CerasRawConfig"/> and <see cref="CerasRobustConfig"/>.
	/// Use one of those concrete subclasses — do not instantiate this directly.
	/// </summary>
	public abstract class CerasConfig : ISealedConfiguration
	{
		public CerasConfig() {
			_advanced = new AdvancedConfig(this);
			// Built here rather than in the property's own initializer: 'this' is not available there,
			// and the list needs its owner to know whether it may still change.
			OnResolveFormatter = new ConfigList<FormatterResolverCallback>(this, nameof(OnResolveFormatter));
		}

		bool _isSealed;
		internal bool IsSealed => _isSealed;
		internal void ThrowIfSealed() { if (_isSealed) throw new InvalidOperationException("CerasConfig is already sealed and cannot be modified."); }

		/// <summary>
		/// The named form, used by the collections a property hands out (<see cref="ConfigList{T}"/>):
		/// a refusal there is just as final as one from a setter, so it names the member the caller
		/// reached for instead of pointing at the whole config.
		/// </summary>
		void ISealedConfiguration.ThrowIfSealed(string what)
		{
			if (!_isSealed) return;
			throw new InvalidOperationException(
				"CerasConfig is already sealed and cannot be modified, so " + what + " cannot be changed now. " +
				"A serializer settles everything it needs from its config once, when it is constructed: its constructor seals the config " +
				"before it resolves a single formatter, and nothing reads the config again afterwards. So a change made now would either " +
				"be ignored for the rest of the process or - the worse case - reach only the part of the work that had not happened yet, " +
				"and the code reading it would have no way to tell which. Configure the config in the 'configure' callback of the " +
				"serializer's constructor, or on the config object itself before you hand it to one.");
		}

		internal virtual void Seal() {
			_isSealed = true;
			SizeLimits.Seal();
			_advanced.Seal();
		}

		CerasSerializer? _claimedBy;

		/// <summary>The serializer that claimed this config, or null while nobody has.</summary>
		internal CerasSerializer? Serializer => _claimedBy;
		internal bool Claim(CerasSerializer ceras)
		{
			if (_claimedBy != null && !ReferenceEquals(_claimedBy, ceras)) return false;
			_claimedBy = ceras;
			return true;
		}

		#region Type Configuration

		Dictionary<Type, TypeConfig> _configEntries = new Dictionary<Type, TypeConfig>();
		Dictionary<Type, TypeConfig> _staticConfigEntries = new Dictionary<Type, TypeConfig>();

		// Get a TypeConfig without calling 'OnConfigNewType' (used during explicit user configuration)
		//
		// 'createTypeConfig' is the generic API's contribution: ConfigType<T> passes a factory that
		// constructs TypeConfig<T> directly. The dictionary above is the only cache either route goes
		// through, so a type configured through one overload is the *same* instance in the other - which
		// is why adding the factory changed no semantics, only how the instance is created on a miss.
		TypeConfig GetTypeConfigForConfiguration(Type type, bool isStatic = false, Func<TypeConfig>? createTypeConfig = null)
		{
			var configDict = isStatic ? _staticConfigEntries : _configEntries;
			if (configDict.TryGetValue(type, out var typeConfig))
				return typeConfig;

			// The type has no configuration yet. The open-generic answer comes first, because it is a
			// statement about the argument rather than about the moment: an open generic was never
			// configurable, and that message names the one door that does work for it. After that, the
			// seal: before it, a missing entry is the ordinary case and the entry is created below;
			// after it, creating one is refused - see the throw for why. The lookup above runs first, so
			// the read side stays open: a type that *was* configured can still be inspected after a
			// serializer exists, and every change to it is refused by TypeConfig rather than here.
			if (type.ContainsGenericParameters)
				throw new InvalidOperationException("You cannot configure open generic types like List<>. Only closed types like List<int> can be configured statically. Use OnConfigNewType for dynamic configuration.");

			if (IsSealed)
				throw new InvalidOperationException(
					$"'{Format.TypeIdentity.GetCanonicalNameOrUnknown(type)}' has no configuration, and this CerasConfig is already sealed, " +
					"so none can be created for it now. A serializer seals the config when it is constructed and settles there what it will " +
					"do with every type it will meet, so a configuration that appears afterwards is either never consulted or consulted only " +
					"for the types that had not been reached yet - which is why it is refused rather than accepted and half-applied. " +
					"Configure the type in the 'configure' callback of the serializer's constructor, or, for a type Ceras only meets at runtime, " +
					"from OnConfigNewType (which is still called for a type discovered after the seal) or with [CerasSerializable], whose " +
					"generated registration configures it up front.");

			// The factory is the shape a NativeAOT compiler can satisfy: naming TypeConfig<T> in code
			// is what makes ILC emit native code for that closed instantiation, and MakeGenericType
			// names no type at all. So an application that reached a type only through ConfigType(Type)
			// used to get
			//   NotSupportedException: This object cannot be invoked because no code was generated
			//   for it: 'Ceras.TypeConfig`1[MyType]..ctor(Ceras.CerasConfig,System.Boolean)'
			// on the first call - always for a value-type argument, which has no shared canonical
			// code to fall back on. One ConfigType<T>() anywhere in the app (the generated
			// registration for a [CerasSerializable] type is exactly that) roots the instantiation,
			// and this reflective branch then binds to real code. See ConfigType(Type)'s docs.
			typeConfig = createTypeConfig != null
				? createTypeConfig()
				// Activator returns object?; the constructed type is known to exist here (it is a closed
				// TypeConfig<> over a type that passed the check above), and a null here would be a broken
				// deployment rather than a runtime condition.
				: (TypeConfig)Activator.CreateInstance(
					typeof(TypeConfig<>).MakeGenericType(type),
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
					null, new object[] { this, isStatic }, null)!;
			configDict.Add(type, typeConfig);
			return typeConfig;
		}

		/// <summary>
		/// The pre-existing config for a type, without creating one or firing callbacks. Null when the type
		/// was never configured - including an open generic, which cannot be configured statically.
		/// </summary>
		internal TypeConfig? GetExistingTypeConfig(Type type, bool isStatic)
		{
			var configDict = isStatic ? _staticConfigEntries : _configEntries;
			configDict.TryGetValue(type, out var typeConfig);
			return typeConfig;
		}

		/// <summary>
		/// The config for a type, created on first use (<c>OnConfigNewType</c> fires then). Null for an open
		/// generic, which cannot be configured statically - the caller decides what that means.
		/// </summary>
		internal TypeConfig? GetTypeConfig(Type type, bool isStatic)
		{
			var configDict = isStatic ? _staticConfigEntries : _configEntries;
			if (configDict.TryGetValue(type, out var typeConfig))
				return typeConfig;

			if (type.ContainsGenericParameters)
				return null;

			// The same NativeAOT consideration as GetTypeConfigForConfiguration: this reflective
			// construction is only sound once the closed TypeConfig<T> has been named statically
			// somewhere, which is what the generic entry point (and the generated registration behind
			// it) does. Deliberately not annotated: this is the resolver's own path for a type it
			// discovered at runtime, and it is reached from library code that a user cannot re-root,
			// so an annotation here would only warn about call sites that have no alternative.
			//
			// Activator returns object?; the constructed type is known to exist here (it is a closed
			// TypeConfig<> over a type that passed the check above), and a null here would be a broken
			// deployment rather than a runtime condition.
			typeConfig = (TypeConfig)Activator.CreateInstance(
				typeof(TypeConfig<>).MakeGenericType(type),
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
				null, new object[] { this, isStatic }, null)!;

			// The one post-seal door that stays open, and only for as long as the callback runs and
			// only for the type it was handed: OnConfigNewType is Ceras asking the application to
			// configure a type it has just discovered, which is the one configuration that cannot have
			// happened earlier - the type did not exist in the config until this line. It is scoped to
			// this one TypeConfig instance rather than to the config, so a callback that reaches for
			// some *other* type - already configured, or discovered later - is refused exactly as it
			// would be outside the callback, and a reference to the type it was handed stops working
			// the moment the callback returns.
			try
			{
				typeConfig.BeginNewTypeCallback();
				OnConfigNewType?.Invoke(typeConfig);
			}
			finally
			{
				typeConfig.EndNewTypeCallback();
			}

			configDict.Add(type, typeConfig);
			return typeConfig;
		}

		/// <summary>
		/// The config for a <em>closed</em> type, which always has one. The formatters and resolvers all hold
		/// closed types (a runtime <see cref="Type"/> from a value, or a constructed generic), so they use
		/// this instead of repeating a null check that cannot happen.
		/// </summary>
		internal TypeConfig GetTypeConfigForClosedType(Type type, bool isStatic)
			=> GetTypeConfig(type, isStatic)
			   ?? throw new InvalidOperationException(
				   $"'{Format.TypeIdentity.GetCanonicalNameOrUnknown(type)}' is an open generic type and cannot be configured; " +
				   "a serializer only works with closed types.");

		/// <summary>
		/// Every explicitly configured type config, instance and static. Internal: the robust context
		/// checks have to inspect what the user configured (a user custom formatter is refused in a
		/// robust context), and nothing else needs the whole set.
		/// </summary>
		internal IEnumerable<TypeConfig> EnumerateConfiguredTypes()
		{
			foreach (var kv in _configEntries)
				yield return kv.Value;
			foreach (var kv in _staticConfigEntries)
				yield return kv.Value;
		}

		// Counts every CustomFormatter assignment, so a whole-config check can be skipped while nothing has
		// changed. The check itself is O(configured types) and runs before each Save/Load, which is not a
		// cost worth paying for a context that configures no custom formatter at all — the common case,
		// and the only one a robust context allows through.
		int _formatterAssignmentCount;
		internal int FormatterAssignmentCount => _formatterAssignmentCount;
		internal void NoteFormatterAssignment() => _formatterAssignmentCount++;

		/// <summary>
		/// Configure a specific type. Use this for types you don't control (external libraries, etc.).
		/// Prefer putting attributes directly on your types when possible.
		/// <para>
		/// Under a trimmed or NativeAOT build, prefer <see cref="ConfigType{T}"/>, and make sure
		/// something calls it for <paramref name="type"/>: this overload builds the config by
		/// constructing the closed generic <c>TypeConfig&lt;T&gt;</c> reflectively, and ILC emits native
		/// code for a closed generic only when some code in the compilation names it statically. A
		/// single <c>ConfigType&lt;T&gt;()</c> for the same type - which the generated registration of a
		/// <c>[CerasSerializable]</c> type already is - roots that instantiation, after which this
		/// overload binds to the same code and produces the same config object.
		/// </para>
		/// <para>
		/// Call this to configure a type, which means before a serializer exists: a serializer seals the
		/// config when it is constructed, and a type with no configuration cannot get one afterwards.
		/// After the seal this still returns the configuration of a type that was configured before it,
		/// so it can be read (<see cref="TypeConfig.Members"/>,
		/// <see cref="TypeConfig.CustomFormatter"/>, <see cref="TypeConfig.Type"/>), but every change to
		/// that configuration is refused by <see cref="TypeConfig"/>.
		/// </para>
		/// </summary>
		[RequiresUnreferencedCode("Ceras constructs the closed 'TypeConfig<T>' for 'type' through MakeGenericType/Activator, and a trimmed or NativeAOT build only has native code for that instantiation if some code constructs it statically. Call ConfigType<T>() for the same type - the generated registration of a [CerasSerializable] type does exactly that - so the instantiation is rooted.")]
		[RequiresDynamicCode("Ceras constructs the closed 'TypeConfig<T>' for 'type' through Activator.CreateInstance, which a NativeAOT build cannot compile into native code unless the instantiation is rooted by a static construction. Call ConfigType<T>() instead.")]
		public TypeConfig ConfigType(Type type) => GetTypeConfigForConfiguration(type);

		/// <summary>
		/// Configure the static members of a type.
		/// <para>
		/// The NativeAOT requirement is the same as <see cref="ConfigType(Type)"/>, and
		/// <see cref="ConfigType{T}"/> satisfies it: both overloads construct the same closed
		/// <c>TypeConfig&lt;T&gt;</c> through the same constructor, and only the resulting config object
		/// differs (the static one is a separate cache entry with the static members). So a single
		/// <c>ConfigType&lt;T&gt;()</c> - the generated registration of a <c>[CerasSerializable]</c> type
		/// already is one - roots what this overload needs, and it then binds to that code.
		/// </para>
		/// <para>
		/// Before and after the seal, this is <see cref="ConfigType(Type)"/> applied to the static
		/// members: reading an existing configuration is still allowed, configuring a type that has
		/// none is still refused.
		/// </para>
		/// </summary>
		[RequiresUnreferencedCode("Ceras constructs the closed 'TypeConfig<T>' for 'type' through MakeGenericType/Activator, and a trimmed or NativeAOT build only has native code for that instantiation if some code constructs it statically. Call ConfigType<T>() for the same type - the generated registration of a [CerasSerializable] type does exactly that - so the instantiation is rooted.")]
		[RequiresDynamicCode("Ceras constructs the closed 'TypeConfig<T>' for 'type' through Activator.CreateInstance, which a NativeAOT build cannot compile into native code unless the instantiation is rooted by a static construction. Call ConfigType<T>() for the same type - the generated registration of a [CerasSerializable] type does exactly that.")]
		public TypeConfig ConfigStaticType(Type type) => GetTypeConfigForConfiguration(type, true);

		/// <summary>
		/// Configure a specific type using the generic API.
		/// <para>
		/// This overload constructs <c>TypeConfig&lt;T&gt;</c> directly rather than through
		/// <c>Activator</c>, which is what makes it usable - and rooting - under NativeAOT: naming the
		/// closed type here is what makes ILC emit native code for it. It is also what makes
		/// <see cref="ConfigType(Type)"/> work there; both hand back the same config object either way.
		/// </para>
		/// <para>
		/// Before and after the seal, this is <see cref="ConfigType(Type)"/> in its AOT-friendly form:
		/// reading an existing configuration is still allowed, configuring a type that has none is
		/// still refused.
		/// </para>
		/// </summary>
		public TypeConfig<T> ConfigType<T>() => (TypeConfig<T>)GetTypeConfigForConfiguration(typeof(T), false, () => new TypeConfig<T>(this, false));

		/// <summary>
		/// Called the first time Ceras encounters a type. Use this to configure types you don't know in advance
		/// (e.g. open generics, private types from external assemblies). Only one callback is allowed —
		/// consolidate all logic into one function.
		/// <para>
		/// Null until assigned: Ceras treats "no callback" and "a callback" as the two states, and fires it
		/// only when there is one.
		/// </para>
		/// <para>
		/// This is the one door that stays open after the config is sealed, and it is deliberately
		/// narrow: the callback is still fired for a type Ceras discovers later, and the
		/// <see cref="TypeConfig"/> it is handed accepts changes <em>for that type, for the duration of
		/// the call</em>. Nothing else does - a type that was configured before the seal stays closed, a
		/// reference kept to the type this callback was handed stops working when the callback returns,
		/// and the callback cannot be assigned twice.
		/// </para>
		/// </summary>
		public Action<TypeConfig>? OnConfigNewType
		{
			get => _onConfigNewType;
			set
			{
				if (_onConfigNewType == null)
					_onConfigNewType = value;
				else
					throw new InvalidOperationException($"{nameof(OnConfigNewType)} is already set. Multiple callbacks would overwrite each other's settings. Consolidate all configuration logic into a single function.");
			}
		}
		Action<TypeConfig>? _onConfigNewType;

		#endregion

		#region Settings

		/// <summary>
		/// List of callbacks tried in order when Ceras needs a formatter for a type.
		/// Return null to fall through to the next callback or Ceras's built-in resolution.
		/// <para>
		/// Changeable only while the config is open: the serializer snapshots the list when it is
		/// constructed, so an entry added afterwards would be ignored for the rest of the process -
		/// which is why every change is refused instead, through the list itself rather than through a
		/// property setter, so that a reference taken before the seal is refused as well. Add your
		/// callbacks in the 'configure' callback of the serializer's constructor (this is what
		/// <c>UseImmutableFormatters</c> and the source generator's registration do).
		/// </para>
		/// </summary>
		public ConfigList<FormatterResolverCallback> OnResolveFormatter { get; }

		/// <summary>
		/// Converts between Type and string for type names embedded in serialized data.
		/// Override this to abbreviate type names, remap types between assemblies, etc.
		/// <para>Default: <see cref="SimpleTypeBinder"/></para>
		/// <para>
		/// Never null, and a null assignment is refused. The property is declared non-nullable, so this
		/// enforces the declared contract rather than adding a policy; the reason it is worth enforcing
		/// is that the three places which resolve a stored type name each treated a null binder as a
		/// different thing, so one config with a null binder answered the same string three ways within
		/// a single load. The setter's message names the three, and what to assign instead.
		/// </para>
		/// </summary>
		ITypeBinder _typeBinder = new SimpleTypeBinder();
		public ITypeBinder TypeBinder {
			get => _typeBinder;
			set
			{
				ThrowIfSealed();
				if (value == null)
					throw new ArgumentNullException(nameof(value),
						"CerasConfig.TypeBinder cannot be null. A type binder is how a stored type name is resolved back to a CLR type, and the " +
						"three places that resolve one disagree about a null, so one config carrying it answers the same name three ways inside a " +
						"single load: the manifest's registry reads it as 'this context has no binder' (SchemaRegistry.SetTypeBinder), which also " +
						"silently skips the check that a binder's name does not shadow a manifest entry " +
						"(SchemaRegistry.VerifyBinderNamesDoNotShadowManifest); SchemaTraversalEngine substitutes a private SimpleTypeBinder; and " +
						"CerasRobustSerializer.BuildIdToType dereferences it and reports the resulting NullReferenceException as if it were a schema " +
						"problem, naming neither this property nor the fix. To configure no binder at all, leave TypeBinder unset - the default " +
						"SimpleTypeBinder is the answer. To bind only some names, assign an ITypeBinder that answers for those and behaves as " +
						"ITypeBinder documents for the rest. (A hand-built SchemaRegistry in a test does accept a null binder: it is 'this context has " +
						"no binder', which is a state of the registry and not of a configured serializer.)");
				_typeBinder = value;
			}
		}

		/// <summary>
		/// When true, fields marked with [System.NonSerialized] are excluded from serialization.
		/// <para>Default: true</para>
		/// </summary>
		bool _respectNonSerializedAttribute = true;
		public bool RespectNonSerializedAttribute { 
			get => _respectNonSerializedAttribute; 
			set { ThrowIfSealed(); _respectNonSerializedAttribute = value; } 
		}

		/// <summary>
		/// Size limits to protect against malicious or corrupt input during deserialization.
		/// All limits default to unlimited; set them when deserializing untrusted data.
		/// </summary>
		public SizeLimits SizeLimits { get; } = new SizeLimits();

		/// <summary>
		/// Decides which values the object reference cache treats as "the same value": a value
		/// whose id is already in the cache is written as a back-reference, so the comparer
		/// controls whether two occurrences share one deserialized object.
		/// <para>Default: null, which means reference identity
		/// (<c>System.Collections.Generic.ReferenceEqualityComparer.Instance</c>). Your types'
		/// own <c>Equals</c>/<c>GetHashCode</c> are never consulted.</para>
		/// <para>
		/// Setting a comparer is a deliberate trade, and an identity-losing one: a comparer that
		/// calls <c>Equals</c> makes a second, merely-equal instance collapse into a
		/// back-reference to the first, so it is read back as that first object rather than as a
		/// copy of itself. That is only what you want if equal instances are interchangeable by
		/// definition - an interned or immutable value object, say - because then the loaded graph
		/// still means the same thing and the save is smaller. It is not what you want for a type
		/// whose instances are distinct entities that merely compare equal, and a comparer whose
		/// hash changes while a value is cached additionally breaks the id bookkeeping (the value
		/// stops being found, and is written a second time as if it were new). Leave it null
		/// unless you are sure about that.
		/// </para>
		/// </summary>
		IEqualityComparer<object>? _objectCacheComparer;
		public IEqualityComparer<object>? ObjectCacheComparer {
			get => _objectCacheComparer; 
			set { ThrowIfSealed(); _objectCacheComparer = value; } 
		}

		/// <summary>
		/// When false, Ceras throws if it would fall back to the reflection-based formatter.
		/// Useful to ensure source-generated formatters are always used in performance-critical paths.
		/// <para>Default: true</para>
		/// </summary>
		bool _allowReflectionFallback = true;
		public bool AllowReflectionFallback { 
			get => _allowReflectionFallback; 
			set { ThrowIfSealed(); _allowReflectionFallback = value; } 
		}

		/// <summary>
		/// When true, Ceras throws an exception if a struct contains only auto-properties and no
		/// explicit fields (which would result in an unexpectedly empty serialized representation).
		/// <para>Default: true</para>
		/// </summary>
		bool _warnOnStructWithAutoProperties = true;
		public bool WarnOnStructWithAutoProperties { 
			get => _warnOnStructWithAutoProperties; 
			set { ThrowIfSealed(); _warnOnStructWithAutoProperties = value; } 
		}

		protected readonly AdvancedConfig _advanced;
		public AdvancedConfig Advanced => _advanced;

		public class AdvancedConfig {
			CerasConfig _config;
			internal AdvancedConfig(CerasConfig config) { _config = config; }

			bool _isSealed;
			internal void Seal() => _isSealed = true;

			public bool PersistTypeCache {
				get => _persistTypeCache;
				set { if (_isSealed) throw new InvalidOperationException("Sealed"); _persistTypeCache = value; }
			}
			bool _persistTypeCache;
		}

		#endregion
	}

	/// <summary>
	/// Configuration that accepts changes while it is open and refuses them once it is sealed.
	/// <para>
	/// The collections a configuration hands out ask through this rather than checking a flag of their
	/// own, for one reason: a property that returned a plain <see cref="List{T}"/> could not take that
	/// reference back. A caller who read the property before the seal kept a list that still accepted
	/// changes after it - and for a collection like the resolver list, a change that is accepted and
	/// then ignored is the worst of the available outcomes, because the code that made it reads as
	/// though it took effect. The guarded list keeps the same instance for its whole life, so refusing
	/// is something the list does rather than something the property asked for once.
	/// </para>
	/// </summary>
	internal interface ISealedConfiguration
	{
		/// <summary>
		/// Refuses a change, naming <paramref name="what"/> the thing being changed, so the message
		/// points at the member the caller reached for. Returns normally while the configuration is open.
		/// </summary>
		void ThrowIfSealed(string what);
	}

	/// <summary>
	/// The list behind a configuration setting, e.g. <see cref="CerasConfig.OnResolveFormatter"/>:
	/// <see cref="IList{T}"/> and <see cref="IReadOnlyList{T}"/> over the real list, accepting every
	/// change while the configuration that owns it is open and refusing every change once that
	/// configuration is sealed.
	/// <para>
	/// It is deliberately not a <see cref="List{T}"/>, and cannot be one. A property that hands out
	/// the raw list hands out a reference that sealing cannot revoke, and <see cref="List{T}"/> cannot
	/// be intercepted to change that - its methods are not virtual. So the guard has to live in a
	/// different type, which is also why casting one of these back to <see cref="List{T}"/> fails
	/// instead of quietly producing an unguarded list.
	/// </para>
	/// <para>
	/// Reading is never refused, before or after the seal: enumeration, <see cref="Count"/>, the indexer
	/// and <see cref="Contains(T)"/> are how a configuration is inspected, and inspecting a serializer's
	/// configuration after it exists is legitimate. <see cref="IsReadOnly"/> reports false, like every
	/// writable property on a config does: the list <em>is</em> writable, up until the moment it is not.
	/// </para>
	/// </summary>
	public sealed class ConfigList<T> : IList<T>, IReadOnlyList<T>
	{
		readonly List<T> _items = new List<T>();
		readonly ISealedConfiguration _owner;
		readonly string _name;

		internal ConfigList(ISealedConfiguration owner, string name)
		{
			_owner = owner;
			_name = name;
		}

		// Asked before the change, never after: a refused change must leave the list exactly as it was,
		// and "remove the item that was not there" is still a refusal.
		void Guard() => _owner.ThrowIfSealed(_name);

		public int Count => _items.Count;
		public bool IsReadOnly => false;

		public T this[int index]
		{
			get => _items[index];
			set { Guard(); _items[index] = value; }
		}

		public void Add(T item) { Guard(); _items.Add(item); }
		public void Clear() { Guard(); _items.Clear(); }
		public bool Contains(T item) => _items.Contains(item);
		public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
		public int IndexOf(T item) => _items.IndexOf(item);
		public void Insert(int index, T item) { Guard(); _items.Insert(index, item); }
		public bool Remove(T item) { Guard(); return _items.Remove(item); }
		public void RemoveAt(int index) { Guard(); _items.RemoveAt(index); }

		/// <summary>
		/// The underlying list's struct enumerator, so a <c>foreach</c> over this does not box it - the
		/// same reason <see cref="List{T}"/> exposes its own.
		/// </summary>
		public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();
		IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();
		IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();
	}
}
