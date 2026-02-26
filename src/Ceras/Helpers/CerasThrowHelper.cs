using System;

namespace Ceras.Helpers
{
	public static class CerasThrowHelper
	{
		public static void ThrowGetterOnlyNull(string memberName, string typeName)
		{
			throw new InvalidOperationException($"Cannot deserialize getter-only member '{memberName}' of type '{typeName}' because it is null. In 'Members' mode, Ceras populates existing objects in-place and cannot assign a new reference to a getter-only member. Ensure the field or property is initialized (e.g. '= []' or '= new ...()') before deserialization, or use [Exclude] to skip it.");
		}
	}
}
