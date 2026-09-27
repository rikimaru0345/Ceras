using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ceras.Format
{
    /// <summary>
    /// The instance handed back to the caller, plus the object whose member actually receives the elements.
    /// They differ for a wrapped shape: <c>ReadOnlyCollection&lt;T&gt;</c> has no <c>Add</c>, so its elements
    /// go into the backing <c>List&lt;T&gt;</c> that was passed to its constructor while the wrapper itself is
    /// what the field receives.
    /// </summary>
    readonly struct CollectionFill
    {
        public readonly object Result;
        public readonly object Target;

        public CollectionFill(object result, object target)
        {
            Result = result;
            Target = target;
        }
    }

    /// <summary>
    /// Builds the *current* CLR objects a migrated graph is made of. This is the one place that knows how
    /// to turn a described type into a live instance: reference types, value types, and the collection shapes
    /// that have to be built either directly or by wrapping a built-in collection.
    /// <para>
    /// It knows nothing about the file, the payload or the reader: it is handed a target type and returns an
    /// instance (plus, for a wrapped shape, the object that receives the elements). That separation is what
    /// keeps the construction contract of the two readers comparable — see
    /// <c>ConstructionReuseContractTests</c> and <c>ConstructorReuseTests</c>.
    /// </para>
    /// </summary>
    sealed class CurrentObjectFactory
    {
        readonly CerasSerializer _serializer;

        public CurrentObjectFactory(CerasSerializer serializer)
        {
            _serializer = serializer;
        }

        /// <summary>True when the runtime type of <paramref name="obj"/> is exactly <paramref name="type"/>.</summary>
        public static bool IsExactType(object obj, Type type) => type != null && obj.GetType() == type;

        /// <summary>True for the shapes written as an explicit count plus elements.</summary>
        public static bool IsCollectionShape(Type type)
            => type != null && !type.IsArray && WellKnownTypes.IsCollectionFormatterShape(type);

        /// <summary>
        /// Allocates a reference-typed instance the way the generated reader does when no constructor
        /// arguments are involved: uninitialized, then the parameterless constructor when the type has
        /// one, so field initializers run and the object is fully initialized before its members are
        /// assigned. A configured <see cref="TypeConstruction"/> (a factory or a pool) is deliberately not
        /// consulted here — routing reference types through it is TODO 0.8.11, and doing it now would
        /// change reuse/pooling semantics as a side effect of this contract.
        /// </summary>
        public object CreateReference(Type type)
        {
            if (type == null) return null;

            var obj = RuntimeHelpers.GetUninitializedObject(type);
            var parameterless = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (parameterless != null)
            {
                try
                {
                    parameterless.Invoke(obj, null);
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    throw new Exceptions.CerasSchemaException(
                        $"The parameterless constructor of '{TypeIdentity.GetCanonicalName(type)}' threw while migrating a save: " +
                        $"{e.InnerException.GetType().Name}: {e.InnerException.Message}", e.InnerException);
                }
            }
            return obj;
        }

        /// <summary>
        /// Allocates a value type, and a reference type that has a <c>[CerasConstructor]</c> supplying its own
        /// arguments through the configured construction. Returns null for a null type, and falls back to an
        /// uninitialized instance when nothing is configured, matching <see cref="CreateReference"/>'s
        /// treatment of a type with no usable constructor.
        /// </summary>
        public object CreateInstance(Type type)
        {
            if (type == null) return null;
            if (type.IsValueType) return Activator.CreateInstance(type);

            var typeConfig = _serializer.Config.GetTypeConfig(type, false);
            var tc = typeConfig.TypeConstruction;
            if (tc != null && !tc.HasDataArguments && tc is not ConstructNull)
            {
                var ctor = tc.GetRefFormatterConstructor();
                if (ctor != null) return ctor();
            }

            return RuntimeHelpers.GetUninitializedObject(type);
        }

        /// <summary>
        /// Produces the collection instance a collection payload is read into, in this order:
        /// <list type="number">
        /// <item>reuse <paramref name="existingObject"/> in place when it is the exact target type and
        /// exposes a usable <c>clearMethodName</c> (never silently append instead);</item>
        /// <item>otherwise construct the shape itself through its public parameterless constructor,
        /// which preserves a collection subclass exactly;</item>
        /// <item>otherwise build the built-in collection the shape describes and construct the shape from
        /// it, which is how a shape with no parameterless constructor
        /// (<c>ReadOnlyCollection&lt;T&gt;</c>(<c>IList&lt;T&gt;</c>),
        /// <c>ReadOnlyDictionary&lt;K,V&gt;</c>(<c>IDictionary&lt;K,V&gt;</c>), a <c>List&lt;T&gt;</c> or
        /// <c>Dictionary&lt;K,V&gt;</c> subclass behind a custom wrapper, …) is restored.</item>
        /// </list>
        /// A shape that satisfies none of those is rejected with an actionable schema error rather than
        /// a raw reflection exception.
        /// </summary>
        public CollectionFill CreateCollection(Type collectionType, Type backingDefinition, Type[] backingArgs,
            object existingObject, string clearMethodName, string kind)
        {
            if (existingObject != null && IsExactType(existingObject, collectionType))
            {
                var clear = collectionType.GetMethod(clearMethodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (clear != null)
                {
                    clear.Invoke(existingObject, null);
                    return new CollectionFill(existingObject, existingObject);
                }
            }

            // The shape's own parameterless constructor: exact, and the only way a subclass keeps its type.
            var defaultCtor = collectionType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (defaultCtor != null)
            {
                try
                {
                    var direct = defaultCtor.Invoke(null);
                    if (direct != null)
                        return new CollectionFill(direct, direct);
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    throw new Exceptions.CerasSchemaException(
                        $"The {kind} collection type '{TypeIdentity.GetCanonicalName(collectionType)}' threw from its parameterless constructor " +
                        $"while a save was being migrated: {e.InnerException.Message}", e.InnerException);
                }
            }

            // No parameterless constructor: build the built-in collection this shape describes and wrap it.
            Type backingType = null;
            if (backingDefinition != null && backingArgs != null && backingArgs.Length == backingDefinition.GetGenericArguments().Length
                && Array.TrueForAll(backingArgs, a => a != null))
            {
                backingType = backingDefinition.MakeGenericType(backingArgs);
            }

            object backing = null;
            if (backingType != null && backingType.GetConstructor(Type.EmptyTypes) != null)
                backing = Activator.CreateInstance(backingType);

            if (backing != null)
            {
                var wrappingCtor = FindWrappingConstructor(collectionType, backingType);
                if (wrappingCtor != null)
                {
                    try
                    {
                        return new CollectionFill(wrappingCtor.Invoke(new[] { backing }), backing);
                    }
                    catch (TargetInvocationException e) when (e.InnerException != null)
                    {
                        throw new Exceptions.CerasSchemaException(
                            $"The {kind} collection type '{TypeIdentity.GetCanonicalName(collectionType)}' threw from its wrapping constructor " +
                            $"while a save was being migrated: {e.InnerException.Message}", e.InnerException);
                    }
                }
            }

            throw new Exceptions.CerasSchemaException(
                $"Ceras cannot construct the {kind} collection type '{TypeIdentity.GetCanonicalName(collectionType)}' while migrating a save: it has " +
                $"no public parameterless constructor and no constructor that accepts a {backingType?.Name ?? "matching built-in collection"}, " +
                $"and it exposes no usable '{clearMethodName}' to refill. Give the type a public parameterless constructor (or a wrapping " +
                "constructor Ceras can use), declare a custom formatter for it, or load the save with the build that wrote it.");
        }

        /// <summary>
        /// Finds the public single-argument constructor through which <paramref name="collectionType"/>
        /// can be built from the built-in collection <paramref name="backingType"/> (as
        /// <c>ReadOnlyCollection&lt;T&gt;</c> wraps an <c>IList&lt;T&gt;</c>, and
        /// <c>ReadOnlyDictionary&lt;K,V&gt;</c> an <c>IDictionary&lt;K,V&gt;</c>).
        /// </summary>
        static ConstructorInfo FindWrappingConstructor(Type collectionType, Type backingType)
        {
            var sources = new List<Type> { backingType };
            var args = backingType.GetGenericArguments();
            if (args.Length == 1)
            {
                sources.Add(typeof(IEnumerable<>).MakeGenericType(args));
                sources.Add(typeof(ICollection<>).MakeGenericType(args));
                sources.Add(typeof(IList<>).MakeGenericType(args));
                sources.Add(typeof(IReadOnlyList<>).MakeGenericType(args));
                sources.Add(typeof(IReadOnlyCollection<>).MakeGenericType(args));
            }
            else if (args.Length == 2)
            {
                sources.Add(typeof(IDictionary<,>).MakeGenericType(args));
                sources.Add(typeof(IReadOnlyDictionary<,>).MakeGenericType(args));
                sources.Add(typeof(IEnumerable<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(args)));
            }

            foreach (var ctor in collectionType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length != 1) continue;
                foreach (var source in sources)
                {
                    if (parameters[0].ParameterType.IsAssignableFrom(source))
                        return ctor;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves the member that adds one element to a collection shape: the non-generic
        /// <see cref="System.Collections.IList.Add(object)"/> when the fill target exposes it, otherwise
        /// the shape's own <c>Add(T)</c>/<c>Enqueue(T)</c>/<c>Push(T)</c> (which is also how
        /// <c>CollectionFormatter</c> writes the shape), otherwise <c>Add(K,V)</c>.
        /// </summary>
        public static Action<object, object> ResolveElementAdder(object fillTarget, Type elementType)
        {
            if (elementType != null && fillTarget is System.Collections.IList list)
                return (target, value) => list.Add(value);

            foreach (var name in new[] { "Add", "Enqueue", "Push" })
            {
                var add = fillTarget.GetType().GetMethod(name, new[] { elementType });
                if (add != null)
                    return (target, value) => add.Invoke(target, new[] { value });
            }
            return null;
        }

        public static Action<object, object, object> ResolveEntryAdder(object fillTarget, Type keyType, Type valueType)
        {
            if (keyType != null && valueType != null && fillTarget is System.Collections.IDictionary dictionary)
                return (target, key, value) => dictionary.Add(key, value);

            var add = fillTarget.GetType().GetMethod("Add", new[] { keyType, valueType });
            if (add != null)
                return (target, key, value) => add.Invoke(target, new[] { key, value });

            return null;
        }
    }
}
