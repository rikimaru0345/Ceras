using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ceras.Format
{
    /// <summary>
    /// The instance handed back to the caller, plus the object whose member actually receives the elements.
    /// They differ for a wrapped shape: a <c>HashSet&lt;T&gt;</c> subclass whose only constructor copies
    /// from an <c>IEnumerable&lt;T&gt;</c> is built from a fresh <c>HashSet&lt;T&gt;</c>, so the elements go
    /// into that backing set while the subclass itself is what the field receives.
    /// </summary>
    readonly struct CollectionFill
    {
        public readonly object? Result;
        public readonly object Target;

        public CollectionFill(object? result, object target)
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
        /// The instance for a *fresh* reference-typed value (there is none to reuse), built the way the
        /// generated reader's reference wrapper builds it: the configured construction when one applies
        /// (<see cref="TryConfiguredConstruction"/>), otherwise the allocation the type's own generated
        /// formatter performs for a value it is handed as null.
        /// </summary>
        public object? CreateReference(Type? declaredType, Type? currentType)
        {
            if (currentType == null) return null;
            return TryConfiguredConstruction(declaredType, currentType) ?? AllocateLikeOwnFormatter(currentType);
        }

        /// <summary>
        /// The instance for a fresh reference type whose members are about to be passed to a parameterized
        /// <c>[CerasConstructor]</c>. The configured construction still supplies the instance when it applies
        /// (the generated reader hands that same instance to the generated formatter, which then invokes the
        /// constructor on it); when none does, the instance is uninitialized, because the generated formatter
        /// allocates that way for a type with a parameterized constructor. Either way the caller registers it
        /// before the constructor runs, so a cycle through a constructor parameter resolves.
        /// </summary>
        public object? CreateWithConstructorArguments(Type? declaredType, Type? currentType)
        {
            if (currentType == null) return null;
            return TryConfiguredConstruction(declaredType, currentType) ?? RuntimeHelpers.GetUninitializedObject(currentType);
        }

        /// <summary>
        /// The configured construction's instance, or null when it does not supply one.
        /// <para>
        /// This mirrors <c>ReferenceFormatter&lt;T&gt;.GetOrCreateEntry</c>: a construction the
        /// <em>application</em> selected always applies — it exists precisely to replace the allocation —
        /// while Ceras' own default applies only when nothing else builds the instance, so it cannot
        /// silently take that job away from generated code.
        /// </para>
        /// </summary>
        object? TryConfiguredConstruction(Type? declaredType, Type currentType)
        {
            var typeConfig = _serializer.Config.GetTypeConfigForClosedType(currentType, false);
            if (!UsesConfiguredConstruction(declaredType, currentType, typeConfig)) return null;

            var constructed = InvokeConfiguredConstruction(currentType, typeConfig);
            return constructed == null ? null : VerifyConstructedInstance(currentType, constructed);
        }

        bool UsesConfiguredConstruction(Type? declaredType, Type currentType, TypeConfig typeConfig)
        {
            if (typeConfig.IsConstructionExplicit) return true;                        // a user selection
            if (!OwnFormatterBuildsInstance(currentType, typeConfig)) return true;    // nothing else builds it

            // The one shape whose wrapper asks unconditionally: a slot declared as its own sealed type is read
            // through ReferenceFormatter_KnownSealedType<T>, which has no such check. "Declared as itself" is
            // what identifies it — a polymorphic slot holding a sealed type goes through ReferenceFormatter<T>.
            // The exclusions mirror the wrapper choice: System.Type is served by its own formatter, and object
            // is sealed as far as IsSealed reports but is a polymorphic slot, never a sealed one.
            return declaredType == currentType
                && declaredType != typeof(object)
                && declaredType.IsSealed
                && !typeof(Type).IsAssignableFrom(declaredType);
        }

        /// <summary>
        /// Whether the type's own formatter builds the instance — the interpreter's mirror of
        /// <c>CerasSerializer.IsFormatterConstructedInstance</c>: an abstract type, one of the
        /// formatter-constructed well-known types, or one with a custom formatter (in a robust context every
        /// generated formatter, because a generated manifest registers it as the type's custom formatter).
        /// </summary>
        static bool OwnFormatterBuildsInstance(Type type, TypeConfig typeConfig)
            => CerasSerializer.IsFormatterConstructed(type) || typeConfig.CustomFormatter != null;

        /// <summary>
        /// Asks the configured construction for the instance, or null when it produces none. A selection that
        /// takes arguments is refused exactly as the generated reader refuses it — the arguments would have
        /// come from the removed fixup system — so both readers report the same unsupported case.
        /// (Parameterized construction is supported through <c>[CerasConstructor]</c>, which is handled before
        /// this is ever reached.)
        /// </summary>
        static object? InvokeConfiguredConstruction(Type type, TypeConfig typeConfig)
        {
            var construction = typeConfig.TypeConstruction;
            if (construction == null) return null;

            var ctor = construction.GetRefFormatterConstructor();
            if (ctor == null) return null;

            try
            {
                return ctor();
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                // The configured construction is application code, so its failure is the load's failure. Name
                // the type and unwrap the reflection layer, as the generated reader's would produce too.
                var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                throw new Exceptions.CerasSchemaException(
                    $"The configured construction ({construction.GetType().Name}) of '{TypeIdentity.GetCanonicalName(type)}' threw " +
                    $"while migrating a save: {cause.GetType().Name}: {cause.Message}", cause);
            }
        }

        /// <summary>
        /// Rejects an instance the configured construction returned that is not the type being read. The
        /// generated reader fails on the same instance (it casts the result to the declared type), so the
        /// interpreter must not store it either. A wrong <em>declared</em> return type never gets this far —
        /// the configuration rejects it when it is set — so this guards the remaining ways a wrong-typed
        /// instance can appear (a factory whose result depends on runtime state, an assignment from an
        /// <c>OnConfigNewType</c> callback).
        /// </summary>
        static object VerifyConstructedInstance(Type type, object instance)
        {
            if (type.IsInstanceOfType(instance)) return instance;

            throw new Exceptions.CerasSchemaException(
                $"The configured construction of '{TypeIdentity.GetCanonicalName(type)}' returned a " +
                $"'{TypeIdentity.GetCanonicalNameOrUnknown(instance.GetType())}', which is not a '{TypeIdentity.GetCanonicalName(type)}'. " +
                "The construction selection has to produce an instance of the type it is configured for " +
                "(config.ConfigType<T>().ConstructBy(...)).");
        }

        /// <summary>
        /// The allocation the type's own generated formatter performs for a value it is handed as null:
        /// uninitialized, then the parameterless constructor when the type has one — a plain call when it
        /// is accessible, reflection when it is not (the generated formatter uses an unsafe accessor).
        /// Field initializers therefore run, and the object is initialized before its members are assigned.
        /// </summary>
        static object AllocateLikeOwnFormatter(Type type)
        {
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
        public object? CreateInstance(Type? type)
        {
            if (type == null) return null;
            if (type.IsValueType) return Activator.CreateInstance(type);

            var typeConfig = _serializer.Config.GetTypeConfigForClosedType(type, false);
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
        /// it, which is how a shape with no public parameterless constructor but a wrapping constructor is
        /// restored (a <c>HashSet&lt;T&gt;</c> subclass that takes <c>IEnumerable&lt;T&gt;</c>, for
        /// example).</item>
        /// </list>
        /// A shape that satisfies none of those is rejected with an actionable schema error rather than
        /// a raw reflection exception.
        /// <para>
        /// <b>Nothing the policy refuses reaches the wrapping fallback.</b> The refused shapes
        /// (<c>ReadOnlyCollection&lt;T&gt;</c> / <c>ReadOnlyDictionary&lt;K,V&gt;</c> and their subclasses,
        /// and every <c>List&lt;T&gt;</c> / <c>Dictionary&lt;TKey,TValue&gt;</c> subclass) used to be built
        /// here by wrapping a fresh <c>List&lt;T&gt;</c>/<c>Dictionary&lt;K,V&gt;</c>, and that is exactly
        /// where the worst of the three failures came from: the wrapper is constructed from the
        /// <em>empty</em> backing collection before a single element is added to it, so a wrapping
        /// constructor that copies — which is what a <c>List&lt;T&gt;</c> subclass with no parameterless
        /// constructor has — ends up holding nothing. The load succeeded and the data was gone. They are
        /// already refused by the shape classifier every caller classifies with, and the check at the top
        /// of this method makes that guarantee local rather than a property of each caller: no code path
        /// here can end up quietly returning an empty collection for a refused shape.
        /// </para>
        /// </summary>
        public CollectionFill CreateCollection(Type? collectionType, Type? backingDefinition, Type[]? backingArgs,
            object? existingObject, string clearMethodName, string kind)
        {
            if (collectionType == null)
                throw new ArgumentNullException(nameof(collectionType));

            // The belt to the callers' braces: a refused shape is named here, in the one method that builds
            // collection instances, instead of depending on every caller having classified it first.
            if (UnsupportedCollectionPolicy.IsUnsupported(collectionType))
                throw UnsupportedCollectionPolicy.Reject(collectionType, "a save being migrated");

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
            Type? backingType = null;
            if (backingDefinition != null && backingArgs != null && backingArgs.Length == backingDefinition.GetGenericArguments().Length
                && Array.TrueForAll(backingArgs, a => a != null))
            {
                backingType = backingDefinition.MakeGenericType(backingArgs);
            }

            object? backing = null;
            if (backingType != null && backingType.GetConstructor(Type.EmptyTypes) != null)
                backing = Activator.CreateInstance(backingType);

            // backing is only ever assigned from a non-null backingType above, and the wrapping
            // constructor is looked up by that same type, so the narrowing is stated, not assumed.
            if (backingType != null && backing != null)
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
                "constructor Ceras can use), or load the save with the build that wrote it.");
        }

        /// <summary>
        /// Finds the public single-argument constructor through which <paramref name="collectionType"/>
        /// can be built from the built-in collection <paramref name="backingType"/> — for instance a
        /// subclass of a built-in collection whose only constructor takes the elements to copy.
        /// </summary>
        static ConstructorInfo? FindWrappingConstructor(Type collectionType, Type backingType)
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
        public static Action<object, object?>? ResolveElementAdder(object fillTarget, Type? elementType)
        {
            if (elementType != null && fillTarget is System.Collections.IList list)
                return (target, value) => list.Add(value);

            foreach (var name in new[] { "Add", "Enqueue", "Push" })
            {
                // A null element type is not a shape this lookup can serve, and it is not treated as one
                // here: it goes into the parameter list unchanged, where reflection rejects it with
                // ArgumentNullException("types") - the same outcome as before this annotation. Only the
                // array's element type is asserted, because that is what reflection's signature demands.
                var add = fillTarget.GetType().GetMethod(name, new[] { elementType! });
                if (add != null)
                    return (target, value) => add.Invoke(target, new[] { value });
            }
            return null;
        }

        public static Action<object, object?, object?>? ResolveEntryAdder(object fillTarget, Type? keyType, Type? valueType)
        {
            // The key is whatever the payload holds - the interpreter passes the coerced value in
            // unchanged - while the non-generic IDictionary declares the parameter non-nullable. The
            // annotation is asserted at the call rather than by narrowing the delegate's own parameter
            // type, which would put the same mismatch on the interpreter side of this API.
            if (keyType != null && valueType != null && fillTarget is System.Collections.IDictionary dictionary)
                return (target, key, value) => dictionary.Add(key!, value);

            // As in ResolveElementAdder: a null key/value type is carried into the parameter list
            // unchanged and rejected by reflection, not resolved to anything.
            var add = fillTarget.GetType().GetMethod("Add", new[] { keyType!, valueType! });
            if (add != null)
                return (target, key, value) => add.Invoke(target, new[] { key, value });

            return null;
        }
    }
}
