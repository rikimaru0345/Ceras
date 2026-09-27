using System;

namespace Ceras.Format
{
    /// <summary>
    /// Enum values travel through the schema and the migration map as raw 64-bit patterns, so a
    /// <c>ulong</c> value above <see cref="long.MaxValue"/> is representable. The enum's underlying
    /// type decides how those bits are read: a signed underlying type reads them as two's complement,
    /// an unsigned one as a plain magnitude. That is why no extra signedness flag is needed — the
    /// underlying type id is already in the schema.
    /// </summary>
    public static class EnumBits
    {
        /// <summary>True when <paramref name="enumType"/>'s underlying type is an unsigned integer.</summary>
        public static bool IsUnsigned(Type enumType)
        {
            if (enumType == null || !enumType.IsEnum) return false;
            var underlying = Enum.GetUnderlyingType(enumType);
            return underlying == typeof(ulong) || underlying == typeof(uint)
                || underlying == typeof(ushort) || underlying == typeof(byte);
        }

        /// <summary>True when the schema's underlying-type name denotes an unsigned integer.</summary>
        public static bool IsUnsignedUnderlyingName(string underlyingTypeName)
        {
            switch (underlyingTypeName)
            {
                case "System.UInt64":
                case "System.UInt32":
                case "System.UInt16":
                case "System.Byte":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The raw 64-bit pattern of an enum value, widened from its underlying type without ever
        /// going through <see cref="long"/>, so nothing overflows.
        /// </summary>
        public static ulong ToRawBits(object? value, Type enumType)
        {
            if (value == null) return 0;
            if (IsUnsigned(enumType)) return Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);

            // Signed: Convert.ToUInt64 would throw for a negative value, so widen the signed form.
            return unchecked((ulong)Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Turns raw bits back into a CLR value of the enum's underlying type, so
        /// <c>Enum.ToObject</c> cannot be handed a negative number for a <c>ulong</c> enum.
        /// </summary>
        public static object ToClrValue(ulong rawBits, Type enumType)
        {
            if (IsUnsigned(enumType)) return Convert.ToUInt64(rawBits);
            return unchecked((long)rawBits);
        }

        /// <summary>Formats raw bits for a dump: signed only when the underlying type is signed.</summary>
        public static string Format(ulong rawBits, bool isUnsigned)
            => isUnsigned ? rawBits.ToString(System.Globalization.CultureInfo.InvariantCulture)
                          : unchecked((long)rawBits).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
