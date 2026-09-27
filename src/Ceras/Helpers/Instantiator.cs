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
        /// </summary>
        public static object Create(Type t, params object[] args)
        {
            return Activator.CreateInstance(t, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)
                ?? throw new MissingMethodException(
                    $"Activator returned null for '{t}', which has no usable instance constructor for the arguments given.");
        }
    }
}
