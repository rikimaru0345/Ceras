namespace Ceras
{
	using Ceras.Formatters;
	using Resolvers;
	using System;
	using System.Collections.Generic;
	using Exceptions;
	using Helpers;

	/// <summary>
	/// Allows detailed configuration of the <see cref="CerasSerializer"/>. Advanced options can be found inside <see cref="Advanced"/>
	///
	/// <para>
	/// Keep in mind that changes to the config will make previously serialized data incompatible.
	/// Or in other words: when you serialize something with one specific 'SerializerConfig' and you then later change some settings, you won't be able to deserialize the data again.
	/// The SerializerConfig must be configured with the exact same settings!
	/// There are some exceptions where settings-changes won't cause any issues, but they are not exactly common and you shouldn't rely on them.
	/// </para>
	/// 
	/// <para>
	/// For performance reasons <see cref="CerasSerializer"/>, <see cref="SerializerConfig"/>, and <see cref="TypeConfig"/> are not thread-safe!
	/// You shouldn't share a single instance of a SerializerConfig either
	/// </para>
	/// </summary>
	public class SerializerConfig : IAdvancedConfig, ISizeLimitsConfig, IVersionToleranceConfig, IWarningConfig
	{
		bool _isSealed; // todo
		internal bool IsSealed => _isSealed;
		internal void Seal() => _isSealed = true;

		CerasSerializer _claimedBy;
		internal bool Claim(CerasSerializer ceras)
		{
			if(_claimedBy != null)
				return false;
			_claimedBy = ceras;
			return true;
		}



		#region Basic Settings

		/// <summary>
		/// If you want to, you can add all the types you want to serialize to this collection.
		/// When you add at least one Type to this list, Ceras will run in "sealed mode", which does 2 different things:
		/// 
		/// <para>
		/// 1.) It improves performance.
		/// Usually Ceras already only writes the type-name of an object when absolutely necessary. But sometimes you might have 'object' or 'interface' fields, in which case there's simply no way but to embed the type information. And this is where KnownTypes helps: Since the the types (and their order) are known, Ceras can write just a "TypeId" instead of the full name. This can save *a lot* of space and also increases performance (since less data has to be written).
		/// </para>
		///
		/// <para>
		/// 2.) It protects against bugs and exploits.
		/// When a new type (one that is not in the KnownTypes list) is encountered while reading/writing an exception is thrown!
		/// You can be sure that you will never accidentally drag some object that you didn't intend to serialize (protecting against bugs).
		/// It also prevents exploits when using Ceras to send objects over the network, because an attacker can not inject new object-types into your data (which, depending on what you do, could be *really* bad).
		/// </para>
		/// 
		/// By default this prevents new types being added dynamically, but you can change this setting in <see cref="Advanced.SealTypesWhenUsingKnownTypes"/>.
		/// 
		/// <para>Ceras refers to the types by their index in this list!</para>
		/// So for deserialization the same types must be present in the same order! You can however have new types at the end of the list (so you can still load old data, as long as the types that an object was saved with are still present at the expected indices)
		/// 
		/// See the tutorial for more information.
		/// </summary>
		public List<Type> KnownTypes { get; internal set; } = new List<Type>();

		/// <summary>
		/// If your object implement IExternalRootObject they are written as their external ID, so at deserialization-time you need to provide a resolver for Ceras so it can get back the Objects from their IDs.
		/// When would you use this?
		/// There's a lot of really interesting use cases for this, be sure to read the tutorial section 'GameDatabase' even if you're not making a game.
		/// <para>Default: null</para>
		/// </summary>
		public IExternalObjectResolver ExternalObjectResolver { get; set; }

		/// <summary>
		/// If one of the objects in the graph implements IExternalRootObject, Ceras will only write its ID and then call this function. 
		/// That means this external object for which only the ID was written, was not serialized itself. But often you want to sort of "collect" all the elements
		/// that belong into an object-graph and save them at the same time. That's when you'd use this callback. 
		/// Make sure to read the 'GameDatabase' example in the tutorial even if you're not making a game.
		/// <para>Default: null</para>
		/// </summary>
		public Action<IExternalRootObject> OnExternalObject { get; set; } = null;

		/// <summary>
		/// A list of callbacks that Ceras calls when it needs a formatter for some type. The given methods in this list will be tried one after another until one of them returns a IFormatter instance. If all of them return null (or the list is empty) then Ceras will continue as usual, trying the built-in formatters.
		/// </summary>
		public List<FormatterResolverCallback> OnResolveFormatter { get; } = new List<FormatterResolverCallback>();

		/// <summary>
		/// Whether or not to handle object references.
		/// This feature will correctly handle circular references (which would otherwise just crash with a StackOverflowException), but comes at a (very) small performance cost; so turn it off if you know that you won't need it.
		/// <para>Default: true</para>
		/// </summary>
		public bool PreserveReferences { get; set; } = true;

		/// <summary>
		/// If all the other things (ShouldSerializeMember / Attributes) don't produce a decision, then this setting is used to determine if a member should be included.
		/// By default only public fields are serialized. ReadonlyHandling is a separate option found inside <see cref="Advanced"/>
		/// <para>Default: AllPublic</para>
		/// </summary>
		public TargetMember DefaultTargets { get; set; } = TargetMember.AllPublic;

		#endregion

		#region Version Tolerance

		/// <summary>
		/// Sometimes you want to persist objects even while they evolve (fields being added, removed, renamed).
		/// Type changes are not supported (yet, nobody has requested it so far).
		/// Check out the tutorial for more information (and a way to deal with changing types)
		/// </summary>
		public IVersionToleranceConfig VersionTolerance => this;

		VersionToleranceMode _versionToleranceMode = VersionToleranceMode.Disabled;
		VersionToleranceMode IVersionToleranceConfig.Mode
		{
			get => _versionToleranceMode;
			set
			{
				if (_versionToleranceMode == VersionToleranceMode.Disabled && value != VersionToleranceMode.Disabled)
					Advanced.UseReinterpretFormatter = false;
				_versionToleranceMode = value;
			}
		}
		bool IVersionToleranceConfig.VerifySizes { get; set; } = false;
		//bool IVersionToleranceConfig.IncludeFrameworkTypes { get; set; } = false;

		#endregion

		#region Type Configuration

		Dictionary<Type, TypeConfig> _configEntries = new Dictionary<Type, TypeConfig>();
		Dictionary<Type, TypeConfig> _staticConfigEntries = new Dictionary<Type, TypeConfig>();

		// Get a TypeConfig without calling 'OnConfigNewType'
		TypeConfig GetTypeConfigForConfiguration(Type type, bool isStatic = false)
		{
			var configDict = isStatic ? _staticConfigEntries : _configEntries;
			if (configDict.TryGetValue(type, out var typeConfig))
				return typeConfig;

			if (type.ContainsGenericParameters)
				throw new InvalidOperationException("You can not configure 'open' types (like List<>)! Only 'closed' types (like 'List<int>') can be configured statically. For dynamic configuration (which is what you are trying to do) use the 'OnConfigNewType' callback. It will be called for every fully instantiated type.");

			typeConfig = (TypeConfig)Activator.CreateInstance(typeof(TypeConfig<>).MakeGenericType(type),
															   System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
															   null,
															   new object[] { this, isStatic },
															   null);
			configDict.Add(type, typeConfig);

			return typeConfig;
		}

		// Get a TypeConfig for usage, meaning if by now the type has not been configured then
		// use 'OnConfigNewType' as a last chance (or if no callback is set just use the defaults)
		internal TypeConfig GetTypeConfig(Type type, bool isStatic)
		{
			var configDict = isStatic ? _staticConfigEntries : _configEntries;
			if (configDict.TryGetValue(type, out var typeConfig))
				return typeConfig;

			if (type.ContainsGenericParameters)
				return null;

			typeConfig = (TypeConfig)Activator.CreateInstance(typeof(TypeConfig<>).MakeGenericType(type),
															  System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
															  null,
															  new object[] { this, isStatic },
															  null);

			// Let the user handle it
			OnConfigNewType?.Invoke((TypeConfig)typeConfig);

			configDict.Add(type, typeConfig);
			return typeConfig;
		}



		/// <summary>
		/// Use the generic version of <see cref="ConfigType{T}"/> for a much easier API.
		/// <para>
		/// This overload should only be used if you actually don't know the type in advance (for example when dealing with a private type in another assembly)
		/// </para>
		/// </summary>
		public TypeConfig ConfigType(Type type) => GetTypeConfigForConfiguration(type);

		/// <summary>
		/// Configure a static type (or the static part of a type that is not static but has some static members)
		/// </summary>
		public TypeConfig ConfigStaticType(Type type) => GetTypeConfigForConfiguration(type, true);

		/// <summary>
		/// Use this when you want to configure types directly (instead of through attributes, or <see cref="OnConfigNewType"/>). Any changes you make using this method will override any settings applied through attributes on the type.
		/// </summary>
		public TypeConfig<T> ConfigType<T>() => (TypeConfig<T>)GetTypeConfigForConfiguration(typeof(T));


		/// <summary>
		/// Usually you would just put attributes (like <see cref="MemberConfigAttribute"/>) on your types to define how they're serialized. But sometimes you want to configure some types that you don't control (like types from some external library you're using). In that case you'd use <see cref="ConfigType{T}"/>. But sometimes even that doesn't work, for example when some types are private, or too numerous, or generic (so they don't even exist as "closed" / specific types yet); so when you're in a situation like that, you'd use this <see cref="OnConfigNewType"/> to configure a type right when it's used.
		/// <para>
		/// Keep in mind that this callback will only be called when Ceras encounters it for the first time. 
		/// That means it will not get called for any type that you have already configured using <see cref="ConfigType{T}"/>!
		/// </para>
		/// </summary>
		public Action<TypeConfig> OnConfigNewType
		{
			get => _onConfigNewType;
			set
			{
				if (_onConfigNewType == null)
					_onConfigNewType = value;
				else
					throw new InvalidOperationException(nameof(OnConfigNewType) + " is already set. Multiple type configuration callbacks would overwrite each others changes, you must collect all the callbacks into one function to maintain detailed control over how each Type gets configured.");
			}
		}
		Action<TypeConfig> _onConfigNewType;

		#endregion
		
		#region Advanced

		/// <summary>
		/// Advanced options. In here is everything that is very rarely used, dangerous, or otherwise special. 
		/// </summary>
		public IAdvancedConfig Advanced => this;

		ISizeLimitsConfig IAdvancedConfig.SizeLimits => this;
		uint ISizeLimitsConfig.MaxStringLength { get; set; } = uint.MaxValue;
		uint ISizeLimitsConfig.MaxArraySize { get; set; } = uint.MaxValue;
		uint ISizeLimitsConfig.MaxByteArraySize { get; set; } = uint.MaxValue;
		uint ISizeLimitsConfig.MaxCollectionSize { get; set; } = uint.MaxValue;

		Action<object> IAdvancedConfig.DiscardObjectMethod { get; set; } = null;
		ReadonlyFieldHandling IAdvancedConfig.ReadonlyFieldHandling { get; set; } = ReadonlyFieldHandling.ExcludeFromSerialization;
		bool IAdvancedConfig.EmbedChecksum { get; set; } = false;
		bool IAdvancedConfig.PersistTypeCache { get; set; } = false;
		bool IAdvancedConfig.SealTypesWhenUsingKnownTypes { get; set; } = true;
		bool IAdvancedConfig.SkipCompilerGeneratedFields { get; set; } = true;
		ITypeBinder IAdvancedConfig.TypeBinder { get; set; } = new SimpleTypeBinder();
		DelegateSerializationFlags IAdvancedConfig.DelegateSerialization { get; set; } = DelegateSerializationFlags.Off;
		bool IAdvancedConfig.UseReinterpretFormatter { get; set; } = true;
		bool IAdvancedConfig.RespectNonSerializedAttribute { get; set; } = true;
		BitmapMode IAdvancedConfig.BitmapMode { get; set; } = BitmapMode.DontSerializeBitmaps;
		AotMode IAdvancedConfig.AotMode { get; set; } = AotMode.None;

		#endregion
		
		#region Warnings

		/// <summary>
		/// Ceras can detect some common mistakes and notifies you of them by throwing exceptions.
		/// In case you know better, you can disable those exceptions here.
		/// </summary>
		public IWarningConfig Warnings => this;

		bool IWarningConfig.ExceptionWhenUsingDynamicFormatterInAotMode { get; set; } = true;
		bool IWarningConfig.ExceptionOnStructWithAutoProperties { get; set; } = true;

		#endregion
	}


	public interface IAdvancedConfig
	{
		/// <summary>
		/// Set this to a function you provide. Ceras will call it when an object instance is no longer needed.
		/// For example you want to populate an existing object with data, and one of the fields already has a value (a left-over from the last time it was used),
		/// but the current data says that the field should be 'null'. That's when Ceras will call this this method so you can recycle the object (maybe return it to your object-pool)
		/// </summary>
		Action<object> DiscardObjectMethod { get; set; }

		/// <summary>
		/// Explaining this setting here would take too much space, check out the tutorial section for details.
		/// <para>Default: Off</para>
		/// </summary>
		ReadonlyFieldHandling ReadonlyFieldHandling { get; set; }

		/// <summary>
		/// Embed protocol/serializer checksum at the start of any serialized data, and read it back when deserializing to make sure we're not reading incompatible data on accident.
		/// Intended to be used when writing to files, for networking this should not be used (since it would prefix every message with the serializer-checksum which makes no sense)
		/// <para>Default: false</para>
		/// </summary>
		bool EmbedChecksum { get; set; }

		/// <summary>
		/// Determines whether to keep Type-To-Id maps after serialization/deserialization.
		/// This is ***ONLY*** intended for networking, where the deserializer keeps the state as well, and all serialized data is ephemeral (not saved to anywhere)
		/// This will likely save a huge amount of memory and cpu cycles over the lifespan of a network-session, because it will serialize type-information only once.
		/// 
		/// If the serializer is used as a network protocol serializer, this option should definitely be turned on!
		/// Don't use this when serializing to anything persistent (files, database, ...) as you cannot deserialize any data if the deserializer type-cache is not in **EXACTLY**
		/// the same configuration as it (unless you really know exactly what you're doing)
		/// <para>Default: false</para>
		/// </summary>
		bool PersistTypeCache { get; set; }

		/// <summary>
		/// This setting is only used when KnownTypes is used (has >0 entries).
		/// When set to true, and a new Type (so a Type that is not contained in KnownTypes) is encountered in either serialization or deserialization, an exception is thrown.
		/// 
		/// <para>!! Defaults to true to protect against exploits and bugs.</para>
		/// <para>!! Don't disable this unless you know what you're doing.</para>
		///
		/// If you use KnownTypes you're most likely using Ceras in a network-scenario.
		/// If you then turn off this setting, you're basically allowing the other side (client or server) to construct whatever object they want on your side (which is known to be a huge attack vector for networked software).
		///
		/// It also protects against bugs by ensuring you are 100% aware of all the types that get serialized.
		/// You could easily end up including stuff like passwords, usernames, access-keys, ... completely by accident. 
		/// 
		/// The idea is that when someone uses KnownTypes, they have a fixed list of types they want to serialize (to minimize overhead from serializing type names initially),
		/// which is usually done in networking scenarios;
		/// While working on a project you might add more types or add new fields or things like that, and a common mistake is accidentally adding a new type (or even whole graph!)
		/// to the object graph that was not intended; which is obviously extremely problematic (super risky if sensitive stuff gets suddenly dragged into the serialization)
		/// <para>Default: true</para>
		/// </summary>
		bool SealTypesWhenUsingKnownTypes { get; set; }

		/// <summary>
		/// !! Important:
		/// You may believe you know what you're doing when including things compiler-generated fields, but there are tons of other problems you most likely didn't even realize unless you've read the github issue here: https://github.com/rikimaru0345/Ceras/issues/11. 
		/// 
		/// Hint: You may end up including all sorts of stuff like enumerator statemachines, delegates, remanants of 'dynamic' objects, ...
		/// So here's your warning: Don't set this to false unless you know what you're doing.
		/// 
		/// This defaults to true, which means that fields marked as [CompilerGenerated] are skipped without asking your 'ShouldSerializeMember' function (if you have set one).
		/// For 99% of all use cases this is exactly what you want. For more information read the 'readonly properties' section in the tutorial.
		/// <para>Default: true</para>
		/// </summary>
		bool SkipCompilerGeneratedFields { get; set; }

		/// <summary>
		/// A TypeBinder simply converts a 'Type' to a string and back.
		/// It's easy and really useful to provide your own type binder in many situations.
		/// <para>Examples:</para>
		/// <para>- Mapping server objects to client objects</para>
		/// <para>- Shortening / abbreviating type-names to save space and performance</para>
		/// See the readme on github for more information.
		///
		/// <para>Default: new SimpleTypeBinder()</para>
		/// </summary>
		ITypeBinder TypeBinder { get; set; }

		/// <summary>
		/// Protect against malicious input while deserializing by setting size limits for strings, arrays, and collections
		/// </summary>
		ISizeLimitsConfig SizeLimits { get; }

		/// <summary>
		/// This setting allows Ceras to serialize delegates. In order to make it as safe as possible, set it to the lowest setting that works for you.
		/// 'AllowStatic' will only allow serialization of delegates that point to static methods (so no instances / targets).
		/// While 'AllowInstance' will also allow serialization of instance-methods, meaning that the target object will be "pulled into" the serialization as well.
		/// <para>Default: Off</para>
		/// </summary>
		DelegateSerializationFlags DelegateSerialization { get; set; }

		/// <summary>
		/// Allows Ceras to use an extremely fast formatter for so called "blittable" types. Works for single objects as well as arrays! This formatter always uses the native memory layout, does not respect endianness, and does not support version tolerance.
		/// <para>Default: true</para>
		/// </summary>
		bool UseReinterpretFormatter { get; set; }

		/// <summary>
		/// If true, Ceras will skip fields with the '[System.NonSerialized]' attribute
		/// <para>Default: true</para>
		/// </summary>
		bool RespectNonSerializedAttribute { get; set; }

		/// <summary>
		/// Set this to any mode to enable serialization of 'System.Drawing.Bitmap' (only works on .NET Framework, since other platforms don't have access to System.Drawing)
		/// <para>Default: DontSerializeBitmaps</para>
		/// </summary>
		BitmapMode BitmapMode { get; set; }

		/// <summary>
		/// On an AoT platforms (for example Unity IL2CPP) Ceras can not use dynamic code generation. When enabled, Ceras will use reflection for everything where it would otherwise use dynamic code generation. This is slow, but it allows for testing and debugging on those platforms until 
		/// </summary>
		AotMode AotMode { get; set; }
	}

	public interface ISizeLimitsConfig
	{
		/// <summary>
		/// Maximum string length
		/// </summary>
		uint MaxStringLength { get; set; }
		/// <summary>
		/// Maximum size of any byte[] members
		/// </summary>
		uint MaxArraySize { get; set; }
		/// <summary>
		/// Maximum size of any array members (except byte arrays)
		/// </summary>
		uint MaxByteArraySize { get; set; }
		/// <summary>
		/// Maximum number of elements to read for any collection (everything that implements ICollection, so List, Dictionary, ...)
		/// </summary>
		uint MaxCollectionSize { get; set; }
	}

	public interface IVersionToleranceConfig
	{
		/// <summary>
		/// Checkout the documentation for <see cref="VersionToleranceMode.Standard"/>
		/// <para>Default: <see cref="VersionToleranceMode.Disabled"/></para>
		/// </summary>
		VersionToleranceMode Mode { get; set; }

		/// <summary>
		/// When using VersionTolerance, the size of each member is written/read, but this information can also be used to verify if the data has been read correctly.
		/// Turn it on to gain some protection against data-corruption, or leave it off to not pay the performance penalty of doing the check.
		/// <para>Default: false</para>
		/// </summary>
		bool VerifySizes { get; set; }

		/*
		/// <summary>
		/// By default Ceras will not embed any version-relevant data for framework types (types that come from the assemblies 'mscorlib', 'System', 'System.Core').
		/// Activating this will increase the output size by quite a lot.
		/// It's very rare that any of the base types (at least those that you probably serialize) will change. 
		/// <para>Default: false</para>
		/// </summary>
		bool IncludeFrameworkTypes { get; set; }
		*/
	}

	public interface IWarningConfig
	{
		/// <summary>
		/// When enabled, will throw an exception when the serializer is running with <see cref="AotMode.Enabled"/> and is about to create an instance of <see cref="DynamicFormatter"/>
		/// <para>Default: true</para>
		/// </summary>
		bool ExceptionWhenUsingDynamicFormatterInAotMode { get; set; }

		/// <summary>
		/// When a struct is encountered that only consists of auto-properties, it will not be serialized correctly. That is because structs (by default) are serialized by their fields, but auto-properties make it so those fields become marked as 'CompilerGenerated' (preventing them from being part of the serialization).
		/// </summary>
		bool ExceptionOnStructWithAutoProperties { get; set; }
	}


	[Flags]
	public enum DelegateSerializationFlags
	{
		/// <summary>
		/// Throw an exception when trying to serialize a delegate type
		/// </summary>
		Off = 0,
		/// <summary>
		/// Allow delegates as long as they point to static methods
		/// </summary>
		AllowStatic = 1 << 0,
		/// <summary>
		/// Allow delegates even when they include an object reference (that will get serialized as well)
		/// </summary>
		AllowInstance = 1 << 1,
	}


	/// <summary>
	/// Options how Ceras handles readonly fields. Check the description of each entry.
	/// </summary>
	public enum ReadonlyFieldHandling
	{
		/// <summary>
		/// This is the default, Ceras will not serialize/deserialize readonly fields.
		/// </summary>
		ExcludeFromSerialization = 0,

		/// <summary>
		/// Serialize readonly fields normally, but at deserialization time it is expected that an object is already present (so Ceras does not have to change the readonly-field), however Ceras will deserialize the content of the object inside the readonly field.
		/// <para>
		/// Example: An object that has a 'readonly Settings MySettings;' field. Ceras will not change the field itself, but it will serialize and deserialize all the settings values inside.
		/// That's what you often want. But it obviously requires that you either provide an object that already exists (meaning you're using the <see cref="CerasSerializer.Deserialize{T}(ref T, byte[])"/> overload that takes an existing object to overwrite); or that the containing object will put an instance into the readonly field in its constructor.
		///</para>
		/// If the object in the readonly field itself does not match the expected value an exception is thrown.
		/// Keep in mind that this mode will obviously never work with value-types (int, structs, ...), in that case simply use <see cref="ForcedOverwrite"/>.
		/// </summary>
		Members = 1,

		/// <summary>
		/// This mode means pretty much "treat readonly fields exactly the same as normal fields". But since readonly fields can't normally be changed outside the constructor of the object Ceras will use reflection to forcefully overwrite the object field.
		/// </summary>
		ForcedOverwrite = 2,
	}

	[Flags]
	public enum VersionToleranceMode
	{
		/// <summary>
		/// No version tolerance, any name or type change in any serialized type changes will be a breaking change
		/// </summary>
		Disabled = 0,

		/// <summary>
		/// Embed member names and sizes. The most common way version tolerance is implemented.
		/// This mode is equivalent to how version-tolerant most other formats are, for example Json, Xml, and MessagePack all have the same "depth" of version tolerance as this mode.
		/// 
		/// <para>
		/// This makes the serialized data robust against: renaming members, removing members, adding new members!
		/// </para>
		/// <para>
		/// This does not help against: renaming types themselves, changing the type of a member while keeping the name (you'd need to rename the member so it won't conflict with any old data).
		/// </para>
		///
		/// <para>
		/// In order to find and map members again after their name has changed, you need to place the [PreviousName].
		/// </para>
		///
		/// <para>
		/// To get resistance against type-name-changes use the Extended mode (not yet implemented, contact me on GitHub or Discord if you have a need for it)
		/// </para>
		/// </summary>
		Standard = 1,

		/// <summary>
		/// In addition to the <see cref="Standard"/> mode, this also embeds the type names of serialized objects, as well as the types of the members.
		/// <para>
		/// As far as tolerance to changes goes, this mode is literally as good as it gets. Every relevant bit of information is encoded, and old data is converted/upgraded by calling your conversion functions.
		/// </para>
		/// <para>
		/// Warning: This mode is not implemented yet as it's still a bit unclear how users would prefer the conversion process to look like. Get in contact on GitHub or Discord if you need this mode.
		/// </para>
		/// </summary>
		Extended = 2,
	}

	public delegate IFormatter FormatterResolverCallback(CerasSerializer ceras, Type typeToBeFormatted);

	public enum BitmapMode
	{
		DontSerializeBitmaps = 0,
		SaveAsBmp = 1,
		SaveAsPng = 2,
		SaveAsJpg = 3,
	}

	public enum AotMode
	{
		/// <summary>
		/// The default mode, don't do anything special for compatibility with AoT runtimes
		/// </summary>
		None,
		/// <summary>
		/// Enable AoT mode, which disables all dynamic code-gen and instead uses fallback methods.
		/// </summary>
		Enabled,
	}
}