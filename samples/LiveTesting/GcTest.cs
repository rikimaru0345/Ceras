using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace LiveTesting
{
	static class GcTest
	{
		const int ArraySize = 100;
		static Random _rng = new Random();
		static AutoResetEvent _consumerSignal = new AutoResetEvent(false);
		static AutoResetEvent _interruptorSignal = new AutoResetEvent(false);

		static volatile byte[] _array;
		static WeakReference<byte[]> _weakRefArray;
		static WeakReference<byte[]> _weakRefOther;

		[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
		static byte[] ObtainArray()
		{
			return _array;
		}

		[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
		static ref byte ObtainSlot42_ByRef()
		{
			return ref _array[42];
		}

		[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
		unsafe static byte* ObtainSlot42_ByPtr()
		{
			return (byte*)Unsafe.AsPointer(ref _array[42]);
		}


		internal static void Test()
		{
			RunTest(Start1_Classic);
			RunTest(Start2_Ref);
			RunTest(Start3_Ptr);

			Console.WriteLine("GcTest done.");
			Console.ReadKey();
		}

		static void RunTest(ThreadStart consumer)
		{
			InitializeTest();

			var t1 = new Thread(consumer);
			var t2 = new Thread(Interruptor);
			t1.Start();
			t2.Start();

			t1.Join();
			t2.Join();

			Console.WriteLine();
			Console.WriteLine();
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		static void InitializeTest()
		{
			// Create a few arrays
			byte[][] arrays = new byte[100][];
			for (int i = 0; i < arrays.Length; i++)
			{
				var ar = new byte[ArraySize];
				arrays[i] = ar;
			}
			// Select a random one
			var index = _rng.Next(10, 90);
			_array = arrays[index];
			_weakRefArray = new WeakReference<byte[]>(_array);
			_weakRefOther = new WeakReference<byte[]>(arrays[index + 1]);

			arrays = null;
		}


		static void Start1_Classic()
		{
			Console.WriteLine("1) Consumer start");

			byte[] ar = ObtainArray(); // Obtain a reference to the array

			using (new Timer("Consumer waiting on interruptor"))
			{
				_consumerSignal.Set(); // Let other thread do GC
				_interruptorSignal.WaitOne(); // Wait until the other thread is done
			}

			Console.WriteLine("4) Consumer accessing array...");
			for (int i = 0; i < ar.Length; i++)
				ar[i]++;

			Console.WriteLine("5) Consumer end");
		}
		
		static void Start2_Ref()
		{
			Console.WriteLine("1) Consumer start");

			ref byte byteRef = ref ObtainSlot42_ByRef(); // Obtain a reference to the array

			using (new Timer("Consumer waiting on interruptor"))
			{
				_consumerSignal.Set(); // Let other thread do GC
				_interruptorSignal.WaitOne(); // Wait until the other thread is done
			}

			Console.WriteLine("4) Consumer accessing array...");

			for (int i = 0; i < ArraySize; i++)
			{
				byteRef++;
				byteRef = ref Unsafe.Add(ref byteRef, 1);
			}

			Console.WriteLine("5) Consumer end");
		}
		
		unsafe static void Start3_Ptr()
		{
			Console.WriteLine("1) Consumer start");

			byte* bytePtr = ObtainSlot42_ByPtr(); // Obtain a reference to the array

			using (new Timer("Consumer waiting on interruptor"))
			{
				_consumerSignal.Set(); // Let other thread do GC
				_interruptorSignal.WaitOne(); // Wait until the other thread is done
			}

			Console.WriteLine("4) Consumer accessing array...");

			for (int i = 0; i < ArraySize; i++)
			{
				(*bytePtr)++;
				bytePtr++;
			}

			Console.WriteLine("5) Consumer end");
		}


		static void Interruptor()
		{
			Console.WriteLine("1) Interruptor start");

			_consumerSignal.WaitOne(); // Wait for signal from the consumer

			Console.WriteLine("2) Interruptor running GC...");

			// Now set the array to null, making it collectable
			_array = null;
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
			GC.WaitForPendingFinalizers();
			GC.Collect();

			Console.WriteLine($"3) Interruptor end (array: {(_weakRefArray.IsAlive() ? "alive" : "-")}) (other: {(_weakRefOther.IsAlive() ? "alive" : "-")})");

			_interruptorSignal.Set(); // signal that we're done
		}

		static bool IsAlive<T>(this WeakReference<T> weakRef) where T : class
		{
			bool alive = weakRef.TryGetTarget(out T target);
			target = default;
			return alive;
		}
		static T GetOrNull<T>(this WeakReference<T> weakRef) where T : class
		{
			if (weakRef.TryGetTarget(out T target))
				return target;
			return null;
		}
	}
}