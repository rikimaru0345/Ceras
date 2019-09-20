using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tutorial
{
	class Program
	{
		static void Main(string[] args)
		{
			var t = new Tutorial();
			
			t.Step1_SimpleUsage();
			t.Step2_Attributes();
			t.Step3_Recycling();
			t.Step4_KnownTypes();
			t.Step5_CustomFormatters();
			t.Step6_NetworkExample();
			t.Step7_GameDatabase();
			//t.Step8_DataUpgrade_OLD();
			t.Step9_VersionTolerance();
			t.Step10_ReadonlyHandling();


			Console.WriteLine("End of tutorial program.");
			Console.ReadLine();
		}
	}
}
