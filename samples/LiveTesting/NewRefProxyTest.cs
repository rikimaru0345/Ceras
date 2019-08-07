using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveTesting
{
	using Ceras;
	using Ceras.Helpers;
	using System.Runtime.CompilerServices;
	using Ceras.Formatters;
	using System.Linq.Expressions;

	static class NewRefProxyTest
	{
		const int ConstCacheSize = 2000;

		// Serialization: array search
		// Deserialization: array
		struct BetterCache
		{
			int _nextSlot;
			object[] _ar;

			public static BetterCache Create(int baseSize)
			{
				var obj = new BetterCache();
				obj._ar = new object[baseSize];
				return obj;
			}

			// Serialization
			internal bool TryGetExistingObjectId(object value, out int id)
			{
				for (int i = 0; i < _nextSlot; i++)
				{
					// var proxy = Unsafe.As<object, T>(ref _ar[i]);

					if (ReferenceEquals(value, _ar[i]))
					{
						id = i;
						return true;
					}
				}

				id = 0;
				return false;
			}

			internal int RegisterObject(object obj)
			{
				var slot = _nextSlot;

				ref var proxy = ref _ar[slot];
				proxy = obj;

				_nextSlot++;

				return slot;
			}

			internal void ResetSerialization()
			{
				Array.Clear(_ar, 0, _nextSlot - 1);
				_nextSlot = 0;
			}


			// Deserialization
			internal ref T CreateDeserializationProxy<T>() where T : class
			{
				var index = _nextSlot;
				_nextSlot++;

				ref object slot = ref _ar[index];
				ref T proxy = ref Unsafe.As<object, T>(ref slot);

				return ref proxy;
			}

			internal T GetExistingObject<T>(int id) where T : class
			{
				ref object slot = ref _ar[id];
				return Unsafe.As<object, T>(ref slot);
			}

			internal void ResetDeserialization()
			{
				Array.Clear(_ar, 0, _nextSlot - 1);
				_nextSlot = 0;
			}
		}

		// Serialization: dictionary
		// Deserialization: array
		struct BetterCache2
		{
			Dictionary<object, int> _serializationCache;

			int _nextSlot;
			object[] _ar;


			public static BetterCache2 Create(int baseSize)
			{
				var obj = new BetterCache2();
				obj._ar = new object[baseSize];
				obj._serializationCache = new Dictionary<object, int>(64);

				return obj;
			}

			// Serialization
			internal bool TryGetExistingObjectId<T>(T value, out int id) where T : class
			{
				return _serializationCache.TryGetValue(value, out id);
			}

			internal int RegisterObject<T>(T value) where T : class
			{
				var id = _serializationCache.Count;

				_serializationCache.Add(value, id);

				return id;
			}

			internal void ResetSerialization()
			{
				_serializationCache.Clear();
			}


			// Deserialization
			internal ref T CreateDeserializationProxy<T>() where T : class
			{
				var index = _nextSlot;
				_nextSlot++;

				ref T proxy = ref Unsafe.As<object, T>(ref _ar[index]);

				return ref proxy;
			}

			internal T GetExistingObject<T>(int id) where T : class
			{
				return Unsafe.As<object, T>(ref _ar[id]);
			}

			internal void ResetDeserialization()
			{
				Array.Clear(_ar, 0, _nextSlot - 1);
				_nextSlot = 0;
			}
		}


		

		class RefFormatter<T> : IFormatter<T> where T : class
		{
			const int Null = -1;
			const int New = -2;

			public BetterCache Cache = BetterCache.Create(ConstCacheSize);
			public IFormatter<T> InnerFormatter;

			public void Serialize(ref byte[] buffer, ref int offset, T p)
			{
				if (ReferenceEquals(p, null))
				{
					// Null
					SerializerBinary.WriteInt32(ref buffer, ref offset, Null);
				}
				else if (Cache.TryGetExistingObjectId(p, out int existingId))
				{
					// Existing
					SerializerBinary.WriteInt32(ref buffer, ref offset, existingId);
				}
				else
				{
					// New
					SerializerBinary.WriteInt32(ref buffer, ref offset, New);
					Cache.RegisterObject(p);
					InnerFormatter.Serialize(ref buffer, ref offset, p);
				}
			}

			public void Deserialize(byte[] buffer, ref int offset, ref T value)
			{
				var id = SerializerBinary.ReadInt32(buffer, ref offset);
				switch (id)
				{
				case Null: // Null
					value = default;
					break;

				case New: // Read New
					if (value == null)
						value = Ctor<T>.New();

					ref var proxy = ref Cache.CreateDeserializationProxy<T>();

					proxy = value;
					InnerFormatter.Deserialize(buffer, ref offset, ref proxy);
					value = proxy;
					break;

				default: // Existing
					value = Cache.GetExistingObject<T>(id);
					break;
				}
			}
		}

		class PersonFormatter : IFormatter<Person>
		{
			public RefFormatter<Person> RefFormatter;

			public void Serialize(ref byte[] buffer, ref int offset, Person p)
			{
				SerializerBinary.WriteString(ref buffer, ref offset, p.Name);
				SerializerBinary.WriteInt32(ref buffer, ref offset, p.Health);
				RefFormatter.Serialize(ref buffer, ref offset, p.Friend1);
				RefFormatter.Serialize(ref buffer, ref offset, p.Friend2);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Person value)
			{
				value.Name = SerializerBinary.ReadString(buffer, ref offset);
				value.Health = SerializerBinary.ReadInt32(buffer, ref offset);
				RefFormatter.Deserialize(buffer, ref offset, ref value.Friend1);
				RefFormatter.Deserialize(buffer, ref offset, ref value.Friend2);
			}
		}


		class RefFormatterReaderWriter<T> : IFormatterNew<T> where T : class
		{
			const int Null = -1;
			const int New = -2;

			public BetterCache Cache = BetterCache.Create(ConstCacheSize);
			public IFormatterNew<T> InnerFormatter;

			public void Serialize(ref Writer writer, T p)
			{
				if (ReferenceEquals(p, null))
				{
					// Null
					writer.WriteInt32(Null);
				}
				else if (Cache.TryGetExistingObjectId(p, out int existingId))
				{
					// Existing
					writer.WriteInt32(existingId);
				}
				else
				{
					// New
					writer.WriteInt32(New);
					Cache.RegisterObject(p);
					InnerFormatter.Serialize(ref writer, p);
				}
			}

			public void Deserialize(ref Reader reader, ref T value)
			{
				var id = reader.ReadInt32();
				switch (id)
				{
				case Null: // Null
					value = default;
					break;

				case New: // Read New
					if (value == null)
						value = Ctor<T>.New();

					ref var proxy = ref Cache.CreateDeserializationProxy<T>();

					proxy = value;
					InnerFormatter.Deserialize(ref reader, ref proxy);
					value = proxy;
					break;

				default: // Existing
					value = Cache.GetExistingObject<T>(id);
					break;
				}
			}
		}

		class PersonFormatterReaderWriter : IFormatterNew<Person>
		{
			public RefFormatterReaderWriter<Person> RefFormatter;

			public void Serialize(ref Writer writer, Person p)
			{
				writer.WriteString(p.Name);
				writer.WriteInt32(p.Health);
				RefFormatter.Serialize(ref writer, p.Friend1);
				RefFormatter.Serialize(ref writer, p.Friend2);
			}

			public void Deserialize(ref Reader reader, ref Person value)
			{
				value.Name = reader.ReadString();
				value.Health = reader.ReadInt32();
				RefFormatter.Deserialize(ref reader, ref value.Friend1);
				RefFormatter.Deserialize(ref reader, ref value.Friend2);
			}
		}


		class RefFormatterNewCache2<T> : IFormatter<T> where T : class
		{
			const int Null = -1;
			const int New = -2;

			public BetterCache2 Cache = BetterCache2.Create(ConstCacheSize);
			public IFormatter<T> InnerFormatter;

			public void Serialize(ref byte[] buffer, ref int offset, T p)
			{
				if (ReferenceEquals(p, null))
				{
					// Null
					SerializerBinary.WriteInt32(ref buffer, ref offset, Null);
				}
				else if (Cache.TryGetExistingObjectId(p, out int existingId))
				{
					// Existing
					SerializerBinary.WriteInt32(ref buffer, ref offset, existingId);
				}
				else
				{
					// New
					SerializerBinary.WriteInt32(ref buffer, ref offset, New);
					Cache.RegisterObject(p);
					InnerFormatter.Serialize(ref buffer, ref offset, p);
				}
			}

			public void Deserialize(byte[] buffer, ref int offset, ref T value)
			{
				var id = SerializerBinary.ReadInt32(buffer, ref offset);
				switch (id)
				{
				case Null: // Null
					value = default;
					break;

				case New: // Read New
					if (value == null)
						value = Ctor<T>.New();

					ref var proxy = ref Cache.CreateDeserializationProxy<T>();

					proxy = value;
					InnerFormatter.Deserialize(buffer, ref offset, ref proxy);
					value = proxy;
					break;

				default: // Existing
					value = Cache.GetExistingObject<T>(id);
					break;
				}
			}
		}

		class PersonFormatterNewCache2 : IFormatter<Person>
		{
			public RefFormatterNewCache2<Person> RefFormatter;

			public void Serialize(ref byte[] buffer, ref int offset, Person p)
			{
				SerializerBinary.WriteString(ref buffer, ref offset, p.Name);
				SerializerBinary.WriteInt32(ref buffer, ref offset, p.Health);
				RefFormatter.Serialize(ref buffer, ref offset, p.Friend1);
				RefFormatter.Serialize(ref buffer, ref offset, p.Friend2);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Person value)
			{
				value.Name = SerializerBinary.ReadString(buffer, ref offset);
				value.Health = SerializerBinary.ReadInt32(buffer, ref offset);
				RefFormatter.Deserialize(buffer, ref offset, ref value.Friend1);
				RefFormatter.Deserialize(buffer, ref offset, ref value.Friend2);
			}
		}



		static byte[] _buffer = new byte[100];

		static T Clone<T>(T obj, IFormatter<T> formatter)
		{
			int offset = 0;
			formatter.Serialize(ref _buffer, ref offset, obj);

			offset = 0;
			T clone = default;
			formatter.Deserialize(_buffer, ref offset, ref clone);

			return clone;
		}

		public static void ReinterpretRefProxyTest()
		{
			var refPersonFormatter = new RefFormatter<Person>();
			var personFormatter = new PersonFormatter();
			refPersonFormatter.InnerFormatter = personFormatter;
			personFormatter.RefFormatter = refPersonFormatter;

			var refPersonFormatter2 = new RefFormatterReaderWriter<Person>();
			var personFormatter2 = new PersonFormatterReaderWriter();
			refPersonFormatter2.InnerFormatter = personFormatter2;
			personFormatter2.RefFormatter = refPersonFormatter2;
			
			var refPersonFormatterNewCache2 = new RefFormatterNewCache2<Person>();
			var personFormatterNewCache2 = new PersonFormatterNewCache2();
			refPersonFormatterNewCache2.InnerFormatter = personFormatterNewCache2;
			personFormatterNewCache2.RefFormatter = refPersonFormatterNewCache2;

			var p1 = new Person { Name = "riki", Health = 5 };

			p1.Friend1 = p1;

			Person lastCreated = p1;
			for (int i = 0; i < 300; i++)
			{
				var pi = new Person { Health = 100 + i, Name = "p" + i };
				pi.Friend1 = pi;

				lastCreated.Friend2 = pi;
				lastCreated = pi;
			}


			for (int i = 0; i < 5; i++)
				MicroBenchmark.Run(2,
					("NewCache(ar,ar)", () => Clone(p1, refPersonFormatter)),
					("NewCache2(dict,ar)", () => Clone(p1, refPersonFormatterNewCache2)), // 4-14%

					("empty", () => { }
				));

			Console.WriteLine("done");
			Console.ReadKey();

			new Ceras.Test.Internals().BoxedReferencesAreNotCached();
		}


		internal static void CompareCalls()
		{
			var comp = new CompareCallsTest();
			comp.Prepare();

			for (int i = 0; i < 10; i++)
				comp.Test();

			Console.WriteLine("done");
			Console.ReadKey();
		}

		// Results:
		// - Anything other than simple takes 1.3x ~ 1.8x longer.
		// - readonly doesn't help at all.
		class CompareCallsTest
		{
			public Adder addSimple;
			public IAdder addImplicit;
			public IAdder addExplicit;
			public AdderBase addBase;

			public readonly Adder addSimpleR;
			public readonly IAdder addImplicitR;
			public readonly IAdder addExplicitR;
			public readonly AdderBase addBaseR;

			[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
			public CompareCallsTest()
			{
				addSimpleR = new Adder();
				addImplicitR = new AdderImplicitInterface();
				addExplicitR = new AdderExplicitInterface();
				addBaseR = new AdderImpl();
			}

			[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
			public void Prepare()
			{
				addSimple = new Adder();
				addImplicit = new AdderImplicitInterface();
				addExplicit = new AdderExplicitInterface();
				addBase = new AdderImpl();
			}

			public void Test()
			{
				int counter = 0;

				//MicroBenchmark.Run(5, new (string, Action)[]
				//{
				//	("addSimple", () => counter = addSimple.Add(counter, 1)),
				//	("addImplicit", () => counter = addImplicit.Add(counter, 1)),
				//	("addExplicit", () => counter = addExplicit.Add(counter, 1)),
				//	("addBase", () => counter = addBase.Add(counter, 1)),

				//	("addSimpleR", () => counter = addSimpleR.Add(counter, 1)),
				//	("addImplicitR", () => counter = addImplicitR.Add(counter, 1)),
				//	("addExplicitR", () => counter = addExplicitR.Add(counter, 1)),
				//	("addBaseR", () => counter = addBaseR.Add(counter, 1)),
				//});
			}
		}


		class Adder
		{
			public int Add(int a, int b) => a + b;
		}

		interface IAdder
		{
			int Add(int a, int b);
		}

		class AdderImplicitInterface : IAdder
		{
			public int Add(int a, int b) => a + b;
		}

		class AdderExplicitInterface : IAdder
		{
			int IAdder.Add(int a, int b) => a + b;
		}

		abstract class AdderBase
		{
			public abstract int Add(int a, int b);
		}

		class AdderImpl : AdderBase
		{
			public override int Add(int a, int b) => a + b;
		}



		ref struct Writer
		{
			public byte[] buffer;
			public int offset;

			public void WriteInt32(int value) => SerializerBinary.WriteInt32(ref buffer, ref offset, value);
			public void WriteString(string value) => SerializerBinary.WriteString(ref buffer, ref offset, value);
		}

		ref struct Reader
		{
			public byte[] buffer;
			public int offset;

			public int ReadInt32() => SerializerBinary.ReadInt32(buffer, ref offset);
			public string ReadString() => SerializerBinary.ReadString(buffer, ref offset);
		}

		interface IFormatterNew<T> : IFormatter
		{
			void Serialize(ref Writer writer, T value);
			void Deserialize(ref Reader reader, ref T value);
		}

		static class CtorHelper
		{
			public static readonly Func<object> Null = () => null;

			public static Func<object> GetNew(Type type)
			{
				return Expression.Lambda<Func<object>>(Expression.New(type)).Compile();
			}
		}

		static class Ctor<T> where T : class
		{
			public static readonly Func<T> New;

			static Ctor()
			{
				if (typeof(T).IsArray || typeof(T).IsValueType)
				{
					New = Unsafe.As<Func<T>>(CtorHelper.Null);
				}
				else
				{
					New = Unsafe.As<Func<T>>(CtorHelper.GetNew(typeof(T)));
				}
			}
		}

		class Person
		{
			public string Name;
			public int Health;
			public Person Friend1;
			public Person Friend2;
		}
	}
}
