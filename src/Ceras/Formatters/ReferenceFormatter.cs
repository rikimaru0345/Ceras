namespace Ceras.Formatters
{
	using Ceras.Helpers;
	using Resolvers;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;

#if FAST_EXP
	using FastExpressionCompiler;
#endif

	/*
	 * 
	 * This formatter enables dealing with any object graph, even cyclic references of any kind.
	 * It also save binary size(and thus a lot of cpu cycles as well if the object is large)
	 * 
	 * Important: Different CacheFormatters MUST share a common pool!
	 * Why?
	 * because what if we have an object of a known type (field type and actual type are exactly the same)
	 * and then later we encounter an 'object Field;' with a refernce to the previous object (the one in the field where the type matches).
	 * Of course the references are supposed to match in the end again, and without all objects being part of the same cache that won't work.
	 * (We would try to find the referenced object in the <object> pool but it was put into the <specific> pool when it was first encountered!)
	 * 
	 * todo: there are a few cases in here where we can certainly optimize performance
	 *		 - We can make it so _dispatchers is only used at most once per call. Currently we might have multiple lookups, but all
	 *		   we actually need is getting(or creating) the DispatcherEntry and then only work with that.
	 *		 - globalObjectConstructors is supposed to cache generic constructors, but we always check if there is a user-factory-method, which is not needed.
	 *		   we could compile that into the constructor, but at that point it is not a global ctor anymore, which is fine if we only cache it into the dispatcherEntry.
	 *
	 *
	 * todo: we might be able to eliminate the following check:
	 *			if (value is IExternalRootObject externalObj)
	 *		 by moving it out of here and into the DynamicObjectFormatter.
	 *		 There we know what concrete type we're dealing with and if it is an IExternalObject.
	 *		 So that way we only have to check if the current object is equal to the current root object, easy!
	 *		 For deserialization we can statically compile in the check for the external resolver as well!
	 *			Downside: users that write their own formatters would have to manually take care of external object serialization, which is not cool
	 *
	 * todo: statically compile everything
	 *		we have many variables based on the specificType that will never ever change.
	 *		instead of having Serialize/Deserialize do those checks all the time, we could compile everything into a delegate.
	 *		Then we'd merge all our "sub-delegates" (like ctor and so on), into that one big delegate as well.
	 *		That way we'd save a lot of performance because entire if-chains would be completely gone.
	 *		We *always* have to do GetDispatcherEntry() anyway, so if we could instantly call into a super-optimized delegate, that'd be awesome.
	 *			Downside: Makes the actual code **really** hard to follow and understand, and impossible to debug.
	 *
	 */
	sealed class ReferenceFormatter<T> : IFormatter<T>, ISchemaTaintedFormatter
	where T : class
	{
		// -5: To support stuff like 'object obj = typeof(Bla);'. The story is more complicated and explained at the end of the file.
		// -4: For external objects that the user has to resolve
		// -3: A new value, the type is exactly the same as the target field/prop
		// -2: A new value, which has a type different than the target. Example: field type is ICollection<T> and the actual value is LinkedList<T> 
		// -1: Actually <null> or default value
		//  0: Previously seen item with index 0
		//  1: Previously seen item with index 1
		//  2: Previously seen item with index 2
		// ...
		const int Null = -1;
		const int NewValue = -2;
		const int NewValueSameType = -3;
		const int ExternalObject = -4;
		const int InlineType = -5;

		const int Bias = 5; // Using WriteUInt32Bias is more efficient than WriteInt32(), but it requires a known bias


		readonly TypeFormatter _typeFormatter;
		readonly CerasSerializer _serializer;

		readonly TypeDictionary<DispatcherEntry> _dispatchers = new TypeDictionary<DispatcherEntry>();


		public ReferenceFormatter(CerasSerializer serializer)
		{
			_serializer = serializer;

			_typeFormatter = (TypeFormatter)serializer.GetSpecificFormatter(typeof(Type));
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			if (ReferenceEquals(value, null))
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, Null, Bias);
				return;
			}

			var specificType = value.GetType();
			var entry = GetOrCreateEntry(specificType);
			

			if (entry.IsType) // This is very rare, so we cache the check itself, and do the cast below
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, InlineType, Bias);
				_typeFormatter.Serialize(ref buffer, ref offset, (Type)(object)value);
				return;
			}

			if (entry.IsExternalRootObject)
			{
				var externalObj = (IExternalRootObject)value;

				if (!ReferenceEquals(_serializer.InstanceData.CurrentRoot, value))
				{
					SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, ExternalObject, Bias);

					var refId = externalObj.GetReferenceId();
					SerializerBinary.WriteInt32(ref buffer, ref offset, refId);

					_serializer.Config.OnExternalObject?.Invoke(externalObj);

					return;
				}
			}


			if (_serializer.InstanceData.ObjectCache.TryGetExistingObjectId(value, out int id))
			{
				// Existing value
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, id, Bias);
			}
			else
			{
				// Register new object
				_serializer.InstanceData.ObjectCache.RegisterObject(value);

				// Embedd type (if needed)
				if (ReferenceEquals(typeof(T), specificType))
				{
					// New value (same type)
					SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, NewValueSameType, Bias);
				}
				else
				{
					// New value (type included)
					SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, NewValue, Bias);
					_typeFormatter.Serialize(ref buffer, ref offset, specificType);
				}

				// Write object
				entry.CurrentSerializeDispatcher(ref buffer, ref offset, value);
			}
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			var objId = SerializerBinary.ReadUInt32Bias(buffer, ref offset, Bias);

			if (objId == Null)
			{
				// Null

				// Ok the data tells us that value should be null.
				// But maybe we're recycling an object and it still contains an instance.
				// Lets return it to the user
				if (value != null)
					_serializer.DiscardObjectMethod?.Invoke(value);

				value = default;
				return;
			}

			if (objId == InlineType)
			{
				Type type = null;
				_typeFormatter.Deserialize(buffer, ref offset, ref type);
				value = (T)(object)type; // This is ugly, but there's no way to prevent it, right?
				return;
			}

			if (objId >= 0)
			{
				// Something we already know
				value = _serializer.InstanceData.ObjectCache.GetExistingObject<T>(objId);
				return;
			}

			if (objId == ExternalObject)
			{
				// External object, let the user resolve!
				int externalId = SerializerBinary.ReadInt32(buffer, ref offset);

				// Let the user resolve
				_serializer.Config.ExternalObjectResolver.Resolve(externalId, out value);
				return;
			}


			// New object, see Note#1
			Type specificType = null;
			if (objId == NewValue)
				// == NewValue (EmbeddedType)
				_typeFormatter.Deserialize(buffer, ref offset, ref specificType);
			else
				// == NewValueSameType
				specificType = typeof(T);


			var entry = GetOrCreateEntry(specificType);

			// At this point we know that the 'value' will not be 'null', so if 'value' (the variable) is null we need to create an instance
			if (!entry.IsValueType) // still possible that we're dealing with a boxed value;
			{
				// Do we already have an object?
				if (value != null)
				{
					// Yes, then maybe we can overwrite its values (works for objects and collections)
					// But only if it's the right type!

					// todo: types using a SerializationCtor (in the future) are handled in a different ReferenceFormatter
					//		 where we first read all members into local variables, then create the object (passing some of them into the constructor), and then writing the remaining as usual
					if (value.GetType() != specificType)
					{
						// Discard the old value
						_serializer.DiscardObjectMethod?.Invoke(value);

						// Create instance of the right type
						value = (T)entry.Constructor();
					}
					else
					{
						// Existing object is the right type
					}
				}
				else
				{
					// Instance is null, create one
					value = (T)entry.Constructor();
				}
			}
			else
			{
				// Not a reference type. So it doesn't matter anyway.
			}



			//
			// Deserialize the object
			// 1. First generate a proxy so we can do lookups
			var objectProxy = _serializer.InstanceData.ObjectCache.CreateDeserializationProxy<T>();

			// 2. Make sure that the deserializer can make use of an already existing object (if there is one)
			objectProxy.Value = value;

			// 3. Actually read the object
			entry.CurrentDeserializeDispatcher(buffer, ref offset, ref objectProxy.Value);

			// 4. Write back the actual value, which instantly resolves all references
			value = objectProxy.Value;
		}


		DispatcherEntry GetOrCreateEntry(Type type)
		{
			ref var entry = ref _dispatchers.GetOrAddValueRef(type);
			if (entry != null)
				return entry;

			// Get type meta-data and create a dispatcher entry
			var meta = _serializer.GetTypeMetaData(type);
			entry = new DispatcherEntry(type, meta.IsFrameworkType, meta.CurrentSchema);

			if (entry.IsType)
				return entry; // Don't need to do anything else...

			// Obtain the formatter for this specific type
			var formatter = _serializer.GetSpecificFormatter(type);

			// Create dispatchers and ctor
			entry.CurrentSerializeDispatcher = CreateSpecificSerializerDispatcher(type, formatter);
			entry.CurrentDeserializeDispatcher = CreateSpecificDeserializerDispatcher(type, formatter);
			entry.Constructor = CreateObjectConstructor(type);

			if (!meta.IsFrameworkType) // Framework types do not have a schemata dict
			{
				var pair = new DispatcherPair(entry.CurrentSerializeDispatcher, entry.CurrentDeserializeDispatcher);
				entry.SchemaDispatchers[entry.CurrentSchema] = pair;
			}

			return entry;
		}


		Func<object> CreateObjectConstructor(Type type)
		{
			// todo: the idea here is that we compile a function that has the check against the user provided factory built in
			// and if it returns null, we fall back to normal creation,
			// and if there's no user factory, we only do the normal ctor!

			if (type.IsArray)
			{
				// ArrayFormatter will create a new array
				return () => null;
			}
			else if (CerasSerializer.IsFormatterConstructed(type) || type.IsValueType)
			{
				// The formatter that handles this type also handles its creation, so we return null
				return () => null;
			}


			var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
						   .FirstOrDefault(c => c.GetParameters().Length == 0);
			if (ctor == null)
				throw new Exception($"Cannot deserialize type '{type.FullName}' because it has no parameterless constructor (support for serialization-constructors will be added in the future)");

			// Create a custom factory method, but also respect the userFactory if there is one
			var userFactory = _serializer.Config.Advanced.ObjectFactoryMethod;

			if (userFactory == null)
			{
				var lambda = Expression.Lambda<Func<object>>(Expression.New(ctor));
				return lambda.Compile();
			}
			else
			{
				// Need to call the user factory, and only use 'New()' when we get null from it
				var userFactoryMethod = userFactory.GetMethodInfo();
				var userObj = Expression.Variable(type);

				var callAndAssignUserObj = Expression.Assign(userObj, Expression.Call(Expression.Constant(userFactory), userFactoryMethod));

				var coalesce = Expression.IfThenElse(
									  // if userObj is null
									  test: Expression.ReferenceEqual(userObj, Expression.Constant(null, typeof(object))),
									  // then use new()
									  ifTrue: Expression.New(ctor),
									  // else use the given user obj
									  ifFalse: userObj);

				var body = Expression.Block(callAndAssignUserObj, coalesce);

				var lambda = Expression.Lambda<Func<object>>(body, userObj);
				return lambda.Compile();
			}
		}


		/*
		 * So what even is a SpecificDispatcher and why do we need one??
		 * 
		 * The answer is surprisingly simple.
		 * If we (the reference formatter) are of some sort of 'base type' like ReferenceFormatter<object> or ReferenceFormatter<IList> or ...
		 * then we can serialize the reference itself just fine, yea, but the actual type needs a different serializer.
		 * 
		 * There can be all sorts of actual implementations inside an 'IList' field and we can't know until we look at the current value.
		 * So that means we need to use a different formatter depending on the *actual* type of the object.
		 * 
		 * But doing the lookup from type to formatter and potentially creating one is not the only thing that needs to be done.
		 * Because there's another problem:
		 * 
		 * Our <T> would have to be co-variant and contra-variant at the same time (because we consume and produce a <T>).
		 * Of course in normal C# that's not possible because it's not even safe to do.
		 * But in our case we actually know (well, unless we get corrupted data of course) that everything will work.
		 * So to bypass that limitation we compile our own special delegate that does the forwards and backwards casting for us.
		 */
		static SerializeDelegate<T> CreateSpecificSerializerDispatcher(Type type, IFormatter specificFormatter)
		{
			// What does this method do?
			// It creates a cast+call dynamically
			// Why is that needed?
			// See this example:
			// We have a field of type 'object' containing a 'Person' instance.
			//    IFormatter<object> formatter = new ReferenceFormatter<Person>();
			// The line of code above obviously does not work since the types do not match, which is what this method fixes.

			var serializeMethod = specificFormatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
			Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter");

			// What we want to emulate:
			/*
			 * (buffer, offset, T value) => {
			 *	  formatter.Serialize(buffer, offset, (specificType)value);
			 */

			var refBufferArg = Expression.Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Expression.Parameter(typeof(T), "value");

			Expression convertedValueArg;

			if (typeof(T) == type)
				// Exact match
				convertedValueArg = valueArg; // todo: no need to compile a delegate at all
			else if (!type.IsValueType)
				// Cast general -> derived
				convertedValueArg = Expression.TypeAs(valueArg, type);
			else
				// Unbox
				convertedValueArg = Expression.Convert(valueArg, type);


			var body = Expression.Block(
										Expression.Call(Expression.Constant(specificFormatter), serializeMethod,
														arg0: refBufferArg,
														arg1: refOffsetArg,
														arg2: convertedValueArg)
										);

#if FAST_EXP
			var f = Expression.Lambda<SerializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { refBufferArg, refOffsetArg, valueArg }).CompileFast(true);
