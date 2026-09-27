using System;
using System.Collections.Generic;

#pragma warning disable 0649 // Field is never assigned to

using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Test
{
    using Xunit;

    public class ConstructionAndPooling : TestBase
    {
        static int Add1(int x) => x + 1;
        static int Add2(int x) => x + 2;


        [Fact]
        public void TestDirectPoolingMethods()
        {
            var pool = new InstancePoolTest();

            // Test: Manual config
            {
                CerasRawConfig config = new CerasRawConfig();

                config.ConfigType<Person>()
                      .ConstructBy(TypeConstruction.ByStaticMethod(() => StaticPoolTest.CreatePerson()));

                var clone = DoRoundTripTest(config);
                Assert.True(clone != null);
            }

            // Test: Normal ctor, but explicitly
            {
                CerasRawConfig config = new CerasRawConfig();

                config.ConfigType<Person>()
                      // Select ctor, not delegate
                      .ConstructBy(() => new Person());

                var clone = DoRoundTripTest(config);
                Assert.True(clone != null);
            }

            // Test: Construct from instance-pool
            {
                CerasRawConfig config = new CerasRawConfig();

                config.ConfigType<Person>()
                      // Instance + method select
                      .ConstructBy(pool, () => pool.CreatePerson());

                var clone = DoRoundTripTest(config);
                Assert.True(clone != null);
                Assert.True(pool.IsFromPool(clone));
            }

            // Test: Construct from static-pool
            {
                CerasRawConfig config = new CerasRawConfig();

                config.ConfigType<Person>()
                      // method select
                      .ConstructBy(() => StaticPoolTest.CreatePerson());

                var clone = DoRoundTripTest(config);
                Assert.True(clone != null);
                Assert.True(StaticPoolTest.IsFromPool(clone));
            }

            // Test: Construct from any delegate (in this example: a lambda expression)
            {
                CerasRawConfig config = new CerasRawConfig();

                Person referenceCapturedByLambda = null;

                config.ConfigType<Person>()
                      // Use delegate
                      .ConstructByDelegate(() =>
                      {
                          var obj = new Person();
                          referenceCapturedByLambda = obj;
                          return obj;
                      });

                var clone = DoRoundTripTest(config);
                Assert.True(clone != null);
                Assert.True(ReferenceEquals(clone, referenceCapturedByLambda));
            }

            // Parameterized constructors and factory methods are a separate, deliberately
            // unsupported case (they used to rely on the removed fixup system); see
            // ParameterizedConstruction_IsRejectedActionably below.
        }

        /// <summary>
        /// The legacy fixup system that used to map stream values into constructor/factory parameters
        /// was removed, so a parameterized construction selection made through the runtime
        /// configuration (<c>ConstructBy(() =&gt; new Person("name"))</c> or a factory method that takes
        /// an argument) can no longer be honored. This is the precise, deliberate unsupported case; it
        /// must fail loudly with actionable guidance rather than silently constructing with default
        /// arguments. Parameterized construction on the generated reader path is supported separately
        /// through <c>[CerasConstructor]</c> (see <c>ConstructorReuseTests</c>), so this is not a
        /// blanket "constructors are unsupported" claim.
        /// </summary>
        [Fact]
        public void ParameterizedConstruction_IsRejectedActionably()
        {
            // Parameterized CLR constructor selected from an expression.
            AssertUnsupported("Parameterized constructors",
                config => config.ConfigType<Person>().ConstructBy(() => new Person("name")));

            // Parameterized static factory method.
            AssertUnsupported("Factory methods with parameters",
                config => config.ConfigType<Person>().ConstructBy(() => StaticPoolTest.CreatePersonWithName("abc")));

            // Parameterized instance factory method.
            var pool = new InstancePoolTest();
            AssertUnsupported("Factory methods with parameters",
                config => config.ConfigType<Person>().ConstructBy(pool, () => pool.CreatePersonWithName("abc")));
        }

        /// <summary>
        /// Exercises an unsupported construction selection end-to-end (serialize) and asserts the
        /// actionable inner exception. The runtime formatter is created through the reflection
        /// fallback, which invokes the formatter constructor via <see cref="System.Reflection"/>; that
        /// wraps the deliberate <see cref="NotSupportedException"/> in a
        /// <see cref="System.Reflection.TargetInvocationException"/>, so the chain is unwrapped here.
        /// </summary>
        static void AssertUnsupported(string expectedMessage, Action<CerasRawConfig> configure)
        {
            var config = new CerasRawConfig();
            configure(config);

            var ceras = new CerasFastSerializer(config);

            var p = new Person { Name = "riki" };
            var thrown = Assert.ThrowsAny<Exception>(() => ceras.Serialize(p));

            var notSupported = Unwrap<NotSupportedException>(thrown);
            Assert.NotNull(notSupported);
            Assert.Contains(expectedMessage, notSupported.Message);
        }

        static T Unwrap<T>(Exception ex) where T : Exception
        {
            for (var current = ex; current != null; current = current.InnerException)
                if (current is T match)
                    return match;
            return null;
        }

        static Person DoRoundTripTest(CerasRawConfig config, string name = "riki")
        {
            var ceras = new CerasFastSerializer(config);

            var p = new Person();
            p.Name = name;

            var data = ceras.Serialize(p);

            var clone = ceras.Deserialize<Person>(data);
            return clone;
        }


        class StaticPoolTest
        {
            static HashSet<Person> _objectsCreatedByPool = new HashSet<Person>();

            public static Person CreatePerson()
            {
                var p = new Person();
                _objectsCreatedByPool.Add(p);
                return p;
            }

            public static Person CreatePersonWithName(string name)
            {
                var p = new Person();
                p.Name = name;
                _objectsCreatedByPool.Add(p);
                return p;
            }

            public static bool IsFromPool(Person p) => _objectsCreatedByPool.Contains(p);

            public static void DiscardPooledObjectTest(Person p)
            {
            }
        }

        class InstancePoolTest
        {
            HashSet<Person> _objectsCreatedByPool = new HashSet<Person>();

            public Person CreatePerson()
            {
                var p = new Person();
                _objectsCreatedByPool.Add(p);
                return p;
            }

            public Person CreatePersonWithName(string name)
            {
                var p = new Person();
                p.Name = name;
                _objectsCreatedByPool.Add(p);
                return p;
            }

            public bool IsFromPool(Person p) => _objectsCreatedByPool.Contains(p);

            public void DiscardPooledObjectTest(Person p)
            {
            }
        }
        
        class Person
        {
            public const string CtorSuffix = " (modified by constructor)";

            public string Name;
            public int Health;
            public Person BestFriend;

            public Person()
            {
            }

            public Person(string name)
            {
                Name = name + CtorSuffix;
            }

            public int GetHealth() => Health;

            public string SayHello() => $"Hello I'm {Name}";
        }

    }
}
