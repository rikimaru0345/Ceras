using System.Linq;
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

    /// <summary>
    /// How a type's <c>[CerasConstructor]</c> is supplied while migrating: the constructor, and which
    /// serialized member feeds each of its parameters (a parameter with no member is transient
    /// construction input and receives its own declared default, exactly like the generated reader
    /// passes <c>default(T)</c>).
    /// </summary>
    sealed class CtorArgumentPlan
    {
        readonly Dictionary<FieldInfo, int> _slotByField;
        readonly bool[] _mapped;

        public ConstructorInfo Constructor { get; }
        public ParameterInfo[] Parameters { get; }

        public CtorArgumentPlan(ConstructorInfo constructor, ParameterInfo[] parameters,
            Dictionary<FieldInfo, int> slotByField, bool[] mapped)
        {
            Constructor = constructor;
            Parameters = parameters;
            _slotByField = slotByField;
            _mapped = mapped;
        }

        /// <summary>The parameter index a member feeds, or -1 when the member is not a constructor argument.</summary>
        public int SlotForField(FieldInfo field)
            => field != null && _slotByField.TryGetValue(field, out int slot) ? slot : -1;

        public bool IsMapped(int slot) => _mapped[slot];

        /// <summary>
        /// The value for a parameter that no serialized member feeds: its declared default, or
        /// <c>default(T)</c> when it has none. Mirrors <c>RobustCodeEmitter.FormatDefaultArgument</c>.
        /// </summary>
        public object DefaultFor(int slot)
        {
            var parameter = Parameters[slot];
            var parameterType = parameter.ParameterType;

            if (parameter.HasDefaultValue)
            {
                object declared = parameter.DefaultValue;
                if (declared == null) return parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
                if (parameterType.IsEnum) return Enum.ToObject(parameterType, declared);
                try
                {
                    if (declared.GetType() == parameterType) return declared;
                    return Convert.ChangeType(declared, parameterType, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
                {
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"Cannot migrate a [CerasConstructor] parameter '{parameter.Name}' of '{parameter.Member.DeclaringType?.FullName}': " +
                        $"its declared default '{declared}' is not a valid '{parameterType.Name}'.", e);
                }
            }

            return parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
        }
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
        /// <summary>Old raw bits -> current raw bits, matched by enum member name. See <see cref="EnumBits"/>.</summary>
        public Dictionary<ulong, ulong> EnumValueMap;

        // How this type's [CerasConstructor] gets its values while migrating, resolved once per load
        // (the same resolution the generator does at compile time). Null when the type needs no
        // constructor arguments.
        public CtorArgumentPlan CtorArgs;
        public bool CtorArgsResolved;
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
                    {
                        // The field still exists by name but its type changed in a way Ceras cannot convert
                        // (double -> decimal, long -> DateTimeOffset, int -> short, ...). Treating that as a
                        // *removed* field silently dropped the stored value and left the declared default —
                        // data loss on a perfectly valid save, visible only through
                        // MigrationContext.SkippedFields. Refuse the load instead, naming the field and both
                        // types. Performing the conversions (with their overflow/rounding rules) is the
                        // "defined lossless conversions" milestone; until then, never lose a value quietly.
                        throw new Ceras.Exceptions.CerasSchemaException(
                            $"Cannot migrate the field '{oldField.StableName}' of '{old.StableName}': the save file stores it as " +
                            $"'{DescribeTypeId(oldField.TypeId, oldById)}' but the current type declares '{newField.FieldType.FullName}', " +
                            "and Ceras has no defined conversion between them. Keep the original type, rename one of them, or " +
                            $"mark the field [Exclude]. (Widening conversions Ceras does perform: {string.Join(", ", KnownCoercions)}.)");
                    }
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

        /// <summary>The widening conversions Ceras performs today, for the error message.</summary>
        static readonly string[] KnownCoercions =
        {
            "Int16 -> Int32", "Int32 -> Int64", "Int32 -> Single", "Int32 -> Double", "Single -> Double",
        };

        /// <summary>Names the type a field's stored type id refers to, for an actionable error message.</summary>
        static string DescribeTypeId(int typeId, Dictionary<int, OldTypeSchema> oldById)
        {
            if (WellKnownTypes.TryGetType(typeId, out var wellKnown))
                return TypeIdentity.GetCanonicalName(wellKnown);
            if (oldById != null && oldById.TryGetValue(typeId, out var old))
                return old.StableName;
            return $"type id {typeId}";
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

        static Dictionary<ulong, ulong> BuildEnumMap(OldTypeSchema old, Type currentType)
        {
            var map = new Dictionary<ulong, ulong>();
            var nameToCurrentValue = new Dictionary<string, ulong>(StringComparer.Ordinal);

            foreach (var f in currentType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!f.IsLiteral) continue;

                // Raw bits: a ulong enum member above long.MaxValue must not overflow here.
                ulong currentVal = EnumBits.ToRawBits(f.GetValue(null), currentType);

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
                if (nameToCurrentValue.TryGetValue(oldEv.Name, out ulong currentVal))
                    map[oldEv.Value] = currentVal;
            }
            return map;
        }
    }

    /// <summary>
    /// Resolves how a type's <c>[CerasConstructor]</c> gets its values while migrating. This is the
    /// conversion plan's construction half, so it lives next to <see cref="CtorArgumentPlan"/> and
    /// <see cref="TypeMigrationPlan"/> rather than in the reader: the reader only asks for a plan and
    /// invokes it.
    /// <para>
    /// The selection order is the generator's (<c>GetBestConstructor</c>): an explicit
    /// <c>[CerasConstructor]</c>, then a parameterless constructor, then the constructor with the most
    /// parameters the serialized members can satisfy — so both readers pick the same constructor for the
    /// same type. Resolved once per type per load and cached on the migration plan.
    /// </para>
    /// </summary>
    static class CtorArgumentPlanBuilder
    {
        /// <summary>Names the payload's field order for a constructor error, so the cause can be found.</summary>
        public static string DescribeOldFields(TypeMigrationPlan plan)
        {
            if (plan?.Mappings == null || plan.Mappings.Length == 0) return "none";
            return string.Join(", ", plan.Mappings.Select(m => m.OldField?.StableName).Where(n => n != null));
        }

        /// <summary>
        /// The plan for a type, or null when the type needs no constructor arguments. Cached on the
        /// migration plan, so a type with many instances still resolves its constructor once.
        /// </summary>
        public static CtorArgumentPlan Resolve(Type type, TypeMigrationPlan plan)
        {
            if (plan == null) return null;
            if (!plan.CtorArgsResolved)
            {
                plan.CtorArgsResolved = true;
                plan.CtorArgs = Build(type, plan);
            }
            return plan.CtorArgs;
        }

        static CtorArgumentPlan Build(Type type, TypeMigrationPlan plan)
        {
            // A value type never takes a [CerasConstructor] on the generated path (the generator only looks
            // for one on reference types), and an interface/abstract type has no instance to construct.
            if (type == null || type.IsValueType || type.IsAbstract || type.IsInterface) return null;
            if (plan.Mappings == null) return null;

            var members = new List<(FieldInfo Field, string StableName)>();
            foreach (var mapping in plan.Mappings)
            {
                if (mapping.NewField == null) continue;
                members.Add((mapping.NewField, StableNameOf(mapping.NewField)));
            }
            if (members.Count == 0) return null;

            var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (constructors.Length == 0) return null;

            var chosen = SelectConstructor(constructors, members);
            // A parameterless constructor (or none at all) needs no arguments: the caller runs it as before.
            if (chosen == null || chosen.GetParameters().Length == 0) return null;

            var parameters = chosen.GetParameters();
            var slotByField = new Dictionary<FieldInfo, int>();
            var mapped = new bool[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var match = FindMemberForParameter(parameters[i], members);
                if (match.Field == null) continue; // transient input: its declared default is passed

                if (!slotByField.TryAdd(match.Field, i))
                {
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"Cannot migrate '{TypeIdentity.GetCanonicalName(type)}': two parameters of its [CerasConstructor] both match the " +
                        $"member '{match.StableName}'. This is a construction-mapping bug; rename one of them.");
                }
                mapped[i] = true;
            }

            return new CtorArgumentPlan(chosen, parameters, slotByField, mapped);
        }

        internal static ConstructorInfo SelectConstructor(ConstructorInfo[] constructors, List<(FieldInfo Field, string StableName)> members)
        {
            // 1. Explicit [CerasConstructor]
            foreach (var ctor in constructors)
            {
                if (ctor.GetCustomAttribute<CerasConstructorAttribute>() != null) return ctor;
            }

            // 2. Parameterless (any accessibility) -> nothing to supply
            foreach (var ctor in constructors)
            {
                if (ctor.GetParameters().Length == 0) return null;
            }

            // 3. Heuristic: the most parameters the serialized members can all satisfy (this is what lets a
            //    primary-constructor type be constructed without the attribute).
            foreach (var ctor in constructors.OrderByDescending(c => c.GetParameters().Length))
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length == 0) continue;

                bool allMapped = true;
                foreach (var parameter in parameters)
                {
                    if (FindMemberForParameter(parameter, members).Field == null) { allMapped = false; break; }
                }
                if (allMapped) return ctor;
            }
            return null;
        }

        internal static (FieldInfo Field, string StableName) FindMemberForParameter(ParameterInfo parameter, List<(FieldInfo Field, string StableName)> members)
        {
            string wanted = NormalizeName(parameter.Name);
            if (wanted == null) return default;

            foreach (var member in members)
                if (NormalizeName(member.Field.Name) == wanted) return member;
            foreach (var member in members)
                if (NormalizeName(member.StableName) == wanted) return member;
            return default;
        }

        /// <summary>Mirrors <c>TypeHelpers.NormalizeName</c> in the generator: lower-case, one leading underscore dropped.</summary>
        internal static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var normalized = name.ToLowerInvariant();
            if (normalized.StartsWith("_", StringComparison.Ordinal)) normalized = normalized.Substring(1);
            return normalized;
        }

        /// <summary>The wire name of a field: <c>[CerasStableName]</c>, or the property name for an auto-property.</summary>
        internal static string StableNameOf(FieldInfo field)
        {
            var stable = field.GetCustomAttribute<CerasStableNameAttribute>();
            if (stable?.Name != null) return stable.Name;

            var name = field.Name;
            const string backingSuffix = ">k__BackingField";
            if (name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(backingSuffix, StringComparison.Ordinal))
                name = name.Substring(1, name.Length - backingSuffix.Length);
            return name;
        }
    }
}
