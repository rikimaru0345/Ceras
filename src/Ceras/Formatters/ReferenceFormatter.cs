namespace Ceras.Formatters
{
	using Resolvers;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Reflection.Emit;

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
	 */
	public class ReferenceFormatter<T> : IFormatter<T>
	{
		// -4: For external objects that the user has to resolve
		// -3: A new value, the type is exactly the same as the target field/prop
		// -2: A new value, which has a type different than the target. Example: field type is ICollection<T> and the actual value is LinkedList<T> 
		// -1: Actually <null> or default value
		//  0: Previously seen item with index 0
		//  1: Previously seen item with index 1
		//  2: Previously seen item with index 2
		// ...
		const int ExternalObject = -4;
		const int NewValueSameType = -3;
		const int NewValue = -2;
		const int Null = -1;

		const int Bias = 4; // Using WriteUInt32Bias is more efficient than WriteInt32(), but it requires a known bias


		IFormatter<Type> _typeFormatter;
		readonly CerasSerializer _serializer;

		static Dictionary<Type, Func<object>> _objectConstructors = new Dictionary<Type, Func<object>>();

		Dictionary<Type, SerializeDelegate<T>> _specificSerializers = new Dictionary<Type, SerializeDelegate<T>>();
		Dictionary<Type, DeserializeDelegate<T>> _specificDeserializers = new Dictionary<Type, DeserializeDelegate<T>>();


		public bool IsSealed { get; private set; }

		public ReferenceFormatter(CerasSerializer serializer)
		{
			_serializer = serializer;

			_typeFormatter = (IFormatter<Type>)serializer.GetSpecificFormatter(typeof(Type));
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			if (EqualityComparer<T>.Default.Equals(value, default(T)))
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, Null, Bias);
				return;
			}

			if (value is IExternalRootObject externalObj)
			{
				if (!object.ReferenceEquals(_serializer.InstanceData.CurrentRoot, value))
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
				if (IsSealed)
				{
					throw new InvalidOperationException($"Trying to add '{value.ToString()}' (type '{typeof(T).FullName}') to a sealed cache formatter.");
				}

				// Important: Insert the ID for this value into our dictionary BEFORE calling SerializeFirstTime, as that might recursively call us again (maybe with the same value!)
				_serializer.InstanceData.ObjectCache.RegisterObject(value);

				var specificType = value.GetType();
				if (typeof(T) == specificType)
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

				// Write the object normally
				GetSpecificSerializerDispatcher(specificType)(ref buffer, ref offset, value);
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
					_serializer.Config.DiscardObjectMethod?.Invoke(value);

				value = default(T);
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
				_typeFormatter.Deserialize(buffer, ref offset, ref specificType);
			else // if (objId == NewValueSameType) commented out, its the only possible remaining case
				specificType = typeof(T);


			// At this point we know that the 'value' will not be 'null', so if 'value' (the variable) is null we need to create an instance
			bool isRefType = !specificType.IsValueType;

			if (isRefType)
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
						_serializer.Config.DiscardObjectMethod?.Invoke(value);

						// Create instance of the right type
						value = CreateInstance(specificType);
					}
					else
					{
						// Existing object is the right type
					}
				}
				else
				{
					// Instance is null, create one
					// Note: that we *could* check if the type is one of the types that we cannot instantiate (String, Type, MemberInfo, ...) and then
					//       just not call CreateInstance, but the check itself would be expensive as well (HashSet look up?), so what's the point of complicating the code more?
					//       CreateInstance will do a dictionary lookup for us and simply return null for those types.
					value = CreateInstance(specificType);
				}
			}
			else
			{
				// Not a reference type. So it doesn't matter.
			}



			//
			// Deserialize the object
			// 1. First generate a proxy so we can do lookups
			var objectProxy = _serializer.InstanceData.ObjectCache.CreateDeserializationProxy<T>();

			// 2. Make sure that the deserializer can make use of an already existing object (if there is one)
			objectProxy.Value = value;

			// 3. Actually read the object
			GetSpecificDeserializerDispatcher(specificType)(buffer, ref offset, ref objectProxy.Value);

			// 4. Write back the actual value, which instantly resolves all references
			value = objectProxy.Value;
		}


		SerializeDelegate<T> GetSpecificSerializerDispatcher(Type type)
		{
			if (_specificSerializers.TryGetValue(type, out var f))
				return f;

			// What does this method do?
			// It creates a cast+call dynamically
			// Why is that needed?
			// See this example:
			// We have a field of type 'object' containing a 'Person' instance.
			//    IFormatter<object> formatter = new ReferenceFormatter<Person>();
			// The line of code above obviously does not work since the types do not match, which is what this method fixes.

			var formatter = _serializer.GetSpecificFormatter(type);

			var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
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
										Expression.Call(Expression.Constant(formatter), serializeMethod,
														arg0: refBufferArg,
														arg1: refOffsetArg,
														arg2: convertedValueArg)
										);

