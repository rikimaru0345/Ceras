using System;
using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ceras.Formatters
{
    public class DelegateFormatter<T> : FormatterBase<T> where T : Delegate
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

        public override void Serialize(IBufferWriter<byte> writer, T del)
        {
            var target = del.Target;
            if (target != null)
            {
                if (!_allowInstance) throw new InvalidOperationException("Instance delegate serialization disabled");
                if (target.GetType().GetCustomAttribute<CompilerGeneratedAttribute>() != null) throw new InvalidOperationException("Lambda serialization disabled");
            }
            else if (!_allowStatic) throw new InvalidOperationException("Static delegate serialization disabled");

            _targetFormatter.Serialize(writer, target);
            var invocationList = del.GetInvocationList();
            if (invocationList.Length != 1) throw new InvalidOperationException("Multi-target delegates not supported");
            _methodInfoFormatter.Serialize(writer, del.Method);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            object targetObject = null;
            _targetFormatter.Deserialize(ref reader, ref targetObject);
            MethodInfo methodInfo = null;
            _methodInfoFormatter.Deserialize(ref reader, ref methodInfo);

            if (methodInfo.IsStatic && !_allowStatic) throw new InvalidOperationException("Static delegate serialization disabled");
            if (!methodInfo.IsStatic && !_allowInstance) throw new InvalidOperationException("Instance delegate serialization disabled");

            value = (targetObject == null) ? (T)Delegate.CreateDelegate(typeof(T), methodInfo, true) : (T)Delegate.CreateDelegate(typeof(T), targetObject, methodInfo, true);
        }
    }
}
