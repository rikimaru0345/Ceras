using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
	using System.Reflection;

	/*
	 * 1.) At the moment we support static delegates because they are "reasonable".
	 * 2.) But delegates that have a "target object" are pretty difficult already (do you include the target? how? what if it is not supposed to be serialized?)
	 * 3.) And then serialization of lambda-expressions is utter madness (compiler merging capture-classes so you serialize **completely** unrelated stuff on accident).
	 *
	 * The first one is easily supported.
	 *
	 * The second one can be done if you carefully design your code, and for reliable results the target object is always an IExternalRootObject.
	 *
	 * As for the third one: in real-world scenarios you actually very very rarely want to serialize lambda expressions, but
	 * maybe there are cases where it's needed. To be honest, there are so incredibly many side effects that you are much better off just creating
	 * some sort of "OnDeserializationDone()" method in your objects and then "repair" / recreate the needed lambda expressions.
	 * Chances are that it is actually impossible to come up with a solution that is fully automatic and reliable at the same time.
	 */

	// This formatter serializes Delegate, Action, Func, event, ... (with some caveats)
	class DelegateFormatter<T> : IFormatter<T>
			where T : Delegate
	{
		readonly CerasSerializer _ceras;
		readonly IFormatter<MethodInfo> _methodInfoFormatter;

		public DelegateFormatter(CerasSerializer ceras)
		{
			_ceras = ceras;
			_methodInfoFormatter = ceras.GetFormatter<MethodInfo>();
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			if (value.Target != null)
				throw new InvalidOperationException($"The delegate '{value}' can not be serialized as it references an instance method (targetting the object '{value.Target}'). At the moment only static methods are supported for serialization, but support for instance-objects is work in progress (and there's an easy workaround if you need that functionality right now, check the GitHub page)");

			var invocationList = value.GetInvocationList();
			if (invocationList.Length != 1)
				throw new InvalidOperationException($"The delegate cannot be serialized, its 'invocation list' must have exactly one target, but it has '{invocationList.Length}'.");

			_methodInfoFormatter.Serialize(ref buffer, ref offset, value.Method);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			MethodInfo methodInfo = null;
			_methodInfoFormatter.Deserialize(buffer, ref offset, ref methodInfo);

			if (value != null && value.Method == methodInfo)
				return; // Existing delegate is correct already

			value = (T)Delegate.CreateDelegate(typeof(T), methodInfo, true);
		}

	}
}
