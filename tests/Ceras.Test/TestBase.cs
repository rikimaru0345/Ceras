using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Test
{
	using System.Linq.Expressions;
	using System.Reflection;
	using Xunit;

	public class TestBase
	{
		public T Clone<T>(T source, SerializerConfig config = null)
		{
			var ceras = new CerasSerializer(config);

			byte[] data = new byte[0x1000];
			int len = ceras.Serialize(source, ref data);

			T clone = default(T);
			int read = 0;
			ceras.Deserialize(ref clone, data, ref read, len);

			return clone;
		}


		public void CheckCloneEquality<T>(T obj)
		{
			if (obj == null)
				throw new ArgumentNullException(nameof(obj));

			var clone = Clone(obj);

			Assert.True(clone != null);
			Assert.True(obj.Equals(clone));
		}


		public MethodInfo GetMethod(Expression<Action>  e)
		{
			var b = e.Body;

			if (b is MethodCallExpression m)
				return m.Method;

			throw new ArgumentException();
		}

		public MethodInfo GetMethod<T>(Expression<Func<T>>  e)
		{
			var b = e.Body;

			if (b is MethodCallExpression m)
				return m.Method;

			throw new ArgumentException();
		}

		public ConstructorInfo GetCtor<T>(Expression<Func<T>>  e)
		{
			var b = e.Body;

			if (b is NewExpression n)
				return n.Constructor;

			throw new ArgumentException();
		}
	}
}
