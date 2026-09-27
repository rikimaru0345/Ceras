namespace Ceras.Resolvers
{
    using Formatters;
    using Helpers;
    using System;

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
    