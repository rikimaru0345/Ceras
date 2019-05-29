using Ceras;
using Ceras.Formatters;
using Ceras.Helpers;
using Ceras.Resolvers;
using Nerdbank.Streams;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiveTesting.MergeBlittingTest
{
	using static Expression;

	struct Vector3
	{
		public float X, Y, Z;

		public Vector3(float x, float y, float z)
		{
			X = x;
			Y = y;
			Z = z;
		}
	}

	class SampleObject
	{
		public string Text1;
		public float Float1;
		public int Int1;
		public float Float2;
		public string Text2;
		public int Int2;
		public string Text3;
		public int Int3;
	}

	//class SampleObject
	//{
	//	public string Text1 { get; set; }
	//	public float Float1 { get; set; }
	//	public int Int1 { get; set; }
	//	public float Float2 { get; set; }
	//	public string Text2 { get; set; }
	//	public int Int2 { get; set; }
	//	public string Text3 { get; set; }
	//	public int Int3 { get; set; }
	//}

	// Lessons learned:
	// 1) passing simple values by ref makes no difference
	// 2) don't cache 'offset' into a local
	// 3) Code using "Unsafe.As" should be as short as possible 
	// 4) Using the "Unsafe" class is a *lot* faster than any manual pointer/unsafe code we can manually write; as long as we stay with 'ref' and never use any pointers.
	//    We can completely bypass 'fixed()', the runtime seems to be heavily optimized for that
	// 5) Calling an interface method is slower (-15%) when implemented by a struct instead of a class 
	//
	// 2 and 3 probably mean that the compiler is bad with local variables for some reason

	//
	// MergeBlitting and Inlining
	// Target object is a 'struct Vector3' with 3 floats.
	//
	// Baseline
	// We comapre against a 'classic' formatter that contains an 'IFormatter<float>' field which gets called 3 times.
	//
	// DynamicFormatter (MergeBlitting=ON) inlining all calls
	// +71% faster
	//
	// Even for objects (SampleObject) there is still an improvement (+2-15% with props) (+4-10% with fields)
	//
	internal class MergeBlittingTest
	{
		static byte[] _buffer = new byte[1000];
		static void DoTest<T>(T value, IFormatter<T> formatter)
		{
			int offset = 0;
			formatter.Serialize(ref _buffer, ref offset, value);

			offset = 0;
			T clone = default;
			formatter.Deserialize(_buffer, ref offset, ref clone);
		}

		static void DoTest<T>(T value, IFormatter<T> formatter, ref T cloneTarget)
		{
			int offset = 0;
			formatter.Serialize(ref _buffer, ref offset, value);

			offset = 0;
			formatter.Deserialize(_buffer, ref offset, ref cloneTarget);
		}

		static IFormatter<Vector3> CreateDynamicFormatterWithOptions(Action<SerializerConfig> changeConfig)
		{
			var config = new SerializerConfig();
			config.ConfigType<Vector3>().CustomResolver = (c, t) => c.Advanced.GetFormatterResolver<DynamicObjectFormatterResolver>().GetFormatter(t);
			changeConfig(config);

			var ceras = new CerasSerializer(config);
			return (DynamicFormatter<Vector3>)ceras.GetSpecificFormatter(typeof(Vector3));
		}

		internal static void Test()
		{
			/*
			var defaultF = new DefaultFormatter();
			var defaultFWithCaching = new ImprovedDefaultFormatter();

			IFormatter<Vector3> dynamicNoOptimizations = CreateDynamicFormatterWithOptions(c =>
			{
				c.Experimental.MergeBlittableCalls = false;
				c.Experimental.InlineCalls = false;
			});

			IFormatter<Vector3> dynamicInlineOnly = CreateDynamicFormatterWithOptions(c =>
			{
				c.Experimental.MergeBlittableCalls = false;
				c.Experimental.InlineCalls = true;
			});

			IFormatter<Vector3> dynamicMergeBlit = CreateDynamicFormatterWithOptions(c =>
			{
				c.Experimental.MergeBlittableCalls = true;
				c.Experimental.InlineCalls = false;
			});

			var reinterpret = new ReinterpretFormatter<Vector3>();

			var manual1 = new ManualFormatter();
			var manual5 = new Manual5Formatter();
			var manual6 = new Manual6Formatter();
			var manual7 = new Manual7Formatter();

			var simpleDynamicMergeBlit = new DynamicMergeBlitFormatter();
			var simpleDynamicMergeBlit2 = new DynamicMergeBlit2Formatter();
			var simpleDynamicMergeBlit3 = new DynamicMergeBlit3Formatter();

			var value = new Vector3(2325.123123f, -3524625.3424f, -0.2034324234234f);

			var jobs = new BenchJob[]
				{
					("Default", () => DoTest(value, defaultF)),
					("Default v2", () => DoTest(value, defaultFWithCaching)), // No improvement
					
					("DynamicFormatter (no options)", () => DoTest(value, dynamicNoOptimizations)), // +33%
					("DynamicFormatter (inline only)", () => DoTest(value, dynamicInlineOnly)), // +28%
					("DynamicFormatter (merge blit)", () => DoTest(value, dynamicMergeBlit)), // +33%

					("Reinterpret", () => DoTest(value, reinterpret)), // +95%

					("Manual 1", () => DoTest(value, manual1)), // +85%
					//("Manual 5", () => DoTest(value, manual5)),
					//("Manual 6", () => DoTest(value, manual6)),
					//("Manual 7", () => DoTest(value, manual7)),

					//("DynamicMergeBlit", () => DoTest(value, simpleDynamicMergeBlit)),
					//("DynamicMergeBlit2", () => DoTest(value, simpleDynamicMergeBlit2)),
					("DynamicMergeBlit3", () => DoTest(value, simpleDynamicMergeBlit3)), // +57%
				};

			var runTimes = new[]{ 5, 5, 20, 20, 20 };
			foreach(var t in runTimes)
				MicroBenchmark.Run(t, jobs);

			Console.WriteLine("done");
			Console.ReadKey();
			*/
		}

		static void TestWithObject()
		{
			/*
			var defaultF = new DefaultFormatter();

			IFormatter<SampleObject> dynamic1;
			IFormatter<SampleObject> dynamic2;
			IFormatter<SampleObject> dynamic3;

			{
				var config = new SerializerConfig();
				config.ConfigType<SampleObject>().CustomResolver = (c, t) => c.Advanced.GetFormatterResolver<DynamicObjectFormatterResolver>().GetFormatter(t);
				config.Experimental.MergeBlittableCalls = false;
				config.Experimental.InlineCalls = false;
				var ceras = new CerasSerializer(config);
				dynamic1 = (DynamicFormatter<SampleObject>)ceras.GetSpecificFormatter(typeof(SampleObject));
			}

			{
				var config = new SerializerConfig();
				config.ConfigType<SampleObject>().CustomResolver = (c, t) => c.Advanced.GetFormatterResolver<DynamicObjectFormatterResolver>().GetFormatter(t);
				config.Experimental.MergeBlittableCalls = true;
				config.Experimental.InlineCalls = false;
				var ceras = new CerasSerializer(config);
				dynamic2 = (DynamicFormatter<SampleObject>)ceras.GetSpecificFormatter(typeof(SampleObject));
			}

			{
				var config = new SerializerConfig();
				config.ConfigType<SampleObject>().CustomResolver = (c, t) => c.Advanced.GetFormatterResolver<DynamicObjectFormatterResolver>().GetFormatter(t);
				config.Experimental.MergeBlittableCalls = true;
				config.Experimental.InlineCalls = true;
				var ceras = new CerasSerializer(config);
				dynamic3 = (DynamicFormatter<SampleObject>)ceras.GetSpecificFormatter(typeof(SampleObject));
			}

			var value = new SampleObject
			{
				Text1 = "abc",
				Float1 = -12345667,
				Float2 = 0.23452525f,
				Text2 = "xyz",
				Text3 = "asdasdasd",
				Int1 = 52221,
				Int2 = -2_013_677_673,
				Int3 = 1,
			};

			var clonedObject = new SampleObject();

			var jobs = new BenchJob[]
				{
					("DynamicFormatter (no merge, no inline)",          () => DoTest(value, dynamic1, ref clonedObject)),
					("DynamicFormatter (MergeBlit)",                    () => DoTest(value, dynamic2, ref clonedObject)),
					("DynamicFormatter (MergeBlit, InlineCalls)",       () => DoTest(value, dynamic3, ref clonedObject)),
				};

			MicroBenchmark.Run(5, jobs);
			MicroBenchmark.Run(10, jobs);
			MicroBenchmark.Run(30, jobs);
			MicroBenchmark.Run(30, jobs);
			MicroBenchmark.Run(30, jobs);

			Console.WriteLine("done");
			Console.ReadKey();
			*/
		}
	}

	internal class UnsafeAddition
	{
		private static readonly SequencePool reusableSequenceWithMinSize = new SequencePool(Environment.ProcessorCount);

		const int numberCount = 100;
		const int bufferSize = numberCount * 4;
		static byte[] _staticBuffer = new byte[bufferSize];


		internal static void Test()
		{
			var write1 = new SerializeDelegate<int>(Write1);
			var write2 = new SerializeDelegate<int>(Write2);
			var writePoc = new PocDelegate<int>(WritePoc);

			var writeStruct1 = new StructDelegate<int>(WriteStruct1);
			var writeStruct2 = new StructDelegate<int>(WriteStruct2);

			var writeUnsafe1 = new UnsafeDelegate<int>(WriteUnsafe1);
			var writeUnsafe2 = new UnsafeDelegate<int>(WriteUnsafe2);
			var writeUnsafe3 = new UnsafeDelegate<int>(WriteUnsafe3);

			var write4 = new BufferWriterDelegate<int>(Write4);

			var write8 = new WriterDelegate<int>(Write8);

			for (int i = 0; i < 5; i++)
				MicroBenchmark.Run(5, new BenchJob[]
				{
					("Write1", () => Execute(write1)),
					//("Write2", () => Execute(write2)),
					("WriteStruct1", () => ExecuteStruct(writeStruct1)),
					("WriteStruct2", () => ExecuteStruct(writeStruct2)),
					("WriteUnsafe1", () => ExecuteUnsafe(writeUnsafe1)),
					//("WriteUnsafe2", () => ExecuteUnsafe(writeUnsafe2)),
					//("WriteUnsafe3", () => ExecuteUnsafe(writeUnsafe3)),
					//("WritePoc", () => ExecutePoc(writePoc)),
					//("Write4", () => ExecuteWriter(write4)),
					//("Write8", () => ExecuteNewWriter(write8)),
				});

			Console.WriteLine("done");
			Console.ReadKey();
		}

		#region Classic (Unsafe.As)

		static void Execute(SerializeDelegate<int> func)
		{
			int offset = 0;
			for (int i = 0; i < numberCount; i++)
			{
				func(ref _staticBuffer, ref offset, i * 2);
			}
		}

		// ref buffer[offset]
		static void Write1(ref byte[] buffer, ref int offset, int value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, 4);

			Unsafe.As<byte, int>(ref buffer[offset]) = value;
			offset += 4;
		}

		// ref Unsafe.Add(ref buffer[0], offset)
		static void Write2(ref byte[] buffer, ref int offset, int value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, 4);

			Unsafe.As<byte, int>(ref Unsafe.Add(ref buffer[0], offset)) = value;
			offset += 4;
		}

		#endregion

		#region Classic Struct (like Classic, but passing a struct around instead of both 'buffer' and 'offset')

		delegate void StructDelegate<T>(ref StructWriter writer, T value);

		static void ExecuteStruct(StructDelegate<int> func)
		{
			var writer = new StructWriter(_staticBuffer);
			for (int i = 0; i < numberCount; i++)
			{
				func(ref writer, i * 2);
			}
		}

		static void WriteStruct1(ref StructWriter writer, int value)
		{
			writer.Ensure(4);
			writer.As<int>() = value;
			writer.Advance(4);
		}

		static void WriteStruct2(ref StructWriter writer, int value)
		{
			writer.Write(value);
		}


		ref struct StructWriter
		{
			byte[] _array;
			int _offset;

			public StructWriter(byte[] array)
			{
				_array = array;
				_offset = 0;
			}

			/// <summary>
			/// Calls Ensure(); As()=value; and Advance() for you
			/// <para>If you write more than one single thing, you better call Ensure() once with the sum of everything, then write all your stuff with As() + Advance() calls</para>
			/// </summary>
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Write<T>(T value)
			{
				SerializerBinary.EnsureCapacity(ref _array, _offset, Unsafe.SizeOf<T>());
				Unsafe.As<byte, T>(ref _array[_offset]) = value;
				_offset += Unsafe.SizeOf<T>();
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public ref T As<T>()
				=> ref Unsafe.As<byte, T>(ref _array[_offset]);

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Advance(int bytes)
			{
				_offset += bytes;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Ensure(int requiredSize)
			{
				SerializerBinary.EnsureCapacity(ref _array, _offset, requiredSize);
			}
		}

		#endregion

		#region Unsafe Pointer (GCHandle.Alloc, void*, ...)

		delegate void UnsafeDelegate<T>(ref UnsafeWriter writer, T value);

		static void ExecuteUnsafe(UnsafeDelegate<int> func)
		{
			UnsafeWriter writer = new UnsafeWriter(_staticBuffer);

			for (int i = 0; i < numberCount; i++)
			{
				func(ref writer, i * 2);
			}

			writer.Dispose();
		}

		static void WriteUnsafe1(ref UnsafeWriter writer, int value)
		{
			writer.Ensure(4);
			writer.As<int>() = value;
			writer.Advance(4);
		}
		static void WriteUnsafe2(ref UnsafeWriter writer, int value)
		{
			writer.Write(ref value);
		}
		static void WriteUnsafe3(ref UnsafeWriter writer, int value)
		{
			writer.Write(value);
		}

		unsafe ref struct UnsafeWriter
		{
			void* _ptr;
			int _remainingSpace;

			byte[] _array;
			GCHandle _gcHandle;

			public UnsafeWriter(byte[] array)
			{
				_array = array;
				_gcHandle = GCHandle.Alloc(array, GCHandleType.Pinned);

				_remainingSpace = array.Length;

				_ptr = (byte*)_gcHandle.AddrOfPinnedObject();
			}

			public void Write<T>(ref T value)
			{
				Ensure(Unsafe.SizeOf<T>());

				As<T>() = value;

				Advance(Unsafe.SizeOf<T>());
			}

			public void Write<T>(T value)
			{
				Ensure(Unsafe.SizeOf<T>());

				As<T>() = value;

				Advance(Unsafe.SizeOf<T>());
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public ref T As<T>()
				=> ref Unsafe.AsRef<T>(_ptr);

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Advance(int bytes)
			{
				_remainingSpace -= bytes;
				_ptr = Unsafe.Add<byte>(_ptr, bytes);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Ensure(int requiredSize)
			{
				if (_remainingSpace >= requiredSize)
					return;
				Expand();
			}

			[MethodImpl(MethodImplOptions.NoInlining)]
			void Expand()
			{
				_gcHandle.Free();

				int writtenBytes = _array.Length - _remainingSpace;

				SerializerBinary.ExpandBuffer(ref _array, _array.Length * 2);

				_remainingSpace = _array.Length - writtenBytes;
				_gcHandle = GCHandle.Alloc(_array, GCHandleType.Pinned);
			}

			public void Dispose()
			{
				if (_gcHandle != null && _gcHandle.IsAllocated)
				{
					_gcHandle.Free();
				}
			}
		}

		#endregion

		#region Proof of concept (keep track of _currentTotalOffset)

		delegate void PocDelegate<T>(ref PocWriter writer, T value);

		static void ExecutePoc(PocDelegate<int> func)
		{
			var writer = new PocWriter(_staticBuffer);

			for (int i = 0; i < numberCount; i++)
			{
				func(ref writer, i * 2);
			}

			int offset = writer.Offset;
		}

		static void WritePoc(ref PocWriter writer, int value)
		{
			writer.Ensure(4);
			writer.As<int>() = value;
			writer.Advance(4);
		}

		ref struct PocWriter
		{
			int _length;
			byte[] _array;


			Pinnable<byte> _pinnable;
			IntPtr _byteOffset;

			int _currentTotalOffset; // _byteOffset + Offset

			public int Offset
			{
				get
				{
					return (int)(_currentTotalOffset - (int)_byteOffset);
				}
				set
				{
					_currentTotalOffset = (int)(_byteOffset + value);
				}
			}


			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public PocWriter(byte[] array)
			{
				_array = array;
				_length = array.Length;
				_pinnable = Unsafe.As<Pinnable<byte>>(array);
				_byteOffset = PerTypeValues<byte>.ArrayAdjustment;

				_currentTotalOffset = (int)_byteOffset;
			}


			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public ref T As<T>()
			{
				return ref Unsafe.As<byte, T>(ref DangerousGetPinnableReference());
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public ref byte DangerousGetPinnableReference()
			{
				return ref Unsafe.Add(ref _pinnable.Data, _currentTotalOffset);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Advance(int bytes)
			{
				_currentTotalOffset += bytes;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Ensure(int requiredSize)
			{
				if (_length - ((int)(_currentTotalOffset - (int)_byteOffset)) >= requiredSize)
					return;
				Expand();
			}

			[MethodImpl(MethodImplOptions.NoInlining)]
			void Expand()
			{
				SerializerBinary.ExpandBuffer(ref _array, _array.Length * 2);

				_length = _array.Length;
				_pinnable = Unsafe.As<Pinnable<byte>>(_array);
			}
		}

		#endregion


		#region Like MessagePack (Span etc)

		delegate void BufferWriterDelegate<T>(ref BufferWriter writer, T value);

		static void ExecuteWriter(BufferWriterDelegate<int> func)
		{
			var writer = new BufferWriter(reusableSequenceWithMinSize, _staticBuffer);
			for (int i = 0; i < numberCount; i++)
			{
				func(ref writer, i * 2);
			}
		}

		static void Write4(ref BufferWriter writer, int value)
		{
			var span = writer.GetSpan(4);
			WriteBigEndian(value, span);
			writer.Advance(4);
		}


		static void WriteBigEndian(int value, Span<byte> span)
		{
			unchecked
			{
				// Write to highest index first so the JIT skips bounds checks on subsequent writes.
				span[3] = (byte)value;
				span[2] = (byte)(value >> 8);
				span[1] = (byte)(value >> 16);
				span[0] = (byte)(value >> 24);
			}
		}

		static unsafe void WriteBigEndian(int value, byte* span)
		{
			unchecked
			{
				span[0] = (byte)(value >> 24);
				span[1] = (byte)(value >> 16);
				span[2] = (byte)(value >> 8);
				span[3] = (byte)value;
			}
		}

		internal ref struct BufferWriter
		{
			/// <summary>
			/// The underlying <see cref="IBufferWriter{T}"/>.
			/// </summary>
			private IBufferWriter<byte> _output;

			/// <summary>
			/// The result of the last call to <see cref="IBufferWriter{T}.GetSpan(int)"/>, less any bytes already "consumed" with <see cref="Advance(int)"/>.
			/// Backing field for the <see cref="Span"/> property.
			/// </summary>
			private Span<byte> _span;

			/// <summary>
			/// The result of the last call to <see cref="IBufferWriter{T}.GetMemory(int)"/>, less any bytes already "consumed" with <see cref="Advance(int)"/>.
			/// </summary>
			private ArraySegment<byte> _segment;

			/// <summary>
			/// The number of uncommitted bytes (all the calls to <see cref="Advance(int)"/> since the last call to <see cref="Commit"/>).
			/// </summary>
			private int _buffered;

			/// <summary>
			/// The total number of bytes written with this writer.
			/// Backing field for the <see cref="BytesCommitted"/> property.
			/// </summary>
			private long _bytesCommitted;

			private SequencePool _sequencePool;

			private SequencePool.Rental _rental;

			/// <summary>
			/// Initializes a new instance of the <see cref="BufferWriter"/> struct.
			/// </summary>
			/// <param name="output">The <see cref="IBufferWriter{T}"/> to be wrapped.</param>
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public BufferWriter(IBufferWriter<byte> output)
			{
				_buffered = 0;
				_bytesCommitted = 0;
				_output = output ?? throw new ArgumentNullException(nameof(output));

				_sequencePool = default;
				_rental = default;

				var memory = _output.GetMemory();
				MemoryMarshal.TryGetArray(memory, out _segment);
				_span = memory.Span;
			}

			/// <summary>
			/// Initializes a new instance of the <see cref="BufferWriter"/> struct.
			/// </summary>
			/// <param name="sequencePool">The pool from which to draw an <see cref="IBufferWriter{T}"/> if required..</param>
			/// <param name="array">An array to start with so we can avoid accessing the <paramref name="sequencePool"/> if possible.</param>
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal BufferWriter(SequencePool sequencePool, byte[] array)
			{
				_buffered = 0;
				_bytesCommitted = 0;
				_sequencePool = sequencePool ?? throw new ArgumentNullException(nameof(sequencePool));
				_rental = default;
				_output = null;

				_segment = new ArraySegment<byte>(array);
				_span = _segment.AsSpan();
			}

			/// <summary>
			/// Gets the result of the last call to <see cref="IBufferWriter{T}.GetSpan(int)"/>.
			/// </summary>
			public Span<byte> Span => _span;

			/// <summary>
			/// Gets the total number of bytes written with this writer.
			/// </summary>
			public long BytesCommitted => _bytesCommitted;

			/// <summary>
			/// Gets the <see cref="IBufferWriter{T}"/> underlying this instance.
			/// </summary>
			internal IBufferWriter<byte> UnderlyingWriter => _output;

			internal SequencePool.Rental SequenceRental => _rental;

			public Span<byte> GetSpan(int sizeHint)
			{
				Ensure(sizeHint);
				return this.Span;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public ref byte GetPointer(int sizeHint)
			{
				Ensure(sizeHint);

				if (_segment.Array != null)
				{
					return ref _segment.Array[_segment.Offset + _buffered];
				}
				else
				{
					return ref _span.GetPinnableReference();
				}
			}

			/// <summary>
			/// Calls <see cref="IBufferWriter{T}.Advance(int)"/> on the underlying writer
			/// with the number of uncommitted bytes.
			/// </summary>
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Commit()
			{
				var buffered = _buffered;
				if (buffered > 0)
				{
					this.MigrateToSequence();

					_bytesCommitted += buffered;
					_buffered = 0;
					_output.Advance(buffered);
					_span = default;
				}
			}

			/// <summary>
			/// Used to indicate that part of the buffer has been written to.
			/// </summary>
			/// <param name="count">The number of bytes written to.</param>
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Advance(int count)
			{
				_buffered += count;
				_span = _span.Slice(count);
			}

			/// <summary>
			/// Copies the caller's buffer into this writer and calls <see cref="Advance(int)"/> with the length of the source buffer.
			/// </summary>
			/// <param name="source">The buffer to copy in.</param>
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Write(ReadOnlySpan<byte> source)
			{
				if (_span.Length >= source.Length)
				{
					source.CopyTo(_span);
					Advance(source.Length);
				}
				else
				{
					WriteMultiBuffer(source);
				}
			}

			/// <summary>
			/// Acquires a new buffer if necessary to ensure that some given number of bytes can be written to a single buffer.
			/// </summary>
			/// <param name="count">The number of bytes that must be allocated in a single buffer.</param>
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Ensure(int count = 1)
			{
				if (_span.Length < count)
				{
					EnsureMore(count);
				}
			}

			/// <summary>
			/// Gets the span to the bytes written if they were never committed to the underlying buffer writer.
			/// </summary>
			/// <param name="span"></param>
			/// <returns></returns>
			internal bool TryGetUncommittedSpan(out ReadOnlySpan<byte> span)
			{
				if (this._sequencePool != null)
				{
					span = _segment.AsSpan(0, _buffered);
					return true;
				}

				span = default;
				return false;
			}

			/// <summary>
			/// Gets a fresh span to write to, with an optional minimum size.
			/// </summary>
			/// <param name="count">The minimum size for the next requested buffer.</param>
			[MethodImpl(MethodImplOptions.NoInlining)]
			private void EnsureMore(int count = 0)
			{
				if (_buffered > 0)
				{
					Commit();
				}
				else
				{
					this.MigrateToSequence();
				}

				var memory = _output.GetMemory(count);
				MemoryMarshal.TryGetArray(memory, out _segment);
				_span = memory.Span;
			}

			/// <summary>
			/// Copies the caller's buffer into this writer, potentially across multiple buffers from the underlying writer.
			/// </summary>
			/// <param name="source">The buffer to copy into this writer.</param>
			private void WriteMultiBuffer(ReadOnlySpan<byte> source)
			{
				while (source.Length > 0)
				{
					if (_span.Length == 0)
					{
						EnsureMore();
					}

					var writable = Math.Min(source.Length, _span.Length);
					source.Slice(0, writable).CopyTo(_span);
					source = source.Slice(writable);
					Advance(writable);
				}
			}

			private void MigrateToSequence()
			{
				if (this._sequencePool != null)
				{
					// We were writing to our private scratch memory, so we have to copy it into the actual writer.
					_rental = _sequencePool.Rent();
					_output = _rental.Value;
					var realSpan = _output.GetSpan(_buffered);
					_segment.AsSpan(0, _buffered).CopyTo(realSpan);
					_sequencePool = null;
				}
			}
		}

		internal class SequencePool
		{
			private readonly int maxSize;
			private readonly Stack<Sequence<byte>> pool = new Stack<Sequence<byte>>();

			/// <summary>
			/// Initializes a new instance of the <see cref="SequencePool"/> class.
			/// </summary>
			/// <param name="maxSize">The maximum size to allow the pool to grow.</param>
			internal SequencePool(int maxSize)
			{
				this.maxSize = maxSize;
			}

			/// <summary>
			/// Gets an instance of <see cref="Sequence{T}"/>
			/// This is taken from the recycled pool if one is available; otherwise a new one is created.
			/// </summary>
			/// <returns>The rental tracker that provides access to the object as well as a means to return it.</returns>
			internal Rental Rent()
			{
				lock (this.pool)
				{
					if (this.pool.Count > 0)
					{
						return new Rental(this, this.pool.Pop());
					}
				}

				return new Rental(this, new Sequence<byte> { MinimumSpanLength = 4096 });
			}

			private void Return(Sequence<byte> value)
			{
				value.Reset();
				lock (this.pool)
				{
					if (this.pool.Count < this.maxSize)
					{
						this.pool.Push(value);
					}
				}
			}

			internal struct Rental : IDisposable
			{
				private readonly SequencePool owner;

				internal Rental(SequencePool owner, Sequence<byte> value)
				{
					this.owner = owner;
					this.Value = value;
				}

				/// <summary>
				/// Gets the recyclable object.
				/// </summary>
				public Sequence<byte> Value { get; }

				/// <summary>
				/// Returns the recyclable object to the pool.
				/// </summary>
				/// <remarks>
				/// The instance is cleaned first, if a clean delegate was provided.
				/// It is dropped instead of being returned to the pool if the pool is already at its maximum size.
				/// </remarks>
				public void Dispose()
				{
					this.owner.Return(this.Value);
				}
			}
		}

		#endregion

		#region New

		delegate void WriterDelegate<T>(ref Writer writer, T value);

		static void ExecuteNewWriter(WriterDelegate<int> func)
		{
			Writer writer = new Writer(_staticBuffer);
			for (int i = 0; i < numberCount; i++)
			{
				func(ref writer, i * 2);
			}
		}


		static void Write8(ref Writer writer, int value)
		{
			writer.Ensure(4);

			writer.As<int>() = value;

			writer.Advance(4);
		}


		ref struct Writer
		{
			int _length;
			byte[] _array;


			Pinnable<byte> _pinnable;
			IntPtr _byteOffset;

			IntPtr _currentTotalOffset; // _byteOffset + Offset

			public int Offset
			{
				get
				{
					return (int)(_currentTotalOffset - (int)_byteOffset);
				}
				set
				{
					_currentTotalOffset = _byteOffset + value;
				}
			}



			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public Writer(byte[] array)
			{
				_array = array;
				_length = array.Length;
				_pinnable = Unsafe.As<Pinnable<byte>>(array);
				_byteOffset = PerTypeValues<byte>.ArrayAdjustment;

				_currentTotalOffset = _byteOffset + 0;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public ref byte DangerousGetPinnableReference()
				=> ref Unsafe.AddByteOffset<byte>(ref _pinnable.Data, _currentTotalOffset);

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public ref T As<T>()
				=> ref Unsafe.As<byte, T>(ref DangerousGetPinnableReference());

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Advance(int bytes)
				=> _currentTotalOffset += bytes;

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Ensure(int requiredSize)
			{
				if (_length - Offset >= requiredSize)
					return;

				SerializerBinary.ExpandBuffer(ref _array, _array.Length * 2);

				_length = _array.Length;
				_pinnable = Unsafe.As<Pinnable<byte>>(_array);
			}
		}


		[StructLayout(LayoutKind.Sequential)]
		internal sealed class Pinnable<T>
		{
			public T Data;
		}

		public static class PerTypeValues<T>
		{
			public static readonly T[] EmptyArray = new T[0];

			public static readonly IntPtr ArrayAdjustment = MeasureArrayAdjustment();

			// Array header sizes are a runtime implementation detail and aren't the same across all runtimes. (The CLR made a tweak after 4.5, and Mono has an extra Bounds pointer.)
			private static IntPtr MeasureArrayAdjustment()
			{
				T[] sampleArray = new T[1];
				return Unsafe.ByteOffset<T>(ref Unsafe.As<Pinnable<T>>(sampleArray).Data, ref sampleArray[0]);
			}
		}


		#endregion
	}

	// Result:
	// - interface call to struct is way slower (+30%)
	internal class ClassInterfaceVsStructInterface
	{
		static byte[] _buffer = new byte[1000];
		static void DoTest(Vector3 value, IFormatter<Vector3> formatter)
		{
			int offset = 0;
			formatter.Serialize(ref _buffer, ref offset, value);

			offset = 0;
			Vector3 clone = default;
			formatter.Deserialize(_buffer, ref offset, ref clone);
		}

		internal static void Test()
		{
			var value = new Vector3(2325.123123f, -3524625.3424f, -0.2034324234234f);

			var defaultFStruct = new DefaultFormatter_Struct();
			var defaultF = new DefaultFormatter();

			for (int i = 0; i < 3; i++)
				MicroBenchmark.Run(10, new BenchJob[]
				{
					("DefaultStruct", () => DoTest(value, defaultFStruct)),
					("DefaultClass", () => DoTest(value, defaultF)),
				});

			Console.WriteLine("done");
			Console.ReadKey();
		}



		struct FloatFormatter_Struct : IFormatter<float>
		{
			public void Serialize(ref byte[] buffer, ref int offset, float value)
			{
				SerializerBinary.WriteFloat32Fixed(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref float value)
			{
				value = SerializerBinary.ReadFloat32Fixed(buffer, ref offset);
			}
		}

		class DefaultFormatter_Struct : IFormatter<Vector3>
		{
			IFormatter<float> _floatFormatter = new FloatFormatter_Struct();

			public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
			{
				_floatFormatter.Serialize(ref buffer, ref offset, value.X);
				_floatFormatter.Serialize(ref buffer, ref offset, value.Y);
				_floatFormatter.Serialize(ref buffer, ref offset, value.Z);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
			{
				_floatFormatter.Deserialize(buffer, ref offset, ref value.X);
				_floatFormatter.Deserialize(buffer, ref offset, ref value.Y);
				_floatFormatter.Deserialize(buffer, ref offset, ref value.Z);
			}
		}

	}


	// 3x IFormatter<float>
	class DefaultFormatter : IFormatter<Vector3>
	{
		IFormatter<float> _floatFormatter = new FloatFormatter();

		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			_floatFormatter.Serialize(ref buffer, ref offset, value.X);
			_floatFormatter.Serialize(ref buffer, ref offset, value.Y);
			_floatFormatter.Serialize(ref buffer, ref offset, value.Z);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			_floatFormatter.Deserialize(buffer, ref offset, ref value.X);
			_floatFormatter.Deserialize(buffer, ref offset, ref value.Y);
			_floatFormatter.Deserialize(buffer, ref offset, ref value.Z);
		}
	}

	// Cache formatter field in local
	// >>> No difference
	class ImprovedDefaultFormatter : IFormatter<Vector3>
	{
		IFormatter<float> _floatFormatter = new FloatFormatter();

		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			var f = _floatFormatter;

			f.Serialize(ref buffer, ref offset, value.X);
			f.Serialize(ref buffer, ref offset, value.Y);
			f.Serialize(ref buffer, ref offset, value.Z);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			var f = _floatFormatter;

			f.Deserialize(buffer, ref offset, ref value.X);
			f.Deserialize(buffer, ref offset, ref value.Y);
			f.Deserialize(buffer, ref offset, ref value.Z);
		}
	}


	// ref float p = ref Unsafe.As<byte, float>(ref buffer[offset]);
	// p = ref Unsafe.Add(ref p, 1);
	class ManualFormatter : IFormatter<Vector3>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, 3 * 4);

			ref float p = ref Unsafe.As<byte, float>(ref buffer[offset]);
			p = value.X;

			p = ref Unsafe.Add(ref p, 1);
			p = value.Y;

			p = ref Unsafe.Add(ref p, 1);
			p = value.Z;

			offset += 3 * 4;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			ref float p = ref Unsafe.As<byte, float>(ref buffer[offset]);
			value.X = p;

			p = ref Unsafe.Add(ref p, 1);
			value.Y = p;

			p = ref Unsafe.Add(ref p, 1);
			value.Z = p;


			offset += 3 * 4;
		}
	}

	// ReinterpretFormatter<float>.Write() (best)
	class Manual5Formatter : IFormatter<Vector3>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, 3 * 4);

			ReinterpretFormatter<float>.Write(buffer, offset + 0, ref value.X);
			ReinterpretFormatter<float>.Write(buffer, offset + 4, ref value.Y);
			ReinterpretFormatter<float>.Write(buffer, offset + 8, ref value.Z);

			offset += 3 * 4;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			ReinterpretFormatter<float>.Read(buffer, offset + 0, out value.X);
			ReinterpretFormatter<float>.Read(buffer, offset + 4, out value.Y);
			ReinterpretFormatter<float>.Read(buffer, offset + 8, out value.Z);

			offset += 3 * 4;
		}
	}

	// ReinterpretFormatter<float>.Write() (caching 'offset', no difference)
	class Manual6Formatter : IFormatter<Vector3>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			var off = offset;
			SerializerBinary.EnsureCapacity(ref buffer, off, 3 * 4);

			ReinterpretFormatter<float>.Write(buffer, off + 0, ref value.X);
			ReinterpretFormatter<float>.Write(buffer, off + 4, ref value.Y);
			ReinterpretFormatter<float>.Write(buffer, off + 8, ref value.Z);

			offset += 3 * 4;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			var off = offset;
			ReinterpretFormatter<float>.Read(buffer, off + 0, out value.X);
			ReinterpretFormatter<float>.Read(buffer, off + 4, out value.Y);
			ReinterpretFormatter<float>.Read(buffer, off + 8, out value.Z);

			offset += 3 * 4;
		}
	}

	// Caching 'offset' and increasing it locally: WORSE
	class Manual7Formatter : IFormatter<Vector3>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			var off = offset;
			SerializerBinary.EnsureCapacity(ref buffer, off, 3 * 4);

			ReinterpretFormatter<float>.Write(buffer, off, ref value.X);
			off += 4;
			ReinterpretFormatter<float>.Write(buffer, off, ref value.Y);
			off += 4;
			ReinterpretFormatter<float>.Write(buffer, off, ref value.Z);

			offset += 3 * 4;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			var off = offset;
			ReinterpretFormatter<float>.Read(buffer, off, out value.X);
			off += 4;
			ReinterpretFormatter<float>.Read(buffer, off, out value.Y);
			off += 4;
			ReinterpretFormatter<float>.Read(buffer, off, out value.Z);

			offset += 3 * 4;
		}
	}


	// SerializeInner( value )
	class DynamicMergeBlitFormatter : IFormatter<Vector3>
	{
		SerializeDelegate<Vector3> _serialize;
		DeserializeDelegate<Vector3> _deserialize;

		public DynamicMergeBlitFormatter()
		{
			var ensureCapacityMethod = typeof(SerializerBinary).GetMethod("EnsureCapacity");
			var totalSizeConst = Constant(3 * 4, typeof(int));
			var unsafeAsMethod = typeof(Unsafe).GetMethods(BindingFlags.Public | BindingFlags.Static)
				.First(m => m.Name == "As" && m.GetGenericArguments().Length == 2).MakeGenericMethod(new Type[] { typeof(byte), typeof(float) });
			var unsafeAddMethod = typeof(Unsafe).GetMethods(BindingFlags.Public | BindingFlags.Static)
				.First(m => m.Name == "Add" && m.GetGenericArguments().Length == 1).MakeGenericMethod(new Type[] { typeof(float) });
			var valueMemberX = typeof(Vector3).GetField("X", BindingFlags.Public | BindingFlags.Instance);
			var valueMemberY = typeof(Vector3).GetField("Y", BindingFlags.Public | BindingFlags.Instance);
			var valueMemberZ = typeof(Vector3).GetField("Z", BindingFlags.Public | BindingFlags.Instance);

			var serializeInnnerMethod = typeof(DynamicMergeBlitFormatter).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
				.First(m => m.Name == nameof(DynamicMergeBlitFormatter.SerializeInner) && m.GetGenericArguments().Length == 1).MakeGenericMethod(new Type[] { typeof(float) });
			var deserializeInnnerMethod = typeof(DynamicMergeBlitFormatter).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
				.First(m => m.Name == nameof(DynamicMergeBlitFormatter.DeserializeInner) && m.GetGenericArguments().Length == 1).MakeGenericMethod(new Type[] { typeof(float) });

			GenerateSerializer(ensureCapacityMethod, totalSizeConst, valueMemberX, valueMemberY, valueMemberZ, serializeInnnerMethod);
			GenerateDeserializer(totalSizeConst, valueMemberX, valueMemberY, valueMemberZ, deserializeInnnerMethod);
		}

		void GenerateSerializer(MethodInfo ensureCapacityMethod, ConstantExpression totalSizeConst, FieldInfo valueMemberX, FieldInfo valueMemberY, FieldInfo valueMemberZ, MethodInfo serializeInnnerMethod)
		{
			var refBufferArg = Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Parameter(typeof(Vector3), "value");

			List<ParameterExpression> vars = new List<ParameterExpression>();

			List<Expression> body = new List<Expression>();

			// EnsureCapacity
			body.Add(Call(ensureCapacityMethod, refBufferArg, refOffsetArg, totalSizeConst));


			// SerializeInner(buffer, offset + 0, value.X);
			body.Add(Call(
				method: serializeInnnerMethod,
				refBufferArg,
				Add(refOffsetArg, Constant(0)),
				MakeMemberAccess(valueArg, valueMemberX)));


			body.Add(Call(
				method: serializeInnnerMethod,
				refBufferArg,
				Add(refOffsetArg, Constant(4)),
				MakeMemberAccess(valueArg, valueMemberY)));


			body.Add(Call(
				method: serializeInnnerMethod,
				refBufferArg,
				Add(refOffsetArg, Constant(8)),
				MakeMemberAccess(valueArg, valueMemberZ)));


			// offset += 3 * 4;
			body.Add(AddAssign(refOffsetArg, totalSizeConst));


			_serialize = Lambda<SerializeDelegate<Vector3>>(Block(vars, body),
				new ParameterExpression[] { refBufferArg, refOffsetArg, valueArg }).Compile();
		}

		void GenerateDeserializer(ConstantExpression totalSizeConst, FieldInfo valueMemberX, FieldInfo valueMemberY, FieldInfo valueMemberZ, MethodInfo deserializeInnnerMethod)
		{
			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Parameter(typeof(Vector3).MakeByRefType(), "value");

			List<ParameterExpression> vars = new List<ParameterExpression>();
			List<Expression> body = new List<Expression>();


			// DeserializeInner(buffer, offset + 0, ref value.X);
			body.Add(Call(
				method: deserializeInnnerMethod,
				bufferArg,
				Add(refOffsetArg, Constant(0)),
				MakeMemberAccess(refValueArg, valueMemberX)));


			body.Add(Call(
				method: deserializeInnnerMethod,
				bufferArg,
				Add(refOffsetArg, Constant(4)),
				MakeMemberAccess(refValueArg, valueMemberY)));


			body.Add(Call(
				method: deserializeInnnerMethod,
				bufferArg,
				Add(refOffsetArg, Constant(8)),
				MakeMemberAccess(refValueArg, valueMemberZ)));


			// offset += 3 * 4;
			body.Add(AddAssign(refOffsetArg, totalSizeConst));


			_deserialize = Lambda<DeserializeDelegate<Vector3>>(Block(vars, body),
				new ParameterExpression[] { bufferArg, refOffsetArg, refValueArg }).Compile();
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void SerializeInner<TFieldType>(byte[] buffer, int index, TFieldType field)
		{
			ref byte targetByte = ref buffer[index];
			ref TFieldType target = ref Unsafe.As<byte, TFieldType>(ref targetByte);
			target = field;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void DeserializeInner<TFieldType>(byte[] buffer, int index, ref TFieldType field)
		{
			ref byte sourceByte = ref buffer[index];
			ref TFieldType source = ref Unsafe.As<byte, TFieldType>(ref sourceByte);
			field = source;
		}

		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value) => _serialize(ref buffer, ref offset, value);

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value) => _deserialize(buffer, ref offset, ref value);
	}

	// SerializeInner( ref value ) (same as not passing by ref)
	class DynamicMergeBlit2Formatter : IFormatter<Vector3>
	{
		SerializeDelegate<Vector3> _serialize;
		DeserializeDelegate<Vector3> _deserialize;

		public DynamicMergeBlit2Formatter()
		{
			var ensureCapacityMethod = typeof(SerializerBinary).GetMethod("EnsureCapacity");
			var totalSizeConst = Constant(3 * 4, typeof(int));
			var unsafeAsMethod = typeof(Unsafe).GetMethods(BindingFlags.Public | BindingFlags.Static)
				.First(m => m.Name == "As" && m.GetGenericArguments().Length == 2).MakeGenericMethod(new Type[] { typeof(byte), typeof(float) });
			var unsafeAddMethod = typeof(Unsafe).GetMethods(BindingFlags.Public | BindingFlags.Static)
				.First(m => m.Name == "Add" && m.GetGenericArguments().Length == 1).MakeGenericMethod(new Type[] { typeof(float) });
			var valueMemberX = typeof(Vector3).GetField("X", BindingFlags.Public | BindingFlags.Instance);
			var valueMemberY = typeof(Vector3).GetField("Y", BindingFlags.Public | BindingFlags.Instance);
			var valueMemberZ = typeof(Vector3).GetField("Z", BindingFlags.Public | BindingFlags.Instance);

			var serializeInnnerMethod = typeof(DynamicMergeBlit2Formatter).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
				.First(m => m.Name == nameof(DynamicMergeBlit2Formatter.SerializeInner) && m.GetGenericArguments().Length == 1).MakeGenericMethod(new Type[] { typeof(float) });
			var deserializeInnnerMethod = typeof(DynamicMergeBlit2Formatter).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
				.First(m => m.Name == nameof(DynamicMergeBlit2Formatter.DeserializeInner) && m.GetGenericArguments().Length == 1).MakeGenericMethod(new Type[] { typeof(float) });

			GenerateSerializer(ensureCapacityMethod, totalSizeConst, valueMemberX, valueMemberY, valueMemberZ, serializeInnnerMethod);
			GenerateDeserializer(totalSizeConst, valueMemberX, valueMemberY, valueMemberZ, deserializeInnnerMethod);
		}

		void GenerateSerializer(MethodInfo ensureCapacityMethod, ConstantExpression totalSizeConst, FieldInfo valueMemberX, FieldInfo valueMemberY, FieldInfo valueMemberZ, MethodInfo serializeInnnerMethod)
		{
			var refBufferArg = Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Parameter(typeof(Vector3), "value");

			List<ParameterExpression> vars = new List<ParameterExpression>();

			List<Expression> body = new List<Expression>();

			// EnsureCapacity
			body.Add(Call(ensureCapacityMethod, refBufferArg, refOffsetArg, totalSizeConst));


			// SerializeInner(buffer, offset + 0, value.X);
			body.Add(Call(
				method: serializeInnnerMethod,
				refBufferArg,
				Add(refOffsetArg, Constant(0)),
				MakeMemberAccess(valueArg, valueMemberX)));


			body.Add(Call(
				method: serializeInnnerMethod,
				refBufferArg,
				Add(refOffsetArg, Constant(4)),
				MakeMemberAccess(valueArg, valueMemberY)));


			body.Add(Call(
				method: serializeInnnerMethod,
				refBufferArg,
				Add(refOffsetArg, Constant(8)),
				MakeMemberAccess(valueArg, valueMemberZ)));


			// offset += 3 * 4;
			body.Add(AddAssign(refOffsetArg, totalSizeConst));


			_serialize = Lambda<SerializeDelegate<Vector3>>(Block(vars, body),
				new ParameterExpression[] { refBufferArg, refOffsetArg, valueArg }).Compile();
		}

		void GenerateDeserializer(ConstantExpression totalSizeConst, FieldInfo valueMemberX, FieldInfo valueMemberY, FieldInfo valueMemberZ, MethodInfo deserializeInnnerMethod)
		{
			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Parameter(typeof(Vector3).MakeByRefType(), "value");

			List<ParameterExpression> vars = new List<ParameterExpression>();
			List<Expression> body = new List<Expression>();


			// DeserializeInner(buffer, offset + 0, ref value.X);
			body.Add(Call(
				method: deserializeInnnerMethod,
				bufferArg,
				Add(refOffsetArg, Constant(0)),
				MakeMemberAccess(refValueArg, valueMemberX)));


			body.Add(Call(
				method: deserializeInnnerMethod,
				bufferArg,
				Add(refOffsetArg, Constant(4)),
				MakeMemberAccess(refValueArg, valueMemberY)));


			body.Add(Call(
				method: deserializeInnnerMethod,
				bufferArg,
				Add(refOffsetArg, Constant(8)),
				MakeMemberAccess(refValueArg, valueMemberZ)));


			// offset += 3 * 4;
			body.Add(AddAssign(refOffsetArg, totalSizeConst));


			_deserialize = Lambda<DeserializeDelegate<Vector3>>(Block(vars, body),
				new ParameterExpression[] { bufferArg, refOffsetArg, refValueArg }).Compile();
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void SerializeInner<TFieldType>(byte[] buffer, int index, ref TFieldType field)
		{
			ref byte targetByte = ref buffer[index];
			ref TFieldType target = ref Unsafe.As<byte, TFieldType>(ref targetByte);
			target = field;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void DeserializeInner<TFieldType>(byte[] buffer, int index, ref TFieldType field)
		{
			ref byte sourceByte = ref buffer[index];
			ref TFieldType source = ref Unsafe.As<byte, TFieldType>(ref sourceByte);
			field = source;
		}

		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value) => _serialize(ref buffer, ref offset, value);
		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value) => _deserialize(buffer, ref offset, ref value);
	}

	// Call to ReinterpretFormatter<float> (fastest)
	class DynamicMergeBlit3Formatter : IFormatter<Vector3>
	{
		SerializeDelegate<Vector3> _serialize;
		DeserializeDelegate<Vector3> _deserialize;

		public DynamicMergeBlit3Formatter()
		{
			var ensureCapacityMethod = typeof(SerializerBinary).GetMethod("EnsureCapacity");
			var totalSizeConst = Constant(3 * 4, typeof(int));
			var valueMemberX = typeof(Vector3).GetField("X", BindingFlags.Public | BindingFlags.Instance);
			var valueMemberY = typeof(Vector3).GetField("Y", BindingFlags.Public | BindingFlags.Instance);
			var valueMemberZ = typeof(Vector3).GetField("Z", BindingFlags.Public | BindingFlags.Instance);

			var serializeInnnerMethod = typeof(ReinterpretFormatter<>).MakeGenericType(typeof(float))
				.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).First(m => m.Name == nameof(ReinterpretFormatter<int>.Write));
			var deserializeInnnerMethod = typeof(ReinterpretFormatter<>).MakeGenericType(typeof(float))
				.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).First(m => m.Name == nameof(ReinterpretFormatter<int>.Read));

			GenerateSerializer(ensureCapacityMethod, totalSizeConst, valueMemberX, valueMemberY, valueMemberZ, serializeInnnerMethod);
			GenerateDeserializer(totalSizeConst, valueMemberX, valueMemberY, valueMemberZ, deserializeInnnerMethod);
		}

		void GenerateSerializer(MethodInfo ensureCapacityMethod, ConstantExpression totalSizeConst, FieldInfo valueMemberX, FieldInfo valueMemberY, FieldInfo valueMemberZ, MethodInfo serializeInnnerMethod)
		{
			ParameterExpression refBufferArg = Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			ParameterExpression refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			ParameterExpression valueArg = Parameter(typeof(Vector3), "value");

			List<ParameterExpression> vars = new List<ParameterExpression>();

			List<Expression> body = new List<Expression>();

			// EnsureCapacity
			body.Add(Call(ensureCapacityMethod, refBufferArg, refOffsetArg, totalSizeConst));


			// SerializeInner(buffer, offset + 0, value.X);
			body.Add(Call(
				method: serializeInnnerMethod,
				refBufferArg,
				Add(refOffsetArg, Constant(0)),
				MakeMemberAccess(valueArg, valueMemberX)));


			body.Add(Call(
				method: serializeInnnerMethod,
				refBufferArg,
				Add(refOffsetArg, Constant(4)),
				MakeMemberAccess(valueArg, valueMemberY)));


			body.Add(Call(
				method: serializeInnnerMethod,
				refBufferArg,
				Add(refOffsetArg, Constant(8)),
				MakeMemberAccess(valueArg, valueMemberZ)));


			// offset += 3 * 4;
			body.Add(AddAssign(refOffsetArg, totalSizeConst));


			_serialize = Lambda<SerializeDelegate<Vector3>>(Block(vars, body),
				new ParameterExpression[] { refBufferArg, refOffsetArg, valueArg }).Compile();
		}

		void GenerateDeserializer(ConstantExpression totalSizeConst, FieldInfo valueMemberX, FieldInfo valueMemberY, FieldInfo valueMemberZ, MethodInfo deserializeInnnerMethod)
		{
			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Parameter(typeof(Vector3).MakeByRefType(), "value");

			List<ParameterExpression> vars = new List<ParameterExpression>();
			List<Expression> body = new List<Expression>();


			// DeserializeInner(buffer, offset + 0, ref value.X);
			body.Add(Call(
				method: deserializeInnnerMethod,
				bufferArg,
				Add(refOffsetArg, Constant(0)),
				MakeMemberAccess(refValueArg, valueMemberX)));


			body.Add(Call(
				method: deserializeInnnerMethod,
				bufferArg,
				Add(refOffsetArg, Constant(4)),
				MakeMemberAccess(refValueArg, valueMemberY)));


			body.Add(Call(
				method: deserializeInnnerMethod,
				bufferArg,
				Add(refOffsetArg, Constant(8)),
				MakeMemberAccess(refValueArg, valueMemberZ)));


			// offset += 3 * 4;
			body.Add(AddAssign(refOffsetArg, totalSizeConst));


			_deserialize = Lambda<DeserializeDelegate<Vector3>>(Block(vars, body),
				new ParameterExpression[] { bufferArg, refOffsetArg, refValueArg }).Compile();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value) => _serialize(ref buffer, ref offset, value);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value) => _deserialize(buffer, ref offset, ref value);
	}
}