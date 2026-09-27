using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
    static class MiscHelpers
    {
        public static string Singular<T>(this T enumValue)
        {
            if (!typeof(T).IsEnum)
                throw new ArgumentException();
            
            // An enum's ToString never returns null.
            var str = enumValue!.ToString()!;

            if (str.EndsWith("ies"))
                return str.Substring(0, str.Length - 3) + "y";
            
            return str.TrimEnd('s');
        }

        
        // Remove nonsense like 'm_' from variable names
        public static string CleanMemberName(string name)
        {
            if (name.StartsWith("m_"))
                return name.Remove(0, 2);
            if (name.StartsWith("_"))
                return name.Remove(0, 1);

            return name;
        }

    }
}
