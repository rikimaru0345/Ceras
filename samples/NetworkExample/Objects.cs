using System.Collections.Generic;
using System;

namespace NetworkExample
{
	// This is like a login packet
	// Why do we have it? Just to show the most simple case
	class ClientLogin
	{
		public string Name;
		public string Password;
	}
	

	// More complicated classes are possible as well
	// Like 'Person' which has references to other persons)
	class Person
	{
		public string Name;
		public int Age;
		public List<Person> Friends = new List<Person>();
	}


	// Abstract classes and interfaces are very rarely handled
	// correctly with other serializers
	interface ISpell
	{
		string Cast();
	}

	// A fireball that just deals some direct damage
	class Fireball : ISpell
	{
		public int DamageMin = 40;
		public int DamageMax = 60;

		public string Cast()
		{
			var dmg = new Random().Next(DamageMin, DamageMax);
			return $"Fireball dealt {dmg} damage!";
		}
	}

	// Chain-lightning that jumps over many targets (losing damage with every jump)
	class Lightning : ISpell
	{
		public int InitialDamage = 120;
		public float DamageFactorPerJump = 0.8f;
		public int MaxTargets = 6;

		public string Cast()
		{
			var rng = new Random();

			var numberOfTargets = rng.Next(MaxTargets/2, MaxTargets);

			float totalDamage = 0;
			float currentDamage = InitialDamage;
			for (int i = 0; i < numberOfTargets; i++)
			{
				totalDamage += currentDamage;
				currentDamage *= DamageFactorPerJump;
			}

			return $"Lightning dealt {totalDamage:0.0} damage in total (jumped over {numberOfTargets} targets)!";
		}
	}
}
