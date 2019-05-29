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
	 * Result: no measurable difference.
	 */
	static class InterfaceVsDelegateTest
	{
		static Random rng = new Random();
		static int[] numbers;

		public static void Test()
		{
			numbers = new int[10];
			for (int i = 0; i < numbers.Length; i++)
				numbers[i] = rng.Next(0, 200);

			var a = new ImplA();
			var b = new ImplB();
			var c = new ImplC();

			Tester t1 = new CallInterface(a, b, c);
			Tester t2 = new CallDelegate(a, b, c);
			Tester t3 = new CallDirect(a, b, c);
			Tester t4 = new CallGeneric<ImplA, ImplB, ImplC>(a, b, c);


			var runTimes = new[] { 5, 10, 30, 30, 30 };
			foreach (var t in runTimes)
				MicroBenchmark.Run(t, new BenchJob[]
				{
					("Interface", () => RunTester(t1)),
					("CreateDelegate", () => RunTester(t2)),
					("CallDirect", () => RunTester(t3)),
					("CallGeneric", () => RunTester(t4)),
				});

			Console.WriteLine("done");
			Console.ReadKey();
		}


		static void RunTester(Tester t)
		{
			for (int i = 0; i < numbers.Length; i++)
			{
				int x = numbers[i];
				t.DoTest(ref x);
			}
		}
	}

	abstract class Tester
	{
		public abstract void DoTest(ref int x);
	}

	class CallDirect : Tester
	{
		public ImplA A;
		public ImplB B;
		public ImplC C;

		public CallDirect(ImplA a, ImplB b, ImplC c)
		{
			A = a;
			B = b;
			C = c;
		}

		public override void DoTest(ref int x)
		{
			x += 2;

			A.DoWork(ref x);

			x = (int)(x * 1.4235235);
			x += 35;

			B.DoWork(ref x);

			x = (int)(x * 0.988135471724);
			x += 71;

			C.DoWork(ref x);
		}
	}

	class CallGeneric<T1, T2, T3> : Tester
		where T1 : IExampleCalculation
		where T2 : IExampleCalculation
		where T3 : IExampleCalculation
	{
		public T1 A;
		public T2 B;
		public T3 C;

		public CallGeneric(T1 a, T2 b, T3 c)
		{
			A = a;
			B = b;
			C = c;
		}

		public override void DoTest(ref int x)
		{
			x += 2;

			A.DoWork(ref x);

			x = (int)(x * 1.4235235);
			x += 35;

			B.DoWork(ref x);

			x = (int)(x * 0.988135471724);
			x += 71;

			C.DoWork(ref x);
		}
	}


	class CallInterface : Tester
	{
		public IExampleCalculation A;
		public IExampleCalculation B;
		public IExampleCalculation C;

		public CallInterface(IExampleCalculation a, IExampleCalculation b, IExampleCalculation c)
		{
			A = a;
			B = b;
			C = c;
		}

		public override void DoTest(ref int x)
		{
			x += 2;

			A.DoWork(ref x);

			x = (int)(x * 1.4235235);
			x += 35;

			B.DoWork(ref x);

			x = (int)(x * 0.988135471724);
			x += 71;

			C.DoWork(ref x);
		}
	}

	class CallDelegate : Tester
	{
		public IExampleCalculation A;
		public IExampleCalculation B;
		public IExampleCalculation C;

		delegate void DoWorkDelegate(ref int a);
		DoWorkDelegate delegateA;
		DoWorkDelegate delegateB;
		DoWorkDelegate delegateC;


		public CallDelegate(IExampleCalculation a, IExampleCalculation b, IExampleCalculation c)
		{
			A = a;
			B = b;
			C = c;

			delegateA = (DoWorkDelegate)Delegate.CreateDelegate(typeof(DoWorkDelegate), a, "DoWork");
			delegateB = (DoWorkDelegate)Delegate.CreateDelegate(typeof(DoWorkDelegate), b, "DoWork");
			delegateC = (DoWorkDelegate)Delegate.CreateDelegate(typeof(DoWorkDelegate), c, "DoWork");
		}

		public override void DoTest(ref int x)
		{
			x += 2;

			delegateA(ref x);

			x = (int)(x * 1.4235235);
			x += 35;

			delegateB(ref x);

			x = (int)(x * 0.988135471724);
			x += 71;

			delegateC(ref x);
		}
	}



	interface IExampleCalculation
	{
		void DoWork(ref int a);
	}

	class ImplA : IExampleCalculation
	{
		public void DoWork(ref int a)
		{
			for (int i = 0; i < 5; i++)
			{
				if (a == 1)
					break;

				bool even = (a & 1) == 0;
				if (even)
					a /= 2;
				else
					a = (a * 3) + 1;
			}
		}
	}
	class ImplB : IExampleCalculation
	{
		public void DoWork(ref int a)
		{
			for (int i = 0; i < 3; i++)
			{
				if (a == 1)
					break;

				bool even = (a & 1) == 0;
				if (even)
					a /= 2;
				else
					a = (a * 3) + 1;
			}
		}
	}
	class ImplC : IExampleCalculation
	{
		public void DoWork(ref int a)
		{
			var r = a / 10;

			for (int i = 0; i < r; i++)
			{
				if (a == 1)
					break;

				bool even = (a & 1) == 0;
				if (even)
					a /= 2;
				else
					a = (a * 3) + 1;
			}
		}
	}
}
