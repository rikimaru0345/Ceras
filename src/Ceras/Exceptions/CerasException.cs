using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Exceptions
{
	public class CerasException : Exception
	{
		public CerasException(string message) : base(message)
		{
			
		}
	}

	public class InvalidConfigException : CerasException
	{
		public InvalidConfigException(string message) : base(message)
		{

		}
	}

	public class ConfigurationSealedException : CerasException
	{
		public ConfigurationSealedException(string message) : base(message)
		{
		}
	}

	public class WarningException : CerasException
	{
		public WarningException(string message) : base(message)
		{

		}
	}
}
