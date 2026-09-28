
// ReSharper disable ArgumentsStyleOther
// ReSharper disable ArgumentsStyleNamedExpression
namespace Ceras.Formatters
{
    using Ceras.Helpers;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
#if NETCOREAPP3_0_OR_GREATER
    using System.Runtime.Loader;
#endif
    using System.Threading;
    using Ceras;

    // Some types are banned from serialization
    // and instead of throwing crazy errors that don't help the user at all, we give an explanation
    //
    // There is only once place where we can be 100% sure that every type will eventually pass through: the type meta data entries
    // so that's where we're calling it from
    //
    // A ban has two unrelated sources and they are deliberately kept apart, because only one of
    // them is a mistake Ceras has to complain about:
    //
    //   * A shape ban - the *shape* has no wire representation at all (a delegate, an enumerator,
    //     reflection metadata, ...). Neither a member of that type nor a value of that type can be
    //     written, so dropping it produced a save that looked fine and came back null. See
    //     <see cref="IsBannedShape(Type, out string)"/>.
    //   * A [Exclude] attribute on the type itself. That is the user's own decision that the
    //     type is not for serialization, so a *member* of such a type is still simply not part
    //     of the schema (that is what the attribute asks for). A *value* of such a type in a
    //     reference slot is still an error: the slot says nothing about it, and writing null
    //     would silently lose the value.
    static class BannedTypes
    {
        struct BannedType
        {
            public readonly Type Type;
            public readonly string FullName;

            public readonly string BanReason;
            public readonly bool AlsoCheckInherit;

            public readonly Func<Type, bool> CustomIsBannedCheck;

            public BannedType(Type type, string banReason, bool alsoCheckInherit)
            {
                Type = type;
                FullName = null!;
                CustomIsBannedCheck = null!;

                BanReason = banReason;
                AlsoCheckInherit = alsoCheckInherit;
            }

            public BannedType(string fullName, string banReason, bool alsoCheckInherit)
            {
                Type = null!;
                FullName = fullName;
                CustomIsBannedCheck = null!;

                BanReason = banReason;
                AlsoCheckInherit = alsoCheckInherit;
            }

            public BannedType(Func<Type, bool> customCheck, string banReason)
            {
                Type = null!;
                FullName = null!;
                CustomIsBannedCheck = customCheck;

                BanReason = banReason;
                AlsoCheckInherit = false;
            }
        }

        static List<BannedType> _bannedTypes = new List<BannedType>();


        static BannedTypes()
        {
            BanBase(typeof(System.Collections.IEnumerator), "Enumerators are potentially infinite, and also most likely have no way to be instantiated at deserialization-time. If you think this is a mistake, report it as a github issue.");

            // Delegates/Actions/Funcs/Events
            BanBase(typeof(Delegate), "Serialization of delegates, actions, functions, or events is not supported by default. These types contain internal state and references to methods that cannot be reliably restored.");

            // Reflection types
            BanCustom(t => typeof(System.Reflection.MemberInfo).IsAssignableFrom(t) && t != typeof(Type) && (t.FullName == null || !t.FullName.StartsWith("System.RuntimeType")), "Serialization of reflection metadata (MethodInfo, PropertyInfo, etc) is not supported.");
            BanBase(typeof(System.Reflection.Assembly), "Serialization of Assembly objects is not supported.");

            string reasonExploit = "This type can be exploited when deserializing malicious data";


            Ban(typeof(System.IO.FileSystemInfo), reasonExploit);
            Ban(typeof(System.Data.DataSet), reasonExploit);
            Ban("System.Management.IWbemClassObjectFreeThreaded", reasonExploit);


#if NETFRAMEWORK
            BanBase(typeof(System.Security.Claims.ClaimsIdentity), reasonExploit);
            Ban(typeof(System.Security.Principal.WindowsIdentity), reasonExploit);
            Ban(typeof(System.CodeDom.Compiler.TempFileCollection), reasonExploit);
            Ban(typeof(System.Security.Policy.HashMembershipCondition), reasonExploit);
#endif

            // The owner's collection policy. A *custom* check rather than a base-type ban, because the
            // rule is not "derives from List<T>": the exact List<T> and Dictionary<TKey,TValue> are
            // supported shapes and a base ban would take them down with their subclasses. One entry also
            // covers all four refused families, so the reason is the one sentence the policy states and
            // the same reason the robust bake and the source generator give.
            //
            // Placement: after the exploit-prone bans and before the engine-object ban, so a type some
            // earlier entry already refuses keeps that entry's reason (the scan returns the first
            // match). Cost is unaffected either way: IsBannedCached below memoizes the *verdict* per type
            // rather than the list, and on the uncached path the check walks a base chain that is almost
            // always two non-generic entries.
            BanCustom(Ceras.Format.UnsupportedCollectionPolicy.IsUnsupported, Ceras.Format.UnsupportedCollectionPolicy.BanReason);

            // Ban all 'native' unity objects by banning the base type.
            // Component, MonoBehaviour, GameObject, Transform, ...
            // If there are requests, we can add specialized formatters for them.
            // Note: The types handled in the UnityAddon are structs and don't inherit from UnityEngine.Object
            BanCustom(IsUnityNativeType,
            reason: "Native Unity objects cannot be serialized because they're not real C#/.NET objects. It is possible to create specialized formatters for some situations so Ceras can handle those objects, but it's not really possible to do this in a general and reliable way.");
        }

