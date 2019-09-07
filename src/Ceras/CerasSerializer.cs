// ReSharper disable RedundantTypeArgumentsOfMethod
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LiveTesting")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ceras.Test")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ceras.AotGenerator")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ceras.TestDebugger")]
namespace Ceras
{
	using Exceptions;
	using Formatters;
	using Helpers;
	using Resolvers;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Runtime.CompilerServices;
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
	/// <para>For performance reasons <see cref="CerasSerializer"/>, <see cref="SerializerConfig"/>, and <see cref="TypeConfig"/> are not thread-safe! You shouldn't share a single instance of a SerializerConfig either.</para>
	/// </summary>
	public class CerasSerializer : ICerasAdvanced
	{
		// Some types are constructed by the formatter directly
		internal static readonly Type _rtTypeType, _rtFieldType, _rtPropType, _rtCtorType, _rtMethodType;
		static readonly HashSet<Type> _formatterConstructedTypes = new HashSet<Type>();

		/// <summary>
		/// Use only when you're creating a IFormatter implementation. Otherwise use config.ConfigType()!
		/// </summary>
		public static void AddFormatterConstructedType(Type type)
		{
			lock (_formatterConstructedTypes)
				_formatterConstructedTypes.Add(type);
		}

		internal static bool IsFormatterConstructed(Type type)
		{
			// Array is also always constructed by the caller, but it is handled separately

			if (type.IsAbstract)
				return true;

			lock (_formatterConstructedTypes)
				return _formatterConstructedTypes.Contains(type);
		}

		internal static bool IsPrimitiveType(Type type)
		{
			// Ceras has built-in support for some special types. Those are considered "primitives".
			// This definition has little to do with 'Type.IsPrimitive', it's more about what Types have a "Schema".

			if (type.IsPrimitive)
				return true;

			if (type.IsArray)
				return IsPrimitiveType(type.GetElementType());

			if (type == typeof(string))
				return true;

			if (type == typeof(Type))
				return true;

			if (type == _rtTypeType || type == _rtFieldType || type == _rtPropType || type == _rtCtorType || type == _rtMethodType)
				return true;

			return false;
		}

		internal static HashSet<Assembly> _frameworkAssemblies = new HashSet<Assembly>
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

		/// <summary>
		/// Ceras caches and pools its internal objects to make serialization/deserialization as fast as possible and avoid GC-pressure. When you serialize/deserialize large object graphs those caches will grow in size but they won't shrink by themselves. When you know that you won't be serializing any more large objects (or just want to free some memory) you can call this method and Ceras will clear its caches.
		/// <para>The internal memory is not referenced anymore so the garbage collector will reclaim it the next time it runs.</para>
		/// <para>Might have a performance impact on any subsequent Serialize/Deserialize calls since the caches will have to be rebuilt/reallocated again.</para>
		/// </summary>
		public static void ClearGenericCaches()
		{
			ObjectCache.RefProxyPoolRegister.TrimAll();
		}

		internal readonly SerializerConfig Config;
		/// <summary>
		/// Get the config instance that was passed to the constructor of the serializer. Don't use this to modify any settings!
		/// </summary>
		public SerializerConfig GetConfig() => Config;

		/// <summary>
		/// More advanced features...
		/// </summary>
		public ICerasAdvanced Advanced => this;


		readonly Type[] _knownTypes; // Copy of the list given by the user in Config; Array iteration is faster

		// A special resolver. It creates instances of the "dynamic formatter", the DynamicFormatter<> is a type that uses dynamic code generation to create efficient read/write methods
		// for a given object type.
		readonly DynamicObjectFormatterResolver _dynamicResolver;

		// The user provided resolver, will always be queried first
		readonly FormatterResolverCallback[] _userResolvers;

		internal readonly ITypeBinder TypeBinder;

		// The primary list of resolvers. A resolver is a class that somehow (by instantiating, or finding it somewhere, ...) comes up with a formatter for a requested type
		// If a resolver can't fulfill the request for a specific formatter, it returns null.
		readonly List<IFormatterResolver> _resolvers = new List<IFormatterResolver>();


