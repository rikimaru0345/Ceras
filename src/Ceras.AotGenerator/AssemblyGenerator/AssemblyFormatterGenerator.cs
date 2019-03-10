using System;

namespace CerasAotFormatterGenerator
{
	using Ceras;
	using Ceras.Formatters;
	using Ceras.Helpers;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Reflection.Emit;
	using System.Security.Permissions;

	//
	// Generating an assembly is a nice idea, but there are some problems right now that make it harder than it needs to be.
	// There's actually not really any advantage to generate an already compiled assembly, instead of just emitting .cs files.
	// At least you can debug .cs files, but you can't debug a generated assembly.
	//
	// Conclusion:
	// Generating an assembly has literally no advantages, is harder to do, and also has the down side of not being able to debug it. 
	// So for now we'll just stick with generating source code...
	//

	class AssemblyFormatterGenerator
	{
		const BindingFlags BindingFlags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

		readonly CerasSerializer _ceras;
		readonly Type _type;
		readonly TypeMetaData _meta;

		public AssemblyFormatterGenerator(CerasSerializer ceras, Type type)
		{
			_ceras = ceras;
			_type = type;
			_meta = _ceras.GetTypeMetaData(_type);
		}

		public void Generate(ModuleBuilder dynamicModule)
		{
			var formatter = _ceras.GetSpecificFormatter(_type);
			var realFormatterType = formatter.GetType();

			if (!realFormatterType.IsGenericType || realFormatterType.GetGenericTypeDefinition() != typeof(DynamicFormatter<>))
				throw new Exception($"Ceras uses '{realFormatterType.FriendlyName()}', instead of '{nameof(DynamicFormatter<int>)}<>' to format the type '{_type.FriendlyName()}'. The AotTool could generate a formatter for this type, but it would not be used. This must mean there's some kind of configuration error?");


			// Generate a new type to implement this formatter
			var interfaces = new Type[] { typeof(IFormatter<>).MakeGenericType(_type) };
			// var formatterType = dynamicModule.DefineType($"ADASD_{_type.Name}Formatter", TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Public, null, interfaces);
			var formatterType = dynamicModule.DefineType($"{_type.Name}Formatter", TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Public);

			// Use CerasDI to auto inject IFormatter<>
			var neededFormatterTypes = _meta.PrimarySchema.Members.DistinctBy(m => m.MemberType).ToArray();
			Dictionary<Type, FieldInfo> formattedTypeToFormatterField = new Dictionary<Type, FieldInfo>();

			foreach (var member in neededFormatterTypes)
			{
				var fieldName = MakeFormatterFieldName(member.MemberType);
				//var fieldType = typeof(IFormatter<>).MakeGenericType(member.MemberType);
				var actualFormatterForMember = _ceras.GetReferenceFormatter(member.MemberType);
				var formatterFieldType = actualFormatterForMember.GetType();

				var field = formatterType.DefineField(fieldName, formatterFieldType, FieldAttributes.Private | FieldAttributes.InitOnly);

				formattedTypeToFormatterField.Add(member.MemberType, field);
			}

			// Get the expressions
			Expression serializeExpr = (LambdaExpression)realFormatterType.GetMethod("GenerateSerializer", BindingFlags).Invoke(null, new object[] { _ceras, _meta.PrimarySchema });
			Expression deserializeExpr = (LambdaExpression)realFormatterType.GetMethod("GenerateDeserializer", BindingFlags).Invoke(null, new object[] { _ceras, _meta.PrimarySchema });

			// Redirect constants from lambda closure to DI
			//ParameterExpression self = Expression.Parameter(formatterType, "self");

			//var redirectVisitor = new LambdaConstantRedirect(formatterType, self, formattedTypeToFormatterField);
			//serializeExpr = redirectVisitor.Visit(serializeExpr);

			//var injVisitor = new InjectSelfArg(self);
			//serializeExpr = injVisitor.Visit(serializeExpr);



			// Emit body
			var serializeMethod = formatterType.DefineMethod(nameof(IFormatter<int>.Serialize),
															 MethodAttributes.Final | MethodAttributes.Public | MethodAttributes.Static,
															 typeof(void),
															 new Type[] { typeof(byte).MakeByRefType(), typeof(int).MakeByRefType(), _type });

			var serializeLambdaExpr = (LambdaExpression)serializeExpr;

			serializeLambdaExpr.CompileToMethod(serializeMethod);

			//var compilerType = typeof(Expression).Assembly.GetType("System.Linq.Expressions.Compiler.LambdaCompiler");
			// void Compile(LambdaExpression lambda, MethodBuilder method, DebugInfoGenerator debugInfoGenerator)
			//var compileMethod = compilerType
			//                    .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
			//					.Single(m => m.Name == "Compile" && m.GetParameters().Length == 3);

			//AppDomain.CurrentDomain.TypeResolve += (object sender, ResolveEventArgs resolveEventArgs) =>
			//{
			//	if(resolveEventArgs.Name == formatterType.Name)
			//		return formatterType.Assembly;
			//	return null;
			//};
			//compileMethod.Invoke(null, new object[] { serializeLambdaExpr, serializeMethod, null });
			
			//System.Linq.Expressions.Compiler.LambdaCompiler.Compile(this, serializeMethod, null);
			// compilerType.AsDynamicType().Compile(serializeLambdaExpr, serializeMethod, null);


			formatterType.CreateType();
		}

