
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

            while (t != null)
            {
                if(t.FullName == unityObject)
                    return true;
                
                t = t.BaseType;
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
            if (IsBanned(type, out var ban))
            {
                reason = ban.BanReason;
                return true;
            }

            reason = null!;
            return false;
        }

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

