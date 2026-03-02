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


        protected CerasConfig CreateConfig(Action<CerasConfig> f, bool robust = false)
        {
            if (robust)
            {
                var robustSerializer = new CerasRobustSerializer(config => f(config));
                return robustSerializer._config;
            }
            else
            {
                var config = new CerasRawConfig();
                f(config);
                return config;
            }
        }

        protected Func<CerasConfig> Config_WithReinterpret => () => CreateConfig(x =>
        {
            x.AllowReflectionFallback = true;
            if (x is CerasRawConfig raw) raw.UseReinterpretFormatter = true;
            x.UseImmutableFormatters();
        });
        protected Func<CerasConfig> Config_NoReinterpret => () => CreateConfig(x =>
        {
            x.AllowReflectionFallback = true;
            if (x is CerasRawConfig raw) raw.UseReinterpretFormatter = false;
            x.UseImmutableFormatters();
        });
        protected Func<CerasConfig> Config_WithVersioning => () => CreateConfig(x =>
        {
            x.AllowReflectionFallback = true;
            x.UseImmutableFormatters();
        }, robust: true);

        Func<CerasConfig>[] _currentTestConfigurations;

        public TestBase()
        {
            _currentTestConfigurations = new Func<CerasConfig>[]
            {
                () => new CerasRawConfig { AllowReflectionFallback = true },
                () => CreateConfig(x => { x.AllowReflectionFallback = true; x.UseImmutableFormatters(); }),
                () => CreateConfig(x => { x.AllowReflectionFallback = true; x.UseImmutableFormatters(); }, robust: true),
            };
        }

        protected void SetSerializerConfigurations(params Func<CerasConfig>[] configs) => _currentTestConfigurations = configs;




        public void TestDeepEquality<T>(T obj, TestMode testMode = TestMode.Default)
        {
            if (_currentTestConfigurations == null || _currentTestConfigurations.Length == 0)
                throw new InvalidOperationException("no test configurations");

            foreach (var configFunc in _currentTestConfigurations)
            {
                if (!testMode.HasFlag(TestMode.AllowNull))
                    Assert.NotNull(obj);

                var config = configFunc();
                var clone = Clone(obj, config);

                if (!testMode.HasFlag(TestMode.AllowNull))
                    Assert.NotNull(clone);

                if (!typeof(T).IsValueType)
                    if (ReferenceEquals(obj, null) ^ ReferenceEquals(clone, null))
                        Assert.Fail("objects must both have a value or both be null");

                bool areEqual = DeepComparer.Instance.CheckEquality(obj, clone);
                Assert.True(areEqual, "Equality test failed!");
            }
        }


        public T Clone<T>(T source, CerasConfig config = null)
        {
            if (config == null) config = new CerasRawConfig();

            // Try to find and call the source-generated registration helper
            var generatedType = Type.GetType("Ceras.Generated.RobustFormatters, Ceras.Generated") ??
                                Type.GetType("Ceras.Generated.RobustFormatters");

            if (generatedType != null)
            {
                var method = generatedType.GetMethod("UseRobustFormatters", BindingFlags.Static | BindingFlags.Public);
                if (method != null)
                    method.Invoke(null, new object[] { config });
            }

            if (config is CerasRobustConfig robustConfig)
            {
                var robustSerializer = (CerasRobustSerializer)robustConfig.Serializer;
                var robustData = robustSerializer.Save(source, 1);
                var cloneResult = robustSerializer.Load<T>(robustData, 1);
                _callsToClone++;
                return cloneResult;
            }

            var serializer = new CerasFastSerializer((CerasRawConfig)config);

            var data = serializer.Serialize(source);
            var cloneResult2 = serializer.Deserialize<T>(data);

            _callsToClone++;

            return cloneResult2;
        }

        int _callsToClone = 0;
        protected void CheckAndResetTotalRunCount(int i)
        {
            int expected = (i / 3) * _currentTestConfigurations.Length;
            Assert.True(_callsToClone == expected, $"Expected {expected} clones, but got {_callsToClone}");
            _callsToClone = 0;
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
                    if (xEq.Equals(yEq, DeepComparer.Instance))
                        return true;
                }
            }
            else if (x is IEnumerable xEnum)
            {
                var ar1 = xEnum.Cast<object>().ToArray();
                var yEnum = (IEnumerable)y;
                if (yEnum == null) return false;
                var ar2 = yEnum.Cast<object>().ToArray();

                if (ar1.Length != ar2.Length) return false;

                foreach (var (a, b) in Zip(ar1, ar2))
                    if (!AreEqual(a, b))
                        return false;

                return true;
            }
            else
            {
                if (x.Equals(y))
                    return true;

            }

            return false;
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
