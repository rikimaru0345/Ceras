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
}