		readonly TypeDictionary<TypeMetaData> _metaData = new TypeDictionary<TypeMetaData>();
		readonly TypeDictionary<TypeMetaData> _staticMetaData = new TypeDictionary<TypeMetaData>();
		readonly FactoryPool<InstanceData> _instanceDataPool;

		internal readonly Action<object> DiscardObjectMethod;

		readonly Stack<InstanceData> _recursionStack = new Stack<InstanceData>();
		internal InstanceData InstanceData;
		int _recursionDepth = 0;
		RecursionMode _mode = RecursionMode.Idle; // while in one mode we cannot enter the others

		/// <summary>
		/// A "free" property where you can store anything.
		/// <para>This is useful for scenarios where callbacks like <see cref="OnBeforeSerializeAttribute"/> are used, because the <see cref="CerasSerializer"/> instance is passed to those methods (assuming they take a <see cref="CerasSerializer"/> instance as an argument). That way you can obtain the <see cref="UserContext"/> inside those methods.</para>
		/// </summary>
		public object UserContext
		{
			get => InstanceData.UserContext;
			set => InstanceData.UserContext = value;
		}

		/// <summary>
		/// <para>The state-checksum of the serializer.</para>
		/// <para>Many configuration settings and all KnownTypes contribute to the checksum.</para>
		/// <para>Useful for networking scenarios, so when connecting you can ensure client and server are using the same settings and KnownTypes.</para>
		/// <para>Keep in mind that any dynamically configured types (<see cref="SerializerConfig.OnConfigNewType"/>) obviously cannot contribute to the checksum, but are still able to influence the serialization (and thus break network interoperability even when the checksum matches)</para>
		/// </summary>
		public ProtocolChecksum ProtocolChecksum { get; } = new ProtocolChecksum();

