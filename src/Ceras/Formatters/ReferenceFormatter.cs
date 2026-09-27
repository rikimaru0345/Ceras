namespace Ceras.Formatters {
	using Ceras.Helpers;
	using Ceras.Format;
	using Resolvers;
	using System;
	using System.Buffers;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Runtime.CompilerServices;
	using System.Reflection;

	public sealed class ReferenceFormatter<T> : IFormatter<T>
	where T : class {
		const int Null = Protocol.RefNull;
		const int NewValue = Protocol.RefNewValue;
		const int NewValueSameType = Protocol.RefNewValueSameType;
		const int InlineType = Protocol.RefInlineType;
		const int Bias = Protocol.RefBias;

		readonly CerasSerializer _ceras;
		readonly IFormatter<Type> _typeFormatter;
		readonly TypeDictionary<DispatcherEntry> _dispatchers = new TypeDictionary<DispatcherEntry>();

		internal ReferenceFormatter(CerasSerializer ceras) {
			_ceras = ceras;
			if (typeof(T).IsStatic()) throw new InvalidOperationException("static");
			_typeFormatter = ceras.GetFormatter<Type>();
		}

		public void SerializeUntyped(IBufferWriter<byte> writer, object value) {
			Serialize(writer, (T)value);
		}

		public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) {
			T value = default!;
			if (member is FieldInfo f) {
				var existing = f.GetValue(container);
				if (existing is T t) value = t;
				Deserialize(ref reader, ref value);
				f.SetValue(container, value);
			}
			else if (member is PropertyInfo p) {
				var existing = p.GetValue(container);
				if (existing is T t) value = t;
				Deserialize(ref reader, ref value);
				p.SetValue(container, value);
			}
		}

		public void Serialize(IBufferWriter<byte> writer, T value) {
			if (ReferenceEquals(value, null)) { SerializerBinary.WriteUInt32Bias(writer, Null, Bias); return; }
			var specificType = value.GetType();

			if (BannedTypes.IsBanned(specificType)) {
				// We should only skip it if it is NOT one of the types we actually MUST be able to serialize.
				SerializerBinary.WriteUInt32Bias(writer, Null, Bias);
				return;
			}

			var entry = GetOrCreateEntry(specificType);
			if (entry.IsType) { SerializerBinary.WriteUInt32Bias(writer, InlineType, Bias); _typeFormatter.Serialize(writer, (Type)(object)value); return; }
		// ExternalRootObject support removed

			if (_ceras.InstanceData.ObjectCache.TryGetExistingObjectId(value, out int id)) {
				SerializerBinary.WriteUInt32Bias(writer, id, Bias);
			}
			else {
				_ceras.InstanceData.ObjectCache.RegisterObject(value);
				SerializerBinary.WriteUInt32Bias(writer, ReferenceEquals(typeof(T), specificType) ? NewValueSameType : NewValue, Bias);
				if (!ReferenceEquals(typeof(T), specificType)) _typeFormatter.Serialize(writer, specificType);
				try { entry.SerializeDispatcher(writer, value); } catch (Exception e) { throw new Ceras.Exceptions.CerasException($"Error while serializing object of type '{specificType.FullName}'", e); }
			}
		}

		public void Deserialize(ref SequenceReader<byte> reader, ref T value) {
			var objId = SerializerBinary.ReadUInt32Bias(ref reader, Bias);
			if (objId == Null) { value = default; return; }
			if (objId == InlineType) {
				// Serialize only writes the InlineType marker for a non-null Type instance, so the type
				// read back here is never null. The seed is what the non-nullable IFormatter contract
				// needs; the value itself is always overwritten by the deserialization.
				Type type = null!;
				_typeFormatter.Deserialize(ref reader, ref type);
				value = (T)(object)type;
				return;
			}
			if (objId >= 0) {
				value = _ceras.InstanceData.ObjectCache.GetExistingObject<T>(objId);
				return;
			}
		// ExternalRootObject support removed
			// A NewValue marker is always written together with the concrete runtime type (Serialize
			// writes the type right behind the marker), while NewValueSameType means "the declared T".
			Type specificType;
			if (objId == NewValue) {
				specificType = null!;
				_typeFormatter.Deserialize(ref reader, ref specificType);
			}
			else specificType = typeof(T);
			var entry = GetOrCreateEntry(specificType);

			if (!entry.IsValueType) {
				bool allocated = false;
				if (value != null) {
					if (value.GetType() != specificType) {
						value = (T)entry.Constructor();
						allocated = true;
					}
				}
				else {
					value = (T)entry.Constructor();
					allocated = true;
				}
				// Tell the specific formatter (if it is generated) whether it must run the
				// constructor exactly once (new instance) or must leave the reused instance alone.
				_ceras.SetCurrentObjectFresh(allocated);
			}

			var objectProxy = _ceras.InstanceData.ObjectCache.CreateDeserializationProxy<T>(specificType);
			objectProxy.Value = value;
			
			var oldProxy = _ceras.InstanceData.CurrentProxy;
			_ceras.InstanceData.CurrentProxy = objectProxy;
			
			var proxyValue = objectProxy.Value;
			try {
				entry.DeserializeDispatcher(ref reader, ref proxyValue);
			}
			catch (Exception e) {
				throw new Exceptions.CerasException($"Error while deserializing object of type '{specificType?.FullName ?? typeof(T).FullName}'", e);
			}
			finally {
				_ceras.InstanceData.CurrentProxy = oldProxy;
				if (proxyValue == null && objectProxy.Value != null)
					proxyValue = (T)objectProxy.Value;
			}
			objectProxy.Value = proxyValue;
			value = objectProxy.Value;
		}

		/// <summary>
		/// The dispatcher for <paramref name="type"/>, creating it on first use. Never returns null: a
		/// type without a formatter is a configuration error and throws.
		/// </summary>
		DispatcherEntry GetOrCreateEntry(Type type) {
			if (type == null) throw new ArgumentNullException(nameof(type));

			ref var entry = ref _dispatchers.GetOrAddValueRef(type);
			if (entry != null) return entry;
			entry = new DispatcherEntry(type);
			if (entry.IsType) return entry;

			var formatter = _ceras.GetSpecificFormatter(type);
			if (formatter == null) throw new InvalidOperationException($"Could not find specific formatter for type '{type.FullName}'");

			entry.SerializeDispatcher = CreateSpecificSerializerDispatcher(type, formatter);
			entry.DeserializeDispatcher = CreateSpecificDeserializerDispatcher(type, formatter);
			
			if (_ceras.IsFormatterConstructedInstance(type))
				// A formatter-constructed type is built by its own formatter, so there is no constructor
				// to call here: the placeholder hands back null and the dispatcher fills the instance in.
				// The call sites cast the result to T (a reference type), where null is a legal value.
				entry.Constructor = () => null!;
			else
				entry.Constructor = _ceras.Config.GetTypeConfigForClosedType(type, false).TypeConstruction.GetRefFormatterConstructor();
			
			return entry;
		}

		
		static SerializeDelegate<T> CreateSpecificSerializerDispatcher(Type type, IFormatter specificFormatter) {
			var method = specificFormatter.GetType().ResolveSerializeMethod(type);
			if (type == typeof(T))
				return (SerializeDelegate<T>)Delegate.CreateDelegate(typeof(SerializeDelegate<T>), specificFormatter, method);

			// Use a helper class to avoid boxing and object[] allocation
			var helperType = typeof(ReflectionSerializationHelper<,>).MakeGenericType(typeof(T), type);
			var helper = (IReflectionSerializer<T>)Instantiator.Create(helperType, specificFormatter);
			return helper.Serialize;
		}
		static DeserializeDelegate<T> CreateSpecificDeserializerDispatcher(Type type, IFormatter specificFormatter) {
			var method = specificFormatter.GetType().ResolveDeserializeMethod(type);
			if (type == typeof(T))
				return (DeserializeDelegate<T>)Delegate.CreateDelegate(typeof(DeserializeDelegate<T>), specificFormatter, method);

			// Use a helper class to avoid boxing ref struct
			var helperType = typeof(ReflectionDeserializationHelper<,>).MakeGenericType(typeof(T), type);
			var helper = (IReflectionDeserializer<T>)Instantiator.Create(helperType, specificFormatter);
			return helper.Deserialize;
		}

		class DispatcherEntry {
			public readonly Type Type;
			public readonly bool IsType, IsValueType;
			// GetOrCreateEntry fills these in for every entry it does not leave early. A Type entry is
			// written and read through the InlineType marker, which returns before any of the three is
			// reached, so they stay null for that one case instead of being allocated per Type.
			public SerializeDelegate<T> SerializeDispatcher = default!;
			public DeserializeDelegate<T> DeserializeDispatcher = default!;
			public Func<object> Constructor = default!;
			public DispatcherEntry(Type type) {
				Type = type;
				IsType = typeof(Type).IsAssignableFrom(type);
				IsValueType = type.IsValueType;
			}
		}
	}

	interface IReflectionSerializer<TBase> {
		void Serialize(IBufferWriter<byte> writer, TBase value);
	}

	class ReflectionSerializationHelper<TBase, TSpecific> : IReflectionSerializer<TBase>
		where TSpecific : TBase {
		// The instance handed to a specific formatter may be null: a reference type that the payload
		// marks as null, and a formatter-constructed type whose constructor placeholder produced null.
		readonly IFormatter<TSpecific?> _formatter;
		public ReflectionSerializationHelper(IFormatter specificFormatter) {
			_formatter = (IFormatter<TSpecific?>)specificFormatter;
		}

		public void Serialize(IBufferWriter<byte> writer, TBase value) {
			_formatter.Serialize(writer, (TSpecific?)value);
		}
	}

	interface IReflectionDeserializer<TBase> {
		void Deserialize(ref SequenceReader<byte> reader, ref TBase value);
	}

	class ReflectionDeserializationHelper<TBase, TSpecific> : IReflectionDeserializer<TBase>
		where TSpecific : TBase {
		// Nullable for the same reason as in ReflectionSerializationHelper: null goes in and null can
		// come out, because a null instance is a legal wire value and because the constructor
		// placeholder of a formatter-constructed type yields null.
		readonly IFormatter<TSpecific?> _formatter;
		public ReflectionDeserializationHelper(IFormatter specificFormatter) {
			_formatter = (IFormatter<TSpecific?>)specificFormatter;
		}

		public void Deserialize(ref SequenceReader<byte> reader, ref TBase value) {
			TSpecific? specific = value != null ? (TSpecific)value : default;
			_formatter.Deserialize(ref reader, ref specific);
			// The ref parameter of the deserialize contract is declared non-nullable, but a null instance
			// is a legal value here and the caller (ReferenceFormatter.Deserialize) handles it, so the
			// null-forgiving operator is used rather than pretending the formatter cannot return null.
			value = specific!;
		}
	}

	public sealed class ReferenceFormatter_KnownSealedType<T> : IFormatter<T> where T : class {
		const int Null = Protocol.RefNull, NewValueSameType = Protocol.RefNewValueSameType, Bias = Protocol.RefBias;
		readonly CerasSerializer _ceras; readonly IFormatter<T> _formatter; 
		readonly Func<object> _constructor;
		internal ReferenceFormatter_KnownSealedType(CerasSerializer ceras) {
			_ceras = ceras;
			var type = typeof(T);
			if (type.IsStatic()) throw new InvalidOperationException("static");
			if (!type.IsSealed) throw new InvalidOperationException("Not sealed");
			_formatter = (IFormatter<T>)ceras.GetSpecificFormatter(type);
			_constructor = _ceras.Config.GetTypeConfigForClosedType(type, false).TypeConstruction.GetRefFormatterConstructor();
			}

		
		public void SerializeUntyped(IBufferWriter<byte> writer, object value) {
			Serialize(writer, (T)value);
		}

		public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) {
			T value = default!;
			if (member is FieldInfo f) {
				var existing = f.GetValue(container);
				if (existing is T t) value = t;
				Deserialize(ref reader, ref value);
				f.SetValue(container, value);
			}
			else if (member is PropertyInfo p) {
				var existing = p.GetValue(container);
				if (existing is T t) value = t;
				Deserialize(ref reader, ref value);
				p.SetValue(container, value);
			}
		}

		public void Serialize(IBufferWriter<byte> writer, T value) {
			if (ReferenceEquals(value, null)) { SerializerBinary.WriteUInt32Bias(writer, Null, Bias); return; }
			if (_ceras.InstanceData.ObjectCache.TryGetExistingObjectId(value, out int id)) SerializerBinary.WriteUInt32Bias(writer, id, Bias);
			else { _ceras.InstanceData.ObjectCache.RegisterObject(value); SerializerBinary.WriteUInt32Bias(writer, NewValueSameType, Bias); _formatter.Serialize(writer, value); }
		}

		public void Deserialize(ref SequenceReader<byte> reader, ref T value) {
			var objId = SerializerBinary.ReadUInt32Bias(ref reader, Bias);
			if (objId == Null) { value = default; return; }
			if (objId >= 0) {
				value = _ceras.InstanceData.ObjectCache.GetExistingObject<T>(objId);
				return;
			}
			if (value == null) {
				_ceras.SetCurrentObjectFresh(true);
				value = (T)_constructor();
			}
			else {
				_ceras.SetCurrentObjectFresh(false);
			}
			var objectProxy = _ceras.InstanceData.ObjectCache.CreateDeserializationProxy<T>(typeof(T));
			objectProxy.Value = value;
			
			var oldProxy = _ceras.InstanceData.CurrentProxy;
			_ceras.InstanceData.CurrentProxy = objectProxy;
			
			var proxyValue = objectProxy.Value;
			try {
				_formatter.Deserialize(ref reader, ref proxyValue);
			}
			finally {
				_ceras.InstanceData.CurrentProxy = oldProxy;
				if (proxyValue == null && objectProxy.Value != null)
					proxyValue = (T)objectProxy.Value;
			}
			objectProxy.Value = proxyValue;
			value = objectProxy.Value;
		}
	}
}
