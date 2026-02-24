using System;
using System.Collections.Generic;

namespace Ceras.Format
{
    static class RobustContext
    {
        [ThreadStatic]
        static ContextData _current;

        public static StringInternTable InternTable => _current?.InternTable;
        public static string[] StringTable => _current?.StringTable;
        public static Dictionary<Type, int> TypeToId => _current?.TypeToId;
        public static Dictionary<int, Type> IdToType => _current?.IdToType;

        public static void BeginWrite(StringInternTable internTable, Dictionary<Type, int> typeToId)
        {
            _current = new ContextData { InternTable = internTable, TypeToId = typeToId };
        }

        public static void BeginRead(string[] stringTable, Dictionary<int, Type> idToType)
        {
            _current = new ContextData { StringTable = stringTable, IdToType = idToType };
        }

        public static void End() { _current = null; }

        class ContextData
        {
            public StringInternTable InternTable;
            public string[] StringTable;
            public Dictionary<Type, int> TypeToId;
            public Dictionary<int, Type> IdToType;
        }
    }
}
