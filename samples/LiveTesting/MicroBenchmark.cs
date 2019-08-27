using System;

namespace LiveTesting
{
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;

	static class MicroBenchmark
	{
		public static ulong EstimateIterations(TimeSpan targetTime, Action action)
		{
			// Jit and warmup
			for (int i = 0; i < 300; i++)
				action();

			// Find iteration count
			int miniBatchSize = 1000;
			long pilotIterations = 0;

			Stopwatch pilotWatch = Stopwatch.StartNew();
			while (pilotWatch.ElapsedMilliseconds < 500)
			{
				for (int i = 0; i < miniBatchSize; i++)
					action();
				pilotIterations += miniBatchSize;
			}
			pilotWatch.Stop();
			double msPerInvoke = pilotWatch.Elapsed.TotalMilliseconds / pilotIterations;

			return (ulong)(targetTime.TotalMilliseconds / msPerInvoke);
		}

		// todo:
		// - internal BenchmarkEntry to track AverageTimePerInvoke, and adjust InvokesPerSecond live, easy warmup, pilot, RunFor(ms), ...
		// - update timings for each action in short time steps; update display with projected numbers (cycle updates)
		// - add `-` and `|`
		// - PrintTable() method
		// - Order by execution time
		// - Remember cursor pos, redraw on every update


		public static void Run(double runTimeSeconds, params BenchJob[] actions)
		{
			var iterations = EstimateIterations(TimeSpan.FromSeconds(runTimeSeconds), actions[0].Action);

			Console.WriteLine($"[MicroBenchmark] Runtime={runTimeSeconds:0.0}sec  Actions:{actions.Length}  Iterations:{iterations}");

			Stopwatch totalTime = Stopwatch.StartNew();
			RunManual(iterations, actions);
			totalTime.Stop();

			Console.WriteLine($"[MicroBenchmark] Done in {totalTime.Elapsed.TotalSeconds:0.0} sec");
			Console.WriteLine();
		}

		public static void RunManual(ulong iterationCount, params BenchJob[] jobs)
		{
			int padLeft = jobs.Max(a => a.Name.Length) + 3;

			// Warmup
			foreach (var p in jobs)
				for (int i = 0; i < 200; i++)
					p.Action();

			double[] elapsedMs = new double[jobs.Length];

			for (int actionIndex = 0; actionIndex < jobs.Length; actionIndex++)
			{
				var entry = jobs[actionIndex];

				GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
				GC.WaitForPendingFinalizers();

				Stopwatch watch = Stopwatch.StartNew();

				for (ulong i = 0; i < iterationCount; i++)
					entry.Action();

				watch.Stop();

				elapsedMs[actionIndex] = watch.Elapsed.TotalMilliseconds;

				Report(actionIndex);
			}

			// for (int i = 0; i < actions.Length; i++)
			//	Report(i);

			void Report(int i)
			{
				double factor = i == 0
					? 1.0
					: elapsedMs[i] / elapsedMs[0];

				string fasterSlower = i == 0
					? ""
					: factor < 1
						? $"{(1 / factor * 100) - 100:0}% faster!"
						: $"{((1 - factor) * 100):0}% SLOWER!!";

				var name = jobs[i].Name.PadLeft(padLeft);
				Console.WriteLine($"{name}: {elapsedMs[i],5:0} ms ({factor:0.00}x) {fasterSlower}");
			}
		}
	}

	class BenchJob
	{
		// Basic
		public string Name;
		public Action Action;

		// Stats
		public Stopwatch WarmupTimer;
		public int WarmupRuns;
		public int RunsPerSec;

		public void Warmup()
		{
			for (int i = 0; i < 10; i++)
				Action();

			WarmupTimer = Stopwatch.StartNew();
			while (true)
			{
				WarmupTimer.Start();
				Action();
				WarmupTimer.Stop();

				WarmupRuns++;
				if (WarmupRuns > 10 * 1000)
					break;
				if (WarmupTimer.ElapsedMilliseconds > 20)
					break;
			}
		}

		public BenchJob(string name, Action action)
		{
			Name = name;
			Action = action;
		}

		public static implicit operator BenchJob((string name, Action action) data) => new BenchJob(data.name, data.action);
	}

	struct Timer : IDisposable
	{
		readonly string _name;
		readonly Stopwatch _stopwatch;

		public Timer(string name)
		{
			_name = name;
			_stopwatch = Stopwatch.StartNew();
		}

		public void Dispose()
		{
			_stopwatch.Stop();
			Console.WriteLine($" \"{_name}\" took {_stopwatch.Elapsed.TotalMilliseconds:0.00} ms");
		}
	}
}
