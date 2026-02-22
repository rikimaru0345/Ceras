namespace Ceras.Savegame
{
    /// <summary>
    /// Thread-local context that bridges <see cref="SavegameStringFormatter"/> to
    /// the active <see cref="StringInternTable"/> (write) or loaded <c>string[]</c>
    /// table (read).
    ///
    /// Set before calling <c>CerasSerializer.Serialize/Deserialize</c> and cleared
    /// in a <c>finally</c> block immediately after.  Not thread-safe between
    /// concurrent save/load operations on the same thread, which is fine — game
    /// saves are always single-threaded per serializer instance.
    /// </summary>
    internal static class SavegameContext
    {
        [System.ThreadStatic]
        static StringInternTable _writeTable;

        [System.ThreadStatic]
        static string[] _readTable;

        [System.ThreadStatic]
        static System.Collections.Generic.Dictionary<System.Type, int> _typeToId;

        [System.ThreadStatic]
        static System.Collections.Generic.Dictionary<int, System.Type> _idToType;

        /// <summary>The active intern table for the current write operation, or <c>null</c>.</summary>
        internal static StringInternTable WriteTable => _writeTable;

        /// <summary>The loaded string table for the current read operation, or <c>null</c>.</summary>
        internal static string[] ReadTable => _readTable;

        internal static System.Collections.Generic.Dictionary<System.Type, int> TypeToId => _typeToId;
        internal static System.Collections.Generic.Dictionary<int, System.Type> IdToType => _idToType;

        internal static void BeginWrite(StringInternTable table, System.Collections.Generic.Dictionary<System.Type, int> typeToId)
        {
            _writeTable = table;
            _typeToId   = typeToId;
            _readTable  = null;
            _idToType   = null;
        }

        internal static void BeginRead(string[] table, System.Collections.Generic.Dictionary<int, System.Type> idToType)
        {
            _readTable  = table;
            _idToType   = idToType;
            _writeTable = null;
            _typeToId   = null;
        }

        internal static void End()
        {
            _writeTable = null;
            _readTable  = null;
            _typeToId   = null;
            _idToType   = null;
        }
    }
}
