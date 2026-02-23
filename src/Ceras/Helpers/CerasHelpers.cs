using Ceras.Formatters;
using System;

namespace Ceras.Helpers
{
    public static class CerasHelpers
    {
        public static bool IsReflectionFallbackFormatter(Type formatterType)
        {
            if (formatterType.IsGenericType)
                if (formatterType.GetGenericTypeDefinition().Name == typeof(ReflectionFallbackFormatter<int>).GetGenericTypeDefinition().Name)
                    return true;

            return false;
        }
    }
}
