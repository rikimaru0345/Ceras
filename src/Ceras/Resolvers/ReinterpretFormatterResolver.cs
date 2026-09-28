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
    /// Creates super-fast formatters for so called "blittable" types. Only returns results when <see cref="CerasRawConfig.UseReinterpretFormatter"/> is true.
    /// <para>
    /// A formatter here writes the value's memory image, so which types may be claimed is a question
    /// about agreement rather than about the CLR: the same value must come out the same bytes whether
    /// or not the type has a generated formatter, and the member-by-member encoding is what every other
    /// path writes. <see cref="ReflectionHelper.IsBlittableType"/> is the whole rule, and it only claims
    /// a type whose memory image already is that encoding — the same members, in the same order, at the
    /// same widths, with no padding. A composite struct that fails the test (its CLR field order differs
    /// from the member order, or it carries padding) is declined here and written member by member,
    /// exactly as a robust context would write it, which is what makes a transient buffer's bytes
    /// independent of whether the type has a generated formatter.
    /// </para>
    /// </summary>
    public sealed class ReinterpretFormatterResolver : IFormatterResolver
    {
        readonly CerasSerializer _ceras;

        internal ReinterpretFormatterResolver(CerasSerializer ceras)
        {
            _ceras = ceras;
        }

        public IFormatter? GetFormatter(Type type)
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
