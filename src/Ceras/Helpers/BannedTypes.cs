
// ReSharper disable ArgumentsStyleOther
// ReSharper disable ArgumentsStyleNamedExpression
namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;

	// Some types are banned from serialization
	// and instead of throwing crazy errors that don't help the user at all, we give an explanation
	static class BannedTypes
	{
		struct BannedType
		{
			public readonly Type Type;
			public readonly string BanReason;
			public readonly bool AlsoCheckInherit;

			public BannedType(Type type, string banReason, bool alsoCheckInherit)
			{
				Type = type;
				BanReason = banReason;
				AlsoCheckInherit = alsoCheckInherit;
			}
		}

		static List<BannedType> _bannedTypes = new List<BannedType>
		{
				new BannedType(typeof(System.Collections.IEnumerator), "Enumerators are potentially infinite, and also most likely have no way to be instantiated at deserialization-time. If you think this is a mistake, report it as a github issue or provide a custom IFormatter for this case.", true),

				/*
				new BannedType(typeof(System.Delegate), "Delegates cannot be serialized easily because they often drag in a lot of unintended objects. Support for delegates to static methods is on the todo list though! If you want to know why this is complicated then check this out: https://github.com/rikimaru0345/Ceras/issues/11", true),
				*/
		};


		internal static void ThrowIfBanned(Type type)
		{
			for (var i = 0; i < _bannedTypes.Count; i++)
			{
				var ban = _bannedTypes[i];

				bool isBanned = false;
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

				if (isBanned)
					throw new BannedTypeException($"The type '{type.FullName}' cannot be serialized, please mark the field/property with the [Ignore] attribute or filter it out using the 'ShouldSerialize' callback. Reason: {ban.BanReason}");
			}
		}

		internal static void ThrowIfNonspecific(Type type)
		{
			if (type.IsAbstract || type.IsInterface)
				throw new InvalidOperationException("Can only generate code for specific types. The type " + type.Name + " is abstract or an interface.");
		}

	}
	
	class BannedTypeException : Exception
	{
		public BannedTypeException(string message) : base(message)
		{

		}
	}
}

