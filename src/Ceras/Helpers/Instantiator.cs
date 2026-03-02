using System;
using System.Reflection;

namespace Ceras.Helpers
{
    internal static class Instantiator
    {
        public static object Create(Type t, params object[] args)
        {
            return Activator.CreateInstance(t, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null);
        }
    }
}
