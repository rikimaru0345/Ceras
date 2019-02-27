namespace Ceras.Resolvers
{
	using System;
	using System.Reflection;
	using Formatters;

	/// <summary>
	/// This resolver handles some really special types like <see cref="MemberInfo"/> and <see cref="MulticastDelegate"/>
	/// </summary>
	public sealed class ReflectionFormatterResolver : IFormatterResolver
	{
		readonly CerasSerializer _ceras;

		public ReflectionFormatterResolver(CerasSerializer ceras)
		{
			_ceras = ceras;
		}

		public IFormatter GetFormatter(Type type)
		{
			if (typeof(MemberInfo).IsAssignableFrom(type))
			{
				var memberInfoFormatterType = typeof(MemberInfoFormatter<>).MakeGenericType(type);
				var memberInfoFormatter = Activator.CreateInstance(memberInfoFormatterType, args: _ceras);
				return (IFormatter)memberInfoFormatter;
			}

			if (typeof(MulticastDelegate).IsAssignableFrom(type))
			{
				if (_ceras.Config.Advanced.DelegateSerialization == DelegateSerializationFlags.Off)
					throw new InvalidOperationException($"The type '{type.FullName}' can not be serialized because it is a delegate; and 'config.Advanced.DelegateSerialization' is turned off.");

				// Every delegate type is created by the formatter, there can't be any exceptions (unless you do some really dangerous stuff)
				CerasSerializer.AddFormatterConstructedType(type);

				var formatterType = typeof(DelegateFormatter<>).MakeGenericType(type);
				var formatter = Activator.CreateInstance(formatterType, args: _ceras);
				return (IFormatter)formatter;
			}

			return null;
		}
	}
}