		/// <summary>
		/// Creates a CerasSerializer. Using the default settings should be ok for most use-cases, but there are lots of settings you can use to optimize for all kinds of scenarios!
		/// <para>Make sure to check out the wiki for lots of guides, tips, ...</para>
		/// <para>https://github.com/rikimaru0345/Ceras/wiki </para>
		/// </summary>
		public CerasSerializer(SerializerConfig config = null)
		{
			if (config != null)
				if (!config.Claim(this))
					throw new CerasException($"The given SerializerConfig was used to construct a CerasSerializer already. A SerializerConfig can not be shared/reused.");


			Config = config ?? new SerializerConfig();

			if (Config.ExternalObjectResolver == null)
				Config.ExternalObjectResolver = new ErrorResolver();

			if (Config.Advanced.UseReinterpretFormatter && Config.VersionTolerance.Mode != VersionToleranceMode.Disabled)
				throw new NotSupportedException("You can not use 'UseReinterpretFormatter' together with version tolerance. Either disable version tolerance, or use the old formatter for blittable types by setting 'Config.Advanced.UseReinterpretFormatter' to false.");

			if (Config.Advanced.AotMode != AotMode.None && Config.VersionTolerance.Mode != VersionToleranceMode.Disabled)
				throw new NotSupportedException("You can not use 'AotMode.Enabled' and version tolerance at the same time for now. If you would like this feature implemented, please open an issue on GitHub explaining your use-case, or join the Discord server.");

			if (Config.VersionTolerance.Mode == VersionToleranceMode.Extended)
				throw new NotSupportedException($"Extended VersionTolerance mode has not yet been implemented. Please read the documentation by hovering over '{nameof(VersionToleranceMode)}.{nameof(VersionToleranceMode.Extended)}' or pressing F12 with the cursor on it.");

			TypeBinder = Config.Advanced.TypeBinder;
			DiscardObjectMethod = Config.Advanced.DiscardObjectMethod;

			_userResolvers = Config.OnResolveFormatter.ToArray();

			// Int, Float, Enum, ...
			_resolvers.Add(new PrimitiveResolver(this));

			// Fast native copy for unmanaged types;
			// can not handle generic structs like ValueTuple<> because they always have to be ".IsAutoLayout"
			_resolvers.Add(new ReinterpretFormatterResolver(this));

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
			PrepareFormatter(stringFormatter);
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

			// DynamicObjectResolver is a special case, so it is not in the resolver-list
			// That is because we only want to have specific resolvers in the resolvers-list
			_dynamicResolver = new DynamicObjectFormatterResolver(this);

			// System.Linq.Expressions - mostly handled by special configurations and DynamicFormatter, but there are some special cases.
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

				var duplicateTypesStr = string.Join(", ", foundDuplicates.Select(t => t.FriendlyName()));

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
			_instanceDataPool = new FactoryPool<InstanceData>(() =>
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


			void SetFormatters(Type type, IFormatter specific, IFormatter reference)
			{
				var meta = GetTypeMetaData(type);

				meta.SpecificFormatter = specific;
				meta.ReferenceFormatter = reference;
			}
		}



		/// <summary>!! This method is slow, and only intended for testing!!
		/// <para>In addition to just serializing the given object, this method also allocates a new buffer to fit the given data perfectly. That way there are no excess bytes at the end of the returned buffer. That obviously has a small performance impact though!</para>
		/// <para>When you are done experimenting and want to get an immediate performance improvement, then take a look at the <see cref="Serialize{T}(T, ref byte[], int)"/> method instead</para>
		/// <para>Ceras is not thread-safe</para>
		/// </summary>
		public byte[] Serialize<T>(T obj)
		{
			// This method is often used for testing / smaller objects, so we allocate a relatively small buffer
			byte[] buffer = CerasBufferPool.Pool.RentBuffer(0x1000);

			int length = Serialize(obj, ref buffer);

			// Return the result in a perfectly sized buffer
			var result = new byte[length];

			if (length > 0)
			{
				unsafe
				{
					fixed (byte* source = &buffer[0])
					fixed (byte* target = &result[0])
						Unsafe.CopyBlock(target, source, (uint)length);
				}
			}

			// Return the rented buffer
			CerasBufferPool.Pool.Return(buffer);

			return result;
		}

		/// <summary>
		/// This is the main method you should use.
		/// <para>The intention is that you reuse the serialization buffer instead of creating a new array for every Serialize() call, this lets you avoid GC-pressure.</para>
		/// <para>You can pass in <c>null</c> for the buffer to let Ceras allocate a new one.</para>
		/// <para>If you have set <see cref="CerasBufferPool.Pool"/>, it will be used to create/expand the buffer. Since the whole idea is to keep reusing the serialization buffer as much as possible, this method will *only* return the buffer to the pool when expanding it (so when it will rent a new larger buffer anyway).</para>
		/// <para>Ceras is fully reentrant to support <see cref="IExternalRootObject"/>, but it is not thread-safe! </para>
		/// </summary>
		public int Serialize<T>(T obj, ref byte[] buffer, int offset = 0)
		{
			EnterRecursive(RecursionMode.Serialization);

			if (buffer == null)
				buffer = CerasBufferPool.Rent(0x4000); // 16k

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
				InstanceData.EncounteredSchemaTypes.Clear();
				InstanceData.CurrentRoot = null;

				LeaveRecursive(RecursionMode.Serialization);
			}
		}


		/// <summary>
		/// Convenience method that will most likely allocate a T to return (using 'new T()'). Unless the data says the object really is null, in that case no instance of T is allocated.
		/// It would be smart to not use this method and instead use another overload. 
		/// That way the deserializer will set/populate the object you've provided. Obviously this only works if you can overwrite/reuse objects like this! (which, depending on what you're doing, might not be possible at all)
		/// <para>Ceras is not thread-safe</para>
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
		/// <para>Ceras is not thread-safe</para>
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
		/// <para>Ceras is fully reentrant to support <see cref="IExternalRootObject"/>, but it is not thread-safe! </para>
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
				InstanceData.EncounteredSchemaTypes.Clear();


