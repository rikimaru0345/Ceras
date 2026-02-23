using System;
using System.Buffers;
using System.Reflection;
using System.Linq;

namespace Ceras.Formatters
{
    public class MemberInfoFormatter<T> : FormatterBase<T> where T : MemberInfo
    {
        readonly IFormatter<Type> _typeFormatter;

        public MemberInfoFormatter(CerasSerializer ceras)
        {
            _typeFormatter = ceras.GetFormatter<Type>();
        }

        public override void Serialize(IBufferWriter<byte> writer, T member)
        {
            if (member == null)
            {
                _typeFormatter.Serialize(writer, null);
                return;
            }

            _typeFormatter.Serialize(writer, member.DeclaringType);
            SerializerBinary.WriteByte(writer, (byte)member.MemberType);
            SerializerBinary.WriteString(writer, member.Name);

            if (member is MethodBase method)
            {
                var parameters = method.GetParameters();
                SerializerBinary.WriteByte(writer, (byte)parameters.Length);
                foreach (var p in parameters) _typeFormatter.Serialize(writer, p.ParameterType);

                if (member is MethodInfo mi && mi.IsGenericMethod)
                {
                    SerializerBinary.WriteByte(writer, 1);
                    var args = mi.GetGenericArguments();
                    SerializerBinary.WriteByte(writer, (byte)args.Length);
                    foreach (var arg in args) _typeFormatter.Serialize(writer, arg);
                }
                else
                {
                    SerializerBinary.WriteByte(writer, 0);
                }
            }
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            Type declaringType = null;
            _typeFormatter.Deserialize(ref reader, ref declaringType);
            if (declaringType == null) { value = null; return; }

            var memberType = (MemberTypes)SerializerBinary.ReadByte(ref reader);
            var name = SerializerBinary.ReadString(ref reader);

            if (memberType == MemberTypes.Method || memberType == MemberTypes.Constructor)
            {
                var paramCount = SerializerBinary.ReadByte(ref reader);
                var paramTypes = new Type[paramCount];
                for (int i = 0; i < paramCount; i++) _typeFormatter.Deserialize(ref reader, ref paramTypes[i]);

                bool isGeneric = false;
                Type[] genericArgs = null;
                if (SerializerBinary.ReadByte(ref reader) != 0)
                {
                    isGeneric = true;
                    var argCount = SerializerBinary.ReadByte(ref reader);
                    genericArgs = new Type[argCount];
                    for (int i = 0; i < argCount; i++) _typeFormatter.Deserialize(ref reader, ref genericArgs[i]);
                }

                if (memberType == MemberTypes.Method)
                {
                    MethodInfo method = null;

                    if (isGeneric)
                    {
                        // For generic methods, we must find the definition first.
                        // We can't easily use GetMethod with paramTypes because those types are already closed.
                        var candidates = declaringType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                            .Where(m => m.Name == name && m.IsGenericMethodDefinition && m.GetParameters().Length == paramCount)
                            .ToArray();

                        if (candidates.Length == 1)
                        {
                            method = candidates[0];
                        }
                        else if (candidates.Length > 1)
                        {
                            // Ambiguous generic overloads. 
                            // Try to match by checking if the closed paramTypes are assignable to the open paramTypes.
                            method = candidates.FirstOrDefault(c => 
                            {
                                var cParams = c.GetParameters();
                                for (int i = 0; i < paramCount; i++)
                                {
                                    var openT = cParams[i].ParameterType;
                                    var closedT = paramTypes[i];

                                    if (!openT.IsGenericParameter)
                                    {
                                        if (openT != closedT)
                                            return false;
                                    }
                                }
                                return true;
                            });
                        }
                    }
                    else
                    {
                        method = declaringType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, paramTypes, null);
                    }

                    if (method != null && isGeneric && method.IsGenericMethodDefinition)
                        method = method.MakeGenericMethod(genericArgs);
                    
                    value = (T)(object)method;
                }
                else
                {
                    value = (T)(object)declaringType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, paramTypes, null);
                }
            }
            else if (memberType == MemberTypes.Field) value = (T)(object)declaringType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            else if (memberType == MemberTypes.Property) value = (T)(object)declaringType.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        }
    }
}