#else
			var f = Expression.Lambda<SerializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { refBufferArg, refOffsetArg, valueArg }).Compile();
#endif

			return f;
		}

		// See the comment on GetSpecificSerializerDispatcher
		static DeserializeDelegate<T> CreateSpecificDeserializerDispatcher(Type type, IFormatter specificFormatter)
		{
			var deserializeMethod = specificFormatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
			Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter");

			// What we want to emulate:
			/*
			 * (buffer, offset, T value) => {
			 *    (specificType) obj = (specificType)value;
			 *	  formatter.Deserialize(buffer, offset, ref obj);
			 *    value = (specificType)obj;
			 */

			var bufferArg = Expression.Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Expression.Parameter(typeof(T).MakeByRefType(), "value");

			var valAsSpecific = Expression.Variable(type, "valAsSpecific");

			Expression intro, outro;
			if (typeof(T) == type)
			{
				// Same type, the best case
				intro = Expression.Assign(valAsSpecific, refValueArg);
				outro = Expression.Assign(refValueArg, valAsSpecific);
			}
			else if (!typeof(T).IsValueType && type.IsValueType)
			{
				// valueType = (castToValueType)object;

				// Handle unboxing: we might have a null-value.
				intro = Expression.IfThenElse(Expression.ReferenceEqual(refValueArg, Expression.Constant(null)),
											  ifTrue: Expression.Default(type),
											  ifFalse: Expression.Unbox(refValueArg, type));

				// Box the value type again
				outro = Expression.Assign(refValueArg, Expression.Convert(valAsSpecific, typeof(T)));
			}
			else
			{
				// Types are not equal, but there are no value-types involved.
				// Some kind of casting. Maybe the field type is an interface or 'object'
				intro = Expression.Assign(valAsSpecific, Expression.TypeAs(refValueArg, type));
				// No need to up-cast.
				outro = Expression.Assign(refValueArg, valAsSpecific);
			}


			var body = Expression.Block(variables: new[] { valAsSpecific },
										expressions: new Expression[]
										{
											intro,

											Expression.Call(Expression.Constant(specificFormatter), deserializeMethod,
															arg0: bufferArg,
															arg1: refOffsetArg,
															arg2: valAsSpecific),

											outro
										});

#if FAST_EXP
			var f = Expression.Lambda<DeserializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { bufferArg, refOffsetArg, refValueArg }).CompileFast(true);
