using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
	using System.Reflection;
	using System.Runtime.CompilerServices;

	// This formatter serializes Delegate, Action, Func, event, ... (with some caveats)
	class DelegateFormatter<T> : IFormatter<T>
			where T : Delegate
	{
		readonly IFormatter<MethodInfo> _methodInfoFormatter;
		readonly IFormatter<object> _targetFormatter;
		readonly IFormatter<Delegate> _recursiveDispatch; // for multicasts, the inner delegates might have a different type than the container delegate (will that ever really happen without much hackery? well, better safe than sorry)
		readonly bool _allowStatic;
		readonly bool _allowInstance;

		public DelegateFormatter(CerasSerializer ceras)
		{
			_methodInfoFormatter = ceras.GetFormatter<MethodInfo>();
			_targetFormatter = ceras.GetFormatter<object>();
			_recursiveDispatch = (IFormatter<Delegate>)ceras.GetReferenceFormatter(typeof(Delegate));

			_allowStatic = (ceras.Config.Advanced.DelegateSerialization & DelegateSerializationFlags.AllowStatic) != 0;
			_allowInstance = (ceras.Config.Advanced.DelegateSerialization & DelegateSerializationFlags.AllowInstance) != 0;
		}

		//
		// Delegates with just one entry in their invocation list can be serialized directly.
		// But '.Target' and '.Method' only ever reflect the last entry, so for anything above we must iterate through the sub-delegates to get the individual methods and target objects
		public void Serialize(ref byte[] buffer, ref int offset, T del)
		{
			//
			// 1. Recursion for multiple invocations
			var invocationList = del.GetInvocationList();

			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)invocationList.Length);

			if (invocationList.Length > 1)
			{
				// Multiple different methods and/or target objects; we must serialize them individually.
				for (int i = 0; i < invocationList.Length; i++)
					_recursiveDispatch.Serialize(ref buffer, ref offset, invocationList[i]);

				return;
			}


			//
			// 2. Check if we're even allowed to serialize this
			var target = del.Target;

			if (target != null)
			{
				// Check: Instance
				if (!_allowInstance)
					ThrowInstance(del, target);

				// Check: Lambda
				var targetType = target.GetType();
				if (targetType.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
					throw new InvalidOperationException($"The delegate '{del}' is targeting a 'lambda'. This makes it impossible to serialize because the compiler can (and will!) merge all lambda \"closures\" of the containing method or type, which is very dangerous even in the most simple scenarios. For more information of what exactly this means you should read this: 'https://github.com/rikimaru0345/Ceras/issues/11'. If you have a good use-case and/or a solution for the problems described in the link, open an issue on GitHub or join the Discord server...");
			}
			else
			{
				// Check: Static
				if (!_allowStatic)
					ThrowStatic(del);
			}


			//
			// 3. Serialize target object and method
			_targetFormatter.Serialize(ref buffer, ref offset, target);
			_methodInfoFormatter.Serialize(ref buffer, ref offset, del.Method);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			//
			// 1. How many invocations are there?
			int invocationListLength = (int)SerializerBinary.ReadUInt32(buffer, ref offset);

			if (invocationListLength > 1)
			{
				// Deserialize individual invocations and construct combined delegate
				Delegate[] invocationList = new Delegate[invocationListLength];
				for (int i = 0; i < invocationListLength; i++)
					_recursiveDispatch.Deserialize(buffer, ref offset, ref invocationList[i]);

				value = (T)Delegate.Combine(invocationList);

				return;
			}

			//
			// 2. Single target+method combo

			// Get object
			object targetObject = null;
			_targetFormatter.Deserialize(buffer, ref offset, ref targetObject);

			// Get method
			MethodInfo methodInfo = null;
			_methodInfoFormatter.Deserialize(buffer, ref offset, ref methodInfo);

			// Check settings
			if (methodInfo.IsStatic && !_allowStatic)
				ThrowStatic(null);
			if (!methodInfo.IsStatic && !_allowInstance)
				ThrowInstance(null, targetObject);

			// Construct bound delegate
			if (targetObject == null)
				value = (T)Delegate.CreateDelegate(typeof(T), methodInfo, true);
			else
				value = (T)Delegate.CreateDelegate(typeof(T), targetObject, methodInfo, true);
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
