// ReSharper disable RedundantTypeArgumentsOfMethod
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LiveTesting")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ceras.Test")]
namespace Ceras
{
	using Exceptions;
	using Formatters;
	using Helpers;
	using Resolvers;
	using System;
	using System.Collections.Generic;
	using System.ComponentModel;
	using System.Linq;
	using System.Reflection;
	using System.Text;

	/*
	 * Todo:
	 * 
	 * VersionTolerance:
	 * - It would be nice if we could embed a hash + size offset into the binary, so that we can easily detect that we already have a given schema, and then skip it (using the one we already have)
	 * 
	 * Robustness:
	 * - ProtocolChecksum should include every setting of the config as well.
	 *   So all the bool and enum settings also contribute to the checksum so it is more reliable.
	 *   Unfortunately we can't capture all the user provided stuff like callbacks, type binder, ...
	 *  
	 * - GenerateChecksum should be automatic when KnownTypes contains types and AutoSeal is active
	 *
	 */
	/// <summary>
	/// <para>Ceras serializes any object to a byte-array and back.</para>
	/// <para>Want more features? Or something not working right?</para>
	/// <para>-> Then go here: https://github.com/rikimaru0345/Ceras </para>
	/// </summary>
	public class CerasSerializer
	{
		// Some types are constructed by the formatter directly
		internal static readonly Type _rtTypeType, _rtFieldType, _rtPropType, _rtCtorType, _rtMethodType;
		static readonly HashSet<Type> _formatterConstructedTypes = new HashSet<Type>();

		[Obsolete("Use 'config.ConfigType(type)' instead to configure how new objects are created."), EditorBrowsable(EditorBrowsableState.Never)]
		public static void AddFormatterConstructedType(Type type)
		{
			_formatterConstructedTypes.Add(type);
		}

		internal static bool IsFormatterConstructed(Type type)
		{
			// Array is also always constructed by the caller, but it is handled separately

			return _formatterConstructedTypes.Contains(type);
		}

		static HashSet<Assembly> _frameworkAssemblies = new HashSet<Assembly>
		{
				typeof(object).Assembly, // mscorelib
				typeof(Uri).Assembly, // System.dll
				typeof(Enumerable).Assembly, // System.Core.dll
		};


