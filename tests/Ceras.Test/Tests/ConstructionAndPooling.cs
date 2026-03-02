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


        [Fact(Skip = "Parameterized constructors are no longer supported")]
        public void TestDirectPoolingMethods()
        {
            var pool = new InstancePoolTest();

            // Test: Ctor with argument
            {
                CerasRawConfig config = new CerasRawConfig();

                config.ConfigType<Person>()
                      // Select ctor, not delegate
                      .ConstructBy(() => new Person("name"));

                var clone = DoRoundTripTest(config);
                Assert.True(clone != null);
                Assert.StartsWith("riki", clone.Name);
                Assert.EndsWith(Person.CtorSuffix, clone.Name);
            }

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

            // Test: Construct from instance-pool, with parameter
            {
                CerasRawConfig config = new CerasRawConfig();

                config.ConfigType<Person>()
                      // Use instance + method selection
                      .ConstructBy(pool, () => pool.CreatePersonWithName("abc"));

                var clone = DoRoundTripTest(config);
                Assert.True(clone != null);
                Assert.StartsWith("riki", clone.Name);
                Assert.True(pool.IsFromPool(clone));
            }

            // Test: Construct from static-pool, with parameter
            {
                CerasRawConfig config = new CerasRawConfig();

                config.ConfigType<Person>()
                      // Use instance + method selection
                      .ConstructBy(() => StaticPoolTest.CreatePersonWithName("abc"));

                var clone = DoRoundTripTest(config);
                Assert.True(clone != null);
                Assert.StartsWith("riki", clone.Name);
                Assert.True(StaticPoolTest.IsFromPool(clone));
            }
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
