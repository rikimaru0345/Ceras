namespace Ceras.Formatters
{
	public interface IFormatter
	{
	}

	public interface IFormatter<T> : IFormatter
	{
		void Serialize(ref byte[] buffer, ref int offset, T value);

		void Deserialize(byte[] buffer, ref int offset, ref T value);
	}
}