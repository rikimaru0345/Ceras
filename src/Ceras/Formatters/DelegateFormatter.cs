using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
	using System.Reflection;
	using System.Runtime.CompilerServices;

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
		readonly IFormatter<object> _targetFormatter;

		public DelegateFormatter(CerasSerializer ceras)
		{
			_ceras = ceras;
			_methodInfoFormatter = ceras.GetFormatter<MethodInfo>();
			_targetFormatter = ceras.GetFormatter<object>();
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			var target = value.Target;

			if (target != null)
			{
				if(_ceras.Config.Advanced.DelegateSerialization != DelegateSerializationMode.AllowInstance)
					throw new InvalidOperationException($"The delegate '{value}' can not be serialized as it references an instance method (targeting the object '{target}'). You can turn on 'config.Advanced.DelegateSerialization' if you want Ceras to serialize this delegate.");

				var targetType = target.GetType();
				if(targetType.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
					throw new InvalidOperationException($"The delegate '{value}' is targeting a 'lambda expression'. This makes it impossible to serialize because the compiler can (and will!) merge all lambda \"closures\" of the containing method or type, which is very dangerous even in the most simple scenarios. For more information of what exactly this means you should read this: 'https://github.com/rikimaru0345/Ceras/issues/11'. If you have a good use-case and/or a solution for the problems described in the link, open an issue on GitHub or join the Discord server...");
			}

			_targetFormatter.Serialize(ref buffer, ref offset, target);

			var invocationList = value.GetInvocationList();
			if (invocationList.Length != 1)
				throw new InvalidOperationException($"The delegate cannot be serialized, its 'invocation list' must have exactly one target, but it has '{invocationList.Length}'.");

			_methodInfoFormatter.Serialize(ref buffer, ref offset, value.Method);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			object targetObject = null;
			_targetFormatter.Deserialize(buffer, ref offset, ref targetObject);

			MethodInfo methodInfo = null;
			_methodInfoFormatter.Deserialize(buffer, ref offset, ref methodInfo);
			
			value = (T)Delegate.CreateDelegate(typeof(T), methodInfo, true);
		}

	}
}
