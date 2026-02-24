using Ceras.Formatters;
using System;

namespace Ceras.Helpers
{
    public static class CerasHelpers
    {
        public static bool IsReflectionFallbackFormatter(Type formatterType)
        {
            if (formatterType.IsGenericType)
                return formatterType.GetGenericTypeDefinition() == typeof(ReflectionFallbackFormatter<>);

            return false;
        }

        internal static void VerifyName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Name cannot be null or empty");

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == ' ' || c == '[' || c == ']' || c == '(' || c == ')' || c == ',' || c == '<' || c == '>' || c == '`')
                    throw new ArgumentException($"Name '{name}' contains invalid character '{c}'");
            }
        }
    }
}
