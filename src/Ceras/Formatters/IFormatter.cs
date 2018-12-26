namespace Ceras.Formatters
{
	public interface IFormatter
	{
	}

	
	delegate void SerializeDelegate<T>(ref byte[] buffer, ref int offset, T value);
	delegate void DeserializeDelegate<T>(byte[] buffer, ref int offset, ref T value);

	public interface IFormatter<T> : IFormatter
	{
		void Serialize(ref byte[] buffer, ref int offset, T value);

		void Deserialize(byte[] buffer, ref int offset, ref T value);
	}
}