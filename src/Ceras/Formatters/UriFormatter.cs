using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
	class UriFormatter : IFormatter<Uri>
	{
		public UriFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(Uri));
		}

		public void Serialize(ref byte[] buffer, ref int offset, Uri value)
		{
			SerializerBinary.WriteString(ref buffer, ref offset, value.OriginalString);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Uri value)
		{
			var uri = SerializerBinary.ReadString(buffer, ref offset);

			if (uri == null)
				value = null;
			else
				value = new Uri(uri, UriKind.RelativeOrAbsolute);
		}
	}
}
