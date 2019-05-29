using Ceras.Formatters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveTesting.InterfaceVsDelegateTest
{
	/*
	 * Calls to interface methods use an expensive lookup.
	 * Can we bypass that by "resolving" the method into a delegate?
	 * 
	 */
	static class InterfaceVsDelegateTest
	{
		public static void Test()
		{
			for (int i = 0; i < 3; i++)
				MicroBenchmark.Run(5, new BenchJob[]
				{
					// ("Default", () => DoTest(value, defaultF)),
				});

			Console.WriteLine("done");
			Console.ReadKey();
		}


		static void Test(IExampleCalculation calc)
		{

		}
	}

	interface IExampleCalculation
	{
		void DoWork(ref int a);
	}

	class Implementation : IExampleCalculation
	{
		public void DoWork(ref int a)
		{
			// Hailstone
			while(true)
			{
				if(a == 1)
					break;

				bool even = (a & 1) == 0;
				if(even)
					a /= 2;
				else
					a = (a * 3) + 1;
			}
		}
	}
}
