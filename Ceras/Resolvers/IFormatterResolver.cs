namespace Ceras.Resolvers
{
	using System;
	using Formatters;

	public interface IFormatterResolver
	{
		IFormatter GetFormatter(Type type);
	}
}