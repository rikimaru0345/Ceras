using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Resolvers
{
    using Formatters;
    using Helpers;

    /// <summary>
    /// Creates super-fast formatters for so called "blittable" types. Only returns results when <see cref="IAdvancedConfig.UseReinterpretFormatter"/> is true.
    /// </summary>
    public sealed class ReinterpretFormatterResolver : IFormatterResolver
    {
        readonly CerasSerializer _ceras;

        internal ReinterpretFormatterResolver(CerasSerializer ceras)
        {
            _ceras = ceras;
        }

        public IFormatter GetFormatter(Type type)
        {
            if (!(_ceras.Config is CerasRawConfig rawCfg) || !rawCfg.UseReinterpretFormatter)
                return null;

            if (!ReflectionHelper.IsBlittableType(type))
                return null;

            var formatterType = typeof(ReinterpretFormatter<>).MakeGenericType(type);

            return (IFormatter) Ceras.Helpers.Instantiator.Create(formatterType);
        }
    }
}
