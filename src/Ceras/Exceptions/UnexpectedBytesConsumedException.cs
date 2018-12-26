using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Exceptions
{
	public class UnexpectedBytesConsumedException : CerasException
	{
		public int BytesExpectedToRead { get; }
		public int BytesActuallyRead { get; }
		public int OffsetBeforeDeserialize { get; }
		public int OffsetAfterDeserialize { get; }

		public UnexpectedBytesConsumedException(string message, int bytesExpectedToRead, int bytesActuallyRead, int offsetBeforeDeserialize, int offsetAfterDeserialize) : base(message)
		{
			BytesExpectedToRead = bytesExpectedToRead;
			BytesActuallyRead = bytesActuallyRead;
			OffsetBeforeDeserialize = offsetBeforeDeserialize;
			OffsetAfterDeserialize = offsetAfterDeserialize;
		}
	}
}
