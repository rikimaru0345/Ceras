using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ceras.Format
{
    enum CoercionKind
    {
        None = 0,
        Int32ToInt64,
        Int16ToInt32,
        Int32ToSingle,
        Int32ToDouble,
        SingleToDouble,
    }

    struct FieldMapping
    {
        public OldFieldSchema OldField;
        public FieldInfo NewField;  // null = removed (skip)
        public CoercionKind Coercion;
    }

    /// <summary>The parameter shape of a deserialization callback accepted by the interpreter.</summary>
    enum CallbackArgumentKind
    {
        None = 0,
        Serializer = 1,
        MigrationContext = 2,
    }

    sealed class TypeMigrationPlan
    {
        public Type CurrentType;
        public FieldMapping[] Mappings;
        public string[] AddedFieldNames;
        public bool HasChanges;
        public MethodInfo OnBeforeDeserialize;
        public CallbackArgumentKind OnBeforeDeserializeArgument;
        public MethodInfo OnAfterDeserialize;
        public CallbackArgumentKind OnAfterDeserializeArgument;
        // Cached invokers that already carry the migration context for this type. Built by the
        // traversal engine once per plan (the migration context is identical for every instance).
        public Action<CerasSerializer, object> BeforeInvoker;
        public Action<CerasSerializer, object> AfterInvoker;
        public Dictionary<long, long> EnumValueMap;
    }

    static class MigrationPlanBuilder
    {
        public static Dictionary<string, TypeMigrationPlan> BuildAll(
            Dictionary<string, OldTypeSchema> oldByName, SchemaRegistry registry)
        {
            var result = new Dictionary<string, TypeMigrationPlan>(StringComparer.Ordinal);
            var oldById = new Dictionary<int, OldTypeSchema>();
            foreach (var s in oldByName.Values) oldById.TryAdd(s.TypeId, s);

            foreach (var kv in oldByName)
            {
                var old = kv.Value;
                // Only index by the canonical stable name (not aliases)
                if (kv.Key != old.StableName) continue;
                if (result.ContainsKey(old.StableName)) continue;
                
                result[old.StableName] = BuildOne(old, oldById, registry);
            }

            return result;
        }

        static TypeMigrationPlan BuildOne(OldTypeSchema old, Dictionary<int, OldTypeSchema> oldById, SchemaRegistry registry)
        {
            registry.TryResolveType(old.StableName, out Type currentType);

            var plan = new TypeMigrationPlan { CurrentType = currentType };

            if (old.Kind == SchemaTypeKind.Enum)
            {
                if (currentType != null && currentType.IsEnum)
                    plan.EnumValueMap = BuildEnumMap(old, currentType);
                return plan;
            }

            if (currentType == null)
            {
                plan.Mappings = BuildSkipAllMappings(old);
                plan.AddedFieldNames = Array.Empty<string>();
                plan.HasChanges = true;
                return plan;
            }

            var currentFieldMap = BuildCurrentFieldMap(currentType);
            var mappedStableNames = new HashSet<string>(StringComparer.Ordinal);

            plan.Mappings = new FieldMapping[old.Fields.Length];
            for (int i = 0; i < old.Fields.Length; i++)
            {
                var oldField = old.Fields[i];
                FieldInfo newField = FindCurrentField(oldField, currentFieldMap);
                CoercionKind coerce = CoercionKind.None;

                if (newField != null)
                {
                    coerce = DetectCoercion(oldField.TypeId, newField.FieldType);
                    bool compatible = coerce != CoercionKind.None
                        || TypeIdsCompatible(oldField.TypeId, newField.FieldType, oldById, registry);
                    
                    if (!compatible)
                        newField = null;
                    else
                    {
                        var sa = newField.GetCustomAttribute<CerasStableNameAttribute>();
                        mappedStableNames.Add(sa?.Name ?? newField.Name);
                    }
                }

                plan.Mappings[i] = new FieldMapping
                { OldField = oldField, NewField = newField, Coercion = coerce };
            }

            // Determine added fields (in current schema but not matched from old)
            if (registry.TryGetInfo(currentType, out var currentInfo))
            {
                var added = new List<string>();
                foreach (var fi in currentInfo.Fields)
                    if (!mappedStableNames.Contains(fi.StableName))
                        added.Add(fi.StableName);
                plan.AddedFieldNames = added.ToArray();
            }
            else
            {
                // Fallback to reflection for unregistered types
                var added = new List<string>();
                foreach (var kv in currentFieldMap)
                    if (!mappedStableNames.Contains(kv.Key))
                        added.Add(kv.Key);
                plan.AddedFieldNames = added.ToArray();
            }

            plan.HasChanges = plan.AddedFieldNames.Length > 0
                || Array.Exists(plan.Mappings, m => m.NewField == null);

            plan.OnBeforeDeserialize = FindCallback(currentType, typeof(OnBeforeDeserializeAttribute), out var beforeArg);
            plan.OnBeforeDeserializeArgument = beforeArg;
            plan.OnAfterDeserialize = FindCallback(currentType, typeof(OnAfterDeserializeAttribute), out var afterArg);
            plan.OnAfterDeserializeArgument = afterArg;
            return plan;
        }

        static FieldMapping[] BuildSkipAllMappings(OldTypeSchema old)
        {
            var m = new FieldMapping[old.Fields.Length];
            for (int i = 0; i < old.Fields.Length; i++)
                m[i] = new FieldMapping { OldField = old.Fields[i] };
            return m;
        }

        static Dictionary<string, FieldInfo> BuildCurrentFieldMap(Type type)
        {
            var map = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            foreach (var f in type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.GetCustomAttribute<ExcludeAttribute>() != null) continue;
                if (f.GetCustomAttribute<NonSerializedAttribute>() != null) continue;

                var sa = f.GetCustomAttribute<CerasStableNameAttribute>();
                string stableName = sa?.Name ?? f.Name;
                
                // Handle compiler-generated auto-property backing fields (e.g., <Map>k__BackingField)
                if (sa == null && stableName.StartsWith("<") && stableName.EndsWith(">k__BackingField"))
                {
                    stableName = stableName.Substring(1, stableName.Length - 17);
                }

                // If it's a private field without an attribute, it might be a backing field.
                // For now we just include it if it's not excluded.
                map.TryAdd(stableName, f);

                foreach (var pn in f.GetCustomAttributes<PreviousNameAttribute>())
                {
                    if (pn.Name != null)
                        map.TryAdd(pn.Name, f);
                    foreach (var alt in pn.AlternativeNames)
                        if (alt != null)
                            map.TryAdd(alt, f);
                }
            }
            return map;
        }

        static FieldInfo FindCurrentField(
            OldFieldSchema old,
            Dictionary<string, FieldInfo> currentFieldMap)
        {
            if (currentFieldMap.TryGetValue(old.StableName, out var fi))
                return fi;
            foreach (var prev in old.PreviousNames)
                if (currentFieldMap.TryGetValue(prev, out fi))
                    return fi;
            
            return null;
        }

        internal static CoercionKind DetectCoercion(int oldTypeId, Type newFieldType)
        {
            if (oldTypeId == WellKnownTypes.Int32 && newFieldType == typeof(long)) return CoercionKind.Int32ToInt64;
            if (oldTypeId == WellKnownTypes.Int16 && newFieldType == typeof(int)) return CoercionKind.Int16ToInt32;
            if (oldTypeId == WellKnownTypes.Int32 && newFieldType == typeof(float)) return CoercionKind.Int32ToSingle;
            if (oldTypeId == WellKnownTypes.Int32 && newFieldType == typeof(double)) return CoercionKind.Int32ToDouble;
            if (oldTypeId == WellKnownTypes.Single && newFieldType == typeof(double)) return CoercionKind.SingleToDouble;
            return CoercionKind.None;
        }

        static bool TypeIdsCompatible(int oldTypeId, Type newFieldType, Dictionary<int, OldTypeSchema> oldById, SchemaRegistry registry)
        {
            // 1. Well-known ID match
            int newId;
            bool isWellKnown = WellKnownTypes.TryGetId(newFieldType, out newId);
            
            // If the new type is a generic instantiation, it might not be "well-known" itself,
            // but its definition might be.
            if (!isWellKnown && newFieldType.IsGenericType && !newFieldType.IsGenericTypeDefinition)
            {
                if (WellKnownTypes.TryGetId(newFieldType.GetGenericTypeDefinition(), out newId))
                    isWellKnown = true;
            }

            if (isWellKnown)
            {
                if (oldTypeId == newId) return true;

                // Object can accept anything
                if (newId == WellKnownTypes.Object) return true;

                // Generic case: old List<int> (201) vs current List<int> (newId=17)
                if (oldById.TryGetValue(oldTypeId, out var old) && old.Kind == SchemaTypeKind.GenericInstantiation)
                {
                    return old.DefinitionTypeId == newId;
                }

                return false;
            }

            // 2. User type case: Match by stable name
            if (oldById.TryGetValue(oldTypeId, out var oldSchema))
            {
                if (registry.TryResolveType(oldSchema.StableName, out var resolvedType))
                {
                    if (resolvedType == newFieldType) return true;
                }
                
                // Unregistered type fallback (canonical identity; legacy FullName too)
                if (newFieldType.FullName == oldSchema.StableName ||
                    TypeIdentity.GetCanonicalName(newFieldType) == oldSchema.StableName) return true;
            }

            return false;
        }

        /// <summary>
        /// Finds a deserialization callback on <paramref name="type"/> or a base type. The hierarchy is
        /// walked from the most derived type up so a private base callback is found (reflection's
        /// <c>GetMethods</c> would miss a private base method declared on a derived type's formatter).
        /// Within one type the parameterless form is preferred, then <c>(MigrationContext)</c>, then
        /// <c>(CerasSerializer)</c>.
        /// </summary>
        static MethodInfo FindCallback(Type type, Type attributeType, out CallbackArgumentKind kind)
        {
            kind = CallbackArgumentKind.None;
            if (type == null) return null;

            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                MethodInfo best = null;
                CallbackArgumentKind bestKind = CallbackArgumentKind.None;
                int bestRank = int.MaxValue;

                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.IsStatic || m.ReturnType != typeof(void) || m.IsGenericMethod) continue;
                    if (m.GetCustomAttribute(attributeType) == null) continue;

                    var ps = m.GetParameters();
                    CallbackArgumentKind k;
                    if (ps.Length == 0) k = CallbackArgumentKind.None;
                    else if (ps.Length == 1 && ps[0].ParameterType == typeof(MigrationContext)) k = CallbackArgumentKind.MigrationContext;
                    else if (ps.Length == 1 && ps[0].ParameterType == typeof(CerasSerializer)) k = CallbackArgumentKind.Serializer;
                    else continue;

                    int rank = k switch
                    {
                        CallbackArgumentKind.None => 0,
                        CallbackArgumentKind.MigrationContext => 1,
                        _ => 2,
                    };
                    if (best == null || rank < bestRank)
                    {
                        best = m;
                        bestKind = k;
                        bestRank = rank;
                    }
                }

                if (best != null)
                {
                    kind = bestKind;
                    return best;
                }
            }

            return null;
        }

        static Dictionary<long, long> BuildEnumMap(OldTypeSchema old, Type currentType)
        {
            var map = new Dictionary<long, long>();
            var nameToCurrentValue = new Dictionary<string, long>(StringComparer.Ordinal);
            
            foreach (var f in currentType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!f.IsLiteral) continue;
                
                long currentVal = Convert.ToInt64(f.GetValue(null));
                
                var sa = f.GetCustomAttribute<CerasStableNameAttribute>();
                nameToCurrentValue[sa?.Name ?? f.Name] = currentVal;
                
                foreach (var pn in f.GetCustomAttributes<PreviousNameAttribute>())
                {
                    if (pn.Name != null) nameToCurrentValue[pn.Name] = currentVal;
                    foreach (var alt in pn.AlternativeNames)
                        if (alt != null) nameToCurrentValue[alt] = currentVal;
                }
            }

            foreach (var oldEv in old.EnumValues)
            {
                if (nameToCurrentValue.TryGetValue(oldEv.Name, out long currentVal))
                    map[oldEv.Value] = currentVal;
            }
            return map;
        }
    }
}
