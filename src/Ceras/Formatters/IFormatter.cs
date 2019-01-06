using System;
using Ceras.Helpers;

namespace Ceras.Formatters
{

	public interface IFormatter { }
	
	public interface IFormatter<T> : IFormatter
	{
		void Serialize(ref byte[] buffer, ref int offset, T value);
		void Deserialize(byte[] buffer, ref int offset, ref T value);
	}
	
	delegate void SerializeDelegate<T>(ref byte[] buffer, ref int offset, T value);
	delegate void DeserializeDelegate<T>(byte[] buffer, ref int offset, ref T value);


	interface ISchemaTaintedFormatter
	{
		void OnSchemaChanged(TypeMetaData meta);
	}
}