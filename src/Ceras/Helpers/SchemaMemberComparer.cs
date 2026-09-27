namespace Ceras.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.Serialization;

    class SchemaMemberComparer : IComparer<SchemaMember>
    {
        public static readonly SchemaMemberComparer Instance = new SchemaMemberComparer();

        public int Compare(SchemaMember? x, SchemaMember? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            // Generator sort key: CLR-TypeFullName + MemberName + DeclaringTypeFqn + "f"/"p"
            var xKey = GetSortKey(x);
            var yKey = GetSortKey(y);

            return string.Compare(xKey, yKey, StringComparison.Ordinal);
        }

        static string GetSortKey(SchemaMember m)
        {
            string typeName = m.MemberType.FullName ?? m.MemberType.Name;
            string memberName = m.Member.Name;
            // DeclaringType is null only for a global type, which a serialized member cannot be.
        var declaringType = m.Member.DeclaringType!;
        string declaringFqn = declaringType.FullName ?? declaringType.Name;
            string kind = m.Member is FieldInfo ? "f" : "p";

            return typeName + memberName + declaringFqn + kind;
        }
    }
}
