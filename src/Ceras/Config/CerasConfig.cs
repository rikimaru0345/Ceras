namespace Ceras
{
	using Ceras.Formatters;
	using Ceras.Helpers;
	using Ceras.Resolvers;
	using System;
	using System.Collections.Generic;
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
	public abstract class CerasConfig
	{
		public CerasConfig() {
			_advanced = new AdvancedConfig(this);
		}

		bool _isSealed;
		internal bool IsSealed => _isSealed;
		internal void ThrowIfSealed() { if (_isSealed) throw new InvalidOperationException("CerasConfig is already sealed and cannot be modified."); }
		internal virtual void Seal() {
			_isSealed = true;
			SizeLimits.Seal();
			_advanced.Seal();
		}

		CerasSerializer _claimedBy;
		internal CerasSerializer Serializer => _claimedBy;
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
		TypeConfig GetTypeConfigForConfiguration(Type type, bool isStatic = false)
		{
			var configDict = isStatic ? _staticConfigEntries : _configEntries;
			if (configDict.TryGetValue(type, out var typeConfig))
				return typeConfig;

			if (type.ContainsGenericParameters)
				throw new InvalidOperationException("You cannot configure open generic types like List<>. Only closed types like List<int> can be configured statically. Use OnConfigNewType for dynamic configuration.");

			// Activator returns object?; the constructed type is known to exist here (it is a closed
			// TypeConfig<> over a type that passed the check above), and a null here would be a broken
			// deployment rather than a runtime condition.
			typeConfig = (TypeConfig)Activator.CreateInstance(
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

			// Activator returns object?; the constructed type is known to exist here (it is a closed
			// TypeConfig<> over a type that passed the check above), and a null here would be a broken
			// deployment rather than a runtime condition.
			typeConfig = (TypeConfig)Activator.CreateInstance(
				typeof(TypeConfig<>).MakeGenericType(type),
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
				null, new object[] { this, isStatic }, null)!;

			OnConfigNewType?.Invoke(typeConfig);
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
		/// Configure a specific type. Use this for types you don't control (external libraries, etc.).
		/// Prefer putting attributes directly on your types when possible.
		/// </summary>
		public TypeConfig ConfigType(Type type) => GetTypeConfigForConfiguration(type);

		/// <summary>
		/// Configure the static members of a type.
		/// </summary>
		public TypeConfig ConfigStaticType(Type type) => GetTypeConfigForConfiguration(type, true);

		/// <summary>
		/// Configure a specific type using the generic API.
		/// </summary>
		public TypeConfig<T> ConfigType<T>() => (TypeConfig<T>)GetTypeConfigForConfiguration(typeof(T));

		/// <summary>
		/// Called the first time Ceras encounters a type. Use this to configure types you don't know in advance
		/// (e.g. open generics, private types from external assemblies). Only one callback is allowed —
		/// consolidate all logic into one function.
		/// </summary>
		public Action<TypeConfig> OnConfigNewType
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
		Action<TypeConfig> _onConfigNewType;

		#endregion

		#region Settings

		/// <summary>
		/// List of callbacks tried in order when Ceras needs a formatter for a type.
		/// Return null to fall through to the next callback or Ceras's built-in resolution.
		/// </summary>
		public List<FormatterResolverCallback> OnResolveFormatter { get; } = new List<FormatterResolverCallback>();

		/// <summary>
		/// Converts between Type and string for type names embedded in serialized data.
		/// Override this to abbreviate type names, remap types between assemblies, etc.
		/// <para>Default: <see cref="SimpleTypeBinder"/></para>
		/// </summary>
		ITypeBinder _typeBinder = new SimpleTypeBinder();
		public ITypeBinder TypeBinder { 
			get => _typeBinder; 
			set { ThrowIfSealed(); _typeBinder = value; } 
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
		/// Custom equality comparer for the object reference cache.
		/// Only needed if your types override object equality in unusual ways.
		/// <para>Default: null (uses default reference equality)</para>
		/// </summary>
		IEqualityComparer<object> _objectCacheComparer;
		public IEqualityComparer<object> ObjectCacheComparer { 
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
}