		static CerasSerializer()
		{
			// Type
			var type = typeof(Type);

			// ReSharper disable once PossibleMistakenCallToGetType.2
			_rtTypeType = type.GetType(); // It's extremely rare, but yes, we do indeed want to call GetType() on Type

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

		Type[] _knownTypes; // Copy of the list given by the user in Config; Array iteration is faster though

		// A special resolver. It creates instances of the "dynamic formatter", the DynamicObjectFormatter<> is a type that uses dynamic code generation to create efficient read/write methods
		// for a given object type.
		readonly IFormatterResolver _dynamicResolver;

		// The user provided resolver, will always be queried first
		readonly FormatterResolverCallback[] _userResolvers;

		internal readonly ITypeBinder TypeBinder;

		// The primary list of resolvers. A resolver is a class that somehow (by instantiating, or finding it somewhere, ...) comes up with a formatter for a requested type
		// If a resolver can't fulfill the request for a specific formatter, it returns null.
		readonly List<IFormatterResolver> _resolvers = new List<IFormatterResolver>();


		readonly TypeDictionary<TypeMetaData> _metaData = new TypeDictionary<TypeMetaData>();
		readonly FactoryPool<InstanceData> _instanceDataPool;

		internal readonly Action<object> DiscardObjectMethod;

		readonly Stack<InstanceData> _recursionStack = new Stack<InstanceData>();
		internal InstanceData InstanceData;
		int _recursionDepth = 0;
		RecursionMode _mode = RecursionMode.Idle; // while in one mode we cannot enter the others

		/// <summary>
		/// <para>The state-checksum of the serializer.</para>
		/// <para>Many configuration settings and all KnownTypes contribute to the checksum.</para>
		/// <para>Useful for networking scenarios, so when connecting you can ensure client and server are using the same settings and KnownTypes.</para>
		/// <para>Keep in mind that any dynamically configured types (<see cref="SerializerConfig.OnConfigNewType"/>) obviously cannot contribute to the checksum, but are still able to influence the serialization (and thus break network interoperability even when the checksum matches)</para>
		/// </summary>
		public ProtocolChecksum ProtocolChecksum { get; } = new ProtocolChecksum();

		/// <summary>
		/// Creates a new CerasSerializer, be sure to check out the tutorial.
		/// </summary>
		public CerasSerializer(SerializerConfig config = null)
		{
			Config = config ?? new SerializerConfig();

			if (Config.ExternalObjectResolver == null)
				Config.ExternalObjectResolver = new ErrorResolver();

			if (Config.Advanced.UseReinterpretFormatter && Config.VersionTolerance == VersionTolerance.AutomaticEmbedded)
				throw new NotSupportedException("You can not use 'UseReinterpretFormatter' together with version tolerance. Either disable version tolerance, or use the old formatter for blittable types by setting 'Config.Advanced.UseReinterpretFormatter' to false.");

			TypeBinder = Config.Advanced.TypeBinder ?? new NaiveTypeBinder();
			DiscardObjectMethod = Config.Advanced.DiscardObjectMethod;

			_userResolvers = Config.OnResolveFormatter.ToArray();

			// Int, Float, Enum, ...
			_resolvers.Add(new PrimitiveResolver(this));

			// DateTime, Guid, KeyValuePair, Tuple, ...
			_resolvers.Add(new StandardFormatterResolver(this));

			// Array, List, Dictionary, ICollection<T>, ...
			_resolvers.Add(new CollectionFormatterResolver(this));

			// String Formatter should never be wrapped in a RefFormatter, that's too slow and not necessary
			IFormatter stringFormatter;
			if (Config.Advanced.SizeLimits.MaxStringLength < uint.MaxValue)
				stringFormatter = new MaxSizeStringFormatter(Config.Advanced.SizeLimits.MaxStringLength);
			else
				stringFormatter = new StringFormatter();
			InjectDependencies(stringFormatter);
			SetFormatters(typeof(string), stringFormatter, stringFormatter);

			//
			// Type formatter is the basis for all complex objects,
			// It is special and has its own caching system (so no wrapping in a ReferenceFormatter)
			var typeFormatter = new TypeFormatter(this);

			var runtimeType = GetType().GetType();

			SetFormatters(typeof(Type), typeFormatter, typeFormatter);
			SetFormatters(runtimeType, typeFormatter, typeFormatter);

			// MemberInfos (FieldInfo, RuntimeFieldInfo, ...)
			_resolvers.Add(new ReflectionFormatterResolver(this));

			// Fast native copy for unmanaged types
			_resolvers.Add(new ReinterpretFormatterResolver(this));

			// DynamicObjectResolver is a special case, so it is not in the resolver-list
			// That is because we only want to have specific resolvers in the resolvers-list
			_dynamicResolver = new DynamicObjectFormatterResolver(this);

			// System.Linq.Expressions - mostly handled by special configurations and DynamicObjectFormatter, but there are some special cases.
			_resolvers.Add(new ExpressionFormatterResolver());


			//
			// Basic setup is done
			// Now calculate the protocol checksum
			_knownTypes = Config.KnownTypes.ToArray();
			if (Config.KnownTypes.Distinct().Count() != _knownTypes.Length)
			{
				// We want a *good* error message. Simply saying "can't contain any type multiple times" is not enough.
				// We have to figure out which types are there more than once.
				HashSet<Type> hashSet = new HashSet<Type>();
				List<Type> foundDuplicates = new List<Type>();

				for (int i = 0; i < _knownTypes.Length; i++)
				{
					var t = _knownTypes[i];
					if (!hashSet.Add(t))
						if (!foundDuplicates.Contains(t))
							foundDuplicates.Add(t);
				}

				var duplicateTypesStr = string.Join(", ", foundDuplicates.Select(t => t.Name));

				throw new Exception("KnownTypes can not contain any type multiple times! Your KnownTypes collection contains the following types more than once: " + duplicateTypesStr);
			}

			//
			// Generate checksum
			{
				foreach (var t in _knownTypes)
				{
					ProtocolChecksum.Add(t.FullName);

					if (t.IsEnum)
					{
						// Enums are a special case, they are classes internally, but they only have one field ("__value")
						// We're always serializing them in binary with their underlying type, so there's no reason changes like Adding/Removing/Renaming
						// enum-members could ever cause any binary incompatibility
						//
						// A change in the base-type however WILL cause problems!
						ProtocolChecksum.Add(t.GetEnumUnderlyingType().FullName);
						continue;
					}

					if (!t.ContainsGenericParameters)
					{
						var meta = GetTypeMetaData(t);
						if (meta.PrimarySchema != null)
							foreach (var m in meta.PrimarySchema.Members)
							{
								ProtocolChecksum.Add(m.MemberType.FullName);
								ProtocolChecksum.Add(m.MemberName);

								foreach (var a in m.MemberInfo.GetCustomAttributes(true))
									ProtocolChecksum.Add(a.ToString());
							}
					}

				}

				ProtocolChecksum.Finish();
			}

			//
			// We can already pre-warm formatters
			// - dynamic serializers generate their code
			// - reference formatters generate their wrappers
			foreach (var t in _knownTypes)
				if (!t.ContainsGenericParameters)
					GetReferenceFormatter(t);



			//
			// Finally we need "instance data"
			_instanceDataPool = new FactoryPool<InstanceData>(p =>
			{
				var d = new InstanceData();
				d.CurrentRoot = null;
				d.ObjectCache = new ObjectCache();
				d.TypeCache = new TypeCache(_knownTypes);
				d.EncounteredSchemaTypes = new HashSet<Type>();

				return d;
			});
			InstanceData = _instanceDataPool.RentObject();

			if (Config.Advanced.SealTypesWhenUsingKnownTypes)
				if (_knownTypes.Length > 0)
					typeFormatter.Seal();
		}



		/// <summary>!! Only use this method for testing !!
		/// <para>This method is pretty inefficient because it has to allocate an array for you and later resize it!</para>
		/// For much better performance use <see cref="Serialize{T}(T, ref byte[], int)"/> instead.
		/// <para>Take a quick look at the first step of the tutorial (it's on GitHub) if you are not sure how.</para>
		/// </summary>
		public byte[] Serialize<T>(T obj)
		{
			// Most of the time users write smaller objects when using this overload
			byte[] result = new byte[0x1000];

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

			if (buffer == null)
				buffer = new byte[0x4000]; // 16k			

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
					if (Config.Advanced.EmbedChecksum)
						SerializerBinary.WriteInt32Fixed(ref buffer, ref offset, ProtocolChecksum.Checksum);

					var formatter = (IFormatter<T>)GetReferenceFormatter(typeof(T));
					formatter.Serialize(ref buffer, ref offset, obj);
				}
				int offsetAfterWrite = offset;


				//
				// After we're done, we have to clear all our caches!
				// Only very rarely can we avoid that
				// todo: implement check-pointing inside the TypeDictionary itself
				if (!Config.Advanced.PersistTypeCache)
					InstanceData.TypeCache.ResetSerializationCache();

				InstanceData.ObjectCache.ClearSerializationCache();

				int dataSize = offsetAfterWrite - offsetBeforeWrite;


				return dataSize;
			}
			finally
			{
				//
				// Clear the root object again
				//InstanceData.WrittenSchemata.Clear();
				InstanceData.EncounteredSchemaTypes.Clear();
				InstanceData.CurrentRoot = null;

				LeaveRecursive(RecursionMode.Serialization);
			}
		}



