using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Ceras.SourceGenerator
{
    internal static class AccessHelperGenerator
    {
        public static void WritePartialClassHelper(SourceProductionContext context, INamedTypeSymbol type, List<SerializedMember> members)
        {
            // No longer needed. UnsafeAccessors are emitted locally in the formatter class.
        }
    }
}
