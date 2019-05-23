namespace Ceras.TestDebugger
{
	using Ceras.Helpers;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Drawing;
    using System.IO;
    using System.Runtime.CompilerServices;
    using Test;

	class Program
	{
		static void Main(string[] args)
		{
#if NET45
			global::System.Console.WriteLine("Running on NET4.5");
#elif NET451
			global::System.Console.WriteLine("Running on NET4.5.1");
#elif NET452
			global::System.Console.WriteLine("Running on NET4.5.2");
#elif NET47
			global::System.Console.WriteLine("Running on NET4.7");
#elif NET47
			global::System.Console.WriteLine("Running on NET4.7");
#elif NET471
			global::System.Console.WriteLine("Running on NET4.7.1");
#elif NET472
			global::System.Console.WriteLine("Running on NET4.7.2");
#elif NETSTANDARD2_0
			global::System.Console.WriteLine("Running on NET STANDARD 2.0");
#else
#error Unhandled framework version!
#endif
			
			
			new BuiltInTypes().MultidimensionalArrays();
			new BuiltInTypes().ImmutableCollections();
			new BuiltInTypes().Collections();

			new Blitting().BlittableTypesUseCorrectFormatter();
			new Blitting().CouldCopyValueTupleDirectly();
			new Internals().FastCopy();
			new BuiltInTypes().Bitmap();

			
			
		}

		/*
		#if NETFRAMEWORK
		static void TestDynamic()
		{
			dynamic dyn = new ExpandoObject();
			dyn.number = 5;
			dyn.list = new List<string> { "a", "b"};
			dyn.c = "c";
			dyn.func = new Func<string>(((object)dyn).ToString);
			
			var ceras = new CerasSerializer();
			var data = ceras.Serialize(dyn);
			var dyn2 = ceras.Deserialize<dynamic>(data);
		}
		#endif
		*/

		static void TestBitmapFormatter()
		{
			var config = new SerializerConfig();
			config.Advanced.BitmapMode = BitmapMode.SaveAsBmp;
			var ceras = new CerasSerializer(config);

			var home = Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%");
			var downloads = Path.Combine(home, "Downloads");

			var images = new Image[]
			{
				// todo: add test images
				Image.FromFile(Path.Combine(downloads, @".png")),
			};

			for (int iteration = 0; iteration < 5; iteration++)
			{
				var imgData1 = ceras.Serialize(images);
				var clones = ceras.Deserialize<Image[]>(imgData1);

				for (var cloneIndex = 0; cloneIndex < clones.Length; cloneIndex++)
				{
					var c = clones[cloneIndex];
					c.Dispose();
					clones[cloneIndex] = null;
				}
			}


			byte[] sharedBuffer = new byte[100];
			int offset = 0;
			foreach (var sourceImage in images)
				offset += ceras.Serialize(sourceImage, ref sharedBuffer, offset);
			offset += ceras.Serialize(images, ref sharedBuffer, offset);

			int writtenLength = offset;

			List<Image> clonedImages = new List<Image>();
			offset = 0;

			for (var i = 0; i < images.Length; i++)
			{
				Image img = null;
				ceras.Deserialize(ref img, sharedBuffer, ref offset);
				clonedImages.Add(img);
			}
			Image[] imageArrayClone = null;
			ceras.Deserialize(ref imageArrayClone, sharedBuffer, ref offset);

			// Ensure all bytes consumed again
			Debug.Assert(offset == writtenLength);

			foreach (var img in clonedImages)
				img.Dispose();
			foreach (var img in imageArrayClone)
				img.Dispose();

		}
	}
}