		/// <summary>
		/// Convenience method that will most likely allocate a T to return (using 'new T()'). Unless the data says the object really is null, in that case no instance of T is allocated.
		/// It would be smart to not use this method and instead use another overload. 
		/// That way the deserializer will set/populate the object you've provided. Obviously this only works if you can overwrite/reuse objects like this! (which, depending on what you're doing, might not be possible at all)
		/// </summary>
		public T Deserialize<T>(byte[] buffer)
		{
			T value = default;
			int offset = 0;
			Deserialize(ref value, buffer, ref offset);
			return value;
		}


		/// <summary>
		/// Deserializes an object from previously serialized data.
		/// <para>You can put in anything for the <paramref name="value"/>, and if the object in the data matches, Ceras will populate your existing object (overwrite its fields, clear/refill the collections, ...)</para>
		/// <para>Keep in mind that the config settings used for serialization must match exactly (should be obvious tho)</para>
		/// </summary>
		public void Deserialize<T>(ref T value, byte[] buffer)
		{
			int offset = 0;
			Deserialize(ref value, buffer, ref offset, -1);
		}

		/// <summary>
		/// The most advanced deserialize method.
		/// <para>Again, you can put in an existing object to populate (or a variable that's currently null, in which case Ceras creates an object for you)</para>
		/// <para>In this version you can put in the offset as well, telling Ceras where to start reading from inside the buffer.</para>
		/// <para>After the method is done, the offset will be where Ceras has stopped reading.</para>
		/// <para>If you pass in a value >0 for <paramref name="expectedReadLength"/> then Ceras will check how many bytes it has read (only rarely useful)</para>
		/// </summary>
		public void Deserialize<T>(ref T value, byte[] buffer, ref int offset, int expectedReadLength = -1)
		{
			if (buffer == null)
				throw new ArgumentNullException("Must provide a buffer to deserialize from!");

			EnterRecursive(RecursionMode.Deserialization);

			try
			{
				int offsetBeforeRead = offset;

				//
				// Actual deserialization
				{
					if (Config.Advanced.EmbedChecksum)
					{
						var checksum = SerializerBinary.ReadInt32Fixed(buffer, ref offset);
						if (checksum != ProtocolChecksum.Checksum)
							throw new InvalidOperationException($"Checksum does not match embedded checksum (Serializer={ProtocolChecksum.Checksum}, Data={checksum})");
					}

					var formatter = (IFormatter<T>)GetReferenceFormatter(typeof(T));
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

				// todo: use a custom 'Bag' collection or so for deserialization caches, so we can quickly remove everything above a certain index 

				// Clearing and re-adding the known types is really bad...
				// But how would we optimize it best?
				// Maybe having a sort of fallback-cache? I guess it would be faster than what we're doing now,
				// but then we'd have an additional if() check every time :/
				//
				// The deserialization cache is a list, we'd check for out of range, and then check the secondary cache?
				//
				// Or maybe the most straight-forward approach would be to simply remember at what index the KnownTypes end and the dynamic types start,
				// and then we can just RemoveRange() everything above the known index...
				if (!Config.Advanced.PersistTypeCache)
					InstanceData.TypeCache.ResetDeserializationCache();

				InstanceData.ObjectCache.ClearDeserializationCache();
			}
			finally
			{
				LeaveRecursive(RecursionMode.Deserialization);
			}
		}

		/// <summary>
		/// Allows you to "peek" the object the data contains without having to fully deserialize the whole object.
		/// <para>Only works for data that was saved without version tolerance (maybe that will be supported eventually, if someone requests it)</para>
		/// </summary>
		public Type PeekType(byte[] buffer)
		{
			Type t = null;
			int offset = 0;
			GetFormatter<Type>().Deserialize(buffer, ref offset, ref t);

			return t;
		}


		/// <summary>
		/// Get all resolvers that this <see cref="CerasSerializer"/> has available. Does not include any user-registered callbacks in <see cref="SerializerConfig.OnResolveFormatter"/>.
		/// </summary>
		public IEnumerable<IFormatterResolver> GetFormatterResolvers()
		{
			foreach (var r in _resolvers)
				yield return r;
			yield return _dynamicResolver;
		}

		/// <summary>
		/// Get an instance of any specific type of resolver (or null if no resolver matching that type can be found)
		/// </summary>
		public IFormatterResolver GetFormatterResolver<TResolver>() where TResolver : IFormatterResolver
		{
			return GetFormatterResolvers().OfType<TResolver>().FirstOrDefault();
		}



		/// <summary>
		/// This is a shortcut to the <see cref="GetReferenceFormatter(Type)"/> method
		/// </summary>
		public IFormatter<T> GetFormatter<T>() => (IFormatter<T>)GetReferenceFormatter(typeof(T));

		/// <summary>
		/// Returns one of Ceras' internal formatters for some type.
		/// It automatically returns the right one for whatever type is passed in.
		/// </summary>
		public IFormatter GetReferenceFormatter(Type type)
		{
			var meta = GetTypeMetaData(type);

			if (meta.IsValueType)
			{
				// Value types are not reference types, so they are not wrapped
				return GetSpecificFormatter(type, meta);
			}

			// 1.) Cache
			if (meta.ReferenceFormatter != null)
				return meta.ReferenceFormatter;


			// 2.) Create a reference formatter (which internally obtains the matching specific one)

			// var refFormatterType = typeof(ReferenceFormatter<>).MakeGenericType(type);
			Type refFormatterType;

			refFormatterType = typeof(ReferenceFormatter<>).MakeGenericType(type);

			var referenceFormatter = (IFormatter)Activator.CreateInstance(refFormatterType, this);

			meta.ReferenceFormatter = referenceFormatter;

			return referenceFormatter;
		}

		/// <summary>
		/// Similar to <see cref="GetReferenceFormatter(Type)"/> it returns a formatter, but one that is not wrapped in a <see cref="ReferenceFormatter{T}"/>.
		/// <para>You probably always want to use <see cref="GetReferenceFormatter(Type)"/>, and only use this method instead when you are 100% certain you have emulated everything that <see cref="ReferenceFormatter{T}"/> does for you.</para>
		/// </summary>
		public IFormatter GetSpecificFormatter(Type type)
		{
			var meta = GetTypeMetaData(type);

			return GetSpecificFormatter(type, meta);
		}

		IFormatter GetSpecificFormatter(Type type, TypeMetaData meta)
		{
			// 1.) Cache
			if (meta.SpecificFormatter != null)
				return meta.SpecificFormatter;


			// 2.) TypeConfig - Custom Formatter, Custom Resolver 
			if (meta.TypeConfig.CustomFormatter != null)
			{
				meta.SpecificFormatter = meta.TypeConfig.CustomFormatter;
				FormatterHelper.ThrowOnMismatch(meta.SpecificFormatter, type);
				InjectDependencies(meta.SpecificFormatter);
				return meta.SpecificFormatter;
			}
			if (meta.TypeConfig.CustomResolver != null)
			{
				var formatter = meta.TypeConfig.CustomResolver(this, type);
				meta.SpecificFormatter = formatter ?? throw new InvalidOperationException($"The custom formatter-resolver registered for Type '{type.FullName}' has returned 'null'.");
				FormatterHelper.ThrowOnMismatch(meta.SpecificFormatter, type);
				InjectDependencies(meta.SpecificFormatter);
				return meta.SpecificFormatter;
			}
			

			// 3.) User
			for (int i = 0; i < _userResolvers.Length; i++)
			{
				var formatter = _userResolvers[i](this, type);
				if (formatter != null)
				{
					meta.SpecificFormatter = formatter;
					FormatterHelper.ThrowOnMismatch(meta.SpecificFormatter, type);
					InjectDependencies(formatter);
					return formatter;
				}
			}


			// 4.) Depending on the VersionTolerance we use different formatters
			if (Config.VersionTolerance == VersionTolerance.AutomaticEmbedded)
			{
				if (!meta.IsFrameworkType)
				{
					// Create SchemaFormatter, it will automatically adjust itself to the schema when it's read
					var formatterType = typeof(SchemaDynamicFormatter<>).MakeGenericType(type);
					var schemaFormatter = (IFormatter)Activator.CreateInstance(formatterType, args: new object[] { this, meta.PrimarySchema });

					meta.SpecificFormatter = schemaFormatter;
					return schemaFormatter;
				}
			}


			// 5.) Built-in
			for (int i = 0; i < _resolvers.Count; i++)
			{
				var formatter = _resolvers[i].GetFormatter(type);
				if (formatter != null)
				{
					meta.SpecificFormatter = formatter;
					InjectDependencies(formatter);
					return formatter;
				}
			}


			// 6.) Dynamic
			{
				var formatter = _dynamicResolver.GetFormatter(type);
				if (formatter != null)
				{
					meta.SpecificFormatter = formatter;
					InjectDependencies(formatter);
					return formatter;
				}
			}

			throw new NotSupportedException($"Ceras could not find any IFormatter<T> for the type '{type.FullName}'. Maybe exclude that field/prop from serializaion or write a custom formatter for it.");
		}


		internal TypeMetaData GetTypeMetaData(Type type)
		{
			ref var meta = ref _metaData.GetOrAddValueRef(type);
			if (meta != null)
				return meta;

			bool isFrameworkType;
			// In the context of versioning, arrays are considered a framework type, because arrays are always serialized the same way.
			// It's only the elements themselves that are serialized differently!
			if (type.IsArray)
				isFrameworkType = true;
			else
				isFrameworkType = _frameworkAssemblies.Contains(type.Assembly);

			BannedTypes.ThrowIfBanned(type);

			var typeConfig = Config.GetTypeConfig(type);

			meta = new TypeMetaData(type, typeConfig, isFrameworkType);

			meta.CurrentSchema = meta.PrimarySchema = CreatePrimarySchema(type);

			return meta;
		}

		void SetFormatters(Type type, IFormatter specific, IFormatter reference)
		{
			var meta = GetTypeMetaData(type);

			meta.SpecificFormatter = specific;
			meta.ReferenceFormatter = reference;
		}


		void InjectDependencies(IFormatter formatter)
		{
			// Straightforward DI system
			// Injects:
			// - IFormatter<> and types derived from it
			// - CerasSerializer

			var formatterType = formatter.GetType();
			var config = formatterType.GetCustomAttribute<CerasInjectAttribute>() ?? CerasInjectAttribute.Default;

			var flags = BindingFlags.Public | BindingFlags.Instance;
			if (config.IncludePrivate)
				flags |= BindingFlags.NonPublic;

			var fields = formatter.GetType().GetFields(flags);
			foreach (var f in fields)
			{
				if (f.GetCustomAttribute<NoInjectAttribute>() != null)
					continue;

				var t = f.FieldType;

				// Reference to the serializer
				if (t == typeof(CerasSerializer))
				{
					SafeInject(formatter, f, this);
					continue;
				}

				// Any formatter?
				if (!typeof(IFormatter).IsAssignableFrom(t))
					continue;


				var formatterInterface = ReflectionHelper.FindClosedType(t, typeof(IFormatter<>));

				if (formatterInterface == null)
					continue; // Not a formatter? Then that's not something we can handle

				var formattedType = formatterInterface.GetGenericArguments()[0];

				// Any formatter that can handle the given type
				if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IFormatter<>))
				{
					var requestedFormatter = GetReferenceFormatter(formattedType);

					SafeInject(formatter, f, requestedFormatter);

					continue;
				}

				// Some very specific formatter
				if (ReflectionHelper.IsAssignableToGenericType(t, typeof(IFormatter<>)))
				{
					var refFormatter = GetReferenceFormatter(formattedType);

					if (refFormatter.GetType() == t)
					{
						// This was the formatter we were looking for
						SafeInject(formatter, f, refFormatter);
						continue;
					}

					// Maybe we're dealing with a reference type and the user explicitly wants the direct formatter?
					// todo: there should be some kind of warning maybe, but how/where would we output it?
					// If the formattedType is a referenceType and the user uses the direct formatter things could get ugly (references are not handled at all, user has to do it on his own)
					var directFormatter = GetSpecificFormatter(formattedType);

					if (directFormatter.GetType() == t)
					{
						SafeInject(formatter, f, directFormatter);
						continue;
					}


					var anyExisting = directFormatter ?? refFormatter;

					throw new InvalidOperationException($"The formatter '{formatter.GetType().FullName}' has a dependency on '{t.GetType().FullName}' (via the field '{f.Name}') to format '{formattedType.FullName}', but this Ceras instance is already using '{anyExisting.GetType().FullName}' to handle this type.");
				}
			}

