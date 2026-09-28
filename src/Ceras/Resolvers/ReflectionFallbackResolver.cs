namespace Ceras.Resolvers
{
    using Formatters;
    using Helpers;
    using System;
    using System.Diagnostics.CodeAnalysis;

        /// <summary>
        /// This resolver creates instances of <see cref="ReflectionFallbackFormatter{T}"/>, which can handle pretty much every complex object (assuming it has a correct TypeConfig to work with). It is always used last because it *always* returns a result.
        /// </summary>
        [Obsolete("ReflectionFallbackFormatter is based on reflection and is being phased out in favor of Source Generators. Consider adding [CerasSerializable] to your types or providing a custom formatter. You can disable this fallback via CerasConfig.AllowReflectionFallback.")]
        public class ReflectionFallbackResolver : IFormatterResolver
        {
            CerasSerializer _ceras;
    
            internal ReflectionFallbackResolver(CerasSerializer ceras)
            {
                _ceras = ceras;
            }
    
            /// <summary>
            /// This is the only place <see cref="ReflectionFallbackFormatter{T}"/> is closed for an
            /// instance type, and nothing in a compilation names the closed type: the fallback is
            /// picked at runtime for whichever type a payload names, so every closed instantiation
            /// exists solely as a <c>MakeGenericType</c> result. That is also why the requirement
            /// cannot be written on <paramref name="type"/> with
            /// <see cref="DynamicallyAccessedMembersAttribute"/> - it describes the members of a type
            /// the trimmer can see - and a dependency on the open definition is the one form that
            /// covers every closed construction, including types that only ever appear in a payload.
            /// <para>
            /// <c>CerasSerializer.PrepareStaticSerializationDispatchers</c> closes the same
            /// definition for a static formatter and this resolver closes it for an instance one, so
            /// stating it here covers both: a dependency on the definition is not per-call-site.
            /// </para>
            /// <para>
            /// Only the constructors: the formatter is handed the serializer and the static flag, so
            /// that is what <c>Activator</c> binds, and its <c>Serialize</c>/<c>Deserialize</c> are
            /// already rooted as the overrides of the <c>FormatterBase&lt;T&gt;</c> methods that the
            /// non-generic <see cref="IFormatter"/> contract calls.
            /// </para>
            /// </summary>
            [DynamicDependency(
                DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors,
                typeof(ReflectionFallbackFormatter<>))]
            public IFormatter? GetFormatter(Type type)
            {
                _ceras.VerifyReflectionFallbackAllowed(type);

                var meta = _ceras.GetTypeMetaData(type);
    
                if (meta.IsPrimitive)
                    throw new InvalidOperationException("ReflectionFallbackFormatter is not allowed to serialize serialization-primitives.");
    
                var formatterType = typeof(ReflectionFallbackFormatter<>).MakeGenericType(type);
                return (IFormatter)Ceras.Helpers.Instantiator.Create(formatterType, new object[] { _ceras, false });
            }
        }
    }
    