				// Reading beyond the buffer is possible in many scenarios (when using unsafe pointers to read some stuff directly)
				// Checking in front of every call would totally trash performance, so that's not a good option
				// But checking once, after we're done reading, if we've read beyond the array bounds is cheap and offers the same amount of
				// protection because we're still throwing an exception and not returning a value, so there's no danger of returning objects built from invalid data.
				if (offset > buffer.Length)
				{
					throw new IndexOutOfRangeException($"The read cursor ended up beyond the array bounds, meaning that the given data is corrupted. Was the given data serialized using different settings? Did any of the classes/structs change?");
				}
			}
			finally
			{
				LeaveRecursive(RecursionMode.Deserialization);
			}
		}


		T ICerasAdvanced.Clone<T>(T obj)
		{
			var buffer = CerasBufferPool.Rent(0x4000);

			Serialize(obj, ref buffer);

			T clone = default;
			Deserialize(ref clone, buffer);

			CerasBufferPool.Return(buffer);

			return clone;
		}


		byte[] ICerasAdvanced.SerializeStatic(Type type)
		{
			var tempBuffer = CerasBufferPool.Rent(0x1000);

			int offset = 0;
			int length = ((ICerasAdvanced)this).SerializeStatic(type, ref tempBuffer, ref offset);

			// Resize
			var result = new byte[length];

			if (length > 0)
				unsafe
				{
					fixed (byte* source = &tempBuffer[0])
					fixed (byte* target = &result[0])
						Unsafe.CopyBlock(target, source, (uint)length);
				}

			CerasBufferPool.Return(tempBuffer);

			return result;
		}

		int ICerasAdvanced.SerializeStatic(Type type, ref byte[] buffer, ref int offset)
		{
			if (type.ContainsGenericParameters)
				throw new InvalidOperationException("Type must not have any unspecified generic arguments");

			var meta = GetStaticTypeMetaData(type);
			if (meta.StaticSerialize == null)
				PrepareStaticSerializationDispatchers(meta);


			int startOffset = offset;
			meta.StaticSerialize(ref buffer, ref offset);

			return offset - startOffset;
		}

		void ICerasAdvanced.DeserializeStatic(Type type, byte[] buffer)
		{
			int offset = 0;
			((ICerasAdvanced)this).DeserializeStatic(type, buffer, ref offset);
		}

		int ICerasAdvanced.DeserializeStatic(Type type, byte[] buffer, ref int offset)
		{
			if (type.ContainsGenericParameters)
				throw new InvalidOperationException("Type must not have any unspecified generic arguments");

			var meta = GetStaticTypeMetaData(type);
			if (meta.StaticDeserialize == null)
				PrepareStaticSerializationDispatchers(meta);

			int offsetBefore = offset;
			meta.StaticDeserialize(buffer, ref offset);

			return offset - offsetBefore;
		}

		void PrepareStaticSerializationDispatchers(TypeMetaData meta)
		{
			var formatterType = typeof(DynamicFormatter<>).MakeGenericType(meta.Type);
			var f = (IFormatter)Activator.CreateInstance(formatterType, new object[] { this, true });
			meta.SpecificFormatter = f;
			((DynamicFormatter)f).Initialize();

			/* (ref buffer, ref offset) => 
			 * {
			 *    f.Serialize(ref buffer, ref offset, null);
			 * }
			 */
			var serialize = f.GetType().ResolveSerializeMethod(meta.Type);
			var refBufferArg = Expression.Parameter(typeof(byte[]).MakeByRefType(), "refBuffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "refOffset");

			meta.StaticSerialize = Expression.Lambda<StaticSerializeDelegate>(
				Expression.Call(
					instance: Expression.Constant(f),
					method: serialize,
					arguments: new Expression[] { refBufferArg, refOffsetArg, Expression.Constant(null, meta.Type) }),
				new ParameterExpression[] { refBufferArg, refOffsetArg }).Compile();


			/* (buffer, ref offset) =>
			 * {
			 *    f.Deserialize(buffer, ref offset, null);
			 * }
			 */
			var deserialize = f.GetType().ResolveDeserializeMethod(meta.Type);
			var bufferArg = Expression.Parameter(typeof(byte[]), "buffer");

			meta.StaticDeserialize = Expression.Lambda<StaticDeserializeDelegate>(
				Expression.Call(
					instance: Expression.Constant(f),
					method: deserialize,
					arguments: new Expression[] { bufferArg, refOffsetArg, Expression.Constant(null, meta.Type) }),
				new ParameterExpression[] { bufferArg, refOffsetArg }).Compile();
		}



		Type ICerasAdvanced.PeekType(byte[] buffer)
		{
			Type t = null;
			int offset = 0;
			GetFormatter<Type>().Deserialize(buffer, ref offset, ref t);

			return t;
		}

		IEnumerable<IFormatterResolver> ICerasAdvanced.GetFormatterResolvers()
		{
			foreach (var r in _resolvers)
				yield return r;
			yield return _dynamicResolver;
		}

		IFormatterResolver ICerasAdvanced.GetFormatterResolver<TResolver>()
		{
			return Advanced.GetFormatterResolvers().OfType<TResolver>().FirstOrDefault();
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

			if (meta.Type.IsValueType)
			{
				// Value types are not reference types, so they are not wrapped
				return GetSpecificFormatter(type, meta);
			}

			// 1.) Cache
			if (meta.ReferenceFormatter != null)
				return meta.ReferenceFormatter;


			// 2.) Create a reference formatter (which internally obtains the matching specific one)
			Type refFormatterType;

			bool isType = typeof(Type).IsAssignableFrom(type);
			bool isExternalRootObj = typeof(IExternalRootObject).IsAssignableFrom(type);
			bool isVersionTolerantMode = Config.VersionTolerance.Mode != VersionToleranceMode.Disabled;
			if (type.IsSealed && !isType && !isExternalRootObj && !isVersionTolerantMode)
				refFormatterType = typeof(ReferenceFormatter_KnownSealedType<>).MakeGenericType(type);
			else
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

			// Sanity checks
			if (type.IsAbstract() || type.IsInterface || type.ContainsGenericParameters)
				throw new InvalidOperationException("You cannot get a formatter for abstract, static, open generic, or interface types.");


			// 2.) TypeConfig - Custom Formatter, Custom Resolver
			if (!meta.IsPrimitive && meta.TypeConfig.CustomFormatter != null)
			{
				meta.SpecificFormatter = meta.TypeConfig.CustomFormatter;
				FormatterHelper.ThrowOnMismatch(meta.SpecificFormatter, type);
				PrepareFormatter(meta.SpecificFormatter);
				return meta.SpecificFormatter;
			}
			if (!meta.IsPrimitive && meta.TypeConfig.CustomResolver != null)
			{
				var formatter = meta.TypeConfig.CustomResolver(this, type);
				meta.SpecificFormatter = formatter ?? throw new InvalidOperationException($"The custom formatter-resolver registered for Type '{type.FullName}' has returned 'null'.");
				FormatterHelper.ThrowOnMismatch(meta.SpecificFormatter, type);
				PrepareFormatter(meta.SpecificFormatter);
				return meta.SpecificFormatter;
			}


			// 3.) User
			if (!meta.IsPrimitive)
				for (int i = 0; i < _userResolvers.Length; i++)
				{
					var formatter = _userResolvers[i](this, type);
					if (formatter != null)
					{
						meta.SpecificFormatter = formatter;
						FormatterHelper.ThrowOnMismatch(meta.SpecificFormatter, type);
						PrepareFormatter(formatter);
						return formatter;
					}
				}

			// 4.) Built-in
			for (int i = 0; i < _resolvers.Count; i++)
			{
				var formatter = _resolvers[i].GetFormatter(type);
				if (formatter != null)
				{
					meta.SpecificFormatter = formatter;
					PrepareFormatter(formatter);
					return formatter;
				}
			}

			// 5.) Dynamic (optionally using Schema)
			{
				var formatter = _dynamicResolver.GetFormatter(type);
				if (formatter != null)
				{
					meta.SpecificFormatter = formatter;
					PrepareFormatter(formatter);
					return formatter;
				}
			}

			throw new NotSupportedException($"Ceras could not find any IFormatter<T> for the type '{type.FullName}'. Maybe exclude that field/prop from serializaion or write a custom formatter for it.");
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal TypeMetaData GetTypeMetaData(Type type)
		{
			ref var meta = ref _metaData.GetOrAddValueRef(type);
			if (meta != null)
				return meta;

			return CreateMetaData(type, false);
		}

		internal TypeMetaData GetStaticTypeMetaData(Type type)
		{
			ref var meta = ref _staticMetaData.GetOrAddValueRef(type);
			if (meta != null)
				return meta;

			return CreateMetaData(type, true);
		}

		TypeMetaData CreateMetaData(Type type, bool isStatic)
		{
			var dict = isStatic ? _staticMetaData : _metaData;
			ref var meta = ref dict.GetOrAddValueRef(type);
			if (meta != null)
				return meta;

			BannedTypes.ThrowIfBanned(type);

			bool isSerializationPrimitive = IsPrimitiveType(type);
			bool isFrameworkType = _frameworkAssemblies.Contains(type.Assembly);

			var typeConfig = isSerializationPrimitive ? null : Config.GetTypeConfig(type, isStatic);

			meta = new TypeMetaData(type, typeConfig, isFrameworkType, isSerializationPrimitive);

			if (!isSerializationPrimitive)
				meta.CurrentSchema = meta.PrimarySchema = CreatePrimarySchema(type, isStatic);

			return meta;
		}



		void PrepareFormatter(IFormatter formatter)
		{
			if (formatter is DynamicFormatter dyn)
				dyn.Initialize();

			// Simple DI system, injects:
			// - IFormatter<> and types derived from it
			// - CerasSerializer

			var formatterType = formatter.GetType();
			var config = formatterType.GetCustomAttribute<CerasInjectAttribute>() ?? CerasInjectAttribute.Default;

			if (formatterType.GetCustomAttribute<CerasNoInjectAttribute>() != null)
				return; // Don't inject anything

			var flags = BindingFlags.Public | BindingFlags.Instance;
			if (config.IncludePrivate)
				flags |= BindingFlags.NonPublic;

			var fields = formatter.GetType().GetFields(flags);
			foreach (var f in fields)
			{
				if (f.GetCustomAttribute<CerasNoInjectAttribute>() != null)
					continue;

				bool noRef = f.GetCustomAttribute<CerasNoReference>() != null;

				var fieldType = f.FieldType;

				// Reference to the serializer, config, or any config interface
				if (fieldType == typeof(CerasSerializer))
				{
					SafeInject(formatter, f, this);
					continue;
				}
				else if (fieldType == typeof(SerializerConfig))
				{
					SafeInject(formatter, f, Config);
					continue;
				}
				else if (fieldType == typeof(IAdvancedConfig))
				{
					SafeInject(formatter, f, Config.Advanced);
					continue;
				}
				else if (fieldType == typeof(ISizeLimitsConfig))
				{
					SafeInject(formatter, f, Config.Advanced.SizeLimits);
					continue;
				}

				// Any formatter?
				if (!typeof(IFormatter).IsAssignableFrom(fieldType))
					continue;


				var formatterInterface = ReflectionHelper.FindClosedType(fieldType, typeof(IFormatter<>));

				if (formatterInterface == null)
					continue; // Not a formatter? Then that's not something we can handle

				var formattedType = formatterInterface.GetGenericArguments()[0];

				// Any formatter that can handle the given type
				if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(IFormatter<>))
				{
					var requestedFormatter = noRef ? GetSpecificFormatter(formattedType) : GetReferenceFormatter(formattedType);

					SafeInject(formatter, f, requestedFormatter);

					continue;
				}

				// Some very specific formatter
				if (ReflectionHelper.IsAssignableToGenericType(fieldType, typeof(IFormatter<>)))
				{
					var requestedFormatter = noRef ? GetSpecificFormatter(formattedType) : GetReferenceFormatter(formattedType);

					if (requestedFormatter.GetType() == fieldType)
					{
						// This was the formatter we were looking for
						SafeInject(formatter, f, requestedFormatter);
						continue;
					}

					// Maybe we're dealing with a reference type and the user explicitly wants the direct formatter?
					// todo: there should be some kind of warning maybe, but how/where would we output it?
					// If the formattedType is a referenceType and the user uses the direct formatter things could get ugly (references are not handled at all, user has to do it on his own)
					var directFormatter = GetSpecificFormatter(formattedType);

					if (directFormatter.GetType() == fieldType)
					{
						SafeInject(formatter, f, directFormatter);
						continue;
					}

					var anyExisting = directFormatter ?? requestedFormatter;

					throw new InvalidOperationException($"The formatter '{formatter.GetType().FriendlyName(true)}' has a dependency on '{fieldType.GetType().FriendlyName()}' (via the field '{f.Name}') to format '{formattedType.FriendlyName(true)}', but this Ceras instance is already using '{anyExisting.GetType().FriendlyName(true)}' to handle this type.");
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

		internal void EnsurePrimarySchema(Type type)
		{
			var meta = GetTypeMetaData(type);

			if (!meta.CurrentSchema.IsPrimary)
				ActivateSchemaOverride(type, meta.PrimarySchema);
		}


		// Creates the primary schema for a given type
		Schema CreatePrimarySchema(Type type, bool isStatic)
		{
			if (IsPrimitiveType(type))
				return null;

			if (type.IsAbstract() || type.IsInterface || type.ContainsGenericParameters)
				return null;

			var typeConfig = Config.GetTypeConfig(type, isStatic);
			typeConfig.Seal();

			Schema schema = new Schema(true, type, typeConfig, isStatic);

			foreach (var memberConfig in typeConfig._allMembers)
			{
				if (memberConfig.ComputeFinalInclusionFast())
				{
					var schemaMember = new SchemaMember(memberConfig.PersistentName, memberConfig.Member, memberConfig.WriteBackOrder);
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

		internal Schema ReadSchema(byte[] buffer, ref int offset, Type type, bool isStatic)
		{
			var meta = GetTypeMetaData(type);

			if (meta.IsPrimitive)
				throw new InvalidOperationException("Cannot read a Schema for a primitive type! This must be either a serious bug, or the given data has been tampered with. Please report it on GitHub!");

			var typeConfig = Config.GetTypeConfig(type, isStatic);

			//
			// Read Schema
			var schema = new Schema(false, type, typeConfig, isStatic);

			var memberCount = SerializerBinary.ReadInt32(buffer, ref offset);
			for (int i = 0; i < memberCount; i++)
			{
				var name = SerializerBinary.ReadString(buffer, ref offset);

				var member = Schema.FindMemberInType(type, name, isStatic);

				if (member == null)
				{
					// Skip
					schema.Members.Add(new SchemaMember(name));
				}
				else
				{
					var memberConfig = typeConfig.GetMemberConfig(member);
					schema.Members.Add(new SchemaMember(name, member, memberConfig.WriteBackOrder));
				}
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


		/// <summary>
		/// If you're ever not sure why some member gets included (or doesn't) this method will help.
		/// The report will explain for every member why exactly Ceras decided that it should get serialized (or why it should be excluded).
		/// You can also look into each member individually by checking out <see cref="TypeConfig.Members"/> if you don't need the full report.
		/// </summary>
		public string GenerateSerializationDebugReport(Type type)
		{
			if (IsPrimitiveType(type))
				return type.FullName + " is a serialization primitive. It's serialization logic is hard-coded, so it has no schema.";


			var typeConfig = Config.GetTypeConfig(type, false);
			var allMembers = typeConfig._allMembers;

			var inc = allMembers.Where(m => m.ComputeFinalInclusionFast()).ToArray();
			var exc = allMembers.Where(m => !m.ComputeFinalInclusionFast()).ToArray();

			string report = $"Schema report for Type '{type.FriendlyName()}' ({allMembers.Count} data members):\r\n";


			var formatter = GetSpecificFormatter(type);
			var formatterType = formatter.GetType();

			if (formatterType.IsGenericType && formatterType.GetGenericTypeDefinition() != typeof(DynamicFormatter<>))
			{
				report += "\r\n";
				report += "!! Warning: This report only makes sense for types handled by 'DynamicFormatter<>'.\r\n";
				report += $"!! Ceras uses '{formatterType.FriendlyName()}' for this type.\r\n";
				report += "\r\n";
			}


			report += "\r\n";
			report += $"Serialized Members (Count = {inc.Length}):\r\n";
			foreach (var m in inc)
				report += $"[{m.Member.Name}] {m.ComputeFinalInclusion().Reason}\r\n";
			if (inc.Length == 0)
				report += "(empty)\r\n";

			report += "\r\n";

			report += $"Members excluded from serialization (Count = {exc.Length}):\r\n";
			foreach (var m in exc)
				report += $"[{m.Member.Name}] {m.ComputeFinalInclusion().Reason}\r\n";
			if (exc.Length == 0)
				report += "(empty)\r\n";

			return report;
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

	public interface ICerasAdvanced
	{
		/// <summary>
		/// Serialize the static members of a type.
		/// </summary>
		byte[] SerializeStatic(Type type);

		/// <summary>
		/// Serialize the static members of a type.
		/// </summary>
		/// <returns>The number of bytes written</returns>
		int SerializeStatic(Type type, ref byte[] buffer, ref int offset);

		/// <summary>
		/// Deserialize the static members of a type.
		/// </summary>
		void DeserializeStatic(Type type, byte[] buffer);

		/// <summary>
		/// Deserialize the static members of a type.
		/// </summary>
		/// <returns>The number of bytes read</returns>
		int DeserializeStatic(Type type, byte[] buffer, ref int offset);

		/// <summary>
		/// Read the type of the serialized object without fully deserializing it.
		/// <para>Only works if the type was actually written, which is not always the case. You can make sure the type-data is available by explicitly specifying 'object' while serializing:</para>
		/// <c>ceras.Serialize&lt;object&gt;(...)</c>
		/// <para/>
		/// Also will not work if the data was saved with version tolerance (can be implemented when someone requests it)
		/// </summary>
		Type PeekType(byte[] buffer);

		/// <summary>
		/// Clones the given object. Simple helper method that just calls Serialize and Deserialize with a temporary (reused) buffer.
		/// </summary>
		T Clone<T>(T obj);

		/// <summary>
		/// Get all resolvers that this <see cref="CerasSerializer"/> has available. Does not include any user-registered callbacks in <see cref="SerializerConfig.OnResolveFormatter"/>.
		/// </summary>
		IEnumerable<IFormatterResolver> GetFormatterResolvers();

		/// <summary>
		/// Get an instance of any specific type of resolver (or null if no resolver matching that type can be found)
		/// </summary>
		IFormatterResolver GetFormatterResolver<TResolver>() where TResolver : IFormatterResolver;
	}

	// In order to support recursive serialization/deserialization, we need to "instantiate"
	// some values for each call.
	struct InstanceData
	{
		public TypeCache TypeCache;
		public ObjectCache ObjectCache;

		public IExternalRootObject CurrentRoot;

		public object UserContext;

		// Why <Type> instead of <Schema>?
		// -> While reading we'll never encounter multiple different schemata for the same type.
		// -> While writing we always use the primary schema.
		// So only the Type differs!
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
		public readonly bool IsPrimitive; // serialization primitive is meant
		public bool HasSchema => !IsPrimitive && !IsFrameworkType;

		public readonly TypeConfig TypeConfig;

		public IFormatter SpecificFormatter;
		public IFormatter ReferenceFormatter;

		public Schema CurrentSchema;


		public Schema PrimarySchema;
		public readonly List<Schema> SecondarySchemata = new List<Schema>();

		// Anyone (any formatter) who is interested in schema changes for this type should enter themselves in this list
		public readonly List<ISchemaTaintedFormatter> OnSchemaChangeTargets = new List<ISchemaTaintedFormatter>();

		public StaticSerializeDelegate StaticSerialize;
		public StaticDeserializeDelegate StaticDeserialize;

		public TypeMetaData(Type type, TypeConfig typeConfig, bool isFrameworkType, bool isSerializationPrimitive)
		{
			Type = type;
			IsFrameworkType = isFrameworkType;
			IsPrimitive = isSerializationPrimitive;
			TypeConfig = typeConfig;
		}
	}
}
