using System;
using System.Collections.Generic;

namespace Ceras.Format
{
    /// <summary>
    /// The per-operation state the robust format needs while encoding or decoding: the string table, the
    /// type-id map, and the string-length limit.
    /// <para>
    /// The state is a <b>scoped stack</b>, not a single slot. A deserialization callback that saves or
    /// loads on the same thread used to overwrite the outer operation's context, so the outer read
    /// continued with the inner operation's string table and type map — silently, and only for the
    /// remainder of the graph. Each operation now pushes its own state and disposing the scope restores
    /// the previous one, which matches how <c>CerasSerializer</c> already stacks its per-operation
    /// <c>InstanceData</c>.
    /// </para>
    /// </summary>
    static class RobustContext
    {
        [ThreadStatic]
        static ContextData _current = null!;

        // The four collections are nullable because a scope only carries the half it needs: a write
        // scope has the intern table and the type-to-id map, a read scope the string table and the
        // id-to-type map, and the other half of each pair is null for the whole scope. Every accessor
        // below also answers null when no scope is pushed at all, so a formatter used outside a robust
        // operation sees "not active" instead of a half-built context.
        public static StringInternTable? InternTable => _current?.InternTable;
        public static string[]? StringTable => _current?.StringTable;
        public static Dictionary<Type, int>? TypeToId => _current?.TypeToId;
        public static Dictionary<int, Type>? IdToType => _current?.IdToType;

        /// <summary>
        /// The configured longest string a read accepts, in UTF-8 bytes. Taken from the context rather
        /// than from a formatter, so a formatter cannot be configured without a limit (0.8.7).
        /// </summary>
        public static uint MaxStringLength => _current?.MaxStringLength ?? uint.MaxValue;

        /// <summary>Pushes a write context. Dispose the result to restore the previous context.</summary>
        public static IDisposable BeginWrite(StringInternTable internTable, Dictionary<Type, int> typeToId)
            => Push(new ContextData { InternTable = internTable, TypeToId = typeToId, MaxStringLength = uint.MaxValue });

        /// <summary>Pushes a read context. Dispose the result to restore the previous context.</summary>
        public static IDisposable BeginRead(string[] stringTable, Dictionary<int, Type> idToType, uint maxStringLength)
            => Push(new ContextData { StringTable = stringTable, IdToType = idToType, MaxStringLength = maxStringLength });

        static IDisposable Push(ContextData data)
        {
            var previous = _current;
            _current = data;
            return new Scope(previous);
        }

        sealed class Scope : IDisposable
        {
            ContextData _previous;
            public Scope(ContextData previous) { _previous = previous; }
            public void Dispose() { _current = _previous; _previous = null!; }
        }

        class ContextData
        {
            public StringInternTable? InternTable;
            public string[]? StringTable;
            public Dictionary<Type, int>? TypeToId;
            public Dictionary<int, Type>? IdToType;
            public uint MaxStringLength;
        }
    }
}
