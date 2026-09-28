using System;
using System.Reflection;

namespace Ceras.Format
{
    /// <summary>
    /// The name an enum member is recorded and migrated under: <c>[CerasStableName]</c> when it carries
    /// one, otherwise the C# member name.
    /// <para>
    /// There is exactly one such function, and both places that have to agree use it: the schema entry
    /// an enum member is written into (generated, or derived at runtime for a declared/blacklisted enum)
    /// and <c>MigrationPlanBuilder.BuildEnumMap</c>, which matches a file's recorded names against the
    /// current members. When the two disagreed, a file written with a renamed-but-pinned member recorded
    /// one name while the migration looked for another, the member matched nothing, and a renumbered
    /// value was silently reinterpreted as whatever the raw number happened to be.
    /// </para>
    /// <para>
    /// A name that is <em>not</em> one of the two current spellings cannot be recovered from here, and
    /// the function deliberately does not try: the migration map is what additionally accepts a member's
    /// current C# name (which is what the schema recorded before wire names were written) and its
    /// <c>[PreviousName]</c> aliases. A name from an older build that matches neither has to be declared
    /// as an alias — nothing in a file records it.
    /// </para>
    /// </summary>
    static class EnumWireName
    {
        /// <summary>
        /// The wire name of the enum member called <paramref name="memberName"/>.
        /// <para>
        /// The name this returns is never null: it is <paramref name="memberName"/> itself when the
        /// type has no such field (or when the field carries no <c>[CerasStableName]</c>), and the
        /// pinned name when it does. So the two ways a caller could get a null out of it are refused
        /// rather than passed on — a null <paramref name="enumType"/> here, and a null
        /// <paramref name="memberName"/> at the reflection lookup below, which names the argument it
        /// rejects. Neither is reachable from the two call sites, which pass a type they are
        /// describing and a name <see cref="Enum.GetNames(Type)"/> produced.
        /// </para>
        /// </summary>
        public static string Of(Type enumType, string memberName)
        {
            ArgumentNullException.ThrowIfNull(enumType);
            return Of(enumType.GetField(memberName, BindingFlags.Public | BindingFlags.Static), memberName);
        }

        /// <summary>
        /// The wire name of one enum member field, falling back to <paramref name="fallback"/> (the C#
        /// member name) when the member carries no <c>[CerasStableName]</c>.
        /// <para>
        /// <paramref name="member"/> is nullable because that is what reflection answers with for a name
        /// the type has no such field for, which is the same case the fallback covers.
        /// </para>
        /// </summary>
        public static string Of(FieldInfo? member, string fallback)
        {
            if (member == null) return fallback;
            var stable = member.GetCustomAttribute<CerasStableNameAttribute>();
            return string.IsNullOrEmpty(stable?.Name) ? fallback : stable.Name;
        }
    }
}
