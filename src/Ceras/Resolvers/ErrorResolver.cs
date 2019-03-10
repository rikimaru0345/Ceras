// ReSharper disable RedundantTypeArgumentsOfMethod
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LiveTesting")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ceras.Test")]
namespace Ceras
{
	using Resolvers;
	using System;
	using Helpers;

	sealed class ErrorResolver : IExternalObjectResolver
	{
		public void Resolve<T>(int id, out T value)
		{
			throw new FormatException($"The data to deserialize tells us to resolve an external object (Type: {typeof(T).FriendlyName()} Id: {id}), but no IExternalObjectResolver has been set to deal with that.");
		}
	}
}
