
//#define FAST_EXP

// ReSharper disable ArgumentsStyleOther
// ReSharper disable ArgumentsStyleNamedExpression
namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq.Expressions;
	using System.Reflection;

#if FAST_EXP
	using FastExpressionCompiler;
#endif

	interface IDynamicFormatterMarker { }

	// todo 1: check if FastExpressionCompiler works without crashing now (a lot of work has been put into it recently)
	// todo 2: cache formatters in a static-generic instead of dict? we need to know exactly how much we'd save; the static-ctor can just generate the corrosponding serializers
	public class DynamicObjectFormatter<T> : IFormatter<T>, IDynamicFormatterMarker
	{
		delegate void DynamicSerializer(ref byte[] buffer, ref int offset, T value);
		delegate void DynamicDeserializer(byte[] buffer, ref int offset, ref T value);

		static FieldComparer _fieldComparer = new FieldComparer();

		Dictionary<Type, DynamicSerializer> _specificSerializers = new Dictionary<Type, DynamicSerializer>();
		Dictionary<Type, DynamicDeserializer> _specificDeserializers = new Dictionary<Type, DynamicDeserializer>();

		CerasSerializer _serializer;


		public DynamicObjectFormatter(CerasSerializer serializer)
		{
			_serializer = serializer;
		}


		DynamicSerializer GenerateSerializer(List<FieldInfo> fields, Type specificType)
		{
			ThrowIfNonspecific(specificType);

			var refBufferArg = Expression.Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Expression.Parameter(typeof(T), "value");

			List<Expression> block = new List<Expression>();

			var valAsSpecific = Expression.Variable(specificType, "valAsSpecific");
			block.Add(Expression.Assign(valAsSpecific, Expression.Convert(valueArg, specificType)));

			var specificFormatter = _serializer.GetFormatter(specificType, false, false);
			if (specificFormatter != null)
			{
				// Primitives are serialized directly using a known formatter

				var serialize = specificFormatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));

				Debug.Assert(serialize != null, "Can't find serialize method on formatter");

				// Assuming 'int', valAsSpecific is here:
				// "object value;"
				// "int valAsSpecific = (int)value;"
				// And since our primitive serializer supports that, we write that directly
				block.Add(Expression.Call(Expression.Constant(specificFormatter), serialize, refBufferArg, refOffsetArg, valAsSpecific));

				var serializeBlock = Expression.Block(variables: new[] { valAsSpecific }, expressions: block);
#if FAST_EXP
				return Expression.Lambda<DynamicSerializer>(serializeBlock, refBufferArg, refOffsetArg, valueArg).CompileFast(true);
#else
				return Expression.Lambda<DynamicSerializer>(serializeBlock, refBufferArg, refOffsetArg, valueArg).Compile();
#endif
			}
			else
			{
				// Fallback for the general case, try to simply write out each field
				// Write out each field...
				foreach (var fieldInfo in fields)
				{
					// Get the formatter and its Serialize method
					var formatter = _serializer.GetFormatter(fieldInfo.FieldType, extraErrorInformation: $"DynamicObjectFormatter ObjectType: {specificType.FullName} FieldType: {fieldInfo.FieldType.FullName}");
					var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));

					// Access the field that we want to serialize
					var fieldExp = Expression.Field(valAsSpecific, fieldInfo);

					Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter");

					// Call "Serialize"
					var serializeCall = Expression.Call(Expression.Constant(formatter), serializeMethod, refBufferArg, refOffsetArg, fieldExp);
					block.Add(serializeCall);
				}

				var serializeBlock = Expression.Block(variables: new[] { valAsSpecific }, expressions: block);

#if FAST_EXP
				return Expression.Lambda<DynamicSerializer>(serializeBlock, refBufferArg, refOffsetArg, valueArg).CompileFast(true);
#else
				return Expression.Lambda<DynamicSerializer>(serializeBlock, refBufferArg, refOffsetArg, valueArg).Compile();
