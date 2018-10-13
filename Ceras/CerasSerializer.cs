// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Ceras
{
	using Exceptions;
	using Formatters;
	using Helpers;
	using Resolvers;
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Linq;
	using System.Reflection;
	using System.Text;

	// Todo: 
	/*
		- write out specific type. only needed when the type of the current value is different (more specific) than the field type
		->  automatic discovery of classes is too brittle, we'll provide a way to add a map of known types
	 
		- overwrite objects/reuse/object pooling
			a) always new()
			b) use object that exists, then fallback to factory method

		let the user specify where to use caching exactly, what types get objectCached always, ..., what fields
		sometimes it makes sense to ref-serialize arrays and collections as well

		omit type information (serialize as "infer from target" code) if the specific type is exactly equal to the target type

		todo: change all internal IDs to uint (object cache, external object, ...)
	*/
	public class CerasSerializer
	{
		internal readonly SerializerConfig Config;
		public ProtocolChecksum ProtocolChecksum { get; } = new ProtocolChecksum();
		
		// A special resolver. It creates instances of the "dynamic formatter", the DynamicObjectFormatter<> is a type that uses dynamic code generation to create efficient read/write methods
		// for a given object type.
		readonly IFormatterResolver _dynamicResolver;

		// The user provided resolver, will always be queried first
		readonly FormatterResolverCallback _userResolver;

		// todo: allow the user to provide their own binder. So they can serialize a type-name however they want; but then again they could override the TypeFormatter anyway, so what's the point? maybe it would be best to completely remove the typeBinder (merging it into the default TypeFormatter)?
		internal readonly ITypeBinder TypeBinder = new NaiveTypeBinder();

		// The primary list of resolvers. A resolver is a class that somehow (by instantiating, or finding it somewhere, ...) comes up with a formatter for a requested type
		// If a resolver can't fulfill the request for a specific formatter, it returns null.
		List<IFormatterResolver> _resolvers = new List<IFormatterResolver>();

		// The specific formatters we have. For example a formatter that knows how to read/write 'List<int>'. This will never contain
		// unspecific formatters (for example for types like 'object' or 'List<>')
		Dictionary<Type, IFormatter> _referenceFormatters = new Dictionary<Type, IFormatter>();
		Dictionary<Type, IFormatter> _specificFormatters = new Dictionary<Type, IFormatter>();


		IFormatter<Type> _typeFormatter;


		readonly FactoryPool<InstanceData> _instanceDataPool;
		readonly Stack<InstanceData> _recursionStack = new Stack<InstanceData>();
		internal InstanceData InstanceData;
		int _recursionDepth = 0;
		RecursionMode _mode = RecursionMode.Idle; // while in one mode we cannot enter the others


		public CerasSerializer(SerializerConfig config = null)
		{
			Config = config ?? new SerializerConfig();

			// Check if the config is even valid
			if (Config.EmbedChecksum && !Config.GenerateChecksum)
				throw new InvalidOperationException($"{nameof(Config.GenerateChecksum)} must be true if {nameof(Config.EmbedChecksum)} is true!");
			if (Config.EmbedChecksum && Config.PersistTypeCache)
				throw new InvalidOperationException($"You can't have '{nameof(Config.EmbedChecksum)}' and also have '{nameof(Config.PersistTypeCache)}' because adding new types changes the checksum. You can use '{nameof(Config.GenerateChecksum)}' alone, but the checksum might change after every serialization call...");

			if (Config.ExternalObjectResolver == null)
				Config.ExternalObjectResolver = new ErrorResolver();


			_userResolver = Config.OnResolveFormatter;

			// Int, Float, Enum, String
			_resolvers.Add(new PrimitiveResolver(this));

			_resolvers.Add(new ReflectionTypesFormatterResolver(this));
			_resolvers.Add(new KeyValuePairFormatterResolver(this));
			_resolvers.Add(new CollectionFormatterResolver(this));

			// DateTime, Guid
			_resolvers.Add(new BclFormatterResolver());

			// DynamicObjectResolver is a special case, so it is not in the resolver-list
			// That is because we only want to have specific resolvers in the resolvers-list
			_dynamicResolver = new DynamicObjectFormatterResolver(this);

			// Type formatter is the basis for all complex objects
			var typeFormatter = new TypeFormatter(this);
			_specificFormatters.Add(typeof(Type), typeFormatter);

			//if (Config.KnownTypes.Count > 0)
			//	if (Config.SealTypesWhenUsingKnownTypes)
			//		typeFormatter.Seal();
			
			_typeFormatter = (IFormatter<Type>)GetSpecificFormatter(typeof(Type));


			//
			// Basic setup is done
			// Now we're adding our "known types"
			// generating serializers and updating the protocol checksum
			//

			Config.KnownTypes.Seal();
			foreach (var t in Config.KnownTypes)
			{
				if (Config.GenerateChecksum)
				{
					ProtocolChecksum.Add(t.FullName);

					if (t.IsEnum)
					{
						// Enums are a special case, they are classes internally, but they only have one field ("__value")
						// We're always serializing them in binary with their underlying type, so there's no reason changes like Adding/Removing/Renaming
						// enum-members could ever cause any binary incompatibility
						//
						// A change in the base-type however WILL cause problems!
						//
						// Note, even without this if() everything would be ok, since the code below also writes the field-type
						// but it is better to *explicitly* do this here and explain why we're doing it!
						ProtocolChecksum.Add(t.GetEnumUnderlyingType().FullName);

						continue;
					}


					var fields = DynamicObjectFormatter<object>.GetSerializableFields(t);
					foreach (var f in fields)
					{
						ProtocolChecksum.Add(f.FieldType.FullName);
						ProtocolChecksum.Add(f.Name);

						foreach (var a in f.GetCustomAttributes(true))
							ProtocolChecksum.Add(a.ToString());
					}
				}
			}

			if (Config.GenerateChecksum)
				ProtocolChecksum.Finish();

			//
			// Finally we need "instance data"
			_instanceDataPool = new FactoryPool<InstanceData>(p =>
			{
				var d = new InstanceData();
				d.CurrentRoot = null;
				d.ObjectCache = new ObjectCache();
				d.TypeCache = new ObjectCache();

				foreach (var t in Config.KnownTypes)
				{
					d.TypeCache.RegisterObject(t); // For serialization
					d.TypeCache.AddKnownType(t);   // For deserialization
				}

				return d;
			});
			InstanceData = _instanceDataPool.RentObject();

		}



		/// <summary>Simple usage, but will obviously allocate an array for you, use the other overload for better performance!</summary>
		public byte[] Serialize<T>(T obj)
		{
			byte[] result = null;

			int length = Serialize(obj, ref result);

			Array.Resize(ref result, length);

			return result;
		}

		/// <summary>
		/// Use this overload whenever you can. The intention is that you reuse the serialization buffer so the serializer only has to resize/reallocate a newer (larger) one if there really is not enough space; instead of allocating an array for every Serialize() call, this lets you avoid GC-pressure.
		/// You *can* pass in null for 'targetByteArray' and let the serializer allocate one for you.
		/// </summary>
		public int Serialize<T>(T obj, ref byte[] targetByteArray, int offset = 0)
		{
			EnterRecursive(RecursionMode.Serialization);

			if (Config.EmbedChecksum)
			{
				SerializerBinary.WriteInt32Fixed(ref targetByteArray, ref offset, ProtocolChecksum.Checksum);
			}


			try
			{
				//
				// Root object is the IExternalObject we're serializing (if any)
				// We have to keep track of it so the CacheFormatter knows what NOT to skip
				// otherwise we'd obviously only write one byte lol (the external ID) and nothing else.
				InstanceData.CurrentRoot = obj as IExternalRootObject;


				var formatter = (IFormatter<T>)GetGenericFormatter(typeof(T));

				//
				// The actual serialization
				int offsetBeforeWrite = offset;
				formatter.Serialize(ref targetByteArray, ref offset, obj);
				int offsetAfterWrite = offset;


				//
				// After we're done, we probably have to clear all our caches!
				// Only very rarely can we avoid that
				// todo: would it be more efficient to have one static and one dynamic dictionary?
				if (!Config.PersistTypeCache)
				{
					InstanceData.TypeCache.ClearSerializationCache();
					foreach (var t in Config.KnownTypes)
						InstanceData.TypeCache.RegisterObject(t);
				}

				if (!Config.PersistObjectCache)
					InstanceData.ObjectCache.ClearSerializationCache();


				return offsetAfterWrite - offsetBeforeWrite;
			}
			finally
			{
				//
				// Clear the root object again
				LeaveRecursive(RecursionMode.Serialization);
			}
		}


		/// <summary>
		/// Convenience method that will most likely allocate a T to return (using 'new T()'). Unless the data says the object really is null, in that case no instance of T is allocated.
		/// It would be smart to not use this method and instead use the (ref T value, byte[] buffer) overload with an object you have cached. 
		/// That way the deserializer will set/populate the object you've provided. Obviously this only works if you can overwrite/reuse objects like this! (which, depending on what you're doing, might not be possible at all)
		/// </summary>
		public T Deserialize<T>(byte[] buffer)
		{
			T value = default(T);
			int offset = 0;
			Deserialize(ref value, buffer, ref offset);
			return value;
		}

		public void Deserialize<T>(ref T value, byte[] buffer)
		{
			int offset = 0;
			Deserialize(ref value, buffer, ref offset, -1);
		}

		public void Deserialize<T>(ref T value, byte[] buffer, ref int offset, int expectedReadLength = -1)
		{
			if (buffer == null)
				throw new ArgumentNullException("Must provide a buffer to deserialize from!");

			EnterRecursive(RecursionMode.Deserialization);

			try
			{
				var formatter = (IFormatter<T>)GetGenericFormatter(typeof(T));

				int offsetBeforeRead = offset;

				if (Config.EmbedChecksum)
				{
					var checksum = SerializerBinary.ReadInt32Fixed(buffer, ref offset);
					if (checksum != ProtocolChecksum.Checksum)
						throw new InvalidOperationException($"Checksum does not match embedded checksum (Serializer={ProtocolChecksum.Checksum}, Data={checksum})");
				}

				formatter.Deserialize(buffer, ref offset, ref value);

				if (expectedReadLength != -1)
				{
					int bytesActuallyRead = offset - offsetBeforeRead;

					if (bytesActuallyRead != expectedReadLength)
					{
						throw new UnexpectedBytesConsumedException("The deserialization has completed, but not all of the given bytes were consumed. " +
															" Maybe you tried to deserialize something directly from a larger byte-array?",
																   expectedReadLength, bytesActuallyRead, offsetBeforeRead, offset);
					}
				}

				// todo: instead of clearing and re-adding the known types, the type-cache should have a fallback cache inside it
				// todo: .. that gets used when the ID is out of range. So outside is the dynamic stuff (with an offset of where IDs will start), and inside are the
				// todo: .. known elements, and if we're given an ID that is too low, we defer to the inner cache.
				// Very important to clear out caches and stuff that is only valid for this call
				if (!Config.PersistTypeCache)
				{
					InstanceData.TypeCache.ClearDeserializationCache();
					foreach (var t in Config.KnownTypes)
						InstanceData.TypeCache.AddKnownType(t);

				}
				if (!Config.PersistObjectCache)
					InstanceData.ObjectCache.ClearDeserializationCache();
			}
			finally
			{
				LeaveRecursive(RecursionMode.Deserialization);
			}
		}

		public Type PeekType(byte[] buffer)
		{
			Type t = null;
			int offset = 0;
			_typeFormatter.Deserialize(buffer, ref offset, ref t);

			return t;
		}


		// todo: what if a value-type gets serialized? we can't wrap value-types into reference-serializers.
		// We only need reference-serialization when the inner type is actually a reference type

		public IFormatter<T> GetFormatter<T>()
		{
			if (typeof(T).IsValueType)
				return (IFormatter<T>)GetSpecificFormatter(typeof(T));
			else
				return (IFormatter<T>)GetGenericFormatter(typeof(T));
		}

		public IFormatter GetGenericFormatter(Type type)
		{
			// 1.) Cache
			if (_referenceFormatters.TryGetValue(type, out var formatter))
				return formatter;

			// 2.) Get specific & wrap
			var generic = DynamicObjectFormatterResolver.WrapInCache(type, this);
			_referenceFormatters[type] = generic;


			return generic;
		}

		public IFormatter GetSpecificFormatter(Type type)
		{
			// 1.) Cache - todo: maybe do static-generic caching
			if (_specificFormatters.TryGetValue(type, out var formatter))
				return formatter;

			// 2.) User
			if (_userResolver != null)
			{
				formatter = _userResolver(this, type);
				if (formatter != null)
				{
					_specificFormatters[type] = formatter;
					InjectDependencies(formatter);
					return formatter;
				}
			}

			// 3.) Built-in
			for (int i = 0; i < _resolvers.Count; i++)
			{
				formatter = _resolvers[i].GetFormatter(type);
				if (formatter != null)
				{
					_specificFormatters[type] = formatter;
					return formatter;
				}
			}

			// 4.) Dynamic
			formatter = _dynamicResolver.GetFormatter(type);
			if (formatter != null)
			{
				_specificFormatters[type] = formatter;
				return formatter;
			}


			throw new NotSupportedException($"Ceras could not find any IFormatter<T> for the type '{type.FullName}'. Maybe exclude that field/prop from serializaion or write a custom formatter for it.");
		}

		public IFormatter GetFormatterOld(Type type, bool allowDynamicResolver = true, bool throwIfNoneFound = true, string extraErrorInformation = null)
		{
			/*
			IFormatter formatter;

			// 1.) Cache (todo: cache into static-generic fields, but that's only possible if we can guarantee there won't be multiple different instances of a serializer/formatter!) 
			if (_formatters.TryGetValue(type, out formatter))
			{
				if (!allowDynamicResolver)
				{
					// This code is structured a bit weird, because we want to avoid
					// doing the check below unless the the caller does NOT want a dynamci resolver
					// todo: could we just instantly return 'null' then? Or is there a situation where we would still want to try the resolvers etc..??
					bool isDynamic = formatter is IDynamicFormatterMarker ||
									 (formatter is ICacheFormatter cacheFormatter && cacheFormatter.GetInnerFormatter() is IDynamicFormatterMarker);

					if (isDynamic)
						formatter = null;
				}

				if (formatter != null)
					return formatter;
			}

			// 2.) User formatter resolver
			// todo: wrap user resolvers into CacheFormatter<> ?
			if (_userResolver != null)
			{
				formatter = _userResolver(this, type);
				if (formatter != null)
				{
					bool isCacheFormatterAlready = ReflectionHelper.FindClosedType(formatter.GetType(), typeof(ReferenceFormatter<>)) != null;

					if (!type.IsValueType && !isCacheFormatterAlready)
					{
						// Need to wrap the formatter...

						// Create wrapper first
						var wrapper = DynamicObjectFormatterResolver.WrapInCache(type, formatter, this);

						// Then add the wrapper to the cache
						_formatters.Add(type, wrapper);

						// Finally inject dependencies into the original
						InjectDependencies(formatter);

						formatter = wrapper;
					}
					else
					{
						// No need to wrap the formatter, it can be used as is.

						// Add to formatters, so DI can recursively find this instance
						_formatters.Add(type, formatter);
						InjectDependencies(formatter);
					}

					return formatter;
				}
			}

			// 3.) Resolver chain 
			for (int i = 0; i < _resolvers.Count; i++)
			{
				var genericFormatter = _resolvers[i].GetFormatter(type);
				if (genericFormatter != null)
				{
					// put it in before initialization, so other formatters can get a reference already (If we have to call Initialize())
					_formatters[type] = genericFormatter;
					return genericFormatter;
				}
			}

			// 4.) No existing resolver can find a formatter to handle this
			//	   Maybe we can use code-generation to create one dynamically...
			if (allowDynamicResolver && !type.IsPrimitive)
			{
				var dynamicFormatter = _dynamicResolver.GetFormatter(type);

				if (dynamicFormatter != null)
				{
					_formatters[type] = dynamicFormatter;
					return dynamicFormatter;
				}
			}

			if (throwIfNoneFound)
				throw new NotSupportedException($"Ceras could not find any IFormatter<T> for the type '{type.FullName}'. {extraErrorInformation}");
			*/
			return null;
		}

		void InjectDependencies(IFormatter formatter)
		{
			// Extremely simple DI system

			// We can inject formatters and the serializer itself
			var fields = formatter.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
			foreach (var f in fields)
			{
				var t = f.FieldType;

				if (t == typeof(CerasSerializer))
				{
					f.SetValue(formatter, this);
					continue;
				}
				else
				{
					var formatterType = ReflectionHelper.FindClosedType(t, typeof(IFormatter<>));
					if (formatterType != null)
					{
						var formattedType = formatterType.GetGenericArguments()[0];
						var requestedFormatter = GetGenericFormatter(formattedType);

						f.SetValue(formatter, requestedFormatter);
					}
				}
			}
		}

		void EnterRecursive(RecursionMode enteringMode)
		{
			_recursionDepth++;

			if (_recursionDepth == 1)
			{
				// First level, just use the existing data block
				_mode = enteringMode;
			}
			else
			{
				if (_mode != enteringMode)
					throw new InvalidOperationException("Cannot start a serialization call while a deserialization is still in progress (and vice versa)");

				// Next level of recursion
				_recursionStack.Push(InstanceData);
				InstanceData = _instanceDataPool.RentObject();
			}
		}

		void LeaveRecursive(RecursionMode leavingMode)
		{
			_recursionDepth--;

			if (_recursionDepth == 0)
			{
				// Leave the data as it is, we'll use it for the next call
				_mode = RecursionMode.Idle;
			}
			else
			{
				// We need to restore the previous data
				_instanceDataPool.ReturnObject(InstanceData);
				InstanceData = _recursionStack.Pop();
			}
		}

	}


	// In order to support recursive serialization/deserialization, we need to "instantiate"
	// some values for each call.
	struct InstanceData
	{
		public ObjectCache TypeCache;
		public ObjectCache ObjectCache;
		public IExternalRootObject CurrentRoot;
	}

	enum RecursionMode
	{
		Idle,
		Serialization,
		Deserialization,
	}

	sealed class ErrorResolver : IExternalObjectResolver
	{
		public void Resolve<T>(int id, out T value)
		{
			throw new FormatException($"The data to deserialize tells us to resolve an external object (Type: {typeof(T).Name} Id: {id}), but no IExternalObjectResolver has been set to deal with that.");
		}
	}

	// todo: make it so settings cannot be changed after instantiating a serializer. Maybe copy the values? Or implement a "freezable" pattern?
	public class SerializerConfig
	{
		/// <summary>
		/// Determines whether to keep Type-To-Id maps after serialization/deserialization.
		/// This is ***ONLY*** intended for networking, where the deserializer keeps the state as well, and all serialized data is ephemeral (not saved to anywhere)
		/// This will likely save a huge amount of memory and cpu cycles over the lifespan of a network-session, because it will serialize type-information only once.
		/// 
		/// If the serializer is used as a network protocol serializer, this option should definitely be turned on!
		/// Don't use this when serializing to anything persistent (files, database, ...) as you cannot deserialize any data if the deserializer type-cache is not in **EXACTLY**
		/// the same configuration as it (unless you really know exactly what you're doing)
		/// </summary>
		public bool PersistTypeCache { get; set; } = false;

		/// <summary>
		/// Same as PersistTypeCache, but if turned on, all other objects will get cached as well.
		/// Pretty interesting for networking as objects that have previously been sent to the other side will
		/// be serialized as a very short ID when they are encountered again (the other sides deserializer will keep the objects in memory as well).
		/// It is strongly suggested that you either completely clear, or manually remove objects from the object cache when an object is no longer used,
		/// as this frees up ID-space (making the connection more efficient) and also allows objects to be garbage collected.
		/// That means if you don't manage (clear/remove) the object cache explicitly, you'll eventually get an OutOfMemoryException.
		/// (Again, ONLY for networking purposes as serializer and deserializer have to stay in perfect sync!)
		/// </summary>
		public bool PersistObjectCache { get; set; } = false;


		public Func<Type, object> ObjectFactoryMethod { get; set; } = null;

		/// <summary>
		/// Set this to a function you provide. Ceras will call it when an object instance is no longer needed.
		/// For example you want to populate an existing object with data, and one of the fields already has a value (a left-over from the last time it was used),
		/// but the current data says that the field should be 'null'. That's when Ceras will call this this method so you can recycle the object (maybe return it to your object-pool)
		/// </summary>
		public Action<object> DiscardObjectMethod { get; set; } = null;


		public Func<FieldInfo, SerializationOverride> ShouldSerializeField { get; set; } = null;

		public IExternalObjectResolver ExternalObjectResolver { get; set; }

		/// <summary>
		/// If one of the objects in the graph implements IExternalRootObject, Ceras will only write its ID and then call this function. 
		/// This is useful in case you want to also serialize the external objects as well.
		/// </summary>
		public Action<IExternalRootObject> OnExternalObject { get; set; } = null;

		// todo: settings per-type: ShouldRecylce
		// todo: settings per-field: Formatter<> to override

		public FormatterResolverCallback OnResolveFormatter { get; set; } = null;

		public KnownTypesCollection KnownTypes { get; } = new KnownTypesCollection();

		/// <summary>
		/// Defaults to true to protect against unintended usage. 
		/// Which means that when KnownTypes has any entries the TypeFormatter will be sealed to prevent adding more types.
		/// The idea is that when someone uses KnownTypes, they have a fixed list of types
		/// they want to serialize (to minimize overhead from serializing type names initially), which is usually done in networking scenarios;
		/// While working on a project you might add more types or add new fields or things like that, and a common mistake is accidentally adding a new type (or even whole graph!)
		/// to the object graph that was not intended; which is obviously extremely problematic (super risky if sensitive 
		/// stuff gets suddenly dragged into the serialization; or might even just crash when encountering types that can't even be serialized correctly; ...).
		/// Don't disable this unless you know what you're doing.
		/// </summary>
		public bool SealTypesWhenUsingKnownTypes { get; set; } = true;



		/// <summary>
		/// If true, the serializer will generate dynamic object formatters early (in the constructor).
		/// This can obviously only work if you use sealed KnownTypes (meaning you put all your types into KnownTypes and then have the serializer seal it at construction time).
		/// Then it is assured that no new types will be added dynamically, which in turn means that the "protocol hash" will not change.
		/// </summary>
		public bool GenerateChecksum { get; set; } = true;

		/// <summary>
		/// Embed protocol/serializer checksum at the start of any serialized data, and read it back when deserializing to make sure we're not reading incompatible data on accident
		/// </summary>
		public bool EmbedChecksum { get; set; } = false;
	}

	public delegate IFormatter FormatterResolverCallback(CerasSerializer ceras, Type typeToBeFormatted);

	/// <summary>
	/// A class that the serializer uses to keep track of the "checksum" of its internal state.
	/// Why? What for?
	/// Since we have absolutely no "backwards compatibility" or "versioning" in the binary data,
	/// the serializer/deserializer has to be exactly the same, meaning the same classes with the same type-codes
	/// in exactly the same order. Each class must have the same fields in the same order, with the same types and attributes.
	/// To make all this easier, the serializer simply puts all the information into this class.
	/// - constructs the dynamic serializers directly when the serializer gets created
	/// - optionally emits a checksum into the binary (just 1 int)
	/// </summary>
	public class ProtocolChecksum
	{
		xxHash _hash = new xxHash();
		bool _isClosed;

		bool _useDebugString = true;
		string _debugString = "";

		int _checksum;
		public int Checksum => _isClosed ? _checksum : throw new InvalidOperationException("not yet computed");

		internal ProtocolChecksum()
		{
			// ReSharper disable once RedundantArgumentDefaultValue
			_hash.Init(0);
		}

		internal void Add(string name)
		{
			if (_isClosed)
				throw new InvalidOperationException("IsClosed");

			if (_useDebugString)
				_debugString += "\r\n" + name;

			var bytes = Encoding.UTF8.GetBytes(name);
			_hash.Update(bytes, bytes.Length);
		}

		internal void Finish()
		{
			_isClosed = true;
			_checksum = (int)_hash.Digest();
		}
	}

	public class KnownTypesCollection : ICollection<Type>
	{
		ICollection<Type> _collection = new HashSet<Type>();

		/* todo: eventually we want to make sure that serializer/deserializer stats NEVER go out of sync
		 *
		 * Stage 1 (soon):
		 *
		 * Write: every type, every field name, field type, all attributes
		 * into a buffer and hash it.
		 *
		 * The serializer will provide its setup hash after initialization in a property.
		 * The user is strongly recommended to use it.
		 *
		 * For networking the hash will be first thing that's sent before any data serialized by this serializer
		 * For communication before that, a different serializer might be used that does everything the normal way.
		 *
		 * For writing to files the user should write the hash first, and then the serialized object graph.
		 * And when reading, compare the hashes before anything else.
		 *
		 *
		 * Stage 2 (eventually):
		 *
		 * To make debugging easier it would be helpful to know where things went wrong,
		 * maybe, while creating the known types, we could already give it an expected hash.
		 * And every change adds another int-sized hash or something (or maybe just after each full object type)
		 * (or maybe write full object descriptions as hash, and for field-names(string) and fields(arrays) just write a single byte as count or so...)
		 *
		 * When something is added, and the hash is known, but the next hash update is not what it should be, then
		 * we know for sure what object screwed up exactly and why. 
		 * 
		 */


		// public string KnownTypeHash

		// Means that the collection is now "sealed" and can't be changed anymore
		bool _isClosed;

		//public void AddHierarchy(Type baseTypeOrInterface)
		//{
		//	ThrowIfClosed();

		//}

		public void Seal()
		{
			if (_isClosed)
				return;
			_isClosed = true;

			// While adding we need to make sure we have no duplicates.
			// While using it, we need to maintain exact order.
			var list = new List<Type>();

			list.AddRange(_collection.OrderBy(t => t.FullName));

			_collection = list;
		}


		void ThrowIfClosed()
		{
			if (_isClosed)
				throw new InvalidOperationException("Can't change the type collection after it has been used by the serializer");
		}


		#region ICollection; thanks resharper

		public int Count => _collection.Count;
		public bool IsReadOnly => _isClosed || _collection.IsReadOnly;


		public void Add(Type item)
		{
			ThrowIfClosed();
			_collection.Add(item);
		}

		public void Clear()
		{
			ThrowIfClosed();
			_collection.Clear();
		}

		public bool Remove(Type item)
		{
			ThrowIfClosed();
			return _collection.Remove(item);
		}


		public bool Contains(Type item)
		{
			return _collection.Contains(item);
		}

		public void CopyTo(Type[] array, int arrayIndex)
		{
			_collection.CopyTo(array, arrayIndex);
		}

		public IEnumerator<Type> GetEnumerator()
		{
			return _collection.GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return ((IEnumerable)_collection).GetEnumerator();
		}

		#endregion
	}

	public interface ITypeBinder
	{
		string GetBaseName(Type type);
		Type GetTypeFromBase(string baseTypeName);
		Type GetTypeFromBaseAndAgruments(string baseTypeName, params Type[] genericTypeArguments);
	}

	public class NaiveTypeBinder : ITypeBinder
	{
		// todo: do this right. We can't find/resolve types if they don't come from the same assembly.
		// todo: so we need to provide a way for the user to give us all the assemblies in which types to serialize/deserialize might be
		public readonly HashSet<Assembly> TypeAssemblies = new HashSet<Assembly>();

		public NaiveTypeBinder()
		{
			TypeAssemblies.Add(typeof(int).Assembly);
			TypeAssemblies.Add(typeof(List<>).Assembly);
			TypeAssemblies.Add(Assembly.GetCallingAssembly());
			TypeAssemblies.Add(Assembly.GetEntryAssembly());

			TypeAssemblies.RemoveWhere(a => a == null);
		}

		// given List<int> it would return "System.Collections.List"
		public string GetBaseName(Type type)
		{
			if (type.IsGenericType)
				return type.GetGenericTypeDefinition().FullName;

			return type.FullName;
		}

		public Type GetTypeFromBase(string baseTypeName)
		{
			// todo: let the user provide a way!
			// todo: alternatively, search in ALL loaded assemblies... but that is slow as fuck

			foreach (var a in TypeAssemblies)
			{
				var t = a.GetType(baseTypeName);
				if (t != null)
					return t;
			}

			// Oh no... did the user forget to add the right assembly??
			// Lets search in everything that's loaded...
			foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
			{
				var t = a.GetType(baseTypeName);
				if (t != null)
				{
					TypeAssemblies.Add(a);
					return t;
				}
			}

			throw new Exception("Cannot find type " + baseTypeName + " after searching in all user provided assemblies and all loaded assemblies. Is the type in some plugin-module that was not yet loaded? Or did the assembly that contains the type change (ie the type got removed)?");
		}

		public Type GetTypeFromBaseAndAgruments(string baseTypeName, params Type[] genericTypeArguments)
		{
			var baseType = GetTypeFromBase(baseTypeName);
			return baseType.MakeGenericType(genericTypeArguments);
		}
	}
}
