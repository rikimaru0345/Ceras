
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
		static FieldComparer _fieldComparer = new FieldComparer();

		delegate void DynamicSerializer(ref byte[] buffer, ref int offset, T value);
		delegate void DynamicDeserializer(byte[] buffer, ref int offset, ref T value);

		CerasSerializer _ceras;
		DynamicSerializer _dynamicSerializer;
		DynamicDeserializer _dynamicDeserializer;


		public DynamicObjectFormatter(CerasSerializer serializer)
		{
			_ceras = serializer;

			var type = typeof(T);
			ThrowIfNonspecific(type);

			var fields = GetSerializableFields(type, _ceras.Config.ShouldSerializeField);

			_dynamicSerializer = GenerateSerializer(fields);
			_dynamicDeserializer = GenerateDeserializer(fields);
		}


		DynamicSerializer GenerateSerializer(List<FieldInfo> fields)
		{
			var refBufferArg = Expression.Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Expression.Parameter(typeof(T), "value");

			List<Expression> block = new List<Expression>();


			foreach (var fieldInfo in fields)
			{
				IFormatter formatter;

				var type = fieldInfo.FieldType;

				if (type.IsValueType)
					formatter = _ceras.GetSpecificFormatter(type);
				else
					formatter = _ceras.GetGenericFormatter(type);

				// Get the formatter and its Serialize method
				// var formatter = _ceras.GetFormatter(fieldInfo.FieldType, extraErrorInformation: $"DynamicObjectFormatter ObjectType: {specificType.FullName} FieldType: {fieldInfo.FieldType.FullName}");
				var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
				Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter");

				// Access the field that we want to serialize
				var fieldExp = Expression.Field(valueArg, fieldInfo);

				// Call "Serialize"
				var serializeCall = Expression.Call(Expression.Constant(formatter), serializeMethod, refBufferArg, refOffsetArg, fieldExp);
				block.Add(serializeCall);
			}

			var serializeBlock = Expression.Block(expressions: block);

#if FAST_EXP
				return Expression.Lambda<DynamicSerializer>(serializeBlock, refBufferArg, refOffsetArg, valueArg).CompileFast(true);
#else
			return Expression.Lambda<DynamicSerializer>(serializeBlock, refBufferArg, refOffsetArg, valueArg).Compile();
#endif

		}

		DynamicDeserializer GenerateDeserializer(List<FieldInfo> fields)
		{
			var bufferArg = Expression.Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Expression.Parameter(typeof(T).MakeByRefType(), "value");

			List<Expression> block = new List<Expression>();

			// Go through all fields and assign them
			foreach (var fieldInfo in fields)
			{
				// todo: what about Field attributes that tell us to:
				// - use a specific formatter? (HalfFloat, Int32Fixed, MyUserFormatter) 
				// - assume a type, or exception
				// - Force ignore caching (for ref types) (value types cannot be ref-saved)
				// - Persistent object caching per type or field

				var type = fieldInfo.FieldType;

				IFormatter formatter;

				if (type.IsValueType)
					formatter = _ceras.GetSpecificFormatter(type);
				else
					formatter = _ceras.GetGenericFormatter(type);
				
				//var formatter = _ceras.GetFormatter(fieldInfo.FieldType, extraErrorInformation: $"DynamicObjectFormatter ObjectType: {specificType.FullName} FieldType: {fieldInfo.FieldType.FullName}");
				var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
				Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter");

				var fieldExp = Expression.Field(refValueArg, fieldInfo);

				var serializeCall = Expression.Call(Expression.Constant(formatter), deserializeMethod, bufferArg, refOffsetArg, fieldExp);
				block.Add(serializeCall);
			}
			

			var serializeBlock = Expression.Block(expressions: block);
#if FAST_EXP
			return Expression.Lambda<DynamicDeserializer>(serializeBlock, bufferArg, refOffsetArg, refValueArg).CompileFast(true);
#else
			return Expression.Lambda<DynamicDeserializer>(serializeBlock, bufferArg, refOffsetArg, refValueArg).Compile();
#endif

		}

		void ThrowIfNonspecific(Type type)
		{
			if (type.IsAbstract || type.IsInterface)
				throw new InvalidOperationException("Can only generate code for specific types. The type " + type.Name + " is abstract or an interface.");
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			_dynamicSerializer(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			_dynamicDeserializer(buffer, ref offset, ref value);
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

				if (f.IsPublic)
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