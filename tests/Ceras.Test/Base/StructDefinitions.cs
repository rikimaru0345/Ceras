namespace Ceras.Test
{
	using System.Runtime.InteropServices;

	[StructLayout(LayoutKind.Sequential)]
	// Size: 12
	struct Vector3
	{
		public float X;
		public float Y;
		public float Z;

		public Vector3(float x, float y, float z)
		{
			X = x;
			Y = y;
			Z = z;
		}
	}

	// Size: 4
	struct Half2
	{
		public short X;
		public short Y;

		public Half2(short x, short y)
		{
			X = x;
			Y = y;
		}
	}
	
	// Size: 8
	struct Half4
	{
		public Half2 Left;
		public Half2 Right;

		public Half4(Half2 left, Half2 right)
		{
			Left = left;
			Right = right;
		}

		public Half4(short leftX, short leftY, short rightX, short rightY)
		{
			Left = new Half2(leftX, leftY);
			Right = new Half2(rightX, rightY);
		}
	}
	
	// Size: 50
	unsafe struct BigStruct
	{
		public byte First;					// 1
		public double Second;				// 4
		public fixed byte Third[3];			// 3
		public Half4 Fourth;				// 8
		public char Fifth;					// 2

		// Space for 2x Vector3
		public fixed byte Sixth[2 * 12];	// 2*12 = 24

		/*
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
		public int[] Seventh;				// 3*4 = 12
		*/
		
		public ulong Eighth;				// 8

	}
}