#if FAST_EXP
			f = Expression.Lambda<SerializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { refBufferArg, refOffsetArg, valueArg }).CompileFast(true);
#else
			f = Expression.Lambda<SerializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { refBufferArg, refOffsetArg, valueArg }).Compile();
#endif
			_specificSerializers[type] = f;

			return f;
		}

		DeserializeDelegate<T> GetSpecificDeserializerDispatcher(Type type)
		{
			if (_specificDeserializers.TryGetValue(type, out var f))
				return f;

			var formatter = _serializer.GetSpecificFormatter(type);

			var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
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

											Expression.Call(Expression.Constant(formatter), deserializeMethod,
															arg0: bufferArg,
															arg1: refOffsetArg,
															arg2: valAsSpecific),

											outro
										});

#if FAST_EXP
			f = Expression.Lambda<DeserializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { bufferArg, refOffsetArg, refValueArg }).CompileFast(true);
#else
			f = Expression.Lambda<DeserializeDelegate<T>>(body: body, parameters: new ParameterExpression[] { bufferArg, refOffsetArg, refValueArg }).Compile();
#endif
			_specificDeserializers[type] = f;

			return f;
		}


		// Create an instance of a given type
		T CreateInstance(Type specificType)
		{
			// todo:
			// we can easily merge null-check (in the caller) and GetConstructor() into GetSpecificDeserializerDispatcher() 
			// which would let us avoid one dictionary lookup, the type check, checking if a factory method is set, ...
			// directly using Expression.New() will likely be faster as well
			// todo 2:
			// in fact, we can even *directly* inline the serializer sometimes!
			// if the specific serializer is a DynamicSerializer, we could just take the expression-tree it generates, and directly inline it here.
			// that would avoid even more overhead!

			// Some objects can not be instantiated directly. Like 'Type', 'string', or 'MemberInfo', ... we return a constructor for them that always returns null. 

			T value;
			var factory = _serializer.Config.ObjectFactoryMethod;
			if (factory != null)
				value = (T)_serializer.Config.ObjectFactoryMethod(specificType);
			else
				value = (T)GetConstructor(specificType)();
			return value;
		}

		// Get a 'constructor' function that creates an instance of a type
		static Func<object> GetConstructor(Type type)
		{
			// Fast path: return already constructed object!
			if (_objectConstructors.TryGetValue(type, out var f))
				return f;

			if (type.IsArray)
			{
				// ArrayFormatter will create a new array
				f = () => null;
			}
			else if(CerasSerializer.IsFormatterConstructed(type))
			{
				// The formatter that handles this type also handles its creation, so we return null
				f = () => null;
			}
			else
			{
				// We create a new instances using the default constructor
				var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
					.FirstOrDefault(c => c.GetParameters().Length == 0);

				if (ctor == null)
					throw new Exception($"Cannot deserialize type '{type.FullName}' because it has no parameterless constructor (support for serialization-constructors will be added in the future)");

				f = (Func<object>)CreateConstructorDelegate(ctor, typeof(Func<object>));
			}

			_objectConstructors[type] = f;
			return f;
		}

		static Delegate CreateConstructorDelegate(ConstructorInfo constructor, Type delegateType)
		{
			if (constructor == null)
				throw new ArgumentNullException(nameof(constructor));

			if (delegateType == null)
				throw new ArgumentNullException(nameof(delegateType));


			MethodInfo delMethod = delegateType.GetMethod("Invoke");
			//if (delMethod.ReturnType != constructor.DeclaringType)
			//	throw new InvalidOperationException("The return type of the delegate must match the constructors delclaring type");


			// Validate the signatures
			ParameterInfo[] delParams = delMethod.GetParameters();
			ParameterInfo[] constructorParam = constructor.GetParameters();
			if (delParams.Length != constructorParam.Length)
			{
				throw new InvalidOperationException("The delegate signature does not match that of the constructor");
			}
			for (int i = 0; i < delParams.Length; i++)
			{
				if (delParams[i].ParameterType != constructorParam[i].ParameterType ||  // Probably other things we should check ??
					delParams[i].IsOut)
				{
					throw new InvalidOperationException("The delegate signature does not match that of the constructor");
				}
			}
			// Create the dynamic method
			DynamicMethod method =
				new DynamicMethod(
					string.Format("{0}__{1}", constructor.DeclaringType.Name, Guid.NewGuid().ToString().Replace("-", "")),
					constructor.DeclaringType,
					Array.ConvertAll<ParameterInfo, Type>(constructorParam, p => p.ParameterType),
					true
					);


			// Create the il
			ILGenerator gen = method.GetILGenerator();
			for (int i = 0; i < constructorParam.Length; i++)
			{
				if (i < 4)
				{
					switch (i)
					{
						case 0:
						gen.Emit(OpCodes.Ldarg_0);
						break;
						case 1:
						gen.Emit(OpCodes.Ldarg_1);
						break;
						case 2:
						gen.Emit(OpCodes.Ldarg_2);
						break;
						case 3:
						gen.Emit(OpCodes.Ldarg_3);
						break;
					}
				}
				else
				{
					gen.Emit(OpCodes.Ldarg_S, i);
				}
			}
			gen.Emit(OpCodes.Newobj, constructor);
			gen.Emit(OpCodes.Ret);

			return method.CreateDelegate(delegateType);
		}


		/// <summary>
		/// Set IsSealed, and will throw an exception whenever a new value is encountered.
		/// The idea is that you populate KnownTypes in the SerializerConfig initially and then call Seal(); That's useful so you'll get notified by an exception *before* something goes wrong (a new, unintended, type getting serialized). For example 
		/// </summary>
		public void Seal()
		{
			IsSealed = true;
		}

	}

	static class MemberHelper
	{
		// Helper members
		internal static byte _field;
		internal static byte _prop { get; set; }
		internal static void _method() { }
	}

}

