namespace Ceras.Resolvers
{
    using Formatters;
    using Helpers;
    using System;

        /// <summary>
        /// This resolver creates instances of <see cref="ReflectionFallbackFormatter{T}"/>, which can handle pretty much every complex object (assuming it has a correct TypeConfig to work with). It is always used last because it *always* returns a result.
        /// </summary>
        [Obsolete("ReflectionFallbackFormatter is based on reflection and is being phased out in favor of Source Generators. Consider adding [GenerateFormatter] to your types or providing a custom formatter. You can disable this fallback via SerializerConfig.Advanced.AllowReflectionFallback.")]
        public class ReflectionFallbackResolver : IFormatterResolver
        {
            CerasSerializer _ceras;
    
            public ReflectionFallbackResolver(CerasSerializer ceras)
            {
                _ceras = ceras;
            }
    
            public IFormatter GetFormatter(Type type)
            {
                if (!_ceras.Config.Advanced.AllowReflectionFallback)
                    throw new InvalidOperationException($"No specialized or generated formatter found for type '{type.FriendlyName(true)}', and reflection-based fallback is disabled in SerializerConfig.Advanced.AllowReflectionFallback. Please add [GenerateFormatter] to the type or register a custom formatter.");

                var meta = _ceras.GetTypeMetaData(type);
    
                if (meta.IsPrimitive)
                    throw new InvalidOperationException("ReflectionFallbackFormatter is not allowed to serialize serialization-primitives.");
    
                var formatterType = typeof(ReflectionFallbackFormatter<>).MakeGenericType(type);
                return (IFormatter)Activator.CreateInstance(formatterType, new object[] { _ceras, false });
            }
        }
    }
    