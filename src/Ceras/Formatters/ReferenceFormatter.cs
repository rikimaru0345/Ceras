namespace Ceras.Formatters {
	using Ceras.Helpers;
	using Ceras.Format;
	using Resolvers;
	using System;
	using System.Buffers;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Diagnostics.CodeAnalysis;
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

			// A polymorphic slot is the one position a multidimensional array cannot survive: the slot
			// declares only a base type, so the concrete type is recorded as the type value written
			// behind the reference marker below, and that record has no array rank in it (see
			// RobustTypeFormatter). The save would be accepted and the value read back as a
			// one-dimensional array over the same flattened elements, so it is refused here instead.
			// Robust only: the raw serializer's TypeFormatter names the full CLR type, rank included, and
			// CerasFastSerializer round-trips int[,] through an object slot today (the guard is checked
			// only while a robust write context is pushed, exactly as RobustTypeFormatter tests it).
			// The check sits ahead of everything this method does to the operation — the dispatcher entry,
			// the object-cache registration, the marker byte and the type value — so a refused save leaves
			// no bytes and no per-save state behind for the next one to trip over. An array is never a
			// banned shape (the only inheritance ban is IEnumerator, which arrays do not implement), so
			// this does not overlap the banned-value refusal below.
			if (RobustContext.InternTable != null && MultidimensionalArrayGuard.IsMultidimensional(specificType))
				throw MultidimensionalArrayGuard.Reject(specificType,
					$"a polymorphic member declared as '{TypeIdentity.GetCanonicalName(typeof(T))}'",
					MultidimensionalArrayGuard.TypeValueReason);

			if (BannedTypes.TryGetBanned(specificType, out var banReason)) {
				// The declared slot says nothing about this value, so a banned runtime type here is
				// data the user put in a place Ceras cannot write. Writing the null marker instead
				// used to produce a save that looked fine and came back with a null, so the value is
				// rejected by name instead. A null value (handled above) is still a legal null.
				throw new BannedTypeException(
					$"A value of the banned type '{specificType.FriendlyName(true)}' was written through a slot declared as '{typeof(T).FriendlyName(true)}'. " +
					"Ceras cannot serialize that value and will not write it as null, because the loaded value would then silently differ from the saved one. " +
					"Keep the value out of the payload on purpose (mark the member [Exclude], or replace it with a serializable representation " +
					$"- for a delegate: store the target and the method identity yourself and re-attach it in an [OnAfterDeserialize] callback). " +
					$"Reason this type is banned: \"{banReason}\".");
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
				try { entry.SerializeDispatcher(writer, value); } catch (Ceras.Exceptions.CerasException) { throw; } catch (Exception e) { throw new Ceras.Exceptions.CerasException($"Error while serializing object of type '{specificType.FullName}'", e); }
			}
		}

		public void Deserialize(ref SequenceReader<byte> reader, ref T value) {
			var objId = SerializerBinary.ReadUInt32Bias(ref reader, Bias);
			// A null marker is a legal value in a reference slot, and IFormatter<T>'s out parameter is
			// deliberately not nullable: the null is a *value* here, so it is assigned with the
			// null-forgiving operator rather than pretended not to happen.
			if (objId == Null) { value = default!; return; }
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
			catch (Ceras.Exceptions.CerasException) {
				// A Ceras refusal arrives as itself, for the same reason Ceras.Helpers.Instantiator does it:
				// a corruption report or a schema refusal raised by the inner formatter *is* the diagnosis,
				// and wrapping it turns a typed refusal a caller can catch into a generic one whose cause
				// is only an InnerException. The wrapper stays for everything else — a formatter of user
				// code throwing its own exception is a case the message here does describe.
				throw;
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

			// Built locally and published only once it is whole. Publishing it first would leave an entry
			// with no dispatchers behind if anything below threw — and the next operation would then find
			// that entry, treat it as complete, and call a null dispatcher. Resolving the formatter is one
			// of the things below, and it can throw for reasons that have nothing to do with this formatter
			// (a robust context refusing a resolver selection is one), so "built" and "published" have to
			// be separate steps for a failure here to leave nothing behind.
			var built = new DispatcherEntry(type);
			if (built.IsType) { entry = built; return built; }   // a Type value needs no specific formatter

			var formatter = _ceras.GetSpecificFormatter(type);
			if (formatter == null) throw new InvalidOperationException($"Could not find specific formatter for type '{type.FullName}'");

			built.SerializeDispatcher = CreateSpecificSerializerDispatcher(type, formatter);
			built.DeserializeDispatcher = CreateSpecificDeserializerDispatcher(type, formatter);

			// Which construction builds a fresh instance:
			//   * a construction the application selected for this type is always honored. It exists
			//     precisely to replace the allocation — a pool, a factory handing out recycled
			//     instances — so it wins even over a generated formatter that would otherwise build the
			//     instance itself.
			//   * a construction Ceras picked by itself is honored only when nothing else builds the
			//     instance. Letting the default take that job away from generated code would silently
			//     change what every generated formatter does (a robust context's default is "uninitialized",
			//     which would skip the constructor the generated formatter was written to run).
			// The migration interpreter mirrors this in Format.CurrentObjectFactory.
			var typeConfig = _ceras.Config.GetTypeConfigForClosedType(type, false);
			// The placeholder hands back null: a formatter-constructed type is built by its own formatter,
			// so there is no constructor to call here and the dispatcher fills the instance in. The call
			// sites cast the result to T (a reference type), where null is a legal value, so the
			// null-forgiving operator is used rather than pretending Func<object> cannot return null.
			built.Constructor = _ceras.IsFormatterConstructedInstance(type) && !typeConfig.IsConstructionExplicit
				? () => null!
				: typeConfig.TypeConstruction.GetRefFormatterConstructor();

			// Every field above is assigned; only now is this a dispatcher rather than a shell.
			entry = built;
			return built;
		}

		
		// The dependency is stated on this method because it is the only place
		// ReflectionSerializationHelper<,> is ever closed: the closed type is a MakeGenericType
		// result over two runtime types, so no compilation names it, and an annotation on the
		// runtime `type` parameter could not be met anyway (see CerasSerializer.GetReferenceFormatter
		// for why the requirement goes on the open definition rather than on that type).
		//
		// Constructors, because that is what Instantiator.Create binds; and PublicMethods, because
		// the helper's only reason to exist is its interface implementation - `helper.Serialize` is a
		// call through IReflectionSerializer<TBase>, and nothing else in the assembly ever touches the
		// helper type. That is the difference from a FormatterBase<T> formatter, whose Serialize and
		// Deserialize are the overrides the non-generic IFormatter contract already roots: this one
		// overrides nothing, so it survives only because it is asked for.
		[DynamicDependency(
			DynamicallyAccessedMemberTypes.PublicConstructors
			| DynamicallyAccessedMemberTypes.NonPublicConstructors
			| DynamicallyAccessedMemberTypes.PublicMethods,
			typeof(ReflectionSerializationHelper<,>))]
		static SerializeDelegate<T> CreateSpecificSerializerDispatcher(Type type, IFormatter specificFormatter) {
			// The by-name lookup belongs to the branch that binds a delegate to what it finds, and to
			// nothing else. The branch below calls the formatter through IFormatter<T> instead, so it has
			// no use for a MethodInfo - and that is what keeps a formatter which implements the interface
			// explicitly usable through a base-typed slot: reflection names such a method after the
			// interface, so this lookup does not find it (and says exactly which method it wanted). Asking
			// for it where nothing binds it would refuse a dispatch that works.
			if (type == typeof(T))
			{
				var method = specificFormatter.GetType().ResolveSerializeMethod(type);
				return (SerializeDelegate<T>)Delegate.CreateDelegate(typeof(SerializeDelegate<T>), specificFormatter, method);
			}

			// Use a helper class to avoid boxing and object[] allocation
			var helperType = typeof(ReflectionSerializationHelper<,>).MakeGenericType(typeof(T), type);
			var helper = (IReflectionSerializer<T>)Instantiator.Create(helperType, specificFormatter);
			return helper.Serialize;
		}
		// The mirror of CreateSpecificSerializerDispatcher's dependency, for the same two reasons:
		// this is the only place ReflectionDeserializationHelper<,> is closed, and the method the
		// helper exists to provide is the implementation of IReflectionDeserializer<TBase> that the
		// `helper.Deserialize` call binds to.
		[DynamicDependency(
			DynamicallyAccessedMemberTypes.PublicConstructors
			| DynamicallyAccessedMemberTypes.NonPublicConstructors
			| DynamicallyAccessedMemberTypes.PublicMethods,
			typeof(ReflectionDeserializationHelper<,>))]
		static DeserializeDelegate<T> CreateSpecificDeserializerDispatcher(Type type, IFormatter specificFormatter) {
			// As in CreateSpecificSerializerDispatcher: the lookup is needed only where a delegate is bound
			// to it, and the interface-based branch below needs no MethodInfo at all.
			if (type == typeof(T))
			{
				var method = specificFormatter.GetType().ResolveDeserializeMethod(type);
				return (DeserializeDelegate<T>)Delegate.CreateDelegate(typeof(DeserializeDelegate<T>), specificFormatter, method);
			}

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
			// As in ReferenceFormatter<T>.Deserialize: the wire's null marker is a value, and the
			// non-nullable IFormatter<T> out parameter is not changed to admit it.
			if (objId == Null) { value = default!; return; }
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
