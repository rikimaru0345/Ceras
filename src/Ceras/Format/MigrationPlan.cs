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

    sealed class TypeMigrationPlan
    {
        public Type CurrentType;
        public FieldMapping[] Mappings;
        public string[] AddedFieldNames;
        public bool HasChanges;
        public MethodInfo OnAfterDeserialize; // method taking MigrationContext, or null
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

            plan.OnAfterDeserialize = FindMigrationCallback(currentType);
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
            if (WellKnownTypes.TryGetId(newFieldType, out int newId))
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
                
                // Unregistered type fallback
                if (newFieldType.FullName == oldSchema.StableName) return true;
            }

            Console.WriteLine("DEBUG:   Types are INCOMPATIBLE");
            return false;
        }

        static MethodInfo FindMigrationCallback(Type type)
        {
            foreach (var m in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.GetCustomAttribute<OnAfterDeserializeAttribute>() == null) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(MigrationContext))
                    return m;
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