			void SafeInject(object f, FieldInfo field, object value)
			{
				var existingValue = field.GetValue(f);

				if (existingValue == null)
				{
					// All good
					field.SetValue(f, value);
				}
				else
				{
					// If the value matches already, that's ok
					// If it doesn't something is wrong.
					if (ReferenceEquals(existingValue, value))
						return; // Value got set from somewhere else already
					else
						throw new InvalidOperationException($"Error while injecting dependencies into Formatter '{f}'. The field '{field.Name}' already has a value ('{existingValue}') that is not equal to the intended value '{value}'");
				}
			}
		}



		internal void ActivateSchemaOverride(Type type, Schema schema)
		{
			var meta = GetTypeMetaData(type);

			//
			// 1. Is this schema already active for the current type?
			//    Maybe we're still set from last serialization/deserialization?
			if (Equals(meta.CurrentSchema, schema))
				return;


			// 2. Set the schema as the active one
			meta.CurrentSchema = schema;


			// 3. Ensure 'SpecificFormatter' is prepared and set
			//    (can only be SchemaDynamicFormatter<>)
			GetSpecificFormatter(type, meta);


			// 4. Notify every formatter that wants to know about schema changes to this type
			for (int i = 0; i < meta.OnSchemaChangeTargets.Count; i++)
			{
				var taintedFormatter = meta.OnSchemaChangeTargets[i];
				taintedFormatter.OnSchemaChanged(meta);
			}
		}



		// Creates the primary schema for a given type
		Schema CreatePrimarySchema(Type type)
		{
			Schema schema = new Schema(true, type);

			var typeConfig = Config.GetTypeConfig(type);
			typeConfig.Seal();

			foreach (var memberConfig in typeConfig._allMembers)
			{
				if (memberConfig.ComputeFinalInclusionFast())
				{
					var schemaMember = new SchemaMember(memberConfig.PersistentName, SerializedMember.Create(memberConfig.Member, true));
					schema.Members.Add(schemaMember);
				}
				else
				{
					// Member is not part of the schema
					continue;
				}
			}

			// Order is super important.
			// Versioning, robustness, performance (batching multiple primtives, ...)
			schema.Members.Sort(SchemaMemberComparer.Instance);

			return schema;
		}

		internal Schema ReadSchema(byte[] buffer, ref int offset, Type type)
		{
			// todo 1: Skipping
			// We should add some sort of skipping mechanism later.
			// We would write the schema-hash as well, and when reading it again we can check for the
			// hash and see of we already have that schema (and skip reading it!)
			// Or maybe we could at least find some way to make reading it cheaper (not instantiating a schema and lists and stuff that we won't use anyway)

			var meta = GetTypeMetaData(type);

			if (meta.IsFrameworkType)
				throw new InvalidOperationException("Cannot read a Schema for a framework type! This must be either a serious bug, or the given data has been tampered with. Please report it on GitHub!");

			//
			// Read Schema
			var schema = new Schema(false, type);

			var memberCount = SerializerBinary.ReadInt32(buffer, ref offset);
			for (int i = 0; i < memberCount; i++)
			{
				var name = SerializerBinary.ReadString(buffer, ref offset);

				var member = Schema.FindMemberInType(type, name);

				if (member == null)
					schema.Members.Add(new SchemaMember(name));
				else
					schema.Members.Add(new SchemaMember(name, SerializedMember.Create(member, true)));
			}

			//
			// Add entry or return existing
			List<Schema> secondaries = meta.SecondarySchemata;
			var existing = secondaries.IndexOf(schema);
			if (existing == -1)
			{
				secondaries.Add(schema);
				return schema;
			}
			else
			{
				return secondaries[existing];
			}
		}

		internal static void WriteSchema(ref byte[] buffer, ref int offset, Schema schema)
		{
			if (!schema.IsPrimary)
				throw new InvalidOperationException("Can't write schema that doesn't match the primary. This is a bug, please report it on GitHub!");

			// Write the schema...
			var members = schema.Members;
			SerializerBinary.WriteInt32(ref buffer, ref offset, members.Count);

			for (int i = 0; i < members.Count; i++)
				SerializerBinary.WriteString(ref buffer, ref offset, members[i].PersistentName);
		}


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
		public TypeCache TypeCache;
		public ObjectCache ObjectCache;

		public IExternalRootObject CurrentRoot;

		// Populated while writing so we know what schemata have actually been used.
		// public HashSet<Schema> WrittenSchemata;

		// Why <Type> instead of <Schema> ? Becasue while reading we'll never encounter multiple different schemata for the same type.
		// And while writing we'll only ever use the primary schema.
		public HashSet<Type> EncounteredSchemaTypes;
	}

	enum RecursionMode
	{
		Idle,
		Serialization,
		Deserialization,
	}


	/*
	todo: this feature will be delayed until there's more need. Delegates work fine for now, maybe we want auto-pooling per object type as well at some point?=

	/// <summary>
	/// This interface marks the 3 different pooling implementations you can use: DelegatePoolingImplementation, StaticTypePoolingImplementation, InstancePoolingImplementation. I strongly recommend reading the guide to get an idea of when to use which pooling implementation.
	/// </summary>
	interface IPoolingImplementation { }

	public class DelegatePoolingImplementation : IPoolingImplementation
	{
		Func<Type, object> ObjectFactoryMethod { get; set; }
		Action<object> DiscardObjectMethod { get; set; }
	}

	public class StaticTypePoolingImplementation : IPoolingImplementation
	{
		public Type StaticPoolType;
	}

	public class InstancePoolingImplementation : IPoolingImplementation
	{
		public object PoolInstance;
	}
	*/

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


	class TypeMetaData
	{
		public readonly Type Type;
		public readonly bool IsFrameworkType;
		public readonly bool IsValueType;

		public readonly TypeConfig TypeConfig;

		public IFormatter SpecificFormatter;
		public IFormatter ReferenceFormatter;

		public Schema CurrentSchema;


		public Schema PrimarySchema;
		public readonly List<Schema> SecondarySchemata = new List<Schema>();

		// Anyone (any formatter) who is interested in schema changes for this type should enter themselves in this list
		public readonly List<ISchemaTaintedFormatter> OnSchemaChangeTargets = new List<ISchemaTaintedFormatter>();


		public TypeMetaData(Type type, TypeConfig typeConfig, bool isFrameworkType)
		{
			Type = type;
			IsFrameworkType = isFrameworkType;
			IsValueType = type.IsValueType;
			TypeConfig = typeConfig;
		}
	}
}
