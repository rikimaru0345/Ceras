namespace Ceras.Resolvers
{
	using System;
	using Formatters;

	interface IFormatterResolver
	{
		IFormatter GetFormatter(Type type);
	}
}