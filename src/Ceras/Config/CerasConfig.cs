namespace Ceras
{
	using Ceras.Formatters;
	using Ceras.Helpers;
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
		/// <summary>Maximum allowed string length. Default: unlimited.</summary>
		public uint MaxStringLength { get; set; } = uint.MaxValue;
		/// <summary>Maximum allowed byte[] size. Default: unlimited.</summary>
		public uint MaxByteArraySize { get; set; } = uint.MaxValue;
		/// <summary>Maximum allowed array size (non-byte arrays). Default: unlimited.</summary>
		public uint MaxArraySize { get; set; } = uint.MaxValue;
		/// <summary>Maximum allowed collection element count. Default: unlimited.</summary>
		public uint MaxCollectionSize { get; set; } = uint.MaxValue;
	}

	/// <summary>
	/// Base configuration shared by <see cref="CerasRawConfig"/> and <see cref="CerasRobustConfig"/>.
	/// Use one of those concrete subclasses — do not instantiate this directly.
	/// </summary>
	public abstract class CerasConfig
	{
		bool _isSealed;
		internal bool IsSealed => _isSealed;
		internal virtual void Seal() => _isSealed = true;

		CerasSerializer _claimedBy;
		internal bool Claim(CerasSerializer ceras)
		{
			if (_claimedBy != null) return false;
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

			typeConfig = (TypeConfig)Activator.CreateInstance(
				typeof(TypeConfig<>).MakeGenericType(type),
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
				null, new object[] { this, isStatic }, null);
			configDict.Add(type, typeConfig);
			return typeConfig;
		}

		// Returns a pre-existing TypeConfig without creating one or firing callbacks.
		internal TypeConfig GetExistingTypeConfig(Type type, bool isStatic)
		{
			var configDict = isStatic ? _staticConfigEntries : _configEntries;
			configDict.TryGetValue(type, out var typeConfig);
			return typeConfig;
		}

		// Get or create a TypeConfig for use, firing OnConfigNewType if needed.
		internal TypeConfig GetTypeConfig(Type type, bool isStatic)
		{
			var configDict = isStatic ? _staticConfigEntries : _configEntries;
			if (configDict.TryGetValue(type, out var typeConfig))
				return typeConfig;

			if (type.ContainsGenericParameters)
				return null;

			typeConfig = (TypeConfig)Activator.CreateInstance(
				typeof(TypeConfig<>).MakeGenericType(type),
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
				null, new object[] { this, isStatic }, null);

			OnConfigNewType?.Invoke(typeConfig);
			configDict.Add(type, typeConfig);
			return typeConfig;
		}

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
		/// Controls which members are serialized by default. Individual types can override this via
		/// <see cref="MemberConfigAttribute"/> or <see cref="ConfigType{T}"/>.
		/// <para>Default: AllFields (public and private)</para>
		/// </summary>
		public TargetMember DefaultTargets { get; set; } = TargetMember.AllFields;

		/// <summary>
		/// Converts between Type and string for type names embedded in serialized data.
		/// Override this to abbreviate type names, remap types between assemblies, etc.
		/// <para>Default: <see cref="SimpleTypeBinder"/></para>
		/// </summary>
		public ITypeBinder TypeBinder { get; set; } = new SimpleTypeBinder();

		/// <summary>
		/// When true, Ceras embeds a protocol checksum at the start of every serialized buffer.
		/// Useful for detecting version mismatches when reading from files or databases.
		/// Do not use for networking (would prefix every message with the checksum).
		/// <para>Default: false</para>
		/// </summary>
		public bool EmbedChecksum { get; set; } = false;

		/// <summary>
		/// When true, fields marked with [System.NonSerialized] are excluded from serialization.
		/// <para>Default: true</para>
		/// </summary>
		public bool RespectNonSerializedAttribute { get; set; } = true;

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
		public IEqualityComparer<object> ObjectCacheComparer { get; set; }

		/// <summary>
		/// Called when Ceras would discard an existing object instance (e.g. when a field that
		/// already has a value is set to null by incoming data). Use this to return objects to a pool.
		/// <para>Default: null</para>
		/// </summary>
		public Action<object> DiscardObjectMethod { get; set; }

		/// <summary>
		/// When false, Ceras throws if it would fall back to the reflection-based formatter.
		/// Useful to ensure source-generated formatters are always used in performance-critical paths.
		/// <para>Default: true</para>
		/// </summary>
		public bool AllowReflectionFallback { get; set; } = true;

		/// <summary>
		/// When true, Ceras throws an exception if a struct contains only auto-properties and no
		/// explicit fields (which would result in an unexpectedly empty serialized representation).
		/// <para>Default: true</para>
		/// </summary>
		public bool WarnOnStructWithAutoProperties { get; set; } = true;

		#endregion
	}
}
