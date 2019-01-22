
// ReSharper disable ArgumentsStyleOther
// ReSharper disable ArgumentsStyleNamedExpression
namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;

	// Some types are banned from serialization
	// and instead of throwing crazy errors that don't help the user at all, we give an explanation
	//
	// There is only once place where we can be 100% sure that every type will eventually pass through: the type meta data entries
	// so that's where we're calling it from
	static class BannedTypes
	{
		struct BannedType
		{
			public readonly Type Type;
			public readonly string FullName;

			public readonly string BanReason;
			public readonly bool AlsoCheckInherit;

			public BannedType(Type type, string banReason, bool alsoCheckInherit)
			{
				Type = type;
				FullName = null;
				BanReason = banReason;
				AlsoCheckInherit = alsoCheckInherit;
			}

			public BannedType(string fullName, string banReason, bool alsoCheckInherit)
			{
				Type = null;
				FullName = fullName;
				BanReason = banReason;
				AlsoCheckInherit = alsoCheckInherit;
			}
		}

		static List<BannedType> _bannedTypes = new List<BannedType>();


		static BannedTypes()
		{
			BanBase(typeof(System.Collections.IEnumerator), "Enumerators are potentially infinite, and also most likely have no way to be instantiated at deserialization-time. If you think this is a mistake, report it as a github issue or provide a custom IFormatter for this case.");




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
		}

		static void Ban(Type type, string reason) => _bannedTypes.Add(new BannedType(type, reason, false));
		static void Ban(string fullName, string reason) => _bannedTypes.Add(new BannedType(fullName, reason, false));
		static void BanBase(Type type, string reason) => _bannedTypes.Add(new BannedType(type, reason, true));
		static void BanBase(string fullName, string reason) => _bannedTypes.Add(new BannedType(fullName, reason, true));


		internal static void ThrowIfBanned(Type type)
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
				else
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
								isBanned =true;
								break;
							}

							t = t.BaseType;
						}
					}
					else
					{
						if(type.FullName == ban.FullName)
							isBanned = true;
					}
				}



				if (isBanned)
					throw new BannedTypeException($"The type '{type.FullName}' cannot be serialized, please mark the field/property with the [Ignore] attribute or filter it out using the 'ShouldSerialize' callback. Reason: {ban.BanReason}");
			}
		}

		[Conditional("DEBUG")]
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