        static bool IsUnityNativeType(Type t)
        {
            const string unityObject = "UnityEngine.Object";

            // The walk ends at the root, which has no base type; that null is the loop's end state, so
            // the cursor is the nullable one rather than the argument it started from.
            Type? current = t;
            while (current != null)
            {
                if(current.FullName == unityObject)
                    return true;
                
                current = current.BaseType;
            }

            return false;
        }

        static void Ban(Type type, string reason) => _bannedTypes.Add(new BannedType(type, reason, false));
        static void Ban(string fullName, string reason) => _bannedTypes.Add(new BannedType(fullName, reason, false));
        static void BanBase(Type type, string reason) => _bannedTypes.Add(new BannedType(type, reason, true));
        static void BanBase(string fullName, string reason) => _bannedTypes.Add(new BannedType(fullName, reason, true));
        static void BanCustom(Func<Type, bool> isBanned, string reason) => _bannedTypes.Add(new BannedType(isBanned, reason));


        internal static void ThrowIfBanned(Type type)
        {
            if (IsBanned(type, out var ban))
                throw new BannedTypeException($"The type '{type.FullName}' cannot be serialized, please mark the field/property that caused this Type to be included with the [Exclude] attribute or filter it out using the 'ShouldSerialize' callback. Specific reason for this type being banned: \"{ban.BanReason}\". You should open an issue on GitHub or join the Discord server for support.");
        }

        internal static bool IsBanned(Type type) => IsBanned(type, out _);

        /// <summary>
        /// True when the type is banned because of its *shape* (delegate, enumerator, reflection
        /// metadata, exploit-prone framework type, native engine object) and not merely because it
        /// carries <c>[Exclude]</c>. Only a shape ban makes a member an error: a member of a type
        /// the user excluded on purpose is simply absent from the payload, which is exactly what
        /// the attribute asked for.
        /// </summary>
        internal static bool IsBannedShape(Type type, out string reason)
        {
            if (TryGetBannedShape(type, out var ban))
            {
                reason = ban.BanReason;
                return true;
            }

            reason = null!;
            return false;
        }

        /// <inheritdoc cref="IsBanned(Type)"/>
        internal static bool TryGetBanned(Type type, out string reason)
        {
            if (IsBannedCached(type))
            {
                // Banned is the error path and is about to be reported and then rejected, so
                // re-deriving the reason here costs nothing measurable. It keeps the reported
                // reason byte-identical to the uncached one, and means no string has to be kept
                // alive per banned type just to produce a message that is usually discarded.
                if (IsBanned(type, out var ban))
                {
                    reason = ban.BanReason;
                    return true;
                }
            }

            reason = null!;
            return false;
        }

