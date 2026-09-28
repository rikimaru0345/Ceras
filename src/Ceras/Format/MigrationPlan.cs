using System.Linq;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

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
        public int SlotForField(FieldInfo? field)
            => field != null && _slotByField.TryGetValue(field, out int slot) ? slot : -1;

        /// <summary>
        /// The member a parameter is fed by, or null when it is transient construction input. Read from
        /// the map in the other direction, on purpose: it is asked only to name a value that is about to be
        /// refused, so a reverse index would cost one array per type on every load to save a scan over a
        /// map that holds one entry per mapped parameter.
        /// </summary>
        FieldInfo? MemberForSlot(int slot)
        {
            foreach (var (field, mappedSlot) in _slotByField)
                if (mappedSlot == slot) return field;

            return null;
        }

        public bool IsMapped(int slot) => _mapped[slot];

        /// <summary>
        /// The value read for the member a parameter takes, or the reason this save cannot supply one.
        /// <para>
        /// A null is a value the payload genuinely carried, and it is <b>accepted for a reference-type
        /// parameter and for a <c>Nullable&lt;T&gt;</c> one</b>: those are the two shapes a save can
        /// record a null for, and passing it through is what a correct round-trip looks like.
        /// </para>
        /// <para>
        /// A null for a <b>non-nullable value type</b> is not a value the format can produce for it —
        /// and reflection does not stop it. <c>ConstructorInfo.Invoke</c> substitutes
        /// <c>default(T)</c>, <b>runs the constructor with it</b>, and returns null without raising
        /// anything: the load succeeds, the constructor body has already run, and the object is built
        /// from an argument the save never contained. That is silent data loss on a save that is
        /// otherwise perfectly readable, and it is the worst of the three answers available, because
        /// the value cannot be used and nothing says so. Refusing it here names the parameter, the
        /// member the value came from and the type, and it refuses it at the first point the value
        /// exists: before the constructor, before any member is assigned, and before the object can
        /// reach a deferred <c>[OnAfterDeserialize]</c>.
        /// </para>
        /// </summary>
        public object? AcceptMemberValue(int slot, object? value, OldFieldSchema? storedField)
        {
            if (value != null) return value;

            var parameter = Parameters[slot];
            var parameterType = parameter.ParameterType;
            if (!parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null)
                return null;   // a reference type and Nullable<T> each have a null of their own

            var field = MemberForSlot(slot);
            string owner = TypeIdentity.GetCanonicalNameOrUnknown(parameter.Member.DeclaringType);
            string member = field != null
                ? $"the member '{CtorArgumentPlanBuilder.StableNameOf(field)}'"
                : "a parameter no serialized member feeds";
            string stored = storedField != null
                ? $"the save file records it under the name '{storedField.StableName}' and the stored value is null"
                : "the save file records no member for it, and its value is null";

            throw new Ceras.Exceptions.CerasSchemaException(
                $"Cannot migrate '{owner}': the [CerasConstructor] parameter '{parameter.Name}' is a '{parameterType.Name}', which has no " +
                $"null, but the value read for {member} is null ({stored}). A value type is written inline, so this save holds no value " +
                $"for that position at all — passing it on would hand the constructor a default it never contained, and the load would " +
                $"report success. Keep the member's declared type a reference type or a Nullable<> on both sides, give the parameter a " +
                $"member whose stored value is never null, or load the save with the build that wrote it.");
        }

        /// <summary>
        /// The value for a parameter that no serialized member feeds: its declared default, or
        /// <c>default(T)</c> when it has none. Mirrors <c>RobustCodeEmitter.FormatDefaultArgument</c>,
        /// and is never null for a value-type parameter — the same reason
        /// <see cref="AcceptMemberValue"/> refuses one that arrived from the payload.
        /// </summary>
        public object? DefaultFor(int slot)
        {
            var parameter = Parameters[slot];
            var parameterType = parameter.ParameterType;

            if (parameter.HasDefaultValue)
            {
                object? declared = parameter.DefaultValue;
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
        public FieldInfo? NewField;  // null = removed (skip)
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
        /// <summary>
        /// The current CLR type the file's name resolved to, or null when this build has no such type:
        /// a type that was removed is read structurally and assigned nothing, so a plan without a
        /// current type is a normal plan rather than an incomplete one.
        /// </summary>
        public Type? CurrentType;
        /// <summary>Null for an enum: an enum has no members, only a value remapping.</summary>
        public FieldMapping[]? Mappings;
        public string[]? AddedFieldNames;
        public bool HasChanges;
        public MethodInfo? OnBeforeDeserialize;
        public CallbackArgumentKind OnBeforeDeserializeArgument;
        public MethodInfo? OnAfterDeserialize;
        public CallbackArgumentKind OnAfterDeserializeArgument;
        // Cached invokers that already carry the migration context for this type. Built by the
        // traversal engine once per plan (the migration context is identical for every instance),
        // and null for a type with no such callback.
        public Action<CerasSerializer, object>? BeforeInvoker;
        public Action<CerasSerializer, object>? AfterInvoker;
        /// <summary>
        /// Old raw bits -> current raw bits, matched by enum member name (see <see cref="EnumBits"/>).
        /// Null for everything but an enum that this build still has, and empty for an enum none of
        /// whose members the file recorded — both are "no remapping to apply", which the reader
        /// distinguishes from a plan that never had a map to consult.
        /// </summary>
        public Dictionary<ulong, ulong>? EnumValueMap;

        // How this type's [CerasConstructor] gets its values while migrating, resolved once per load
        // (the same resolution the generator does at compile time). Null when the type needs no
        // constructor arguments.
        public CtorArgumentPlan? CtorArgs;
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

            // The structural invariant (StructuralShape), in the section's own type-id order so a file with
            // more than one defect always reports the same one — a Dictionary's enumeration order is not part
            // of its contract, and a diagnostic that changed between runs of the same load would be its own
            // small defect. Two clauses first, because they are preconditions for everything below: an
            // entry that describes no payload at all cannot be reasoned about per member, and the verdict a
            // member walk would print for one ("no defined conversion between these two types") is a guess
            // about a type the file never described. The section parser has already asked these two — it is
            // the earliest door — and they are asked again here because this is the boundary every route into
            // the interpreter passes through: Load builds the plans, and the engine's own constructor builds
            // them when a caller hands it none, so a caller that drives the engine with schemas it built
            // itself gets the same refusal.
            var inIdOrder = InTypeIdOrder(oldById);
            RequireReadableEntries(inIdOrder, oldById.ContainsKey);

            foreach (var kv in oldByName)
            {
                var old = kv.Value;
                // Only index by the canonical stable name (not aliases)
                if (kv.Key != old.StableName) continue;
                if (result.ContainsKey(old.StableName)) continue;
                
                result[old.StableName] = BuildOne(old, oldById, registry);
            }

            // The third clause last, and that is deliberate: an argument identity nothing describes is a
            // fact about one position of an otherwise readable entry, and the walk inside BuildOne already
            // reports it with the member and the position inside its shape named, which is the more useful
            // message. What is left is exactly what that walk does not look at — a member the application
            // has since removed, whose payload is skipped so its element count is consumed with nothing to
            // say how wide an element is, and an entry nothing references. A malformed collection
            // description behind a removed member used to consume its count and then nothing, desynchronizing
            // every field after it. The entry is what is malformed, so the entry is what is checked — from
            // wherever it is reached, and on the skip direction as well as the read one, since both run
            // after this.
            RequireResolvableArguments(inIdOrder, oldById.ContainsKey);

            return result;
        }

        /// <summary>
        /// Refuses a section entry that describes no payload at all: a definition the format lays out
        /// without exactly that definition's argument count, or a definition identity the file itself does
        /// not describe. A <em>name</em> this build does not resolve is not a defect — an old file
        /// legitimately names types this build does not have, and the reader consumes those structurally —
        /// so the test is about identities, never about names.
        /// </summary>
        static void RequireReadableEntries(List<OldTypeSchema> inIdOrder, Func<int, bool> isDescribedTypeId)
        {
            foreach (var entry in inIdOrder)
            {
                var verdict = StructuralShape.InspectShape(entry);
                if (!verdict.IsDefect) verdict = StructuralShape.InspectDefinition(entry, isDescribedTypeId);
                if (verdict.IsDefect)
                    throw StructuralShape.UnreadableSchema(entry, verdict);
            }
        }

        /// <summary>
        /// Refuses a section entry that records an argument identity nothing describes, so the element,
        /// key, value or item it stands for has no readable representation.
        /// </summary>
        static void RequireResolvableArguments(List<OldTypeSchema> inIdOrder, Func<int, bool> isDescribedTypeId)
        {
            foreach (var entry in inIdOrder)
            {
                var verdict = StructuralShape.InspectArgumentIdentities(entry, isDescribedTypeId);
                if (verdict.IsDefect)
                    throw StructuralShape.UnreadableSchema(entry, verdict);
            }
        }

        /// <summary>The section's entries in type-id order, so a diagnostic does not depend on hash order.</summary>
        static List<OldTypeSchema> InTypeIdOrder(Dictionary<int, OldTypeSchema> oldById)
        {
            var ordered = new List<OldTypeSchema>(oldById.Values);
            ordered.Sort((a, b) => a.TypeId.CompareTo(b.TypeId));
            return ordered;
        }

        static TypeMigrationPlan BuildOne(OldTypeSchema old, Dictionary<int, OldTypeSchema> oldById, SchemaRegistry registry)
        {
            registry.TryResolveType(old.StableName, out Type? currentType);

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
                FieldInfo? newField = FindCurrentField(oldField, currentFieldMap);
                CoercionKind coerce = CoercionKind.None;

                if (newField != null)
                {
                    // Before anything is decided about the *value*: the two sides have to agree about the
                    // shape, at the member and at every declared argument below it.
                    RejectMovedArrayPayload(old.StableName, oldField.StableName, oldField.TypeId,
                        newField.FieldType, oldById, registry, "the member", 0);
                    RejectChangedStructuralArgument(old.StableName, oldField.StableName, oldField.TypeId,
                        newField.FieldType, oldById, registry, "the member", 0);

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
                        // The name the map found this member under, by the same rule the map used to
                        // record it. It used to be re-derived here as `sa?.Name ?? newField.Name`, which
                        // is a different rule: it skipped the auto-property peel, so a mapped
                        // auto-property was recorded as "<Name>k__BackingField" and therefore never
                        // matched the "Name" the current schema lists — reporting a member the file
                        // carried as one the file never had.
                        mappedStableNames.Add(StableNameOf(newField));
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

        /// <summary>
        /// The current members a file's field names are matched against, keyed by every name one of them
        /// may be recorded under: its wire name, plus each <c>[PreviousName]</c>.
        /// <para>
        /// The member set is <b>every instance field up the base chain</b>, not one
        /// <c>Type.GetFields(Public | NonPublic | Instance)</c> call. That call looks like it walks
        /// inheritance and does not: without <c>FlattenHierarchy</c> the runtime returns a base type's
        /// <em>public and protected</em> fields and stops there, because a private field is not a member
        /// of the derived type. An auto-property's backing field is private by definition, so **every
        /// inherited auto-property was missing from this map** — and both writers do serialize those
        /// members, because <c>MemberExplorer</c> and <c>ReflectionHelper.EnumerateMembers</c> both walk
        /// the chain with <c>DeclaredOnly</c> themselves. The file's value for one was therefore read
        /// structurally, discarded, and the object kept its declared default, with the load reporting
        /// success. That is the whole reason the walk is written out here rather than delegated.
        /// </para>
        /// <para>
        /// The walk is <b>derived-first and first-wins</b>, so a member declared in a more derived type
        /// claims a name before any base declaration of it is considered. That is the generator's own
        /// rule (<c>MemberExplorer</c> builds its <c>typeChain</c> derived-first and drops the base
        /// version of a shadowed auto-property through <c>claimedBackingFieldNames</c>), and it is also
        /// the order the single <c>GetFields</c> call already produced for the names it could see — so
        /// this changes <em>which</em> members are enumerated, not <em>who wins</em> a contested name.
        /// </para>
        /// </summary>
        static Dictionary<string, FieldInfo> BuildCurrentFieldMap(Type type)
        {
            var map = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

            foreach (var f in EnumerateInstanceFields(type))
            {
                // Resolved once and handed to all three rules below, so a backing field costs one
                // GetProperty rather than one per rule.
                var property = AssociatedPropertyOf(f);
                if (!IsSerializedMember(f, property)) continue;

                map.TryAdd(StableNameOf(f, property), f);

                foreach (var previous in PreviousNamesOf(f, property))
                    map.TryAdd(previous, f);
            }
            return map;
        }

        /// <summary>
        /// Every instance field of <paramref name="type"/> and of its base types, most derived first and
        /// stopping at <see cref="object"/>. No filtering: the caller decides which of them are members,
        /// so the shape of the walk and the rules about a member stay separate decisions.
        /// </summary>
        static IEnumerable<FieldInfo> EnumerateInstanceFields(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                foreach (var f in current.GetFields(flags))
                    if (f.DeclaringType == current)
                        yield return f;
        }

        /// <summary>
        /// Would a Ceras writer have put this field in the payload? The rules mirror the two that decide
        /// it on the write side, so the map can never offer a member the file's own writer left out:
        /// <c>TypeConfigDefaults.ApplyMemberAttributes</c> for the reflection schema, and
        /// <c>MemberExplorer.DiscoverMembers</c> for the generator.
        /// </summary>
        static bool IsSerializedMember(FieldInfo field, PropertyInfo? property)
        {
            if (field.GetCustomAttribute<ExcludeAttribute>() != null) return false;
            if (field.GetCustomAttribute<NonSerializedAttribute>() != null) return false;

            // The property is what the user wrote, so its attributes are the declaration's. That is true
            // on the write side for all three of these (MemberConfig carries AssociatedProperty and both
            // TypeConfigDefaults and MemberExplorer consult it), and it is the reason a [CerasStableName]
            // or an [Exclude] written above an auto-property is a real instruction rather than a comment.
            if (property != null)
            {
                if (property.GetCustomAttribute<ExcludeAttribute>() != null) return false;
                if (property.GetCustomAttribute<NonSerializedAttribute>() != null) return false;
            }

            // A compiler-generated field is a member only when a property stands in for it, which is the
            // same correction MemberConfig makes when it clears IsCompilerGenerated for an
            // AssociatedProperty. Everything else the compiler generates is not a member of anything — an
            // event's backing field, a cached delegate — and no file can name it. The base chain is what
            // brings these into view for the first time, so the rule has to be here: without it this walk
            // would start offering members that the writers never serialized.
            if (property == null && field.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
                return false;

            return true;
        }

        const string BackingFieldSuffix = ">k__BackingField";

        /// <summary>
        /// The auto-property <paramref name="field"/> is the backing field of, or null when it is not one
        /// (a real field, or a compiler-generated field no property stands in for). Mirrors
        /// <c>MemberConfig</c>'s constructor and <c>ReflectionHelper.AutoPropertyOf</c>: the property is
        /// looked for on the field's own declaring type, so a shadowing property in a derived type is not
        /// mistaken for the one the field belongs to.
        /// </summary>
        static PropertyInfo? AssociatedPropertyOf(FieldInfo field)
        {
            var name = field.Name;
            if (!name.StartsWith("<", StringComparison.Ordinal) || !name.EndsWith(BackingFieldSuffix, StringComparison.Ordinal))
                return null;

            var propertyName = name.Substring(1, name.Length - 1 - BackingFieldSuffix.Length);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            return field.DeclaringType?.GetProperty(propertyName, flags);
        }

        /// <summary>
        /// The one rule for the name a field is written and matched under: the <c>[CerasStableName]</c> of
        /// the field, else of its auto-property, else the auto-property's own name, else the field's.
        /// <para>
        /// It is a single method because the three places that need it had drifted into three answers. The
        /// map peeled the backing-field wrapper but read no property attribute; the recorded-name set
        /// (<c>BuildOne</c>, which decides <c>MigrationContext.AddedFields</c>) did not peel it at all, so
        /// an auto-property that <em>was</em> mapped was still reported as an added one; and the
        /// constructor matcher peeled it without the property either. A name that does not agree with the
        /// writer's is a value the migration cannot find — or, in the AddedFields case, one it reports as
        /// never having been read.
        /// </para>
        /// </summary>
        internal static string StableNameOf(FieldInfo field) => StableNameOf(field, AssociatedPropertyOf(field));

        static string StableNameOf(FieldInfo field, PropertyInfo? property)
        {
            var stable = field.GetCustomAttribute<CerasStableNameAttribute>()
                ?? property?.GetCustomAttribute<CerasStableNameAttribute>();
            if (stable?.Name != null) return stable.Name;

            var name = field.Name;
            if (name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(BackingFieldSuffix, StringComparison.Ordinal))
                name = name.Substring(1, name.Length - 1 - BackingFieldSuffix.Length);
            return name;
        }

        /// <summary>
        /// The other wire names this member has been recorded under, from the field's own
        /// <c>[PreviousName]</c> attributes and from its auto-property's. Nulls are dropped rather than
        /// keyed: a <c>[PreviousName]</c> with no name records alternatives only.
        /// </summary>
        static IEnumerable<string> PreviousNamesOf(FieldInfo field, PropertyInfo? property)
        {
            foreach (var previous in PreviousNamesOn(field)) yield return previous;
            if (property == null) yield break;
            foreach (var previous in PreviousNamesOn(property)) yield return previous;
        }

        static IEnumerable<string> PreviousNamesOn(MemberInfo member)
        {
            foreach (var previous in member.GetCustomAttributes<PreviousNameAttribute>())
            {
                if (previous.Name != null) yield return previous.Name;
                foreach (var alternative in previous.AlternativeNames)
                    if (alternative != null) yield return alternative;
            }
        }

        static FieldInfo? FindCurrentField(
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

        internal static CoercionKind DetectCoercion(int oldTypeId, Type? newFieldType)
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
        static string? DescribeTypeId(int typeId, Dictionary<int, OldTypeSchema> oldById)
        {
            if (WellKnownTypes.TryGetType(typeId, out var wellKnown))
                return TypeIdentity.GetCanonicalName(wellKnown);
            if (oldById != null && oldById.TryGetValue(typeId, out var old))
                return old.StableName;
            return $"type id {typeId}";
        }

        // ── Array payloads: a member, and every argument below it ────────────────────────────────────
        // A member declared as a concrete `T[]` and a member declared as any other reference type do not
        // start at the same byte, and the difference is the one thing a payload cannot recover from,
        // because it is the *position* of the first byte rather than a length:
        //
        //   * a member declared as a concrete `T[]` is written by ArrayFormatter as a biased element count
        //     and then the elements — no reference marker, no type value, and no object-cache
        //     registration. The schema section describes it as a `GenericInstantiation` entry whose
        //     definition id is `Array`, which is a *different* identity from the well-known id 16 that
        //     stands for the declared type `System.Array` (see Format/LAYOUT.md).
        //   * every other declared reference-typed member is reached through a reference wrapper, so a
        //     marker stands in front of it. `System.Object` and `System.Array` then carry the concrete type
        //     as a Type value; a class or a collection does not.
        //
        // The same two grammars meet at every *declared argument* of a member, which is why the check
        // walks down: a `List<int[]>` element is a marker and then a bare count, while a `List<object>`
        // element is a marker and then a Type value — and the member's own bytes are identical either way,
        // so nothing above the argument can see that anything moved. Every shape the format describes
        // structurally records its arguments as ordered type ids in the one place
        // (`OldTypeSchema.ArgTypeIds`) and reads each one by pairing that id with the current CLR argument,
        // so this one walk covers a collection element, a dictionary key and value, an array element, a
        // tuple item, a KeyValuePair and a Nullable alike.
        //
        // For either position the interpreter has to pick one of the two readings, and it picks the
        // *current* declaration's (`SchemaTraversalEngine.ReadAndApplyFields` decides the array case from
        // the new field type, and the collection readers take their CLR element type from the current
        // model), so the file's count was read as a reference marker and everything after it decoded from
        // the wrong offset. The load then failed late — in the exact-consumption check, reporting unread
        // bytes rather than the change that caused them, or in a bare reflection `ArgumentException` from
        // assigning a value of one shape to a field of another. The generated fast path is never reached for
        // such a file either (a changed member type changes the layout fingerprint), so there is no second
        // reader to disagree with: the file is simply not loadable.
        //
        // So the change is refused *here*, while the old-to-current plans are built and before a single
        // payload byte is interpreted. Both facts it needs are schema facts — the file's recorded identity
        // and this build's declared type — and only the file knows the first, so this is necessarily a
        // load-time check: nothing at generator time can inspect that file, and no compile-time diagnostic
        // can claim to have found it. Untouched by it: a position that is an array on both sides, a
        // *removed* member (skipped by its own file-side grammar, with no current declaration to disagree
        // with), a jagged array in an `object`/`System.Array` slot (one polymorphic value, not declared
        // array members), a renamed type (resolved through its alias, never compared as text), and any
        // change that leaves the array-ness alone. A change of a collection's argument *type* is not this
        // check's business either way — see the identity rule below, which is what decides it.

        /// <summary>
        /// The nesting bound of the walks below, the same one the type-value decoder uses
        /// (<c>TypeValueShapes.MaxNestingDepth</c>), so the two agree on how deep a described type may be.
        /// <para>
        /// Reaching it is a <b>refusal</b>, never a silent stop: an entry that names itself, or a tree deeper
        /// than the format's own bound, means the remainder of the member's shape has not been checked, and
        /// the only honest thing to do with an unchecked remainder is to say so. (An ordinary type cannot come
        /// near this: it takes one closed generic per level, so 64 levels of <c>List&lt;…&gt;</c> nesting.)
        /// </para>
        /// </summary>
        const int MaxArrayShapeDepth = 64;

        /// <summary>What the file's own schema says about one position of a declared member's shape.</summary>
        enum StoredPosition
        {
            /// <summary>A declared concrete array: a <c>GenericInstantiation</c> entry of definition
            /// <c>Array</c>, which is the writer's own record that <c>ArrayFormatter</c> wrote this position
            /// with no reference marker in front of it. The schema section never holds the well-known id of
            /// the declared type <c>System.Array</c>, so that identity cannot be mistaken for it.</summary>
            ArrayPayload,

            /// <summary>A type this build can name, either a well-known id or an entry the manifest resolves
            /// (a rename included, because the entry carries its aliases).</summary>
            Current,

            /// <summary>
            /// An entry the file describes and that nothing in this build's manifest resolves — which in
            /// practice means a name only the configured <c>TypeBinder</c> can resolve, an external type bound
            /// by name. A type that is gone *entirely* never reaches here: <c>BuildIdToType</c> resolves every
            /// id the file's schema section describes before the plans are built and refuses an unresolvable
            /// name there, still with no payload interpreted, naming the type.
            /// <para>
            /// The reader is written for this case — it consumes such a position structurally, assigning
            /// nothing, so the stream stays aligned and the element becomes the current type's default — so
            /// it is left alone here, and the walk does not descend into it either, because there is no
            /// current type to pair the next level with.
            /// </para>
            /// </summary>
            Removed,

            /// <summary>
            /// An id nothing describes: not a well-known id, and not an entry in the file's own schema
            /// section either. Nothing says how such a position is written, and the reader treats an
            /// unrecognised id as a value of no bytes — which is the silent desync these checks exist to
            /// prevent. A real writer's id always comes from its own manifest, so this is a doctored or
            /// truncated file, and it is refused.
            /// </summary>
            Undescribable,
        }

        /// <summary>
        /// Classifies one position of a member's declared shape from the file's own schema, and names the
        /// current CLR type when there is one. Read by both walks so they cannot disagree about what the
        /// file said, and so neither has a path that quietly treats "I don't know" as "it's fine".
        /// </summary>
        static StoredPosition ClassifyStored(int typeId, Dictionary<int, OldTypeSchema> oldById, SchemaRegistry registry,
            out Type? currentType)
        {
            if (IsDeclaredArrayPayload(typeId, oldById))
            {
                currentType = typeof(Array);      // the declared type of an array position, for the walk below
                return StoredPosition.ArrayPayload;
            }

            if (WellKnownTypes.TryGetType(typeId, out var wellKnown))
            {
                currentType = wellKnown;
                return StoredPosition.Current;
            }

            // A user type: described by the file, and either this build has it (a rename resolves through the
            // entry's aliases) or it has been removed.
            if (oldById != null && oldById.TryGetValue(typeId, out var old))
            {
                currentType = registry.TryResolveType(old.StableName, out var resolved) ? resolved : null;
                return currentType == null ? StoredPosition.Removed : StoredPosition.Current;
            }

            currentType = null;
            return StoredPosition.Undescribable;
        }

        /// <summary>How a declared array is written, for the error message.</summary>
        const string ArrayPayloadShape = "a biased element count and then its elements, with no reference marker in front of them";

        /// <summary>How anything else the writer wraps is written, for the error message.</summary>
        const string MarkedPayloadShape = "a reference marker in front of the value";

        /// <summary>
        /// True when the file's member, argument or nested argument at this position is a *declared concrete
        /// array*: a manifest entry of kind <see cref="SchemaTypeKind.GenericInstantiation"/> whose definition
        /// id is <c>Array</c>. That entry is the writer's own record that the position was written by
        /// <c>ArrayFormatter</c>, i.e. with no reference marker in front of it — the schema section never
        /// holds the well-known id of the declared type <c>System.Array</c>, so that identity cannot be
        /// mistaken for it.
        /// </summary>
        static bool IsDeclaredArrayPayload(int typeId, Dictionary<int, OldTypeSchema> oldById)
            => oldById != null
            && oldById.TryGetValue(typeId, out var old)
            && old.Kind == SchemaTypeKind.GenericInstantiation
            && old.DefinitionTypeId == WellKnownTypes.Array;

        /// <summary>
        /// True when a position declared as <paramref name="type"/> is written behind a reference marker.
        /// <para>
        /// A declared array never is (<c>ArrayFormatter</c> writes it by value, which is the whole of
        /// <see cref="IsDeclaredArrayPayload"/>) and neither does a value type. Of the reference types,
        /// <c>string</c> and <c>System.Type</c> are the two the writer emits as a bare payload value — a
        /// string-table index and a <c>Type</c> value — so they carry no marker either; every other
        /// reference-typed declaration is reached through a <c>ReferenceFormatter</c>, including a sealed
        /// class (<c>ReferenceFormatter_KnownSealedType</c>), which writes the marker and then the value.
        /// That is the same set the writer's own formatter selection produces
        /// (<c>RobustCodeEmitter.GetConcreteFormatterType</c> and <c>CerasSerializer.GetReferenceFormatter</c>),
        /// so a future change to that selection has to change this predicate with it.
        /// </para>
        /// <para>
        /// Not to be confused with <c>SchemaTraversalEngine.CarriesReferenceMarker</c>, which answers a
        /// different question about a particular payload value; this one asks what the writer would do for a
        /// <em>declared</em> type, which is a property of the type alone.
        /// </para>
        /// </summary>
        static bool WrittenBehindReferenceMarker(Type? type)
            => type != null
            && !type.IsValueType
            && !type.IsArray
            && type != typeof(string)
            && type != typeof(Type);

        /// <summary>
        /// Refuses a mapped member, or one of its declared arguments, whose array-ness differs between the
        /// file and this build — <c>int[] Slot</c> becoming <c>Array Slot</c>/<c>object Slot</c>/a collection
        /// or a class, or the reverse, and the same change at any argument depth
        /// (<c>List&lt;int[]&gt;</c> against <c>List&lt;object&gt;</c>, a dictionary's value, a nested
        /// <c>List&lt;List&lt;int[]&gt;&gt;</c>). It is deliberately narrow:
        /// <list type="bullet">
        /// <item>it fires only when the array-ness differs <em>and</em> the reference marker genuinely moves,
        /// so a change whose payload shape differs while both sides already agree about the marker (an array
        /// against a scalar) keeps the existing "no defined conversion" refusal, which names the two types
        /// and is accurate for it;</item>
        /// <item>it compares <em>array-ness</em>, and nothing else. Whether an argument's <em>type</em> is
        /// still the same one is a separate question with its own rule
        /// (<see cref="RejectChangedStructuralArgument"/>), which refuses it — so nothing is claimed here
        /// about a widening or any other argument evolution, only that this check is not what refuses
        /// one;</item>
        /// <item>both sides are compared by identity — the file's recorded type id and the schema entry it
        /// resolves to, against the current declaration's <see cref="FieldInfo.FieldType"/> — with no name
        /// comparison, so a rename cannot reach it.</item>
        /// </list>
        /// </summary>
        static void RejectMovedArrayPayload(string ownerName, string memberName, int oldTypeId, Type? newType,
            Dictionary<int, OldTypeSchema> oldById, SchemaRegistry registry, string position, int depth)
        {
            var stored = ClassifyStored(oldTypeId, oldById, registry, out var storedType);
            RejectUndescribablePosition(ownerName, memberName, oldTypeId, position, stored);
            bool oldIsArray = stored == StoredPosition.ArrayPayload;
            bool newIsArray = newType != null && newType.IsArray;

            if (oldIsArray != newIsArray)
            {
                // The non-array side decides whether a marker is involved at all. When it is not (a value
                // type, or string/Type, which the writer also emits without a marker) the two sides already
                // agree about where the value starts, and the shape difference is a different refusal. A
                // removed type is the third way out: nothing here can say where its members ended, and the
                // reader consumes them structurally (see <see cref="StoredPosition.Removed"/>), so there is
                // no marker to compare.
                Type? unmarked = oldIsArray ? newType : storedType;
                if (WrittenBehindReferenceMarker(unmarked))
                {
                    string oldName = DescribeTypeId(oldTypeId, oldById) ?? $"type id {oldTypeId}";
                    string newName = TypeIdentity.GetCanonicalNameOrUnknown(newType);
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"Cannot migrate the field '{memberName}' of '{ownerName}': at {position} the array payload " +
                        $"moved. The save file describes {position} as '{oldName}', which the writer stores as " +
                        $"{(oldIsArray ? ArrayPayloadShape : MarkedPayloadShape)}, while the current type declares it '{newName}', which it " +
                        $"stores as {(newIsArray ? ArrayPayloadShape : MarkedPayloadShape)}. The reference marker moves, so the stored value is " +
                        $"not a '{newName}' and the current declaration is not a '{oldName}': there is no byte offset at which this file's " +
                        "value can be read as the current one, and following the current declaration's grammar instead desynchronizes every " +
                        "member after it. Ceras therefore refuses the load here, while the old-to-current schema is validated and before the " +
                        "payload is read. Only the save file says how the old position was written, so this is a load-time check — nothing at " +
                        "compile time can inspect that file and report it.\nTo migrate this data, keep the position that can hold the stored " +
                        $"grammar: give it a stable name of its own ([CerasStableName], or a [PreviousName] on the member that should receive the " +
                        $"old name) so this file's field maps onto it, and declare '{newName}' as a second member you build from it in your own " +
                        $"code — an [OnAfterDeserialize] migration step, or a hand-written one. One declaration cannot carry both grammars, so " +
                        $"there is no declaration that loads the stored value straight into '{newName}'.");
                }
            }

            DescendIntoArguments(ownerName, memberName, oldTypeId, newType, oldById, registry, position, depth,
                (owner, member, argId, argType, where, d) => RejectMovedArrayPayload(owner, member, argId, argType,
                    oldById, registry, where, d));
        }

        // ── Structural arguments: the same current CLR type on both sides, or nothing ────────────────
        // A member's *declared* type may legitimately change family-wide (`List<int>` becoming `List<long>`
        // keeps the definition the file recorded, so TypeIdsCompatible accepts it), and the reader has an
        // element-coercion path written for exactly that. But the interpreter does not get there: a plan's
        // type comes from resolving the *file's* closed name, `CurrentTypeOf` prefers it over the current
        // member's declared type, and the reader then constructs the file's closed generic — which
        // `FieldInfo.SetValue` refuses, with a bare `ArgumentException` naming a type that is in neither
        // model. So the change fails late, loudly and unhelpfully instead of being decided up front.
        //
        // The owner direction is to fail early and say what to do, not to turn a coincidence into a
        // migration feature. So a structural argument change is refused here, and the only thing that
        // makes an argument position acceptable is that both sides resolve to the **same current CLR
        // type**. That single rule is what keeps the cases that work working, without an allow-list:
        //   * a renamed argument type resolves through its `[PreviousTypeName]` alias to this build's type,
        //     which is the same type the current declaration has, so `List<Ping>` → `List<Pong>` passes;
        //   * an unchanged argument resolves to itself.
        //
        // A widening does **not** pass. `List<int>` → `List<long>` is two different types, and although the
        // reader has an element-coercion path written for it, nothing can reach it: the interpreter
        // resolves the member to the *file's* closed generic (see `CurrentTypeOf`) and constructs that, so
        // the coercion would never be applied. So there is no working conversion to preserve here, and
        // enabling one would be a migration feature nobody asked for; the refusal names the direct-member
        // widening set instead, so a caller can see what does convert.
        //
        // A *direct* member's widening (`int Mana` → `long Mana`) is a different path and stays supported:
        // it is a scalar, not a closed generic, so this walk never sees it, and `DetectCoercion` handles
        // it. An array member's element widening (`int[]` → `long[]`) is likewise untouched — an array is
        // not a generic type in reflection, so the walk returns at it, and `ReadArray` applies
        // `DetectCoercion` to the element, which *is* reachable. Those two are already defined and already
        // work, which is the line this draws: nothing that converts today is taken away.
        //
        // Two boundaries, both deliberate:
        //   * An argument the file's schema describes but this build cannot resolve is *silent* here. There
        //     is no current type to compare, and a removed argument type is the reader's own business
        //     (`CurrentTypeOf` falls back to the current member's declared type, and the collection shape
        //     readers handle what is left).
        //   * Arity is not compared. The definition is checked above, so a mapped member's closed generic
        //     always has this build's arity; the pairings are taken up to the shorter of the two, which is
        //     what the collection readers themselves do.

        /// <summary>
        /// Refuses a mapped member whose declared type is a closed generic whose <em>arguments</em> no longer
        /// name the same current CLR types they did. The two grammars a position is written by are decided by
        /// the file's own identity for it and by this build's declaration for it, so a position whose identity
        /// changed has no common reading — and, for a collection argument, the failure that replaced this
        /// check was a bare reflection exception rather than a desync. See the block comment above.
        /// </summary>
        static void RejectChangedStructuralArgument(string ownerName, string memberName, int oldTypeId, Type? newType,
            Dictionary<int, OldTypeSchema> oldById, SchemaRegistry registry, string position, int depth)
        {
            DescendIntoArguments(ownerName, memberName, oldTypeId, newType, oldById, registry, position, depth,
                (owner, member, argId, argType, where, d) =>
                {
                    var arg = ClassifyStored(argId, oldById, registry, out var argStoredType);
                    RejectUndescribablePosition(owner, member, argId, where, arg);

                    // A position this build has no type for is left alone and not descended into: there is
                    // no current type to pair its own arguments with, and the reader consumes it structurally,
                    // so the stream stays aligned — see <see cref="StoredPosition.Removed"/>.
                    if (arg != StoredPosition.Current) return;
                    if (argStoredType == argType)
                    {
                        // Same type at this level, so the question is the same one level down. (The order
                        // matters: the refusal is a *return of the whole load*, so descending has to happen
                        // on the path that did not throw.)
                        RejectChangedStructuralArgument(owner, member, argId, argType, oldById, registry, where, d);
                        return;
                    }

                    string oldName = DescribeTypeId(argId, oldById) ?? $"type id {argId}";
                    string newName = TypeIdentity.GetCanonicalNameOrUnknown(argType);
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"Cannot migrate the field '{member}' of '{owner}': at {where} the save file and this build do not name the " +
                        $"same type. The file records '{oldName}' there, which resolves to '{TypeIdentity.GetCanonicalNameOrUnknown(argStoredType)}' " +
                        $"in this build, while the current type declares '{newName}'. A structural argument is written by its declared type, " +
                        "so the stored payload is laid out for the one and the interpreter would read it as the other; there is no conversion " +
                        "between them that Ceras performs, and reading on would either desynchronize the rest of the save or fail while " +
                        "assigning a value of one shape to a member of the other. Ceras therefore refuses the load here, while the " +
                        "old-to-current schema is validated and before the payload is read. Which argument a save recorded, and as what, is " +
                        "knowable only from the file, so this is a load-time check — nothing at compile time can inspect that file and report " +
                        "it.\nKeep the argument type unchanged, or make both sides the same type: renaming a type is fine (this build " +
                        "resolves the file's name through [PreviousTypeName] and finds the very type the current declaration has), but changing " +
                        $"what a position holds is not. Ceras's defined conversions apply to a *direct* member, whose type the file and the " +
                        $"current model both name as a scalar: {string.Join(", ", KnownCoercions)}. A collection's argument is a different case " +
                        "and is not among them.");
                });
        }

        /// <summary>
        /// Walks the ordered arguments the file recorded for this position and hands each one to
        /// <paramref name="onArgument"/>, paired with the current declaration's argument of the same index —
        /// which is exactly the pairing every structural reader performs. Shared by the two walks so they
        /// cannot drift on where the walk stops, and so that stopping is one decision rather than two.
        /// <para>
        /// It stops — and says so — in three cases, none of them silent. The nesting bound is *reached* rather
        /// than stepped over, because past it the remainder of the member's shape is unchecked and an unchecked
        /// remainder is not a result. A position the current declaration is not generic for, or whose file entry
        /// is not a closed generic, has no arguments to pair. And an arity mismatch pairs only what both sides
        /// have, which is what the collection readers themselves do — the definition is compared above, so a
        /// mapped member's closed generic always has this build's arity and this cannot be reached.
        /// </para>
        /// </summary>
        static void DescendIntoArguments(string ownerName, string memberName, int oldTypeId,
            Type? newType, Dictionary<int, OldTypeSchema> oldById, SchemaRegistry registry, string position, int depth,
            Action<string, string, int, Type, string, int> onArgument)
        {
            if (newType == null || !newType.IsGenericType) return;      // an array is not a generic type
            if (oldById == null || !oldById.TryGetValue(oldTypeId, out var old)) return;
            if (old.Kind != SchemaTypeKind.GenericInstantiation) return;

            if (depth >= MaxArrayShapeDepth)
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot migrate the field '{memberName}' of '{ownerName}': the declared shape at {position} nests deeper than the " +
                    $"{MaxArrayShapeDepth} levels Ceras checks, so the rest of it has not been verified. A file that nests that deeply is " +
                    "either hand-assembled or self-referential (a schema entry naming itself as its own argument reaches the same bound), and " +
                    "in both cases the remainder of this member's shape is unknown, which is not something to load through. Lower the nesting " +
                    "of the type, or load the save with the build that wrote it.");

            var currentArgs = newType.GetGenericArguments();
            int shared = Math.Min(old.ArgTypeIds.Length, currentArgs.Length);
            for (int i = 0; i < shared; i++)
                onArgument(ownerName, memberName, old.ArgTypeIds[i], currentArgs[i], $"{position} > argument {i}", depth + 1);
        }

        /// <summary>
        /// Refuses a position the file's own schema does not describe: an id that is neither a well-known type
        /// nor an entry in the file's schema section, so nothing says how its payload is written. The reader
        /// treats an unrecognised id as a value of zero bytes, which is precisely the silent desync these
        /// checks exist to prevent, and a real writer only ever emits ids from its own manifest — so this is a
        /// doctored or truncated file rather than an evolution, and refusing it is the honest answer.
        /// </summary>
        static void RejectUndescribablePosition(string ownerName, string memberName, int oldTypeId, string position,
            StoredPosition stored)
        {
            if (stored != StoredPosition.Undescribable) return;

            throw new Ceras.Exceptions.CerasSchemaException(
                $"Cannot migrate the field '{memberName}' of '{ownerName}': at {position} the save file records type id {oldTypeId}, which " +
                "is neither a well-known type nor an entry in its own schema section, so nothing describes how that value is written. A " +
                "reader has to assume it takes no bytes there, which desynchronizes every member after it — so Ceras refuses the load while " +
                "validating the old-to-current schema, before the payload is read. This is not a schema change: a real writer only records " +
                "ids from its own manifest. Load the save with the build that wrote it, or treat the file as damaged.");
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
        static MethodInfo? FindCallback(Type type, Type attributeType, out CallbackArgumentKind kind)
        {
            kind = CallbackArgumentKind.None;
            if (type == null) return null;

            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                MethodInfo? best = null;
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

        /// <summary>
        /// Old raw bits -> current raw bits for one enum, matched by the name the file recorded.
        /// <para>
        /// Three names can answer for a current member, in this precedence:
        /// </para>
        /// <list type="number">
        /// <item>its wire name — <c>[CerasStableName]</c>, or the C# member name when it has none. This is
        /// what a current writer records, so it is the authoritative spelling and never yields to
        /// anything below it.</item>
        /// <item>its current C# member name, when that differs from the wire name. Every file written
        /// before the schema recorded the wire name carries the C# name, so a member that was only
        /// renumbered — or renamed while keeping its <c>[CerasStableName]</c> — still matches. A member
        /// whose C# name changed <em>and</em> whose wire name changed is not reachable this way: the old
        /// name has to be declared as a <c>[PreviousName]</c> alias, because nothing in the file records
        /// it.</item>
        /// <item>its <c>[PreviousName]</c> aliases, with the same caveat: an explicit alias is the only
        /// way to name a spelling this build cannot derive.</item>
        /// </list>
        /// <para>
        /// Two current members can therefore answer to one name — a real member literally called
        /// <c>B</c> and another pinned to <c>[CerasStableName("B")]</c>, or an alias that collides with
        /// a member. That is ambiguous by construction and the higher-precedence spelling wins rather
        /// than the order the members happen to be declared in; rejecting such collisions outright is
        /// separate work (see TODO.md, "reject duplicate stable names and ambiguous aliases").
        /// </para>
        /// </summary>
        static Dictionary<ulong, ulong> BuildEnumMap(OldTypeSchema old, Type currentType)
        {
            var map = new Dictionary<ulong, ulong>();
            var nameToCurrentValue = new Dictionary<string, ulong>(StringComparer.Ordinal);

            var members = new List<(FieldInfo Field, string WireName, ulong Bits)>();
            foreach (var f in currentType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!f.IsLiteral) continue;

                // Raw bits: a ulong enum member above long.MaxValue must not overflow here.
                members.Add((f, EnumWireName.Of(f, f.Name), EnumBits.ToRawBits(f.GetValue(null), currentType)));
            }

            // 1. The wire name each member is recorded and read under.
            foreach (var m in members)
                nameToCurrentValue[m.WireName] = m.Bits;

            // 2. The current C# member name, for the files that recorded that instead. It only fills a
            //    name no member claims, so this can never change how a resolvable name resolves.
            foreach (var m in members)
                nameToCurrentValue.TryAdd(m.Field.Name, m.Bits);

            // 3. Explicit aliases for a name this build cannot derive.
            foreach (var m in members)
                foreach (var pn in m.Field.GetCustomAttributes<PreviousNameAttribute>())
                {
                    if (pn.Name != null) nameToCurrentValue.TryAdd(pn.Name, m.Bits);
                    foreach (var alt in pn.AlternativeNames)
                        if (alt != null)
                            nameToCurrentValue.TryAdd(alt, m.Bits);
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
        /// <summary>
        /// Names the payload's field order for a constructor error, so the cause can be found. "none"
        /// is the answer for a plan with no mappings, which includes a type this build cannot resolve:
        /// its constructor then has nothing to report about.
        /// </summary>
        public static string? DescribeOldFields(TypeMigrationPlan? plan)
        {
            if (plan?.Mappings == null || plan.Mappings.Length == 0) return "none";
            return string.Join(", ", plan.Mappings.Select(m => m.OldField?.StableName).Where(n => n != null));
        }

        /// <summary>
        /// The plan for a type, or null when the type needs no constructor arguments. Cached on the
        /// migration plan, so a type with many instances still resolves its constructor once.
        /// <para>
        /// Both arguments are nullable because both are nullable in the question: the reader asks for
        /// the plan of the *current* type a value decodes into, and a type this build does not have
        /// (a removed one) has no constructor to describe. The resolved flag is separate from the
        /// cached plan so that "resolved to no constructor" is remembered too.
        /// </para>
        /// </summary>
        public static CtorArgumentPlan? Resolve(Type? type, TypeMigrationPlan? plan)
        {
            if (plan == null) return null;
            if (!plan.CtorArgsResolved)
            {
                plan.CtorArgsResolved = true;
                plan.CtorArgs = Build(type, plan);
            }
            return plan.CtorArgs;
        }

        static CtorArgumentPlan? Build(Type? type, TypeMigrationPlan plan)
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

        internal static ConstructorInfo? SelectConstructor(ConstructorInfo[] constructors, List<(FieldInfo Field, string StableName)> members)
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

        /// <summary>
        /// The member a parameter is fed by, matched by name only — exactly the generator's rule
        /// (<c>GetBestConstructor</c>), so both readers choose the same constructor for the same type.
        /// The result is <c>default</c> for a parameter nothing matches: a transient construction input,
        /// which then receives its own declared default.
        /// </summary>
        internal static (FieldInfo? Field, string? StableName) FindMemberForParameter(ParameterInfo parameter, List<(FieldInfo Field, string StableName)> members)
        {
            string? wanted = NormalizeName(parameter.Name);
            if (wanted == null) return default;

            foreach (var member in members)
                if (NormalizeName(member.Field.Name) == wanted) return member;
            foreach (var member in members)
                if (NormalizeName(member.StableName) == wanted) return member;
            return default;
        }

        /// <summary>
        /// Mirrors <c>TypeHelpers.NormalizeName</c> in the generator: lower-case, one leading
        /// underscore dropped. Reflection annotates a parameter's and a field's name as maybe-null
        /// (metadata-only parameters carry none), and a null normalizes to null rather than to an empty
        /// string, so an unnamed parameter matches no member instead of matching every other unnamed one.
        /// </summary>
        internal static string? NormalizeName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var normalized = name.ToLowerInvariant();
            if (normalized.StartsWith("_", StringComparison.Ordinal)) normalized = normalized.Substring(1);
            return normalized;
        }

        /// <summary>
        /// The wire name of a field, by the one rule that defines it. Delegates rather than repeating it:
        /// a constructor parameter is matched against a member's <em>wire</em> name as a fallback after
        /// its C# name, and a <c>[CerasStableName]</c> on an auto-property's property replaces that name
        /// for the generator too — so a matcher that ignored it would pick a different constructor than
        /// the generated reader does.
        /// </summary>
        internal static string StableNameOf(FieldInfo field) => MigrationPlanBuilder.StableNameOf(field);
    }
}
