namespace Ceras.Resolvers
{
	using System;
	using Formatters;

	/// <summary>
	/// A formatter resolver is something that can create instances of <see cref="IFormatter{T}"/> for a given <see cref="Type"/> (or null if the resolver can not handle the given type)
	/// </summary>
	public interface IFormatterResolver
	{
		IFormatter GetFormatter(Type type);
	}
}