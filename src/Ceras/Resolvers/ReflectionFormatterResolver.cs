namespace Ceras.Resolvers
{
    using System;
    using System.Reflection;
    using Formatters;

    /// <summary>
    /// This resolver handles some really special types like <see cref="MemberInfo"/> and <see cref="MulticastDelegate"/>
    /// </summary>
    public sealed class ReflectionFormatterResolver : IFormatterResolver
    {
        readonly CerasSerializer _ceras;

        internal ReflectionFormatterResolver(CerasSerializer ceras)
        {
            _ceras = ceras;
        }

        public IFormatter? GetFormatter(Type type)
        {
            if (typeof(MemberInfo).IsAssignableFrom(type))
            {
                var memberInfoFormatterType = typeof(MemberInfoFormatter<>).MakeGenericType(type);
                var memberInfoFormatter = Ceras.Helpers.Instantiator.Create(memberInfoFormatterType, args: _ceras);
                return (IFormatter)memberInfoFormatter;
            }


            return null;
        }
    }
}