		static string MakeFormatterFieldName(Type formattedType)
		{
			string typeName = formattedType.Name;

			typeName = char.ToLowerInvariant(typeName[0]) + typeName.Remove(0, 1);

			typeName = "_" + typeName;

			typeName = typeName + "Formatter";

			return typeName;
		}

		
		static void GenerateFormattersAssembly(Type[] targets)
		{
			SerializerConfig config = new SerializerConfig(); // get from static method
			config.Advanced.AotMode = AotMode.None; // force aot off
			var ceras = new CerasSerializer(config);

			string assemblyName = "Ceras.GeneratedFormatters";
			var dynamicAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Save);
			var dynamicModule = dynamicAssembly.DefineDynamicModule(assemblyName + "_module", assemblyName + ".dll");

			foreach (var t in targets)
			{
				var generator = new AssemblyFormatterGenerator(ceras, t);
				generator.Generate(dynamicModule);
			}

			dynamicAssembly.Save("Ceras.Gen.dll");
		}

		static void Test()
		{
			new ReflectionPermission(ReflectionPermissionFlag.MemberAccess).Demand();

			//
			// Create lambda expression to implement the method
			//
			var testObjInstance = Expression.Parameter(typeof(TestObj), "instance");
			var newValue = Expression.Parameter(typeof(string), "newValue");

			var privateField = typeof(TestObj).GetField("_privateText", BindingFlags.NonPublic | BindingFlags.Instance);
			var assignment = Expression.Assign(Expression.MakeMemberAccess(testObjInstance, privateField), newValue);

			var lambdaExp = Expression.Lambda<Action<TestObj, string>>(assignment, testObjInstance, newValue);

			var testObj = new TestObj();
			var lambda = lambdaExp.Compile();
			lambda(testObj, "lambda!");


			//
			// Now the same thing using CompileToMethod()
			//
			string assemblyName = "Ceras.GeneratedFormatters";
			var dynamicAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Save);
			var dynamicModule = dynamicAssembly.DefineDynamicModule(assemblyName + "_module", assemblyName + ".dll");
			var dynType = dynamicModule.DefineType("ChangeThing", TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

			var dynMethod = dynType.DefineMethod("ChangeValueOnTestObj", MethodAttributes.Public | MethodAttributes.Static );

			lambdaExp.CompileToMethod(dynMethod);
			
			var createdType = dynType.CreateType();

			//var del = (Action<TestObj, string>)dynMethod.CreateDelegate(typeof(Action<TestObj, string>));
			var createdMethodInfo = createdType.GetMethod("ChangeValueOnTestObj");
			var del = (Action<TestObj, string>) createdMethodInfo.CreateDelegate(typeof(Action<TestObj, string>));

			del(testObj, "changed by compile to method!");
		}
	
	}

	class FixLambdaConstants : ExpressionVisitor
	{
		readonly TypeBuilder _formatterType;
		readonly ParameterExpression _self;
		readonly Dictionary<Type, FieldInfo> _formattedTypeToFormatterField;

		public FixLambdaConstants(TypeBuilder formatterType, ParameterExpression self, Dictionary<Type, FieldInfo> formattedTypeToFormatterField)
		{
			_formatterType = formatterType;
			_self = self;
			_formattedTypeToFormatterField = formattedTypeToFormatterField;
		}

		protected override Expression VisitConstant(ConstantExpression node)
		{
			if (node.Value is IFormatter f)
			{
				var typeOfFormatterConstant = f.GetType();
				var closedType = ReflectionHelper.FindClosedType(typeOfFormatterConstant, typeof(IFormatter<>));
				var formattedType = closedType.GetGenericArguments()[0];

				var field = _formattedTypeToFormatterField[formattedType];

				return Expression.Constant(null, node.Type);
				// return Expression.Field(_self, field);
			}

			var comingOut = base.VisitConstant(node);

			return comingOut;
		}
	}

	class InjectSelfArg : ExpressionVisitor
	{
		readonly ParameterExpression _self;

		public InjectSelfArg(ParameterExpression self)
		{
			_self = self;
		}

		protected override Expression VisitLambda<T>(Expression<T> node)
		{
			var parameters = node.Parameters.ToList();
			parameters.Insert(0, _self);

			return Expression.Lambda(node.Body, parameters);
		}
	}
	
	class TestObj
	{
		string _privateText = "default value";
	}

}
