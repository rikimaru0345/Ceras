using System;
using System.Collections.Generic;
using System.Linq;

namespace Ceras.Test
{
	using System.Collections;
	using System.IO;
	using System.Linq.Expressions;
	using System.Reflection;
	using ImmutableCollections;
	using Xunit;
	using Xunit.Abstractions;

	public class TestBase
	{
		// ReSharper disable once InconsistentNaming
		protected Random rng = new Random(12345);

		protected byte rngByte => (byte)(rng.Next(0, int.MaxValue) % 255);
		protected double rngDouble => rng.NextDouble();
		protected float rngFloat => (float)rng.NextDouble();
		protected int rngInt => rng.Next(int.MinValue, int.MaxValue);
		protected short rngShort => (short)rng.Next(int.MinValue, int.MaxValue);
		protected long rngLong => ((long)rng.Next(int.MinValue, int.MaxValue) << 32) + (long)rng.Next(int.MinValue, int.MaxValue);
		protected Vector3 rngVec => new Vector3(rngFloat, rngFloat, rngFloat);


		protected SerializerConfig CreateConfig(Action<SerializerConfig> f)
		{
			var s = new SerializerConfig();
			f(s);
			return s;
		}

		protected SerializerConfig Config_WithVersioning => CreateConfig(x =>
		{
			x.VersionTolerance.Mode = VersionToleranceMode.Standard;
			x.VersionTolerance.VerifySizes = true;
			x.UseImmutableFormatters();
		});
		
		protected SerializerConfig Config_DefaultIntEncoding => CreateConfig(x =>
		{
			x.IntegerEncoding = IntegerEncoding.Default;
			x.UseImmutableFormatters();
		});
		protected SerializerConfig Config_VarIntEncoding => CreateConfig(x =>
		{
			x.IntegerEncoding = IntegerEncoding.ForceVarInt;
			x.UseImmutableFormatters();
		});
		protected SerializerConfig Config_FixedIntEncoding => CreateConfig(x =>
		{
			x.IntegerEncoding = IntegerEncoding.ForceReinterpret;
			x.UseImmutableFormatters();
		});

		SerializerConfig[] _currentTestConfigurations = { new SerializerConfig() };
		int _runCount = 0;

		protected void SetSerializerConfigurations(params SerializerConfig[] configs) => _currentTestConfigurations = configs;




		public void TestDeepEquality<T>(T obj, TestMode testMode = TestMode.Default, params SerializerConfig[] serializerConfigs)
		{
			if (_currentTestConfigurations == null || _currentTestConfigurations.Length == 0)
				throw new InvalidOperationException("no test configurations");

			if(serializerConfigs == null || serializerConfigs.Length == 0)
				serializerConfigs = _currentTestConfigurations;

			foreach (var config in serializerConfigs)
			{
				if (!testMode.HasFlag(TestMode.AllowNull))
					Assert.NotNull(obj);

				var clone = Clone(obj, config);

				if (!testMode.HasFlag(TestMode.AllowNull))
					Assert.NotNull(clone);

				if (!typeof(T).IsValueType)
					if (ReferenceEquals(obj, null) ^ ReferenceEquals(clone, null))
						Assert.True(false, "objects must both have a value or both be null");

				DeepComparer.Instance.CheckEquality(obj, clone);
			}
		}
		public T Clone<T>(T source, SerializerConfig config = null)
		{
			var ceras = new CerasSerializer(config);

			byte[] data = new byte[0x1000];
			int len = ceras.Serialize(source, ref data);

			T clone = default(T);
			int read = 0;
			ceras.Deserialize(ref clone, data, ref read, len);

			_runCount++;

			return clone;
		}

		protected void CheckAndResetTotalRunCount(int i)
		{
			Assert.True(_runCount == i);
			_runCount = 0;
		}

		public MethodInfo GetMethod(Expression<Action> e)
		{
			var b = e.Body;

			if (b is MethodCallExpression m)
				return m.Method;

			throw new ArgumentException();
		}

		public MethodInfo GetMethod<T>(Expression<Func<T>> e)
		{
			var b = e.Body;

			if (b is MethodCallExpression m)
				return m.Method;

			throw new ArgumentException();
		}

		public ConstructorInfo GetCtor<T>(Expression<Func<T>> e)
		{
			var b = e.Body;

			if (b is NewExpression n)
				return n.Constructor;

			throw new ArgumentException();
		}
	}

	public enum TestMode
	{
		Default = 0,
		AllowNull = 1 << 0,
	}

	public enum TestReinterpret
	{
		DefaultOn,
		TestWithout,
		TestBoth,
	}

	class DeepComparer : IEqualityComparer<object>, IEqualityComparer
	{
		public static DeepComparer Instance { get; } = new DeepComparer();

		public bool CheckEquality(object x, object y) => AreEqual(x, y);

		bool IEqualityComparer.Equals(object x, object y) => AreEqual(x, y);
		bool IEqualityComparer<object>.Equals(object x, object y) => AreEqual(x, y);
		public int GetHashCode(object obj) => obj.GetHashCode();

		public bool AreEqual(object x, object y)
		{
			if (ReferenceEquals(x, null) && ReferenceEquals(y, null))
				return true;
			else if (!ReferenceEquals(x, null) && ReferenceEquals(y, null))
				return false;
			else if (ReferenceEquals(x, null) && !ReferenceEquals(y, null))
				return false;


			if (x is IStructuralEquatable xEq)
			{
				if (x is Array xAr && xAr.Rank > 1)
				{
					var yAr = (Array)y;
					foreach (var (left, right) in ZipObj(xAr, yAr))
						if (!AreEqual(left, right))
							return false;

					return true;
				}
				else
				{
					var yEq = (IStructuralEquatable)y;
					return (xEq.Equals(yEq, DeepComparer.Instance));
				}
			}
			else if (x is IEnumerable xEnum)
			{
				var ar1 = xEnum.Cast<object>().ToArray();
				var ar2 = ((IEnumerable)y).Cast<object>().ToArray();

				foreach (var (a, b) in Zip(ar1, ar2))
					if (!AreEqual(a, b))
						return false;

				return true;
			}
			else
			{
				return x.Equals(y);
			}
		}


		static IEnumerable<(TFirst, TSecond)> Zip<TFirst, TSecond>(
				IEnumerable<TFirst> first,
				IEnumerable<TSecond> second)
		{
			if (first == null)
				throw new ArgumentNullException("first");
			if (second == null)
				throw new ArgumentNullException("second");

			using (var e1 = first.GetEnumerator())
			using (var e2 = second.GetEnumerator())
			{
				while (e1.MoveNext())
				{
					if (e2.MoveNext())
					{
						yield return (e1.Current, e2.Current);
					}
					else
					{
						throw new InvalidOperationException("Sequences differed in length");
					}
				}
				if (e2.MoveNext())
				{
					throw new InvalidOperationException("Sequences differed in length");
				}
			}
		}

		static IEnumerable<(object, object)> ZipObj(
				IEnumerable first,
				IEnumerable second)
		{
			if (first == null)
				throw new ArgumentNullException("first");
			if (second == null)
				throw new ArgumentNullException("second");

			var e1 = first.GetEnumerator();
			var e2 = second.GetEnumerator();

			while (e1.MoveNext())
			{
				if (e2.MoveNext())
				{
					yield return (e1.Current, e2.Current);
				}
				else
				{
					throw new InvalidOperationException("Sequences differed in length");
				}
			}
			if (e2.MoveNext())
			{
				throw new InvalidOperationException("Sequences differed in length");
			}

		}

	}
}
