namespace Ceras.SourceGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using Microsoft.CodeAnalysis;

    /// <summary>
    /// Emits the write half of a generated formatter's member loop, batching runs of fixed-width
    /// primitive members into a single <c>GetSpan</c>/<c>Advance</c> pair.
    /// <para>
    /// A member written on its own costs two interface calls on <c>IBufferWriter&lt;byte&gt;</c> - one
    /// for the span, one for the advance - on top of the store itself. Measured on an 81-field
    /// struct, that pair was about 0.36 ns per field, and a third to a half of the cost of encoding
    /// one. Asking for the whole run's room at once pays for those two calls per <em>run</em> instead
    /// of per member, which is the same win devirtualizing the writer would give, and it gets there
    /// without changing a single signature: nothing here is generic over the writer type, no formatter
    /// contract moves, and the code that does not batch is untouched.
    /// </para>
    /// <para>
    /// Bytes are identical either way, by construction rather than by testing: every store below is
    /// the <c>BinaryPrimitives.Write*LittleEndian</c> call the corresponding
    /// <c>SerializerBinary.Write*Fixed</c> already makes, so a big-endian host gets the same answer.
    /// <see cref="bool"/> is written as an explicit 0 or 1 rather than copied, because nothing
    /// guarantees how a managed <c>bool</c> is laid out in memory.
    /// </para>
    /// <para>
    /// Only members the wire format stores as a single fixed-width field batch.
    /// <c>DateTimeOffset</c> does not - it is a 10-byte ticks-plus-offset pair - so it keeps the
    /// ordinary two-call path, as does anything variable-width. The reader is untouched: batching
    /// changes how many times the buffer is asked for room, not the order or the layout of the bytes.
    /// </para>
    /// </summary>
    static class BatchedMemberEmitter
    {
        /// <summary>
        /// Largest run handed to a single <c>GetSpan</c>. Bounds the request a type with an unusual
        /// number of members can make, and keeps the emitted expression small enough to read.
        /// </summary>
        const int MaxRunBytes = 1024;

        /// <summary>
        /// Width in bytes of a member stored as a single fixed-width field, or false when it is not
        /// stored that way. A run of consecutive members that all return true can share one span.
        /// </summary>
        public static bool TryGetFixedWidth(ITypeSymbol type, out int width)
        {
            width = 0;
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte: width = 1; return true;
                case SpecialType.System_Char:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16: width = 2; return true;
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Single: width = 4; return true;
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Double: width = 8; return true;
                case SpecialType.System_Decimal: width = 16; return true;
            }
            string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (fqn == "global::System.Guid") { width = 16; return true; }
            if (fqn == "global::System.DateTime") { width = 8; return true; }
            if (fqn == "global::System.TimeSpan") { width = 8; return true; }
            // DateTimeOffset is 10 bytes across two writes, so it is not a single fixed-width store.
            return false;
        }

        /// <summary>
        /// Emits the whole member loop: batched blocks for runs of fixed-width members, and the
        /// existing per-member calls for everything else.
        /// </summary>
        /// <param name="sb">Destination.</param>
        /// <param name="members">The members, in the order they are written.</param>
        /// <param name="readExpression">Produces the C# expression that reads a member off the value.</param>
        /// <param name="formatterFieldName">Produces the injected formatter field name for a member's type.</param>
        /// <param name="indent">Indentation for the emitted statements.</param>
        public static void EmitWrites(
            StringBuilder sb,
            List<SerializedMember> members,
            Func<SerializedMember, string> readExpression,
            Func<SerializedMember, string> formatterFieldName,
            string indent)
        {
            int i = 0;
            while (i < members.Count)
            {
                if (TryGetFixedWidth(members[i].Type, out int width))
                {
                    int runBytes = 0;
                    int j = i;
                    while (j < members.Count
                        && TryGetFixedWidth(members[j].Type, out int w)
                        && runBytes + w <= MaxRunBytes)
                    {
                        runBytes += w;
                        j++;
                    }

                    // Two or more members are where sharing the span starts paying; a single member
                    // would ask for exactly the room the ordinary call asks for, and cost an extra
                    // local.
                    if (j - i >= 2)
                    {
                        EmitRun(sb, members, i, j, runBytes, readExpression, indent);
                        i = j;
                        continue;
                    }
                }

                EmitSingle(sb, members[i], readExpression, formatterFieldName, indent);
                i++;
            }
        }

        static void EmitRun(
            StringBuilder sb,
            List<SerializedMember> members,
            int from,
            int to,
            int runBytes,
            Func<SerializedMember, string> readExpression,
            string indent)
        {
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var __cerasSpan = writer.GetSpan({runBytes});");
            int at = 0;
            for (int k = from; k < to; k++)
            {
                var m = members[k];
                var slice = at == 0 ? "__cerasSpan" : $"__cerasSpan.Slice({at})";
                var value = readExpression(m);
                sb.AppendLine($"{indent}    {Store(m.Type, slice, value)}");
                at += widthOf(m.Type);
            }
            sb.AppendLine($"{indent}    writer.Advance({runBytes});");
            sb.AppendLine($"{indent}}}");
        }

        static int widthOf(ITypeSymbol type) => TryGetFixedWidth(type, out int w) ? w : 0;

        /// <summary>
        /// The single store for one member into <paramref name="slice"/>, chosen to be the same bytes
        /// the equivalent <c>SerializerBinary</c> call writes.
        /// </summary>
        static string Store(ITypeSymbol type, string slice, string value)
        {
            switch (type.SpecialType)
            {
                // bool is written as an explicit 0 or 1, never copied, because a managed bool's
                // in-memory representation is not specified.
                case SpecialType.System_Boolean: return $"{slice}[0] = (byte)({value} ? 1 : 0);";
                case SpecialType.System_Byte: return $"{slice}[0] = {value};";
                case SpecialType.System_SByte: return $"{slice}[0] = (byte)({value});";
            }

            if (type.SpecialType == SpecialType.System_Decimal)
            {
                // Same four words in the same order as SerializerBinary.WriteDecimal.
                return $"{{ global::System.Span<int> __b = stackalloc int[4]; global::System.Decimal.GetBits({value}, __b); "
                     + $"{BP}.WriteInt32LittleEndian({slice}, __b[0]); "
                     + $"{BP}.WriteInt32LittleEndian({slice}.Slice(4), __b[1]); "
                     + $"{BP}.WriteInt32LittleEndian({slice}.Slice(8), __b[2]); "
                     + $"{BP}.WriteInt32LittleEndian({slice}.Slice(12), __b[3]); }}";
            }

            string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (fqn == "global::System.Guid")
            {
                // MemoryMarshal.Write takes its argument by `in`, and a member read is an expression
                // rather than a variable, so it needs a local. Same raw 16 bytes as WriteGuid's
                // unaligned write, since both are a byte-for-byte copy of the Guid's representation.
                return $"{{ var __g = {value}; global::System.Runtime.InteropServices.MemoryMarshal.Write({slice}, in __g); }}";
            }
            if (fqn == "global::System.DateTime") return $"{BP}.WriteInt64LittleEndian({slice}, {value}.ToBinary());";
            if (fqn == "global::System.TimeSpan") return $"{BP}.WriteInt64LittleEndian({slice}, {value}.Ticks);";

            switch (type.SpecialType)
            {
                case SpecialType.System_Int16:
                case SpecialType.System_Char: return $"{BP}.WriteInt16LittleEndian({slice}, (short)({value}));";
                case SpecialType.System_UInt16: return $"{BP}.WriteUInt16LittleEndian({slice}, (ushort)({value}));";
                case SpecialType.System_Int32: return $"{BP}.WriteInt32LittleEndian({slice}, {value});";
                case SpecialType.System_UInt32: return $"{BP}.WriteUInt32LittleEndian({slice}, {value});";
                case SpecialType.System_Single: return $"{BP}.WriteSingleLittleEndian({slice}, {value});";
                case SpecialType.System_Int64: return $"{BP}.WriteInt64LittleEndian({slice}, {value});";
                case SpecialType.System_UInt64: return $"{BP}.WriteUInt64LittleEndian({slice}, {value});";
                case SpecialType.System_Double: return $"{BP}.WriteDoubleLittleEndian({slice}, {value});";
            }

            throw new NotSupportedException($"{type.ToDisplayString()} has no batched store");
        }

        static void EmitSingle(
            StringBuilder sb,
            SerializedMember m,
            Func<SerializedMember, string> readExpression,
            Func<SerializedMember, string> formatterFieldName,
            string indent)
        {
            var value = readExpression(m);
            if (TypeHelpers.IsFastPrimitive(m.Type))
            {
                var writeMethod = RobustCodeEmitter.GetPrimitiveWriteMethod(m.Type);
                // The Write* overloads take the width they write; cast so char/sbyte members compile
                // and so the bytes match the readers, which already read back through a cast.
                if (m.Type.SpecialType == SpecialType.System_Boolean) value = $"(byte)({value} ? 1 : 0)";
                else if (m.Type.SpecialType == SpecialType.System_SByte) value = $"(byte)({value})";
                else if (m.Type.SpecialType == SpecialType.System_Char) value = $"(short)({value})";
                sb.AppendLine($"{indent}global::Ceras.SerializerBinary.{writeMethod}(writer, {value});");
            }
            else
            {
                sb.AppendLine($"{indent}{formatterFieldName(m)}.Serialize(writer, {value});");
            }
        }

        const string BP = "global::System.Buffers.Binary.BinaryPrimitives";
    }
}
