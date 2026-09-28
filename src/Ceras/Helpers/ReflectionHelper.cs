namespace Ceras.Helpers
{
    using Ceras.Exceptions;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    public static class ReflectionHelper
    {
        static readonly Dictionary<Type, int> _typeToBlittableSize = new Dictionary<Type, int>();
        static readonly Dictionary<Type, int> _typeToUnsafeSize = new Dictionary<Type, int>();

#pragma warning disable CS8632
        public static object? Invoke(this MethodBase method, object? target, object[]? arguments = null)
#pragma warning restore CS8632
        {
            try
            {
                if (method is ConstructorInfo ctor && target == null)
                    return ctor.Invoke(arguments);

                return method.Invoke(target, arguments);
            }
            catch (TargetInvocationException tie)
            {
                var e = tie.InnerException;
                while (e is TargetInvocationException)
                    e = e.InnerException;
                throw e ?? tie;
            }
        }

        public static Type? FindClosedType(Type type, Type openGeneric)
        {
            if (openGeneric.IsInterface)
            {
                var collectionInterfaces = type.FindInterfaces((f, o) =>
                {
                    if (!f.IsGenericType)
                        return false;
                    return f.GetGenericTypeDefinition() == openGeneric;
                }, null);

                // In the case of interfaces, it can not only be the case where an interface inherits another interface, 
                // but there can also be the case where the interface itself is already the type we are looking for!
                if (type.IsGenericType)
                    if (type.GetGenericTypeDefinition() == openGeneric)
                        return type;

                if (collectionInterfaces.Length > 0)
                {
                    return collectionInterfaces[0];
                }
            }
            else
            {
                // Go up through the hierarchy until we find that open generic
                var t = type;
                while (t != null)
                {
                    if (t.IsGenericType)
                        if (t.GetGenericTypeDefinition() == openGeneric)
                            return t;

                    t = t.BaseType;
                }
            }


            return null;
        }

        public static Type? FindClosedArg(this Type objectType, Type openGeneric, int argIndex = 0)
        {
            var closed = FindClosedType(objectType, openGeneric);
            if (closed == null)
                return null;
            var args = closed.GetGenericArguments();
            return args[argIndex];
        }

        public static bool IsAssignableToGenericType(Type givenType, Type genericType)
        {
            if (genericType.IsAssignableFrom(givenType))
                return true;

            var interfaceTypes = givenType.GetInterfaces();

            foreach (var it in interfaceTypes)
            {
                // Ok, we lied, we also allow it if 'genericType' is an interface and 'givenType' implements it
                if (it == genericType)
                    return true;

                if (it.IsGenericType && it.GetGenericTypeDefinition() == genericType)
                    return true;
            }

            if (givenType.IsGenericType && givenType.GetGenericTypeDefinition() == genericType)
                return true;

            Type? baseType = givenType.BaseType;
            if (baseType == null)
                return false;

            return IsAssignableToGenericType(baseType, genericType);
        }


        public static IEnumerable<MemberInfo> GetAllDataMembers(this Type type, bool fields = true, bool properties = false)
        {
            if (type.IsPrimitive)
                yield break;

            foreach (var m in EnumerateMembers(type))
            {
                if (m is FieldInfo f && fields && !f.IsStatic)
                    yield return m;
                if (m is PropertyInfo p && properties && !p.GetAccessors(true)[0].IsStatic)
                    yield return m;
            }
        }

        public static IEnumerable<MemberInfo> GetAllStaticDataMembers(this Type type, bool fields = true, bool properties = false)
        {
            if (type.IsPrimitive)
                yield break;

            foreach (var m in EnumerateMembers(type))
            {
                if (m is FieldInfo f && fields && f.IsStatic)
                    yield return m;
                if (m is PropertyInfo p && properties && p.GetAccessors(true)[0].IsStatic)
                    yield return m;
            }
        }


        static IEnumerable<MemberInfo> EnumerateMembers(this Type type)
        {
            var names = new HashSet<string>();

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

            // The walk ends at the root, which has no base type; that null is the loop's end state, so
            // the cursor is the nullable one rather than the argument it started from.
            Type? current = type;
            while (current != null)
            {
                foreach (var f in current.GetFields(flags))
                    if (f.DeclaringType == current)
                        yield return f;

                foreach (var p in current.GetProperties(flags))
                {
                    if (p.DeclaringType != current)
                        System.Diagnostics.Debug.Assert(false, "reflection returned a property with a different declaring type"); // ... even though DeclaredOnly was specified!

                    var getter = p.GetGetMethod(true);
                    if (getter == null)
                        continue; // when can that happen??

                    if (getter.GetParameters().Length > 0)
                        continue; // Indexers are not data

                    if (names.Contains(p.Name))
                        continue; // property was already defined in the derived class we inspected before

                    names.Add(p.Name);

                    yield return p;
                }

                current = current.BaseType;
            }
        }


        // Just for testing
        internal static bool ComputeExpectedSize(Type type, out int size)
        {
            size = -1;

            var debugName = type.FullName;

            if (!type.IsValueType)
                return false; // Only value types can be of fixed size

            if (type.ContainsGenericParameters)
                return false;

            if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
                return false; // Pointers can be different sizes on different platforms

            if (type.IsPrimitive)
            {
                size = Marshal.SizeOf(type);
                return true;
            }

            // fixed buffer struct members
            if (type.DeclaringType != null && type.DeclaringType.IsValueType)
            {
                var fields = type.DeclaringType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fieldInParent = fields.Single(f => f.FieldType == type);
                var parentFieldFixedBuffer = fieldInParent.GetCustomAttribute<FixedBufferAttribute>();
                if (parentFieldFixedBuffer != null)
                {
                    if (!ComputeExpectedSize(parentFieldFixedBuffer.ElementType, out var fixedBufferElementSize))
                        throw new Exception();

                    size = parentFieldFixedBuffer.Length * fixedBufferElementSize;
                    return true;
                }
            }


            if (type.IsAutoLayout)
                return false; // Automatic layout means the type might be larger

            var layout = type.StructLayoutAttribute;
            if (layout == null)
                throw new Exception($"Type '{type.FriendlyName(true)}' is a value-type but does not have a StructLayoutAttribute!");


            size = 0;

            foreach (var f in GetAllDataMembers(type, true, false).Cast<FieldInfo>())
            {
                var fixedBuffer = f.GetCustomAttribute<FixedBufferAttribute>();
                if (fixedBuffer != null)
                {
                    if (!ComputeExpectedSize(fixedBuffer.ElementType, out var fixedElementSize))
                        throw new InvalidOperationException();

                    var innerTypeSize = fixedBuffer.Length * fixedElementSize;
                    size += innerTypeSize;
                    continue;
                }

                if (!ComputeExpectedSize(f.FieldType, out var fieldSize))
                {
                    size = -1;
                    return false;
                }

                size += fieldSize;
            }

            if (layout.Size != 0)
                if (layout.Size != size)
                    throw new Exception($"Computed size of '{type.FriendlyName(true)}' did not match the StructLayout value");

            var marshalSize = Marshal.SizeOf(type);
            if (size != marshalSize)
                throw new Exception($"Computed size of '{type.FriendlyName(true)}' does not match marshal size");

            return true;
        }


        /// <summary>
        /// May the raw (transient) path write a value of <paramref name="type"/> by copying its
        /// memory image — an <c>Unsafe.WriteUnaligned</c> of <c>Unsafe.SizeOf</c> bytes — rather than
        /// member by member?
        /// <para>
        /// This is not only a question about the CLR. The same value has to come out the same bytes
        /// whether or not the type happens to have a generated formatter, and the member-by-member
        /// encoding is what both the generated formatter and the reflection fallback write, in
        /// <see cref="SchemaMemberComparer"/> order. So two things have to hold:
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// Ceras must not already encode this type some other way. A type whose bytes are decided by a
        /// built-in formatter — <see cref="decimal"/>, whose payload is the <c>GetBits</c> order —
        /// must never be reinterpreted, or the same value would be written two ways. That is what
        /// the exclusions below are for; the <see cref="Guid"/> exemption is the same question asked
        /// the other way round, since its encoding *is* a copy of its memory image. (An enum is the
        /// same case, and needs no exemption: in a raw context it is served by
        /// <c>ReinterpretFormatter</c> too, i.e. by its own bytes.)
        /// </description></item>
        /// <item><description>
        /// The memory image must *be* the member-by-member encoding: the same members, in the same
        /// order, at the same widths, with no padding. A composite struct whose CLR field order
        /// differs from the member order, or which carries padding, would otherwise serialize to
        /// different bytes depending on whether it got a generated formatter — silently, because
        /// both encodings are self-consistent. <see cref="MemberLayoutMatchesMemoryImage"/> is that
        /// test; it recurses through the field types, because the check above recurses too.
        /// </description></item>
        /// </list>
        /// <para>
        /// The answer depends only on the type's shape, never on a field's contents, so a caller may
        /// treat it as a decision about the type.
        /// </para>
        /// </summary>
        public static bool IsBlittableType(Type type)
        {
            if (!type.IsValueType)
                return false;

            if (type.IsEnum)
                return true; // enums are auto-layout, but they're always blittable

            if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
                return false; // pointers are primitive, but we can never blit them (because they can be a different size)

            if (type == typeof(decimal))
            {
                // decimal.IsPrimitive is true, but the CLR field order (_flags, _hi, _lo, _mid) is not the
                // order the format documents (decimal.GetBits order: lo, mid, hi, flags). Blitting it would
                // make a decimal[] element, a KeyValuePair<decimal,_> and any struct containing a decimal
                // use a different encoding than a standalone decimal — and an endian-dependent one.
                return false;
            }

            if (type.IsPrimitive)
                return true; // any other primitive can definitely be blitted

            if (type == typeof(Guid))
            {
                // The mirror image of the decimal exclusion, and it is here for the same reason: the
                // only question is whether blitting writes something other than what the format
                // documents. For a Guid it does not. SerializerBinary.WriteGuid is an unaligned copy of
                // the Guid's 16 bytes, and a member of type Guid is written by exactly that, so the image
                // and the member encoding are the same 16 bytes whatever order the members sort in.
                // That has to be said rather than left to the check below, because Guid's 11 private
                // fields (_a, _b, _c, _d.._k) do NOT sort into layout order — they sort _d.._k, _b, _c, _a,
                // since the key starts with the member's type name. Without this, Guid and every struct
                // containing one would lose the fast path for no gain: the bytes are the same either way.
                return true;
            }

            if (type.ContainsGenericParameters)
                return false; // open types are not real (yet), so they can't be blitted

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                return false; // we want to have explicit control over nullables

            if (type.IsAutoLayout)
                return false; // generic structs are always auto-layout (since you can't know what layout is best before inserting the generic args)


            foreach (var f in GetAllDataMembers(type, true, false).Cast<FieldInfo>())
            {
                // A `fixed` buffer is refused here rather than skipped (it used to `continue`, and to
                // carry an exemption in MemberLayoutMatchesMemoryImage on the claim that it "has no
                // member encoding to disagree with"). There is one: the reflection fallback's, and it
                // is shorter than the image by the buffer's declared length. For
                // `unsafe struct Fb { public fixed byte Buffer[25]; public int Value; }` that is 5 bytes
                // written against a 32-byte image, of which 24 come back as zeros with no error anywhere
                // — see Formatters/FixedBufferGuard. Declining the blit therefore does not create a
                // disagreement here; it routes the value to the path that refuses it, so a shape Ceras
                // cannot describe is never the one that happens to round-trip. The claim is also why the
                // `continue` was wrong on its own terms: skipping the recursion left the buffer's
                // generated one-element struct unchecked rather than proving it blittable.
                if (f.GetCustomAttribute<FixedBufferAttribute>() != null)
                    return false;

                if (f.GetCustomAttribute<MarshalAsAttribute>() != null)
                {
                    throw new NotSupportedException("The [MarshalAs] attribute is not supported");
                }

                if (!IsBlittableType(f.FieldType))
                    return false;
            }

            return MemberLayoutMatchesMemoryImage(type);
        }

        /// <summary>
        /// Is the memory image of <paramref name="type"/> byte-for-byte the member-by-member encoding
        /// of the same value? That needs three things, and each is a way the two can differ:
        /// <list type="number">
        /// <item><description>
        /// the same members. A field the member list leaves out — one carrying an attribute that
        /// removes it from the payload — has bytes in the image and nothing in the encoding.
        /// </description></item>
        /// <item><description>
        /// the same order. The CLR lays fields out in declaration order, Ceras writes them in
        /// <see cref="SchemaMemberComparer"/> order, and a struct whose two orders differ
        /// (<c>struct S { int One; int Two; int Three; }</c> sorts to One, Three, Two) is written one
        /// way by the blit and another way by any member-by-member formatter. The comparison is done
        /// with the runtime's own comparer rather than a restatement of its key, so the two cannot
        /// drift apart.
        /// </description></item>
        /// <item><description>
        /// the same width, with no padding. The blit writes <c>SizeOf</c> bytes, the encoding writes
        /// the sum of the members' widths, and a padded struct (<c>struct { byte B; int I; }</c> is 8
        /// bytes for 5 bytes of data) differs in width as well as in order. This also rules out a
        /// struct with no members at all, whose image is one padding byte.
        /// </description></item>
        /// </list>
        /// <para>
        /// A struct declaring a <c>fixed</c> buffer never reaches this method:
        /// <see cref="IsBlittableType"/> refuses that shape outright, one level up, because the question
        /// this method answers does not have an answer for it. A fixed buffer is the one member shape
        /// Ceras cannot describe member-by-member at all — the compiler hands reflection a generated
        /// one-element struct and keeps the declared length in a field attribute, so no encoding derived
        /// from the member's type can see how many elements there are. So rather than compare an image
        /// against an encoding that does not exist, the caller declines the blit and the value is
        /// refused by the path it is then routed to (<c>Formatters/FixedBufferGuard</c>).
        /// </para>
        /// </summary>
        static bool MemberLayoutMatchesMemoryImage(Type type)
        {
            // Declaration order, which is also the order the CLR lays the fields out in (the type is
            // known not to be auto-layout, so the runtime does not get to reorder them).
            var fields = GetAllDataMembers(type, fields: true, properties: false).Cast<FieldInfo>().ToArray();

            if (type.GetCustomAttribute<Ceras.ExcludeAttribute>() != null)
                return false; // every member of an excluded type is left out of the payload

            foreach (var f in fields)
                if (!IsWrittenAsMember(f))
                    return false;

            // The same comparer CerasSerializer.CreatePrimarySchema sorts the real member list with.
            var written = new List<SchemaMember>(fields.Length);
            foreach (var f in fields)
                written.Add(new SchemaMember(f.Name, f));
            written.Sort(SchemaMemberComparer.Instance);

            for (int i = 0; i < fields.Length; i++)
                if (!ReferenceEquals(written[i].Member, fields[i]))
                    return false; // the member order is not the layout order

            int payload = 0;
            foreach (var f in fields)
                payload += UnsafeGetSize(f.FieldType);

            return payload == UnsafeGetSize(type);
        }

        /// <summary>
        /// Does Ceras write <paramref name="f"/> as a member of its declaring type? Mirrors the rules
        /// in <see cref="MemberConfig"/> / <c>TypeConfigDefaults.ApplyMemberAttributes</c> that decide
        /// which fields a payload contains, without needing a config: the ones that remove a field.
        /// <para>
        /// A compiler-generated field is kept when it is an auto-property's backing field, because a
        /// property is never a member and the field is what stands in for it — and then the property's
        /// own attributes apply to the field, exactly as they do there.
        /// </para>
        /// </summary>
        static bool IsWrittenAsMember(FieldInfo f)
        {
            if (f.GetCustomAttribute<Ceras.ExcludeAttribute>() != null)
                return false; // [Exclude] on the field itself

            if (f.GetCustomAttribute<NonSerializedAttribute>() != null)
                return false; // left out while RespectNonSerializedAttribute is on, which is the default

            if (f.FieldType.GetCustomAttribute<Ceras.ExcludeAttribute>() != null)
                return false; // a member whose type is excluded is left out, whatever the field itself says

            if (f.GetCustomAttribute<CompilerGeneratedAttribute>() == null)
                return true;

            var property = AutoPropertyOf(f);
            if (property == null)
                return false; // an event's backing field, a closure cache, ...: never a member

            return property.GetCustomAttribute<Ceras.ExcludeAttribute>() == null;
        }

        /// <summary>
        /// The auto-property <paramref name="f"/> is the backing field of, or null when it is not one.
        /// </summary>
        static PropertyInfo? AutoPropertyOf(FieldInfo f)
        {
            const string Suffix = ">k__BackingField";
            if (!f.Name.StartsWith("<", StringComparison.Ordinal) || !f.Name.EndsWith(Suffix, StringComparison.Ordinal))
                return null;

            var propertyName = f.Name.Substring(1, f.Name.Length - 1 - Suffix.Length);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            return f.DeclaringType?.GetProperty(propertyName, flags);
        }

        public static int GetSize(Type type)
        {
            if (!IsBlittableType(type))
                return -1;

            return UnsafeGetSize(type);
        }


        public static int UnsafeGetSize(Type type)
        {
            lock (_typeToUnsafeSize)
            {
                if (_typeToUnsafeSize.TryGetValue(type, out int size))
                    return size;

                // A lookup by name over the BCL, then a constructed generic over a primitive: neither misses.
                var m = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type);
                size = (int)m.Invoke(null, null)!;

                _typeToUnsafeSize.Add(type, size);
                return size;
            }
        }

        public static Type? FieldOrPropType(this MemberInfo memberInfo)
        {
            if (memberInfo is FieldInfo f)
                return f.FieldType;
            if (memberInfo is PropertyInfo p)
                return p.PropertyType;
            throw new InvalidOperationException();
        }

        public static MethodInfo ResolveSerializeMethod(this Type formatterType, Type typeToFormat)
        {
            return ResolveMethod(formatterType, "Serialize", new[] { typeof(System.Buffers.IBufferWriter<byte>), typeToFormat });
        }

        public static MethodInfo ResolveDeserializeMethod(this Type formatterType, Type typeToFormat)
        {
            return ResolveMethod(formatterType, "Deserialize", new[] { typeof(System.Buffers.SequenceReader<byte>).MakeByRefType(), typeToFormat.MakeByRefType() });
        }


        /// <summary>
        /// Find the MethodInfo that matches the given name and specific parameters on the given type. 
        /// All parameter types must be "closed" (not contain any unspecified generic parameters). 
        /// <para>
        /// A match is required, so the method never answers null: the caller is a dispatcher that has
        /// nothing to call without it. A miss therefore throws
        /// <see cref="Ceras.Exceptions.CerasException"/> naming the type it searched, the method it
        /// wanted and the parameter types it asked for. (Answering null left the report to
        /// <see cref="Delegate.CreateDelegate(Type, object, MethodInfo)"/>, which named neither the
        /// formatter nor the method.) More than one match still throws
        /// <see cref="AmbiguousMatchException"/>, as it always has.
        /// </para>
        /// </summary>
        /// <param name="declaringType">The type in which to look for the method</param>
        /// <param name="name">The name of the method (method group) to find</param>
        /// <param name="parameters">The specific parameters</param>
        /// <exception cref="Ceras.Exceptions.CerasException">
        /// The type declares no method with that name taking those parameter types.
        /// </exception>
        public static MethodInfo ResolveMethod(Type declaringType, string name, Type[] parameters)
        {
            var methods = declaringType
                          .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                          .Where(m => m.Name == name &&
                                      m.GetParameters().Length == parameters.Length)
                          .ToArray();

            var match = SelectMethod(methods, parameters);
            if (match == null)
            {
                var searched = Ceras.Format.TypeIdentity.GetCanonicalName(declaringType);
                var wanted = string.Join(", ", parameters.Select(p => Ceras.Format.TypeIdentity.GetCanonicalName(p)));
                throw new CerasException(
                    $"Ceras cannot dispatch to '{searched}': it declares no '{name}({wanted})'. " +
                    $"A formatter has to declare that method under that name, with those parameter types, for the type it " +
                    $"is registered for. A method that implements the interface explicitly does not count: reflection names " +
                    $"such a method after the interface rather than '{name}', so it is never found here. Declare " +
                    $"'{name}' directly, or derive from FormatterBase<T> for the type being formatted.");
            }

            return match;
        }

        // Select exactly one method out of the given methods that matches the specific (closed) arguments
        static MethodInfo? SelectMethod(MethodInfo[] methods, Type[] specificArguments)
        {
            var matches = new List<MethodInfo>();

            foreach (var m in methods)
            {
                if (m.IsGenericMethod)
                {
                    // Inspect generic arguments and parameters in depth
                    var closedMethod = TryCloseOpenGeneric(m, specificArguments);
                    if (closedMethod != null)
                        matches.Add(closedMethod);
                }
                else
                {
                    // Just check if the parameters match
                    var parameters = m.GetParameters();

                    bool allMatch = true;
                    for (int argIndex = 0; argIndex < specificArguments.Length; argIndex++)
                    {
                        var pType = parameters[argIndex].ParameterType;
                        var argType = specificArguments[argIndex];

                        if (pType != argType)
                        // if (!pType.IsAssignableFrom(argType))
                        {
                            allMatch = false;
                            break;
                        }
                    }

                    if (allMatch)
                        matches.Add(m);
                }
            }

            if (matches.Count == 0)
                return null;

            if (matches.Count > 1)
            {
                // If we have multiple matches, we might be able to resolve it by preferring non-generic methods
                var nonGenericMatches = matches.Where(m => !m.IsGenericMethod).ToArray();
                if (nonGenericMatches.Length == 1)
                    return nonGenericMatches[0];

                throw new AmbiguousMatchException("The given parameters can match more than one method overload.");
            }

            return matches[0];
        }

        // Try to close an open generic method, making it take the given argument types
        public static MethodInfo? TryCloseOpenGeneric(MethodInfo openGenericMethod, Type[] specificArguments)
        {
            if (!openGenericMethod.IsGenericMethodDefinition)
                throw new ArgumentException($"'{nameof(openGenericMethod)}' must be a generic method definition");

            foreach (var t in specificArguments)
                if (t.ContainsGenericParameters)
                    throw new InvalidOperationException($"Can't close open generic method '{openGenericMethod}' At least one of the given argument types is not fully closed: '{t.FullName}'");

            var parameters = openGenericMethod.GetParameters();

            // Go through the parameters recursively, once we find a generic parameter (or some nested one) then we can infer the type from the given specific arguments.
            // If that is the first time we encounter this generic parameter, then that establishes what specific type it actually is.
            // If we have already seen this generic parameter, we check if it matches. If not, we can immediately conclude that the method won't match.
            // When we reach the end, we know that the method is a match.

            // - While establishing the generic arguments we can check the generic constraints
            // - We can also check if parameters could still fit even if they're not an exact match (an more derived argument going into a base-type parameter)

            var genArgToConcreteType = new Dictionary<Type, Type>();

            var genArgs = openGenericMethod.GetGenericArguments();

            for (int paramIndex = 0; paramIndex < parameters.Length; paramIndex++)
            {
                var p = parameters[paramIndex];
                var arg = specificArguments[paramIndex];

                // For now we only accept exact matches. We won't make any assumptions about being able to pass in more derived types.
                if (IsParameterMatch(p.ParameterType, arg, genArgToConcreteType, genArgs, true))
                {
                    // Ok, at least this parameter matches, try the others...
                }
                else
                {
                    return null;
                }
            }

            // All the parameters seem to match, let's do a final check to see if everything really matches up
            // Lets extract the genericTypeParameters from the dictionary
            var closedGenericArgs = genArgs.OrderBy(g => g.GenericParameterPosition).Select(g => genArgToConcreteType[g]).ToArray();

            // Try to instantiate the method using those args
            try
            {
                var instantiatedGenericMethod = openGenericMethod.MakeGenericMethod(closedGenericArgs);

                // Ok, do the parameter types match perfectly?
                var createdParams = instantiatedGenericMethod.GetParameters();

                for (int i = 0; i < createdParams.Length; i++)
                    if (createdParams[i].ParameterType != specificArguments[i])
                        // Somehow, after instantiating the method with these parameters, something doesn't match up...
                        return null;


                return instantiatedGenericMethod;
            }
            catch (Exception)
            {
                // Can't instantiate generic with those type-arguments
                return null;
            }
        }

        // Check if the given argument type 'arg' matches the parameter 'parameterType'.
        // Use 'genArgToConcreteType' to either lookup any already specified generic arguments, or infer and define them!
        // 'methodGenericArgs' just contains the "undefined" generic argument types to be used as the key in the dictionary.
        static bool IsParameterMatch(Type parameterType, Type arg, Dictionary<Type, Type> genArgToConcreteType, Type[] methodGenericArgs, bool mustMatchExactly)
        {
            // Direct match?
            //  "typeof(int) == typeof(int)"
            if (mustMatchExactly)
            {
                if (parameterType == arg)
                    return true;
            }
            else
            {
                if (parameterType.IsAssignableFrom(arg))
                    return true;
            }

            // Is it a generic parameter '<T>'?
            var genericArg = methodGenericArgs.FirstOrDefault(g => g == parameterType);
            if (genericArg != null)
            {
                // The parameter is a '<T>'
                if (genArgToConcreteType.TryGetValue(genericArg, out var existingGenericTypeArgument))
                {
                    if (existingGenericTypeArgument != arg)
                    {
                        // Signature does not match
                        return false;
                    }
                    else
                    {
                        // The arg matches the already established one
                    }
                }
                else
                {
                    // Establish that this generic type argument will be 'arg'
                    genArgToConcreteType.Add(genericArg, arg);
                }

                return true;
            }

            // If both of them are generics, we have to open them up
            if (parameterType.IsGenericType && arg.IsGenericType)
            {
                var paramGenericTypeDef = parameterType.GetGenericTypeDefinition();
                var argGenericTypeDef = arg.GetGenericTypeDefinition();

                if (paramGenericTypeDef != argGenericTypeDef)
                    // "Containers" are not compatible
                    return false;

                // Open up the containers and inspect each generic argument
                var specificGenArgs = arg.GetGenericArguments();
                var genArgs = parameterType.GetGenericArguments();

                for (int i = 0; i < genArgs.Length; i++)
                {
                    if (!IsParameterMatch(genArgs[i], specificGenArgs[i], genArgToConcreteType, methodGenericArgs, mustMatchExactly))
                        return false;
                }

                return true;
            }


            // Types don't match, and are not compatible generics
            return false;
        }



        internal static MethodInfo GetMethod(Expression<Action> e)
        {
            var b = e.Body;

            if (b is MethodCallExpression m)
                return m.Method;

            throw new ArgumentException();
        }

        internal static MethodInfo GetMethod<T>(Expression<Func<T>> e)
        {
            var b = e.Body;

            if (b is MethodCallExpression m)
                return m.Method;

            throw new ArgumentException();
        }

        internal static bool IsStatic(this Type type)
        {
            return type.IsAbstract && type.IsSealed;
        }

        internal static bool IsAbstract(this Type type)
        {
            return type.IsAbstract && !type.IsSealed;
        }

        public static string? FriendlyName(this Type type, bool fullName = false)
        {
            if (type == typeof(bool))
                return "bool";

            if (type == typeof(byte))
                return "byte";
            if (type == typeof(sbyte))
                return "sbyte";

            if (type == typeof(short))
                return "short";
            if (type == typeof(ushort))
                return "ushort";

            if (type == typeof(int))
                return "int";
            if (type == typeof(uint))
                return "uint";

            if (type == typeof(long))
                return "long";
            if (type == typeof(ulong))
                return "ulong";

            if (type == typeof(float))
                return "float";
            if (type == typeof(double))
                return "double";

            if (type == typeof(decimal))
                return "decimal";

            if (type == typeof(string))
                return "string";
            if (type == typeof(char))
                return "char";

            if (type.IsGenericType)
            {
                var name = fullName ? type.FullName : type.Name;
                var baseName = name!.Split('`')[0].Replace("+", ".");

                return baseName + "<" + string.Join(", ", type.GetGenericArguments().Select(t => t.FriendlyName(fullName)).ToArray()) + ">";
            }
            else
            {
                return fullName ? type.FullName : type.Name;
            }
        }
    }
}