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
	using System.Runtime.CompilerServices;
	using System.Text;

	/*
	 * Todo:
	 * 
	 * VersionTolerance:
	 * - we could probably somehow reuse the generated schemata, but how to do it efficiently?
	 * - also, it would be nice if we could embed a hash + size offset into the binary, so that we can easily detect that we already have a given schema, and then skip it (using the one we already have)
	 * 
	 * 
	 */
	public class CerasSerializer
	{
		// Some types are constructed by the formatter directly
		internal static readonly Type _rtTypeType, _rtFieldType, _rtPropType, _rtCtorType, _rtMethodType;
		static readonly HashSet<Type> _formatterConstructedTypes = new HashSet<Type>();
		readonly Dictionary<Type, IFormatter> _typeToConstructionFormatter = new Dictionary<Type, IFormatter>();

		internal static bool IsFormatterConstructed(Type type)
		{
			// Array is also always constructed by the caller, but it is handled separately
			return _formatterConstructedTypes.Contains(type);
		}

		static CerasSerializer()
		{
			// Type
			var type = typeof(Type);
			_rtTypeType = type.GetType();

			_formatterConstructedTypes.Add(type);
			_formatterConstructedTypes.Add(_rtTypeType);


			//
			// MemberInfos and their runtime variants
			_formatterConstructedTypes.Add(typeof(FieldInfo));
			var field = typeof(MemberHelper).GetField(nameof(MemberHelper._field), BindingFlags.Static | BindingFlags.NonPublic);
			_rtFieldType = field.GetType();
			_formatterConstructedTypes.Add(_rtFieldType);

			_formatterConstructedTypes.Add(typeof(PropertyInfo));
			var prop = typeof(MemberHelper).GetProperty(nameof(MemberHelper._prop), BindingFlags.Static | BindingFlags.NonPublic);
			_rtPropType = prop.GetType();
			_formatterConstructedTypes.Add(_rtPropType);

			_formatterConstructedTypes.Add(typeof(ConstructorInfo));
			var ctor = typeof(CerasSerializer).GetConstructor(BindingFlags.Static | BindingFlags.NonPublic, null, new Type[0], new ParameterModifier[0]);
			_rtCtorType = ctor.GetType();
			_formatterConstructedTypes.Add(_rtCtorType);

			_formatterConstructedTypes.Add(typeof(MethodInfo));
			var method = typeof(MemberHelper).GetMethod(nameof(MemberHelper._method), BindingFlags.Static | BindingFlags.NonPublic);
			_rtMethodType = method.GetType();
			_formatterConstructedTypes.Add(_rtMethodType);


			// String
			_formatterConstructedTypes.Add(typeof(string));
		}



		internal readonly SerializerConfig Config;
		public ProtocolChecksum ProtocolChecksum { get; } = new ProtocolChecksum();

		// A special resolver. It creates instances of the "dynamic formatter", the DynamicObjectFormatter<> is a type that uses dynamic code generation to create efficient read/write methods
		// for a given object type.
		readonly IFormatterResolver _dynamicResolver;

		// The user provided resolver, will always be queried first
		readonly FormatterResolverCallback _userResolver;

		// todo: allow the user to provide their own binder. So they can serialize a type-name however they want; but then again they could override the TypeFormatter anyway, so what's the point? maybe it would be best to completely remove the typeBinder (merging it into the default TypeFormatter)?
		internal ITypeBinder TypeBinder;

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

			TypeBinder = Config.TypeBinder ?? new NaiveTypeBinder();

			_userResolver = Config.OnResolveFormatter;

			// Int, Float, Enum, String
			_resolvers.Add(new PrimitiveResolver(this));

			_resolvers.Add(new KeyValuePairFormatterResolver(this));
			_resolvers.Add(new CollectionFormatterResolver(this));

			// DateTime, Guid
			_resolvers.Add(new BclFormatterResolver(this));

			// DynamicObjectResolver is a special case, so it is not in the resolver-list
			// That is because we only want to have specific resolvers in the resolvers-list
			_dynamicResolver = new DynamicObjectFormatterResolver(this);

			//
			// Type formatter is the basis for all complex objects,
			// It is special and has its own caching system (so no wrapping in a ReferenceFormatter)
			var typeFormatter = new TypeFormatter(this);

			var runtimeType = GetType().GetType();
			_specificFormatters.Add(typeof(Type), typeFormatter);
			_specificFormatters.Add(runtimeType, typeFormatter);
			_referenceFormatters.Add(typeof(Type), typeFormatter);
			_referenceFormatters.Add(runtimeType, typeFormatter);

			// MemberInfos (FieldInfo, RuntimeFieldInfo, ...)
			_resolvers.Add(new ReflectionTypesFormatterResolver(this));



			//
			// Basic setup is done
			// Now calculate the protocol checksum
			Config.KnownTypes.Seal();
			if (Config.GenerateChecksum)
			{
				foreach (var t in Config.KnownTypes)
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

					var schema = GetSerializationSchema(t, Config);
					foreach (var m in schema.Members)
					{
						ProtocolChecksum.Add(m.Member.MemberType.FullName);
						ProtocolChecksum.Add(m.Member.Name);

						foreach (var a in m.Member.MemberInfo.GetCustomAttributes(true))
							ProtocolChecksum.Add(a.ToString());
					}
				}

				ProtocolChecksum.Finish();
			}

			//
			// We can already pre-warm formatters
			// - dynamic serializers generate their code
			// - reference formatters generate their wrappers
			foreach (var t in Config.KnownTypes)
				if (!t.ContainsGenericParameters)
					GetGenericFormatter(t);



			//
			// Finally we need "instance data"
			_instanceDataPool = new FactoryPool<InstanceData>(p =>
			{
				var d = new InstanceData();
				d.CurrentRoot = null;
				d.ObjectCache = new ObjectCache();
				d.TypeCache = new ObjectCache();
				d.WrittenSchemata = new HashSet<Schema>();
				d.DataSchemata = new Dictionary<Type, Schema>();

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
		public int Serialize<T>(T obj, ref byte[] buffer, int offset = 0)
		{
			EnterRecursive(RecursionMode.Serialization);

			if (Config.EmbedChecksum)
			{
				SerializerBinary.WriteInt32Fixed(ref buffer, ref offset, ProtocolChecksum.Checksum);
			}

			try
			{
				//
				// Root object is the IExternalObject we're serializing (if any)
				// We have to keep track of it so the CacheFormatter knows what NOT to skip
				// otherwise we'd obviously only write one byte lol (the external ID) and nothing else.
				InstanceData.CurrentRoot = obj as IExternalRootObject;


				//
				// The actual serialization
				int offsetBeforeWrite = offset;
				{
					Type type = typeof(T);
					if (Config.VersionTolerance == VersionTolerance.AutomaticEmbedded && !FrameworkAssemblies.Contains(type.Assembly))
					{
						// Embed schemata: force <object>
						var formatter = (IFormatter<object>)GetGenericFormatter(typeof(object));
						formatter.Serialize(ref buffer, ref offset, obj);
					}
					else
					{
						// Normal serialization
						var formatter = (IFormatter<T>)GetGenericFormatter(type);
						formatter.Serialize(ref buffer, ref offset, obj);
					}
				}
				int offsetAfterWrite = offset;

				//
				// After we're done, we probably have to clear all our caches!
				// Only very rarely can we avoid that
				// todo: would it be more efficient to have one static and one dynamic dictionary??
				if (!Config.PersistTypeCache)
				{
					InstanceData.TypeCache.ClearSerializationCache();
					foreach (var t in Config.KnownTypes)
						InstanceData.TypeCache.RegisterObject(t);
				}

				if (!Config.PersistObjectCache)
					InstanceData.ObjectCache.ClearSerializationCache();


				int dataSize = offsetAfterWrite - offsetBeforeWrite;


				return dataSize;
			}
			finally
			{
				//
				// Clear the root object again
				InstanceData.WrittenSchemata.Clear();
				InstanceData.CurrentRoot = null;

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
				int offsetBeforeRead = offset;

				if (Config.EmbedChecksum)
				{
					var checksum = SerializerBinary.ReadInt32Fixed(buffer, ref offset);
					if (checksum != ProtocolChecksum.Checksum)
						throw new InvalidOperationException($"Checksum does not match embedded checksum (Serializer={ProtocolChecksum.Checksum}, Data={checksum})");
				}

				Type type = typeof(T);
				if (Config.VersionTolerance == VersionTolerance.AutomaticEmbedded && !FrameworkAssemblies.Contains(type.Assembly))
				{
					var formatter = (IFormatter<object>)GetGenericFormatter(typeof(object));
					object obj = value;
					formatter.Deserialize(buffer, ref offset, ref obj);
					value = (T)obj;
				}
				else
				{
					var formatter = (IFormatter<T>)GetGenericFormatter(type);
					formatter.Deserialize(buffer, ref offset, ref value);
				}


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
				InstanceData.WrittenSchemata.Clear();

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



		public IFormatter<T> GetFormatter<T>() => (IFormatter<T>)GetGenericFormatter(typeof(T));



		internal static HashSet<Assembly> FrameworkAssemblies = new HashSet<Assembly>
		{
				typeof(int).Assembly,
				typeof(IList<>).Assembly,
				typeof(System.Uri).Assembly,
				typeof(System.Net.Sockets.AddressFamily).Assembly,
				typeof(System.Tuple<>).Assembly,
		};

		public IFormatter GetGenericFormatter(Type type)
		{
			if (type.IsValueType)
			{
				// Value types are not reference types, so they are not wrapped
				return GetSpecificFormatter(type);
			}

			// 1.) Cache
			if (_referenceFormatters.TryGetValue(type, out var formatter))
				return formatter;

			// 2.) Create a reference formatter (which internally obtains the matching specific one)
			var refFormatterType = typeof(ReferenceFormatter<>).MakeGenericType(type);
			var referenceFormatter = (IFormatter)Activator.CreateInstance(refFormatterType, this);
			_referenceFormatters[type] = referenceFormatter;
			return referenceFormatter;
		}

		public IFormatter GetSpecificFormatter(Type type)
		{
			// 1.) Cache - todo: maybe do static-generic caching
			if (_specificFormatters.TryGetValue(type, out var formatter))
				return formatter;

			// 2.) Version Tolerance: Embedded schema descriptors
			if (Config.VersionTolerance == VersionTolerance.AutomaticEmbedded)
				if (InstanceData.DataSchemata.TryGetValue(type, out var embeddedSchema))
				{
					// todo: remove those schemata again after reading
					// todo: restore the formatters we've overwritten
					// maybe put them into their own thing

					var schemaFormatterType = typeof(SchemaDynamicFormatter<>).MakeGenericType(type);
					var schemaFormatter = (IFormatter)Activator.CreateInstance(schemaFormatterType, this, embeddedSchema);
					_specificFormatters[type] = schemaFormatter;
					return schemaFormatter;
				}


			// 3.) User
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


			// Depending on the VersionTolerance we use different formatters
			if (Config.VersionTolerance == VersionTolerance.AutomaticEmbedded)
			{
				bool isFrameworkType = FrameworkAssemblies.Contains(type.Assembly);

				if (type.IsArray)
					isFrameworkType = true;

				if (isFrameworkType == false)
				{
					// Probably a user type, which means it might change, which means it needs Schema-Data
					var objectSchema = GetSerializationSchema(type, Config);

					// todo: right now we create a new schema formatter every time, not very efficient, but how can we cache them reliably?
					//		 the only way would be to embed the hash so we can skip it, or should we still read the schema, calculate the hash, and then just skip the SchemaDynamicFormatter?
					var schemaFormatterType = typeof(SchemaDynamicFormatter<>).MakeGenericType(type);
					var schemaFormatter = (IFormatter)Activator.CreateInstance(schemaFormatterType, this, objectSchema);

					_specificFormatters[type] = schemaFormatter;
					return schemaFormatter;
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





		internal void WriteSchemaForType(ref byte[] buffer, ref int offset, Type type)
		{
			/*
			 * With Schema information we add some things to the file:
			 * - SchemaBlockStartOffset (varint: pointing to the first byte of the schemata block)
			 * - SchemaBlockLength (varint: only used when we need to add this to the offset, when the user requests size validation in deserialize)
			 * - RawData
			 * - SchemataBlock
			 *   - varint: numSchemata
			 *   For 0..numSchemata:
  			 *     - int32: schemaHash
			 *     - varInt: schemaSize
			 *     - schemaData
			 * 
			 *
			 * problem:
			 *  - schema definitions contain type names, which we NEED to read so they're in our cache in the right order
			 *
			 *  - if we write schema types after the data; then the data contains the actual strings,
			 *    and while reading we need to read the schemata FIRST, potentially referring us to a string that was supposedly already there but is not
			 *
			 *  -> can we know the schema beforehand? no, there might be objects that are hidden in <object> or interface fields!
			 *
			 *  -> we must write type names at the very beginning
			 *
			 *
			 * Other approach:
			 * Schema data interweaved. Keep track of what types are written.
			 * When a type name is written in full, also write the schema for it right into the data (with has prefix)
			 * When reading we read the type (and cache it), then the schema hash; potentially ignoring the schema data because we already have a schema+formatter for that
			 *
			 * 1.) Write schema together with type-name as needed
			 * 2.) While reading, make use of the schema hash to reuse an existing schema + use "skip-reader" to quickly read over the schema data (only happens once, so its ok)
			 *
			 * Trying to rescue approach #1?
			 * We would need to ensure type names are written in full only in the schema, because that is read first;
			 * - When a type has to be written: add it to a list, pseudo caching it, and instantly emit the cacheId.
			 * - After writing the data: emit schemata in the correct order; emit the type names in full.
			 *
			 *
			 * Approach 1 vs 2:
			 * - First approach collects all schema data into the beginning of the file
			 *   + could potentially extract it into a separate thing maybe?
			 * - Second approach writes schema data inline with the type-name
			 *   + easier to do
			 *
			 * Is one always faster than the other?
			 *  + inline is likely faster to implement
			 *
			 *
			 * Scenario: Multiple files all with same schema
			 * - would like to have schema data shared somewhere
			 * - but then we'd like to put all the objects into one big file anyway
			 * - doing that would require some sort of database because we need fast access to entries and being able to rewrite an entry (with dynamic size change)
			 *
			 * Abort?
			 * - for simple versioning maybe use json
			 * - but we'd lose IExternalRootObject, which is super-bad, but we could do some special formatting, to write an ID instead just like Ceras
			 * - why do we even want versioning info??
			 *    -> settings files? maybe DB-like functionality?
			 * - assuming db: where do we put lists and strings?
			 *
			 *
			 *
			 * What do we want from our DB?
			 * - Fast
			 * - Automatic deconstruction/construction into RootObjects
			 * - usually 500, to at the very most 5k items (5MB -> 5000 * 1kb)
			 * -> Database is a topic for another day!
			 *    Akavache, LiteDB, or a very simple custom solution might be the best
			 *
			 * That means:
			 * - Very simple embed mode, just so it's feature-complete
			 * - Manual mode where the user is responsible:
			 *     - OnSchemaCreated (given hash+bytes[])
			 *     - schema is separated from data, types are likely in KnownTypes anyway so the raw-data is small
			 *     - user has to take care to give ceras the right schema to read with
			 *       easiest way: prefix every written object with hash of its schema; save schemata in a separate small db
			 * - Ceras in "file mode" separate databases
			 *   - schema db
			 *   - per-root-object-type db
			 * - IKeyValueStore: Get(ulong id, ref byte[] buffer, ref int dataStart, ref int dataSize)
			 * - IKeyValueStoreResolver: IKeyValueStore GetStore(string name)
			 *
			 * 1.) SimpleMode (to please the feature list and beginners)
			 *     Directly embeds version data into the saved data, useful for stuff like graphics-settings, UI-config and setup (keybinds, layout, ...), ...
			 *
			 * 2.) Manual:
			 *     You get notified about schema creation and need to manually save it
			 *     Ceras will ask for schemata while reading. It gives you the hash, and you need to provide the schema data
			 *     todo: maybe not even that, manual mode is intended to be completely manual! so maybe we'll not even do that
			 *     8byte hash is embedded into the data to validate the format
			 *     Only useful if you want maximum control and customize everything
			 *
			 * 3.) Automatic
			 *     IKeyValueStore and IKeyValueStoreResolver solve everything.
			 *     Ceras will obtain one IKeyValueStore from you to save/load schemata and an additional store for each root object type.
			 *     - Ceras will provide Save(obj), Get(id), Delete(id)
			 *     - Ceras will either
			 *         - notify you about all other root objects in the graph (so you can save them)
			 *         - or automatically serialize them, check the hash to see if the object is still up to date (maybe? is this a good idea?)
			 *     - Will automatically use an built-in IExternalRootObjectResolver to load referenced objects
			 *       and will make sure that only one instance of each object is present (so references stay consistent)
			 */


			// Create schema, write it
			var schema = GetSerializationSchema(type, Config);

			var schemaFormatterType = typeof(SchemaDynamicFormatter<>).MakeGenericType(type);
			var schemaFormatter = (IFormatter)Activator.CreateInstance(schemaFormatterType, this, schema);

			// Make the formatter available, if we're called from TypeFormatter then this will be the next thing
			_specificFormatters[type] = schemaFormatter;


			// Write the schema...
			var members = schema.Members;
			SerializerBinary.WriteInt32(ref buffer, ref offset, members.Count);

			for (int i = 0; i < members.Count; i++)
				SerializerBinary.WriteString(ref buffer, ref offset, members[i].PersistentName);
		}

		internal void ReadSchemaForType(byte[] buffer, ref int offset, Type type)
		{
			var schema = new Schema();

			var count = SerializerBinary.ReadInt32(buffer, ref offset);
			for (int i = 0; i < count; i++)
			{
				var name = SerializerBinary.ReadString(buffer, ref offset);

				var schemaMember = new SchemaMember();
				schema.Members.Add(schemaMember);

				var member = Schema.FindMember(type, name);
				if (member == null)
				{
					schemaMember.PersistentName = name;
					schemaMember.IsSkip = true;
				}
				else
				{
					schemaMember.PersistentName = name;
					schemaMember.IsSkip = false;
					schemaMember.Member = new SerializedMember(member);
				}
			}


			//
			// Generate schema-formatter
			var schemaFormatterType = typeof(SchemaDynamicFormatter<>).MakeGenericType(type);
			var schemaFormatter = (IFormatter)Activator.CreateInstance(schemaFormatterType, this, schema);

			// Make the formatter available, if we're called from TypeFormatter then this will be the next thing
			_specificFormatters[type] = schemaFormatter;
		}



		internal static Schema GetSerializationSchema(Type type, SerializerConfig config)
		{
			Schema schema = new Schema();
			schema.Type = type;

			var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

			var classConfig = type.GetCustomAttribute<MemberConfig>();

			foreach (var m in type.GetFields(flags).Cast<MemberInfo>().Concat(type.GetProperties(flags)))
			{
				bool isPublic;
				bool isField = false, isProp = false;
				bool isReadonly = false;
				bool isCompilerGenerated = false;

				if (m is FieldInfo f)
				{
					// Skip readonly
					if (f.IsInitOnly)
						isReadonly = true;

					// Readonly auto-prop backing fields
					if (f.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
						isCompilerGenerated = true;

					// By default we skip hidden/compiler generated fields, so we don't accidentally serialize properties twice (property, and then its automatic backing field as well)
					if (isCompilerGenerated)
						if (config.SkipCompilerGeneratedFields)
							continue;

					isPublic = f.IsPublic;
					isField = true;
				}
				else if (m is PropertyInfo p)
				{
					// There's no way we can serialize a prop that we can't read and write, even {private set;} props would be writeable,
					// That is becasue there is actually physically no set() method we could possibly call. Just like a "computed property".
					// As for readonly props (like string Name { get; } = "abc";), the only way to serialize them is serializing the backing fields, which is handled above (skipCompilerGenerated)
					if (!p.CanRead || !p.CanWrite)
						continue;

					// This checks for indexers, an indexer is classified as a property
					if (p.GetIndexParameters().Length != 0)
						continue;

					isPublic = p.GetMethod.IsPublic;
					isProp = true;
				}
				else
					continue;

				var serializedMember = FieldOrProp.Create(m);

				// should we allow users to provide a formatter for each old-name (in case newer versions have changed the type of the element?)
				var attrib = m.GetCustomAttribute<PreviousNameAttribute>();

				if (attrib != null)
				{
					VerifyName(attrib.Name);
					foreach (var n in attrib.AlternativeNames)
						VerifyName(n);
				}
				

				var schemaMember = new SchemaMember
				{
					IsSkip = false,
					Member = serializedMember,
					//OverrideFormatter = DetermineOverrideFormatter(m),
					PersistentName = attrib?.Name ?? m.Name,
				};


				//
				// 1.) ShouldSerializeMember - use filter if there is one
				if (config.ShouldSerializeMember != null)
				{
					var filterResult = config.ShouldSerializeMember(serializedMember);

					if (filterResult == SerializationOverride.ForceInclude)
					{
						schema.Members.Add(schemaMember);
						continue;
					}
					else if (filterResult == SerializationOverride.ForceSkip)
					{
						continue;
					}
				}

				//
				// 2.) Use member-attribute
				var ignore = m.GetCustomAttribute<Ignore>(true) != null;
				var include = m.GetCustomAttribute<Include>(true) != null;

				if (ignore && include)
					throw new Exception($"Member '{m.Name}' on type '{type.Name}' has both [Ignore] and [Include]!");

				if (ignore)
				{
					continue;
				}

				if (include)
				{
					schema.Members.Add(schemaMember);
					continue;
				}


				//
				// After checking the user callback (ShouldSerializeMember) and the direct attributes (because it's possible to directly target a backing field like this: [field: Include])
				// we now need to check for compiler generated fields again.
				// The intent is that if 'skipCompilerGenerated==false' then we allow checking the callback, as well as the attributes.
				// But (at least for now) we don't want those problematic fields to be included by default,
				// which would happen if any of the class or global defaults tell us to include 'private fields', because it is too dangerous to do it globally.
				// There are all sorts of spooky things that we never want to include like:
				// - enumerator-state-machines
				// - async-method-state-machines
				// - events (maybe?)
				// - cached method invokers for 'dynamic' objects
				// Right now I'm not 100% certain all or any of those would be a problem, but I'd rather test it first before just blindly serializing this unintended stuff.
				if (isCompilerGenerated)
					continue;

				//
				// 3.) Use class-attribute
				if (classConfig != null)
				{
					if (IsMatch(isField, isProp, isPublic, classConfig.TargetMembers))
					{
						schema.Members.Add(schemaMember);
						continue;
					}
				}

				//
				// 4.) Use global defaults
				if (IsMatch(isField, isProp, isPublic, config.DefaultTargets))
				{
					schema.Members.Add(schemaMember);
					continue;
				}
			}

			schema.Members.Sort(SchemaMemberComparer.Instance);

			return schema;
		}

		// Removed until we are ready to deal with the V2 of version tolerance
		/*
		static IFormatter DetermineOverrideFormatter(MemberInfo memberInfo)
		{
			var prevType = memberInfo.GetCustomAttribute<PreviousType>();
			if (prevType != null)
				return GetGenericFormatter(prevType.MemberType);

			var prevFormatter = memberInfo.GetCustomAttribute<PreviousFormatter>();
			if (prevFormatter != null)
			{
				var formatter = ReflectionHelper.FindClosedType(prevFormatter.FormatterType, typeof(IFormatter<>));
				if (formatter == null)
					throw new Exception($"Type '{prevFormatter.FormatterType.FullName}' must inherit from IFormatter<>");

				return (IFormatter)Activator.CreateInstance(formatter);
			}

			return null;
		}
		*/

		static void VerifyName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new Exception("Member name can not be null/empty");
			if (char.IsNumber(name[0]) || char.IsControl(name[0]))
				throw new Exception("Name must start with a letter");

			const string allowedChars = "_";

			for (int i = 1; i < name.Length; i++)
				if (!char.IsLetterOrDigit(name[i]) && !allowedChars.Contains(name[i]))
					throw new Exception($"The name '{name}' has character '{name[i]}' at index '{i}', which is not allowed. Must be a letter or digit.");
		}

		static bool IsMatch(bool isField, bool isProp, bool isPublic, TargetMember targetMembers)
		{
			if (isField)
			{
				if (isPublic)
				{
					if ((targetMembers & TargetMember.PublicFields) != 0)
						return true;
				}
				else
				{
					if ((targetMembers & TargetMember.PrivateFields) != 0)
						return true;
				}
			}

			if (isProp)
			{
				if (isPublic)
				{
					if ((targetMembers & TargetMember.PublicProperties) != 0)
						return true;
				}
				else
				{
					if ((targetMembers & TargetMember.PrivateProperties) != 0)
						return true;
				}
			}

			return false;
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


		// Populated at the start of a deserialization, so we know what types have specialized formatters
		public Dictionary<int, Schema> HashToSchema;
		public Dictionary<Type, Schema> DataSchemata;


		public HashSet<Schema> WrittenSchemata; // Populated while writing so at the end we know what schemata we've read, so we can embed their information

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


		/// <summary>
		/// Whenever Ceras needs to create a new object it will use the factory method (if you have provided one)
		/// The primary intended use for this is object pooling; for example when receiving network messages you obviously don't want to 'new()' a new packet every time a message arrives, instead you want to take them from a pool. When doing so, you should of course also provide a 'DiscardObjectMethod' so Ceras can give you objects back when they are not used anymore (happens when you use the ref-version of deserialize to overwrite existing objects).
		/// Another thing this can be used for is when you have a type that only has a static Create method instead of a parameterless constructor.
		/// </summary>
		public Func<Type, object> ObjectFactoryMethod { get; set; } = null;

		/// <summary>
		/// Set this to a function you provide. Ceras will call it when an object instance is no longer needed.
		/// For example you want to populate an existing object with data, and one of the fields already has a value (a left-over from the last time it was used),
		/// but the current data says that the field should be 'null'. That's when Ceras will call this this method so you can recycle the object (maybe return it to your object-pool)
		/// </summary>
		public Action<object> DiscardObjectMethod { get; set; } = null;


		/// <summary>
		/// !! Important:
		/// You may believe you now what you're doing when including things like the backing fields of {get;}-only properties, but there are tons of other problems you most likely didn't even realize unless you've read the github issue here: https://github.com/rikimaru0345/Ceras/issues/11. 
		/// 
		/// Hint: You may end up including all sorts of stuff like enumerator statemachines, delegates, remanants of 'dynamic' objects, ...
		/// So here's your warning: Don't set this to false unless you know what you're doing.
		/// 
		/// This defaults to true, which means that fields marked as [CompilerGenerated] are skipped without asking your 'ShouldSerializeMember' function (if you have set one).
		/// For 99% of all use cases this is exactly what you want. For more information read the 'readonly properties' section in the tutorial.
		/// </summary>
		public bool SkipCompilerGeneratedFields { get; set; } = true;

		/// <summary>
		/// This is the very first thing that ceras uses to determine whether or not to serialize something.
		/// Important: Compiler generated fields are always skipped by default, for more information about that see the 'readonly properties' section in the tutorial where all of this is explained in detail.
		/// </summary>
		public Func<SerializedMember, SerializationOverride> ShouldSerializeMember { get; set; } = null;

		/// <summary>
		/// If your object implement IExternalRootObject they are written as their external ID, so at deserialization-time you need to provide a resolver for Ceras so it can get back the Objects from their IDs.
		/// When would you use this?
		/// There's a lot of really interesting use cases for this, be sure to read the tutorial section 'GameDatabase' even if you're not making a game.
		/// </summary>
		public IExternalObjectResolver ExternalObjectResolver { get; set; }


		/// <summary>
		/// A TypeBinder does two very simple things: 1. it produces a name of a given 'Type', and 2. it finds a 'Type' when given that name.
		/// The default type binder (NaiveTypeBinder) simply uses '.FullName', but there are many cases where you would want to mess around with that.
		/// For example if your objects have very long full-names (many long namespaces), then you could definitely improve performance and output size of your serialized binary by (for example) shortening the namespaces. See the readme on github for more information.
		/// </summary>
		public ITypeBinder TypeBinder { get; set; } = null;

		/// <summary>
		/// If one of the objects in the graph implements IExternalRootObject, Ceras will only write its ID and then call this function. 
		/// That means this external object for which only the ID was written, was not serialized itself. But often you want to sort of "collect" all the elements
		/// that belong into an object-graph and save them at the same time. That's when you'd use this callback. 
		/// Make sure to read the 'GameDatabase' example in the tutorial even if you're not making a game.
		/// </summary>
		public Action<IExternalRootObject> OnExternalObject { get; set; } = null;

		// todo: settings per-type: ShouldRecylce
		// todo: settings per-field: Formatter<> to override

		/// <summary>
		/// If you want to provide your own formatter resolver, use this.
		/// </summary>
		public FormatterResolverCallback OnResolveFormatter { get; set; } = null;

		/// <summary>
		/// Add all the types you want to serialize to this collection.
		/// When Ceras serializes your objects, and the object field is not exactly matching (for example a base type) then ceras obviously has to write the type.
		/// Even though Ceras is optimized so it only writes the type once, that is sometimes unacceptable (networking for example).
		/// So if you add types here, Ceras can *always* use a pre-calculated typeID directly. 
		/// See the tutorial for more information.
		/// </summary>
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
		/// Sometimes you want to persist objects even while they evolve (fields being added, removed, renamed).
		/// IMPORTANT: Type changes are not yet supported, and there are other things to be aware of, so check out the tutorial for more information (and a way to deal with changing types)
		/// </summary>
		public VersionTolerance VersionTolerance { get; set; } = VersionTolerance.Disabled;

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

		/// <summary>
		/// If all the other things (ShouldSerializeMember and all the attributes) yield no result, then this setting is used to determine if a member should be included.
		/// </summary>
		public TargetMember DefaultTargets { get; set; } = TargetMember.PublicFields;

		/// <summary>
		/// Explaining this setting here would take too much space, check out the tutorial section for details.
		/// </summary>
		public ReadonlyFieldHandling ReadonlyFieldHandling { get; set; } = ReadonlyFieldHandling.Off;
	}


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
}
