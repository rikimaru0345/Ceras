namespace Ceras.Formatters {
	using Ceras.Helpers;
	using Resolvers;
	using System;
	using System.Buffers;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Runtime.CompilerServices;
	using System.Reflection;

	public sealed class ReferenceFormatter<T> : IFormatter<T>
	where T : class {
		const int Null = -1;
		const int NewValue = -2;
		const int NewValueSameType = -3;
		const int ExternalObject = -4;
		const int InlineType = -5;
		const int Bias = 5;

		readonly CerasSerializer _ceras;
		readonly IFormatter<Type> _typeFormatter;
		readonly TypeDictionary<DispatcherEntry> _dispatchers = new TypeDictionary<DispatcherEntry>();

		public ReferenceFormatter(CerasSerializer ceras) {
			_ceras = ceras;
			if (typeof(T).IsStatic()) throw new InvalidOperationException("static");
			_typeFormatter = ceras.GetFormatter<Type>();
		}

		public void SerializeUntyped(IBufferWriter<byte> writer, object value) {
			Serialize(writer, (T)value);
		}

		public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) {
			T value = default;
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
			if (objId == Null) { if (value != null) _ceras.DiscardObjectMethod?.Invoke(value); value = default; return; }
			if (objId == InlineType) { Type type = null; _typeFormatter.Deserialize(ref reader, ref type); value = (T)(object)type; return; }
			if (objId >= 0) {
				value = _ceras.InstanceData.ObjectCache.GetExistingObject<T>(objId);
				return;
			}
		// ExternalRootObject support removed
			var specificType = (objId == NewValue) ? null : typeof(T);
			if (objId == NewValue) _typeFormatter.Deserialize(ref reader, ref specificType);
			var entry = GetOrCreateEntry(specificType);

			if (!entry.IsValueType) {
				if (value != null) {
					if (value.GetType() != specificType) {
						_ceras.DiscardObjectMethod?.Invoke(value);
						value = (T)entry.Constructor();
					}
				}
				else {
					value = (T)entry.Constructor();
				}
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
				entry.Constructor = () => null;
			else
				entry.Constructor = _ceras.Config.GetTypeConfig(type, false).TypeConstruction.GetRefFormatterConstructor();
			
			return entry;
		}

		
		static SerializeDelegate<T> CreateSpecificSerializerDispatcher(Type type, IFormatter specificFormatter) {
			var method = specificFormatter.GetType().ResolveSerializeMethod(type);
			if (type == typeof(T))
				return (SerializeDelegate<T>)Delegate.CreateDelegate(typeof(SerializeDelegate<T>), specificFormatter, method);

			// Use a helper class to avoid boxing and object[] allocation
			var helperType = typeof(ReflectionSerializationHelper<,>).MakeGenericType(typeof(T), type);
			var helper = (IReflectionSerializer<T>)Activator.CreateInstance(helperType, specificFormatter);
			return helper.Serialize;
		}
		static DeserializeDelegate<T> CreateSpecificDeserializerDispatcher(Type type, IFormatter specificFormatter) {
			var method = specificFormatter.GetType().ResolveDeserializeMethod(type);
			if (type == typeof(T))
				return (DeserializeDelegate<T>)Delegate.CreateDelegate(typeof(DeserializeDelegate<T>), specificFormatter, method);

			// Use a helper class to avoid boxing ref struct
			var helperType = typeof(ReflectionDeserializationHelper<,>).MakeGenericType(typeof(T), type);
			var helper = (IReflectionDeserializer<T>)Activator.CreateInstance(helperType, specificFormatter);
			return helper.Deserialize;
		}

		class DispatcherEntry {
			public readonly Type Type;
			public readonly bool IsType, IsValueType;
			public SerializeDelegate<T> SerializeDispatcher;
			public DeserializeDelegate<T> DeserializeDispatcher;
			public Func<object> Constructor;
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
		readonly IFormatter<TSpecific> _formatter;
		public ReflectionSerializationHelper(IFormatter specificFormatter) {
			_formatter = (IFormatter<TSpecific>)specificFormatter;
		}

		public void Serialize(IBufferWriter<byte> writer, TBase value) {
			_formatter.Serialize(writer, (TSpecific)value);
		}
	}

	interface IReflectionDeserializer<TBase> {
		void Deserialize(ref SequenceReader<byte> reader, ref TBase value);
	}

	class ReflectionDeserializationHelper<TBase, TSpecific> : IReflectionDeserializer<TBase>
		where TSpecific : TBase {
		readonly IFormatter<TSpecific> _formatter;
		public ReflectionDeserializationHelper(IFormatter specificFormatter) {
			_formatter = (IFormatter<TSpecific>)specificFormatter;
		}

		public void Deserialize(ref SequenceReader<byte> reader, ref TBase value) {
			TSpecific specific = value != null ? (TSpecific)value : default;
			_formatter.Deserialize(ref reader, ref specific);
			value = specific;
		}
	}

	public sealed class ReferenceFormatter_KnownSealedType<T> : IFormatter<T> where T : class {
		const int Null = -1, NewValueSameType = -3, Bias = 5;
		readonly CerasSerializer _ceras; readonly IFormatter<T> _formatter; 
		readonly Func<object> _constructor;
		public ReferenceFormatter_KnownSealedType(CerasSerializer ceras) {
			_ceras = ceras;
			var type = typeof(T);
			if (type.IsStatic()) throw new InvalidOperationException("static");
			if (!type.IsSealed) throw new InvalidOperationException("Not sealed");
			_formatter = (IFormatter<T>)ceras.GetSpecificFormatter(type);
			_constructor = _ceras.Config.GetTypeConfig(type, false).TypeConstruction.GetRefFormatterConstructor();
			}

		
		public void SerializeUntyped(IBufferWriter<byte> writer, object value) {
			Serialize(writer, (T)value);
		}

		public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) {
			T value = default;
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
			if (objId == Null) { if (value != null) _ceras.DiscardObjectMethod?.Invoke(value); value = default; return; }
			if (objId >= 0) {
				value = _ceras.InstanceData.ObjectCache.GetExistingObject<T>(objId);
				return;
			}
			if (value == null) {
				value = (T)_constructor();
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