        /// <summary>
        /// Caches the ban verdict per <see cref="Type"/>. The returned verdict is authoritative on
        /// both a hit and a miss.
        /// <para>
        /// The verdict is a pure function of the type - a ban comes from the type's shape or from an
        /// attribute on it, and neither can change while the <see cref="Type"/> is alive - but the scan
        /// behind it is neither cheap nor allocation-free: reading <c>Type.FullName</c> for the
        /// name-matched bans, and materializing the whole attribute list for <c>[Exclude]</c>, together
        /// cost on the order of a microsecond and hundreds of bytes <em>per call</em>. This is
        /// reached once per <em>value</em> written, not once per type, so a graph of a few hundred
        /// objects repeated the whole scan hundreds of times to reach the same handful of answers.
        /// </para>
        /// <para>
        /// The table is a fixed-size direct-mapped cache rather than a dictionary on purpose: the hit
        /// is two reads and a reference comparison, on a path that runs once per written value. A
        /// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/> over the
        /// same types was measured about 3x slower per Save - and still about 4x faster than no cache
        /// at all - because its lookup goes through a dependent handle instead of an array index; it
        /// is also the only variant tried that would not have needed the collectible-context check
        /// below, since its keys are weak. A collision evicts, which costs a rescan - never a wrong
        /// answer, because a slot is only trusted when the type in it is reference-equal.
        /// </para>
        /// <para>
        /// The size is a compromise, and the honest failure mode is worth stating: a working set
        /// larger than the table thrashes, and because every miss allocates the <see cref="Verdict"/>
        /// it publishes, a large enough one stops paying for itself. It degrades to break-even rather
        /// than into a loss, though. Measured per lookup against this exact mask (median of four
        /// passes, rotating access): 8 types 0.025 us cached vs 1.04 us uncached, 64 types 0.19 vs
        /// 1.30, 200 types 1.04 vs 1.64, 512 types 1.19 vs 1.77, 1024 types 1.48 vs 1.67, and 2000
        /// types - four times the table - 1.96 vs 1.94, i.e. no benefit and 2% more allocation but
        /// nothing worse. The benefit is concentrated where an application actually lives: a type is
        /// looked up once per value written, and a serialized graph has far fewer types than the
        /// process has loaded. The remedy for a pathological workload would be a bigger table, not a
        /// different structure.
        /// </para>
        /// <para>
        /// Safe to share between serializers and threads: each slot holds one immutable
        /// <see cref="Verdict"/> published with a single reference write, so a reader either sees the
        /// previous entry (wrong type, so it rescans) or the new one (matching type, so the verdict
        /// is the one that was computed for it). The two fields cannot be observed torn, and the
        /// worst a racing writer can do is make the other thread rescan.
        /// </para>
        /// </summary>
        static bool IsBannedCached(Type type)
        {
            // RuntimeHelpers.GetHashCode is what the rest of the library keys Type by (see
            // TypeDictionary) and avoids the virtual call into RuntimeType's name-based hash. Its
            // identity hash is well spread in the low bits, which is what the mask below needs.
            var slot = (int)((uint)RuntimeHelpers.GetHashCode(type) & (VerdictCacheSize - 1));

            var cached = Volatile.Read(ref _verdictCache[slot]);
            if (cached != null && ReferenceEquals(cached.Type, type))
                return cached.Banned;

            bool banned = IsBanned(type, out _);

            // Deliberately not every type is parked here. A static slot that holds a Type keeps that
            // type's AssemblyLoadContext alive for the lifetime of the process, so a host that loads
            // plugins into collectible contexts could never unload a plugin it had serialized once.
            // Only types from a context that cannot be collected are worth keeping; the rest keep
            // paying the uncached cost, which is exactly what they cost before this cache existed.
            // This check is on the miss path only - a hit never reaches it.
#if NETCOREAPP3_0_OR_GREATER
            if (IsSafeToPark(type, 0))
                Volatile.Write(ref _verdictCache[slot], new Verdict(type, banned));
#else
            // No collectible AssemblyLoadContext exists on this platform, so there is nothing a slot
            // could keep alive. (This is the only part of the cache that is not available everywhere:
            // AssemblyLoadContext arrived in .NET Core 3.0, so netstandard2.0 and .NET Framework do
            // not have it. Volatile and RuntimeHelpers.GetHashCode are fine on all of them.)
            Volatile.Write(ref _verdictCache[slot], new Verdict(type, banned));
#endif

            return banned;
        }

#if NETCOREAPP3_0_OR_GREATER
        // Depth at which the walk below gives up and reports the type as unsafe to park. Type
        // structure nests only a handful of levels deep in practice (an array of a List of a nested
        // type is already three), so this is far above anything real; it exists so that the cost of a
        // cache miss is bounded by construction rather than by an argument about the CLR's type
        // model. Exceeding it costs a cache slot, which is the cheap direction to be wrong in.
        const int MaxCollectibleWalkDepth = 16;

