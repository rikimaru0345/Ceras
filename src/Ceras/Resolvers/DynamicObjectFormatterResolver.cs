namespace Ceras.Resolvers
{
    using Formatters;
    using Helpers;
    using System;

        /// <summary>
        /// This resolver creates instances of <see cref="DynamicFormatter{T}"/>, which can handle pretty much every complex object (assuming it has a correct TypeConfig to work with). It is always used last because it *always* returns a result.
        /// </summary>
        public class DynamicObjectFormatterResolver : IFormatterResolver
        {
            CerasSerializer _ceras;
    
            public DynamicObjectFormatterResolver(CerasSerializer ceras)
            {
                _ceras = ceras;
            }
    
            public IFormatter GetFormatter(Type type)
            {
                var meta = _ceras.GetTypeMetaData(type);
    
                if (meta.IsPrimitive)
                    throw new InvalidOperationException("DynamicFormatter is not allowed to serialize serialization-primitives.");
    
                var formatterType = typeof(DynamicFormatter<>).MakeGenericType(type);
                return (IFormatter)Activator.CreateInstance(formatterType, new object[] { _ceras, false });
            }
        }
    }
    