#endif
			}
		}

		DynamicDeserializer GenerateDeserializer(List<FieldInfo> fields, Type specificType)
		{
			ThrowIfNonspecific(specificType);

			var bufferArg = Expression.Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Expression.Parameter(typeof(T).MakeByRefType(), "value");

			List<Expression> block = new List<Expression>();


			var specificFormatter = _serializer.GetFormatter(specificType, false, false);
			if (specificFormatter != null)
			{
				// Probably some user defined formatter...

				var deserialize = specificFormatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
				Debug.Assert(deserialize != null, "Can't find deserialize method on formatter");

				var localVal = Expression.Variable(specificType, "specificLocalValue");

				block.Add(Expression.Call(Expression.Constant(specificFormatter), deserialize, bufferArg, refOffsetArg, localVal));

				// After having the primitive formatter read the value for us, we need to assign it (and box)
				block.Add(Expression.Assign(refValueArg, Expression.Convert(localVal, typeof(T))));

				var serializeBlock = Expression.Block(variables: new[] { localVal }, expressions: block);
#if FAST_EXP
				return Expression.Lambda<DynamicDeserializer>(serializeBlock, bufferArg, refOffsetArg, refValueArg).CompileFast(true);
#else
				return Expression.Lambda<DynamicDeserializer>(serializeBlock, bufferArg, refOffsetArg, refValueArg).Compile();
#endif
			}
			else
			{
				// todo:
				// - what if we don't need to create a new object?
				// - what if field is ISomeInterface, and there is a WrongSpecificImpl inside, but we need MyCorrectImpl
				// - how to return the wrong obj to the user for pooling
				// - how to request a new obj of the right type?
				// - what if the data tells us the obj is null, how to return the obj then?
				// - what about all the fields? where is all of this handled? here? or further down somewhere?

				// We need to create the object so we can assign its fields
				// Only create a new object if there is none already
				Expression newObj;

				if (!specificType.IsValueType)
				{
					/*

					// ReferenceType / Object
					var ctor = specificType.GetConstructor(Type.EmptyTypes);
					if (ctor == null)
						throw new InvalidOperationException("Cannot compile formatter " +
															"for type " + specificType.Name + " because it has " +
															"no parameterless constructor, thus cannot be instantiated when deserializing");

					// What if there is no object for us to deserialize into already?
					var createNew = Expression.Convert(Expression.New(ctor), typeof(T));

					if (_serializer.Config.ObjectFactoryMethod != null)
					{
						var factoryMethod = _serializer.Config.ObjectFactoryMethod;

						Expression factoryCall;
						if (factoryMethod.Target == null)
							factoryCall = Expression.Call(factoryMethod.Method, Expression.Constant(specificType));
						else
							factoryCall = Expression.Call(Expression.Constant(factoryMethod.Target), factoryMethod.Method, Expression.Constant(specificType));

						var userCreate = Expression.Convert(factoryCall, typeof(T));

						// Existing -> UserMethod -> new()
						newObj = Expression.Coalesce(Expression.Coalesce(refValueArg, userCreate), createNew);
					}
					else
					{
						// Existing -> new()
						newObj = Expression.Coalesce(refValueArg, createNew);
					}

					*/
				}
				else
				{
					// ValueType / struct / primitive
					newObj = Expression.Convert(Expression.Default(specificType), typeof(T));
				}


				//block.Add(Expression.Assign(refValueArg, newObj));


				// Cast it to the specific type we actually need (so we can assign the specific fields)
				var valAsSpecific = Expression.Variable(specificType, "valAsSpecific");
				block.Add(Expression.Assign(valAsSpecific, Expression.Convert(refValueArg, specificType)));


				// Go through all fields and assign them
				foreach (var fieldInfo in fields)
				{
					// todo: what about Field attributes that tell us to:
					// - use a specific formatter? (HalfFloat, Int32Fixed, MyUserFormatter) 
					// - assume a type, or exception
					// - Force ignore caching (for ref types) (value types cannot be ref-saved)
					// - Persistent object caching per type or field

					var formatter = _serializer.GetFormatter(fieldInfo.FieldType, extraErrorInformation: $"DynamicObjectFormatter ObjectType: {specificType.FullName} FieldType: {fieldInfo.FieldType.FullName}");
					var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));

					Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter");

					var fieldExp = Expression.Field(valAsSpecific, fieldInfo);

					var serializeCall = Expression.Call(Expression.Constant(formatter), deserializeMethod, bufferArg, refOffsetArg, fieldExp);
					block.Add(serializeCall);
				}

				if (specificType.IsValueType)
					// If we're dealing with a value type, we have to write back from local-variable to ref-parameter
					// todo: warn the user that reference serialization with structs is very wonky in any case, and can't really be fixed
					// maybe we can deserialize reference fields to local vars, and keep track of the objectIDs that are supposed to go in there, then write them again or something?
					block.Add(Expression.Assign(refValueArg, Expression.Convert(valAsSpecific, typeof(T))));


				var serializeBlock = Expression.Block(variables: new[] { valAsSpecific }, expressions: block);