        /// <summary>
        /// True when a static field may hold this <see cref="Type"/> without keeping anything
        /// collectible alive - i.e. when neither the type nor any type it refers to comes from a
        /// collectible <see cref="AssemblyLoadContext"/>. Assemblies in the default context report no
        /// context at all, and a dynamic assembly built with
        /// <see cref="System.Reflection.Emit.AssemblyBuilderAccess.Run"/> is not collectible either,
        /// so both are safe.
        /// <para>
        /// <see cref="Type.Assembly"/> alone is NOT enough, which is the whole reason this walks the
        /// type. It is the assembly of the <em>generic type definition</em> for an instantiated
        /// generic, so <c>List&lt;Plugin&gt;</c> and <c>Dictionary&lt;Plugin, int&gt;</c> both report
        /// the core library - while the <see cref="Type"/> in hand holds a reference to <c>Plugin</c>,
        /// and that reference is exactly what keeps the plugin's context from being collected. An
        /// array, a by-ref and a pointer report their <em>element's</em> assembly, which catches a
        /// bare <c>Plugin[]</c> but not <c>List&lt;Plugin&gt;[]</c>, so the element has to be
        /// classified too rather than trusted.
        /// </para>
        /// <para>
        /// Every way a <see cref="Type"/> can refer to another one is therefore walked: its assembly,
        /// the arguments of an instantiated generic, its element type, and the declaring type of a
        /// nested type. Each of those is itself classified the same way, so
        /// <c>List&lt;List&lt;Plugin&gt;&gt;</c> and an <c>Outer&lt;Plugin&gt;.Inner</c> are covered
        /// too. The structure is finite so the recursion terminates, and
        /// <see cref="MaxCollectibleWalkDepth"/> bounds it regardless.
        /// </para>
        /// <para>
        /// Only reached on a cache miss, next to a scan that costs about a microsecond, so walking the
        /// type here is free in comparison - and a hit never reaches it at all.
        /// </para>
        /// </summary>
        static bool IsSafeToPark(Type type, int depth)
        {
            // Out of depth: assume the worst, which is to leave this type out of the cache.
            if (depth > MaxCollectibleWalkDepth)
                return false;

            var context = AssemblyLoadContext.GetLoadContext(type.Assembly);
            if (context != null && context.IsCollectible)
                return false;

            // Only an instantiated generic carries arguments of its own. A definition's are type
            // parameters, which live nowhere in particular, and a type parameter is never passed here.
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                foreach (var argument in type.GetGenericArguments())
                    if (!IsSafeToPark(argument, depth + 1))
                        return false;
            }

            // Arrays, by-refs and pointers are all made of an element type, which can itself be a
            // composite - and the element's assembly is the one the check above already saw.
            if (type.HasElementType)
            {
                var element = type.GetElementType();
                if (element != null && !IsSafeToPark(element, depth + 1))
                    return false;
            }

            // A nested type lives inside another one, which may come from a different assembly and may
            // itself be an instantiated generic.
            var declaring = type.DeclaringType;
            if (declaring != null && !IsSafeToPark(declaring, depth + 1))
                return false;

            return true;
        }
#endif

        sealed class Verdict
        {
            public readonly Type Type;
            public readonly bool Banned;

            public Verdict(Type type, bool banned)
            {
                Type = type;
                Banned = banned;
            }
        }

