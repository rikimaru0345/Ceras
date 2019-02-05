namespace Ceras
{
	using Ceras.Formatters;
	using Helpers;
	using Resolvers;
	using System;
	using System.Collections.Generic;

	/// <summary>
	/// Allows detailed configuration of the <see cref="CerasSerializer"/>. Advanced options can be found inside <see cref="Advanced"/>
	/// </summary>
	public class SerializerConfig : IAdvancedConfigOptions, ISizeLimitsConfig
	{
		/// <summary>
		/// Add all the types you want to serialize to this collection.
		/// By default, in order to protect you against exploits, when you add at least one "KnownType" Ceras will run in "sealed mode" which means that only the types you have added will be allowed, and when a new type is encountered while reading/writing an exception is thrown.
		/// You can switch this behaviour off, but it very important that you understand that this would be a huge security risk when used in combination with networking!
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
		/// If true, Ceras will skip fields with the '[System.NonSerialized]' attribute
		/// <para>Default: true</para>
		/// </summary>
		public bool RespectNonSerializedAttribute { get; set; } = true;

		/// <summary>
		/// Sometimes you want to persist objects even while they evolve (fields being added, removed, renamed).
		/// Type changes are not supported (yet, nobody has requested it so far).
		/// Check out the tutorial for more information (and a way to deal with changing types)
		/// <para>Default: Disabled</para>
		/// </summary>
		public VersionTolerance VersionTolerance { get; set; } = VersionTolerance.Disabled;

		/// <summary>
		/// If all the other things (ShouldSerializeMember / Attributes) don't produce a decision, then this setting is used to determine if a member should be included.
		/// By default only public fields are serialized. ReadonlyHandling is a separate option found inside <see cref="Advanced"/>
		/// <para>Default: PublicFields</para>
		/// </summary>
		public TargetMember DefaultTargets { get; set; } = TargetMember.PublicFields;



		internal TypeConfiguration TypeConfig = new TypeConfiguration();

		public TypeConfigEntry ConfigType<T>() => ConfigType(typeof(T));
		public TypeConfigEntry ConfigType(Type type) => TypeConfig.GetOrCreate(type);




		/// <summary>
		/// Advanced options. In here is everything that is very rarely used, dangerous, or otherwise special. 
		/// </summary>
		public IAdvancedConfigOptions Advanced => this;

		ISizeLimitsConfig IAdvancedConfigOptions.SizeLimits => this;
		uint ISizeLimitsConfig.MaxStringLength { get; set; } = uint.MaxValue;
		uint ISizeLimitsConfig.MaxArraySize { get; set; } = uint.MaxValue;
		uint ISizeLimitsConfig.MaxByteArraySize { get; set; } = uint.MaxValue;
		uint ISizeLimitsConfig.MaxCollectionSize { get; set; } = uint.MaxValue;
		
		Action<object> IAdvancedConfigOptions.DiscardObjectMethod { get; set; } = null;
		Func<SerializedMember, SerializationOverride> IAdvancedConfigOptions.ShouldSerializeMember { get; set; } = null;
		ReadonlyFieldHandling IAdvancedConfigOptions.ReadonlyFieldHandling { get; set; } = ReadonlyFieldHandling.Off;
		bool IAdvancedConfigOptions.EmbedChecksum { get; set; } = false;
		bool IAdvancedConfigOptions.PersistTypeCache { get; set; } = false;
		bool IAdvancedConfigOptions.SealTypesWhenUsingKnownTypes { get; set; } = true;
		bool IAdvancedConfigOptions.SkipCompilerGeneratedFields { get; set; } = true;
		ITypeBinder IAdvancedConfigOptions.TypeBinder { get; set; } = null;
		DelegateSerializationMode IAdvancedConfigOptions.DelegateSerialization { get; set; } = DelegateSerializationMode.Off;
	}



	public interface IAdvancedConfigOptions
	{
		/// <summary>
		/// Set this to a function you provide. Ceras will call it when an object instance is no longer needed.
		/// For example you want to populate an existing object with data, and one of the fields already has a value (a left-over from the last time it was used),
		/// but the current data says that the field should be 'null'. That's when Ceras will call this this method so you can recycle the object (maybe return it to your object-pool)
		/// </summary>
		Action<object> DiscardObjectMethod { get; set; }

		/// <summary>
		/// This is the very first thing that ceras uses to determine whether or not to serialize something. While not the most comfortable, it is useful because it is called for types you don't control (types from other libraries where you don't have the source code...).
		/// Important: Compiler generated fields are always skipped by default, for more information about that see the 'readonly properties' section in the tutorial where all of this is explained in detail.
		/// </summary>
		Func<SerializedMember, SerializationOverride> ShouldSerializeMember { get; set; }

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
		/// The default type binder (NaiveTypeBinder) simply uses '.FullName'
		/// See the readme on github for more information.
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
		DelegateSerializationMode DelegateSerialization { get; set; }
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

	public enum DelegateSerializationMode
	{
		Off,
		AllowStatic,
		AllowInstance,
	}


	/// <summary>
	/// Options how Ceras handles readonly fields. Check the description of each entry.
	/// </summary>
	public enum ReadonlyFieldHandling
	{
		/// <summary>
		/// By default ceras will ignore readonly fields.
		/// </summary>
		Off = 0,
		/// <summary>
		/// Handle readonly fields the safe way: By serializing and deserializing the inner members of a readonly field. If the field element itself is not as expected, this will throw an exception.
		/// </summary>
		Members = 1,
		/// <summary>
		/// Same as 'Members', but instead of throwing an exception, Ceras will fix the mismatch by force (using reflection). To know what that means and when to use it, check out the tutorial section about readonly handling.
		/// </summary>
		ForcedOverwrite = 2,
	}

	public enum VersionTolerance
	{
		Disabled,
		AutomaticEmbedded,
	}

	public delegate IFormatter FormatterResolverCallback(CerasSerializer ceras, Type typeToBeFormatted);

}