#if FAST_EXP
				return Expression.Lambda<DynamicDeserializer>(serializeBlock, bufferArg, refOffsetArg, refValueArg).CompileFast(true);
#else
				return Expression.Lambda<DynamicDeserializer>(serializeBlock, bufferArg, refOffsetArg, refValueArg).Compile();
#endif
			}
		}

		void ThrowIfNonspecific(Type type)
		{
			if (type.IsAbstract || type.IsInterface)
				throw new InvalidOperationException("Can only generate code for specific types. The type " + type.Name + " is abstract or an interface.");
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			// value might not be of type T. (Just think that T is an abstract class, and value is a concrete implementation. They're different types, and we might get all sorts of things that all inherit from T)
			Type type = value.GetType();

			// Now serialize this closed/specific object
			// typeof(T) is likely some abstract thing, maybe "Object" or "IMyCommonInterface"
			// Or maybe it's just the specific type itself, but we can't know that here, as the following could be possible as well:
			// class UserClassA {}
			// class UserClassB : UserClassA {}
			// If the field-type is UserClassA, then there could either an A or an B inside the field (because A is not abstract!), so we'll have to write the type anyway,
			// since at deserialization time we need to know which one of the two was present. (maybe we could avoid this specific special case eventually, but it's likely not worth the effort)
			DynamicSerializer serializer;
			if (!_specificSerializers.TryGetValue(type, out serializer))
			{
				var fields = GetSerializableFields(type, _serializer.Config.ShouldSerializeField);
				serializer = GenerateSerializer(fields, type);
				_specificSerializers[type] = serializer;
			}

			serializer(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			Type type = value.GetType();

			// Now serialize this closed/specific object
			DynamicDeserializer deserializer;
			if (!_specificDeserializers.TryGetValue(type, out deserializer))
			{
				var fields = GetSerializableFields(type, _serializer.Config.ShouldSerializeField);
				deserializer = GenerateDeserializer(fields, type);
				_specificDeserializers[type] = deserializer;
			}

			deserializer(buffer, ref offset, ref value);
		}


		internal static List<FieldInfo> GetSerializableFields(Type type, Func<FieldInfo, SerializationOverride> fieldFilter = null)
		{
			List<FieldInfo> fields = new List<FieldInfo>();

			var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

			var classConfig = type.GetCustomAttribute<CerasConfig>();


			foreach (var f in type.GetFields(flags))
			{
				// Skip readonly
				if (f.IsInitOnly)
					continue;

				//
				// 1.) Use filter if there is one
				if (fieldFilter != null)
				{
					var filterResult = fieldFilter(f);

					if (filterResult == SerializationOverride.ForceInclude)
					{
						fields.Add(f);
						continue;
					}
					else if (filterResult == SerializationOverride.ForceSkip)
					{
						continue;
					}
				}

				//
				// 2.) Use attribute
				var ignore = f.GetCustomAttribute<Ignore>(true) != null;
				var include = f.GetCustomAttribute<Include>(true) != null;

				if (ignore && include)
					throw new Exception($"Field '{f.Name}' on type '{type.Name}' has both [Ignore] and [Include]!");

				if (ignore)
				{
					continue;
				}

				if (include)
				{
					fields.Add(f);
					continue;
				}

				//
				// 3.) Use class attributes
				if (classConfig != null)
				{
					if (classConfig.IncludePrivate == false)
						if (f.IsPublic == false)
							continue;
					
					if (classConfig.MemberSerialization == MemberSerialization.OptIn)
					{
						// If we are here, that means that fields that have not already been added get skipped.
						continue;
					}

					if (classConfig.MemberSerialization == MemberSerialization.OptOut)
					{
						// Add everything, if there is an [Ignore] we wouldn't be here
						fields.Add(f);
						continue;
					}
				}

				//
				// 4.) Use global defaults
				// Which is simply "is it a public field?"
				
				if(f.IsPublic)
					fields.Add(f);
			}

			fields.Sort(_fieldComparer);

			return fields;
		}

		class FieldComparer : IComparer<FieldInfo>
		{
			public int Compare(FieldInfo x, FieldInfo y)
			{
				if (x == null || y == null)
					return 0;

				var name1 = x.FieldType.FullName + x.Name;
				var name2 = y.FieldType.FullName + y.Name;

				return string.Compare(name1, name2, StringComparison.Ordinal);
			}
		}
	}
}