        // Power of two so the index is a mask. Bounded on purpose: it holds one Verdict per slot and
        // a colliding type evicts, so the cache cannot grow with the number of types seen.
        const int VerdictCacheSize = 512;
        static readonly Verdict?[] _verdictCache = new Verdict?[VerdictCacheSize];

        /// <summary>
        /// The shape ban only - no <c>[Exclude]</c> scan, so the caller decides what a
        /// user-excluded type means for it.
        /// </summary>
        static bool TryGetBannedShape(Type type, out BannedType foundBan)
        {
            for (var i = 0; i < _bannedTypes.Count; i++)
            {
                var ban = _bannedTypes[i];

                bool isBanned = false;

                if (ban.Type != null)
                {
                    if (ban.AlsoCheckInherit)
                    {
                        if (ban.Type.IsAssignableFrom(type))
                            isBanned = true;
                    }
                    else
                    {
                        if (type == ban.Type)
                            isBanned = true;
                    }
                }
                else if (ban.FullName != null)
                {
                    if (ban.AlsoCheckInherit)
                    {
                        var t = type;
                        while (t != null)
                        {
                            if (t.FullName == ban.FullName)
                            {
                                isBanned = true;
                                break;
                            }

                            if (t.GetInterfaces().Any(x => x.FullName == ban.FullName))
                            {
                                isBanned = true;
                                break;
                            }

                            t = t.BaseType;
                        }
                    }
                    else
                    {
                        if (type.FullName == ban.FullName)
                            isBanned = true;
                    }
                }
                else if (ban.CustomIsBannedCheck != null)
                {
                    if (ban.CustomIsBannedCheck(type))
                        isBanned = true;
                }

                if (isBanned)
                {
                    foundBan = ban;
                    return true;
                }
            }

            foundBan = default;
            return false;
        }

        /// <summary>
        /// True when the type itself carries <c>[Exclude]</c>, i.e. the user declared that this type
        /// is not for serialization. A member of such a type is legitimately absent from the
        /// payload; that is not an error (see <see cref="IsBannedShape(Type, out string)"/>).
        /// </summary>
        internal static bool IsExcludedType(Type type)
        {
            foreach (var attr in type.GetCustomAttributes(true))
            {
                if (attr.GetType().FullName == "Ceras.ExcludeAttribute")
                    return true;
            }

            return false;
        }

        static bool IsBanned(Type type, out BannedType foundBan)
        {
            if (TryGetBannedShape(type, out foundBan))
                return true;

            // Check for [Exclude] attribute on the type itself
            if (IsExcludedType(type))
            {
                foundBan = new BannedType(type, "The type is explicitly marked with [Exclude].", false);
                return true;
            }

            foundBan = default;
            return false;
        }

        internal static void ThrowIfNonspecific(Type type)
        {
            if (type.IsAbstract() || type.IsInterface || type.ContainsGenericParameters)
                throw new InvalidOperationException("Can only generate code for specific types. The type " + type.FriendlyName(true) + " is abstract, or an interface, or an open generic.");
        }
    }

    /// <summary>
    /// Thrown when a value or a member would have been written as <c>null</c> because its type is
    /// banned, but losing it silently is not an option.
    /// <para>There are two situations, and both used to produce a file that looked fine and came
    /// back with a <c>null</c> where real data had been:</para>
    /// <list type="bullet">
    /// <item><description>a member whose declared type is banned (a delegate, an enumerator,
    /// reflection metadata, ...), which used to be dropped from the schema;</description></item>
    /// <item><description>a runtime value of a banned type reaching a reference or <see cref="Type"/>
    /// slot, which used to be written as a null marker.</description></item>
    /// </list>
    /// <para>The fix is always the same: keep the value out of the payload on purpose
    /// (<c>[Exclude]</c> the member) or give it a representation Ceras can write.</para>
    /// </summary>
    public class BannedTypeException : Exceptions.CerasException
    {
        public BannedTypeException(string message) : base(message)
        {

        }

        public BannedTypeException(string message, Exception inner) : base(message, inner)
        {

        }
    }
}

