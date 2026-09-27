namespace Ceras.Resolvers
{
    using System;
    using Formatters;

    /// <summary>
    /// A formatter resolver is something that can create instances of <see cref="IFormatter{T}"/> for a given <see cref="Type"/> (or null if the resolver can not handle the given type)
    /// </summary>
    public interface IFormatterResolver
    {
        /// <summary>
        /// The formatter for a type, or null when this resolver does not handle it — which is how a
        /// resolver declines and lets the next one try. Every implementation already returned null in
        /// that case; the signature is what did not say so.
        /// </summary>
        IFormatter? GetFormatter(Type type);
    }
}