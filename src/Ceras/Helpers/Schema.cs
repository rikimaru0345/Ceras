namespace Ceras.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;

    class Schema
    {
        public readonly Type Type;
        public readonly bool IsStatic;
        public readonly List<SchemaMember> Members = new List<SchemaMember>();

        public Schema(Type type, bool isStatic)
        {
            Type = type;
            IsStatic = isStatic;
        }
    }

    class SchemaMember
    {
        public readonly string PersistentName;
        public readonly MemberInfo Member;
        public readonly Type MemberType;
        public readonly bool IsMonomorphic;

        public SchemaMember(string persistentName, MemberInfo member)
        {
            PersistentName = persistentName;
            Member = member;
            MemberType = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;
            
            // Match generator's monomorphic detection:
            // Auto-set for value-type and sealed-class element types
            IsMonomorphic = MemberType.IsValueType || MemberType.IsSealed;
        }
    }
}