#else
			var f = Expression.Lambda<DeserializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { bufferArg, refOffsetArg, refValueArg }).Compile();
#endif
			return f;
		}




		public void OnSchemaChanged(TypeMetaData meta)
		{
			// If we've encountered this specific type already...
			if (_dispatchers.TryGetValue(meta.Type, out var entry))
			{
				// ...then we might have some stuff for this schema of this type.
				//
				// So if we have some cached dispatchers already, we activate them.
				// If we don't have any, set them to null and they will be populated when actually needed
				if (entry.SchemaDispatchers.TryGetValue(meta.CurrentSchema, out var pair))
				{
					entry.CurrentSerializeDispatcher = pair.SerializeDispatcher;
					entry.CurrentDeserializeDispatcher = pair.DeserializeDispatcher;
				}
				else
				{
					entry.CurrentSerializeDispatcher = null;
					entry.CurrentDeserializeDispatcher = null;
				}
			}
		}

		class DispatcherEntry
		{
			public readonly Type Type;
			public readonly bool IsFrameworkType;

			public Func<object> Constructor;

			public readonly bool IsType;
			public readonly bool IsExternalRootObject;
			public readonly bool IsValueType;

			public Schema CurrentSchema;
			public SerializeDelegate<T> CurrentSerializeDispatcher;
			public DeserializeDelegate<T> CurrentDeserializeDispatcher;

			public readonly Dictionary<Schema, DispatcherPair> SchemaDispatchers;

			public DispatcherEntry(Type type, bool isFrameworkType, Schema currentSchema)
			{
				Type = type;
				IsFrameworkType = isFrameworkType;
				CurrentSchema = currentSchema;

				IsType = typeof(Type).IsAssignableFrom(type);
				IsExternalRootObject = typeof(IExternalRootObject).IsAssignableFrom(type);
				IsValueType = type.IsValueType;

				// We only need a dictionary when the schema can actually change, which is never the case for framework types
				if (!isFrameworkType)
					SchemaDispatchers = new Dictionary<Schema, DispatcherPair>();
			}
		}

		struct DispatcherPair
		{
			public readonly SerializeDelegate<T> SerializeDispatcher;
			public readonly DeserializeDelegate<T> DeserializeDispatcher;

			public DispatcherPair(SerializeDelegate<T> serialize, DeserializeDelegate<T> deserialize)
			{
				SerializeDispatcher = serialize;
				DeserializeDispatcher = deserialize;
			}
		}
	}



	/*
	 * The idea for the 'Members' mode of readonly field handling is that
	 * we populate the fields only. Which (and that's just our current interpretation) requires
	 * that the reference itself is already exactly what it should be.
	 * 
	 * That means 'Members' only succeeds when:
	 * - Value is null and data requires null
	 * - KnownObject resolves to what's already there
	 * - ExternalObject resolves to what's already there
	 * - Inline type is the same one that's already there
	 * - NewObject with matching type.
	 * 
	 * The last one is a bit of a special case. It requires a new object, but since we *want* to populate an existing object,
	 * we can just "adopt" the already existing object, which fulfilles the idea of 'Members' as well.
	 * 
	 * - "Why even have this and not just always do the 'read and overwrite' thing??"
	 * -> Because for readonly fields we have more options than just 'do or don't handle them'.
	 *    We also support 'members', aka 'populate only'. And for that we must know if we should immediately throw an exception.
	 *    
	 *    
	 * But wait a second.
	 * Can we not completely see if there are any field-writes needed just by checking if the ReferenceFormatter has changed the given ref-variable??
	
	enum ReferenceRecycleWriteDecision
	{
		//
		// Success:
		// Cases where everything matches already
		Success_Null, // Expected null, and we got null.
		Success_MatchingInstance, // Expected a specific instance (previous object, external object, inline type) and that's what we got.


		//
		// Valid:
		// Case where we get a new object, and we have a matching object present already that can accept the incoming data.
		Valid_TypeMatch, // Expected an instance, and (as expected) there is already an object of the correct type present. We can continue in 'populate' mode.


		//
		// Failure:
		// Cases where the content of the field does not match; we would have to fix it somehow

		Fail_ShouldWriteNull, // The field contains an instance, but the data tells us the field should be null!
		// -> Fix: Read as normal and assign
		
		Fail_InstanceExpected, // The field currently contains null, but the data says there should be an object in there. 
		// -> Fix: Read as normal and assign

		Fail_InstanceMismatch, // The field already contains an instance, but not the right one. The data tells us there should be a very specific object in there, but it's not
		// -> Fix: Read as normal and assign

		Fail_TypeMismatch, // The value is not null, as the data tells us. But the type of the existing object does not match the expected one (so populating fields is impossible)
		// -> Fix: Read as normal and assign
	}
	*/


	// This is here so we are able to get specific internal framework types.
	// Such as "System.RtFieldInfo" or "System.RuntimeTypeInfo", ...
	static class MemberHelper
	{
		// Helper members
		internal static byte _field;
		internal static byte _prop { get; set; }
		internal static void _method() { }
	}

}

/*
	Serializing types as values.
	Let's say someone has a field like this: `object obj = typeof(Bla);`
	We don't know what's inside the field from just the field-type (which is just 'object').
	So as always, we'd have to write the type.
	Usually we would get the type of the value, so "obj.GetType()", but in this case that would not work at all, as the result
	would of 'typeof(Type).GetType()' is actually 'System.RuntimeType'!

	Resolving this would be possible with some special cases in the TypeFormatter.
	But that would slow things down, and there is actually one
	more (even more important) problem that is not immediately apparent: sharing!
	The TypeFormatter has its own specialized cache for types, so not only could we not profit from its specialized code,
	we would also write a huge unoptimized string for many types.
	And if there are any actual instances of that type we'd waste even more space by encoding the type once with type-encoding
	and once as a "value".

	Solution:
	We can resolve all of those problems by making 'Type' a special case (as it should be).
	After implementing it we realize that this is actually "free" in performance terms.
	So we didn't add any performance penalty AND fixed multiple problems at the same time.
*/
