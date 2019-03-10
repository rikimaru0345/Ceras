
// ReSharper disable ArgumentsStyleOther
// ReSharper disable ArgumentsStyleNamedExpression
namespace Ceras.Formatters
{
	using Ceras.Helpers;
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

			public readonly Func<Type, bool> CustomIsBannedCheck;

			public BannedType(Type type, string banReason, bool alsoCheckInherit)
			{
				Type = type;
				FullName = null;
				CustomIsBannedCheck = null;

				BanReason = banReason;
				AlsoCheckInherit = alsoCheckInherit;
			}

			public BannedType(string fullName, string banReason, bool alsoCheckInherit)
			{
				Type = null;
				FullName = fullName;
				CustomIsBannedCheck = null;

				BanReason = banReason;
				AlsoCheckInherit = alsoCheckInherit;
			}

			public BannedType(Func<Type, bool> customCheck, string banReason)
			{
				Type = null;
				FullName = null;
				CustomIsBannedCheck = customCheck;

				BanReason = banReason;
				AlsoCheckInherit = false;
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
				else if(ban.CustomIsBannedCheck != null)
				{
					if (ban.CustomIsBannedCheck(type))
						isBanned = true;
				}
				else
				{
					Debug.Assert(false, "unreachable");
				}

				if (isBanned)
					throw new BannedTypeException($"The type '{type.FullName}' cannot be serialized, please mark the field/property that caused this Type to be included with the [Exclude] attribute or filter it out using the 'ShouldSerialize' callback. Specific reason for this type being banned: \"{ban.BanReason}\". You should open an issue on GitHub or join the Discord server for support.");
			}
		}

		internal static void ThrowIfNonspecific(Type type)
		{
			if (type.IsAbstract() || type.IsInterface || type.ContainsGenericParameters)
				throw new InvalidOperationException("Can only generate code for specific types. The type " + type.FriendlyName(true) + " is abstract, or an interface, or an open generic.");
		}
	}

	class BannedTypeException : Exception
	{
		public BannedTypeException(string message) : base(message)
		{

		}
	}
}

