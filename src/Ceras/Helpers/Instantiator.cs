using System;
using System.Reflection;

namespace Ceras.Helpers
{
    internal static class Instantiator
    {
        /// <summary>
        /// Creates an instance of <paramref name="t"/>. Never returns null: <see cref="Activator"/> either
        /// produces one or throws (a missing constructor, a throwing constructor, a null result from a
        /// value-type default). Every caller casts the result, so a nullable return only spread noise.
        /// <para>
        /// <paramref name="t"/> is deliberately not annotated
        /// <see cref="System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute"/>, so ILLink
        /// reports IL2067 against this line and keeps reporting it. The annotation would be a true
        /// statement of what
        /// <see cref="Activator.CreateInstance(Type, BindingFlags, Binder, object[], System.Globalization.CultureInfo)"/>
        /// needs, but it is not one anything can meet: every caller builds <paramref name="t"/> with
        /// <c>MakeGenericType</c> over a runtime <see cref="Type"/>, so the requirement would have to be
        /// pushed onto that runtime type and then out through the whole internal call chain, ending at
        /// the public <c>GetFormatter&lt;T&gt;</c>/<c>Serialize&lt;T&gt;</c> surface - where it would be an
        /// obligation on the caller to preserve the constructors of their own serialized types, which
        /// is not what <c>Activator</c> is about here. Measured on the trim/AOT smoke, adding it roots
        /// nothing the two dependencies do not already root, and moves IL2067 off this line and onto
        /// the callers that cannot meet it - four more analysis warnings in
        /// <c>Config/TypeConfig.cs</c>, <c>Formatters/MemberInfoFormatter.cs</c>,
        /// <c>Formatters/ReflectionFallbackFormatter.cs</c> and
        /// <c>Resolvers/StandardFormatterResolver.cs</c>, none of which has anything to do with the
        /// formatter this library reflects over. The members that do have to survive are rooted where
        /// the type that needs them is built: <c>CerasSerializer.GetReferenceFormatter</c> states
        /// <c>DynamicDependency</c> for <c>ReferenceFormatter&lt;&gt;</c> and
        /// <c>ReferenceFormatter_KnownSealedType&lt;&gt;</c>, the only two formatter families constructed
        /// through this method whose constructors no compilation names.
        /// </para>
        /// </summary>
        public static object Create(Type t, params object[] args)
        {
            return Activator.CreateInstance(t, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)
                ?? throw new MissingMethodException(
                    $"Activator returned null for '{t}', which has no usable instance constructor for the arguments given.");
        }
    }
}
