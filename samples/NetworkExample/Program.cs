namespace NetworkExample
{
	class Program
	{
		static void Main(string[] args)
		{
			Server.Start();

			var c = new Client();
			c.Start();
		}
	}
}
