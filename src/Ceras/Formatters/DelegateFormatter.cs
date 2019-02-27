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
		readonly IFormatter<MethodInfo> _methodInfoFormatter;
		readonly IFormatter<object> _targetFormatter;
		readonly bool _allowStatic;
		readonly bool _allowInstance;

		public DelegateFormatter(CerasSerializer ceras)
		{
			_methodInfoFormatter = ceras.GetFormatter<MethodInfo>();
			_targetFormatter = ceras.GetFormatter<object>();
			
			_allowStatic = (ceras.Config.Advanced.DelegateSerialization & DelegateSerializationFlags.AllowStatic) != 0;
			_allowInstance = (ceras.Config.Advanced.DelegateSerialization & DelegateSerializationFlags.AllowInstance) != 0;
		}

		public void Serialize(ref byte[] buffer, ref int offset, T del)
		{
			var target = del.Target;
			
			if (target != null)
			{
				// Check: Instance
				if (!_allowInstance)
					ThrowInstance(del, target);
				
				// Check: Lambda
				var targetType = target.GetType();
				if(targetType.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
					throw new InvalidOperationException($"The delegate '{del}' is targeting a 'lambda'. This makes it impossible to serialize because the compiler can (and will!) merge all lambda \"closures\" of the containing method or type, which is very dangerous even in the most simple scenarios. For more information of what exactly this means you should read this: 'https://github.com/rikimaru0345/Ceras/issues/11'. If you have a good use-case and/or a solution for the problems described in the link, open an issue on GitHub or join the Discord server...");
			}
			else
			{
				// Check: Static
				if (!_allowStatic)
					ThrowStatic(del);
			}

			_targetFormatter.Serialize(ref buffer, ref offset, target);

			var invocationList = del.GetInvocationList();
			if (invocationList.Length != 1)
				throw new InvalidOperationException($"The delegate cannot be serialized, its 'invocation list' must have exactly one target, but it has '{invocationList.Length}'.");

			_methodInfoFormatter.Serialize(ref buffer, ref offset, del.Method);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			object targetObject = null;
			_targetFormatter.Deserialize(buffer, ref offset, ref targetObject);

			MethodInfo methodInfo = null;
			_methodInfoFormatter.Deserialize(buffer, ref offset, ref methodInfo);

			if (methodInfo.IsStatic && !_allowStatic)
				ThrowStatic(null);
			if (!methodInfo.IsStatic && !_allowInstance)
				ThrowInstance(null, targetObject);

			if (targetObject == null)
				value = (T) Delegate.CreateDelegate(typeof(T), methodInfo, true);
			else
				value = (T) Delegate.CreateDelegate(typeof(T), targetObject, methodInfo, true);
		}
		
		static void ThrowStatic(T delegateValue)
		{
			throw new InvalidOperationException($"The delegate '{delegateValue}' can not be serialized/deserialized as it references a static method and your settings in 'config.Advanced.DelegateSerialization' don't allow serialization of static-delegates. Change the setting in your config, or exclude the member, ...");
		}

		static void ThrowInstance(T delegateValue, object instance)
		{
			throw new InvalidOperationException($"The delegate '{delegateValue}' can not be serialized/deserialized as it references an instance method (targeting the object '{instance}') and your settings in 'config.Advanced.DelegateSerialization' don't allow serialization of instance-delegates. Change the setting in your config, or exclude the member, ...");
		}

	}
}
