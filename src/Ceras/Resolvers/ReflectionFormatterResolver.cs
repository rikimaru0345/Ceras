namespace Ceras.Resolvers
{
    using System;
    using System.Diagnostics.CodeAnalysis;
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

        /// <summary>
        /// This is the only place <see cref="MemberInfoFormatter{T}"/> is closed, and nothing in a
        /// compilation names the closed type: the only mention of the family anywhere is the
        /// <c>typeof(MemberInfoFormatter&lt;&gt;)</c> on the line below, and a generic
        /// <em>definition</em> names no members of any instantiation. So the closed type exists only
        /// as a <c>MakeGenericType</c> result, which is also why the requirement cannot be written on
        /// <paramref name="type"/> with <see cref="DynamicallyAccessedMembersAttribute"/> - that
        /// annotation describes the members of a type the trimmer can see. A dependency on the open
        /// definition covers every <see cref="MemberInfo"/> subtype, including ones a payload names
        /// that no compilation does.
        /// <para>
        /// Only the constructors: the formatter is handed the serializer, so that is what
        /// <c>Activator</c> binds, and its <c>Serialize</c>/<c>Deserialize</c> are already rooted as
        /// the overrides of the <c>FormatterBase&lt;T&gt;</c> methods that the non-generic
        /// <see cref="IFormatter"/> contract calls.
        /// </para>
        /// </summary>
        [DynamicDependency(
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors,
            typeof(MemberInfoFormatter<>))]
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