/* Note #1
			
	!!Important !!
	When we're deserializing any nested types (ex: Literal<float>)
	then we're writing Literal`1 as 0, and System.Single as 1
	but while reading, we're recursively descending, so System.Single would get read first (as 0)
	which would throw off everything.
	Solution: 
	Reserve some space(inserting < null >) into the object-cache - list before actually reading, so all the nested things will just append to the list as intended.
	and then just overwrite the inserted placeholder with the actual value once we're done

	Problem:
	If there's a root object that has a field that simply references the root object
	then we will write it correctly, but since we only update the reference after the object is FULLY deserialized, we end up with nulls.

	Solution:
	Right when the object is created, it has to be immediately put into the _deserializationCache
	So child fields(like for example a '.Self' field)
	can properly resolve the reference.

	Problem:
	How to even do that?
	So when the object is created (but its fields are not yet fully populated) we must get a reference to
	it immediately, before any member-field is deserialized!(Because one of those fields could be a refernce to the object itself again)
	How can we intercept this assignment / object creation?

	Solution:
	We pass a reference to a proxy.If we'd just have a List<T> we would not be able to use "ref" (like: deserialize(... ref list[x]) )
	So instead we'll pass the reference to a proxy object. 
	The proxy objects are small, but spamming them for every deserialization is still not cool.
	So we use a very simple pool for them.
				 
 */
