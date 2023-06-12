using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using com.bbbirder.unity;
using InjectionParams = com.bbbirder.unity.FixHelper.InjectionParams;
using UnityEngine;

namespace com.bbbirder.unityeditor {
    public static class InjectHelper{

        internal static void InjectAssembly(InjectionParams[] injections, string inputAssemblyPath,string outputAssemblyPath) {

            // var assemblySearchFolders = UnityInjectUtils.GetAssemblySearchFolders(isEditor, buildTarget);
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(outputAssemblyPath));
            // foreach(var folder in assemblySearchFolders){
            //    resolver.AddSearchDirectory(folder);
            // }
            
            var targetAssembly = AssemblyDefinition.ReadAssembly(inputAssemblyPath, new ReaderParameters(){
                AssemblyResolver=resolver,
                ReadingMode=ReadingMode.Immediate,
                InMemory = true,
            });

            //mark check
            var injected = targetAssembly.MainModule.Types.Any(t=>
                Settings.InjectedMarkName == t.Name &&
                Settings.InjectedMarkNamespace ==t.Namespace);
            if(injected){
                targetAssembly.Dispose();
                return;
            }

            foreach(var injection in injections){
                var (type,methodName,owName,miReplace) = injection;
                var targetType = targetAssembly.MainModule.Types
                    .Where(t => IsSameType(type,t))
                    .SingleOrDefault();
                if(targetType is null){
                    throw new($"Cannot find Type `{type}` in target assembly {inputAssemblyPath}");
                }
                var targetMethod = targetType.FindMethod(methodName);
                if(targetMethod is null){
                    throw new($"Cannot find Method `{methodName}` in Type `{type}`");
                }

                //add origin
                var originalMethod = targetType.DuplicateOriginalMethod(targetMethod);
                //add field
                var field = targetType.AddInjectField(targetMethod,methodName);
                //add method
                targetType.AddInjectionMethod(targetMethod,originalMethod,field,methodName);
            }

            //mark make
            var InjectedMark = new TypeDefinition(
                Settings.InjectedMarkNamespace,
                Settings.InjectedMarkName,
                TypeAttributes.Class,
                targetAssembly.MainModule.TypeSystem.Object);
            targetAssembly.MainModule.Types.Add(InjectedMark);

            targetAssembly.Write(outputAssemblyPath);
            targetAssembly.MainModule.AssemblyResolver.Dispose(); // fixes: auto resolved modules not disposed
            targetAssembly.Dispose();
            static bool IsSameType(Type t1,TypeDefinition t2){
                var isSameNamespace = t1.Namespace==t2.Namespace;
                if(string.IsNullOrEmpty(t1.Namespace) && string.IsNullOrEmpty(t2.Namespace)){
                    isSameNamespace = true;
                }
                return t1.Name==t2.Name && isSameNamespace;
            }
        }
        static MethodDefinition DuplicateOriginalMethod(this TypeDefinition targetType,MethodDefinition targetMethod){
            var originName = Settings.GetOriginMethodName(targetMethod.Name);
            var duplicatedMethod = targetMethod.Clone();
            duplicatedMethod.Name = originName;
            targetType.Methods.Add(duplicatedMethod);
            return duplicatedMethod;
        }
        static FieldDefinition AddInjectField(this TypeDefinition targetType,MethodDefinition targetMethod,string methodName){
            var injectionName = Settings.GetInjectedFieldName(methodName);
            var HasThis = targetMethod.HasThis;
            var Parameters = targetMethod.Parameters;
            var GenericParameters = targetMethod.GenericParameters;
            var CustomAttributes = targetMethod.CustomAttributes;
            var ReturnType = targetMethod.ReturnType;
            //define delegate
            var delegateParameters = new List<TypeReference>();
            if(HasThis) delegateParameters.Add(targetType);
            foreach(var p in Parameters) delegateParameters.Add(p.ParameterType);
            var delegateType = targetType.Module.CreateDelegateType(Settings.GetDelegateTypeName(methodName),targetType,ReturnType,delegateParameters);
            targetType.NestedTypes.Add(delegateType);
            //store fields
            var sfldInject = new FieldDefinition(injectionName,
                FieldAttributes.Private|FieldAttributes.Static,
                delegateType);
            // var sfldOrigin = new FieldDefinition(originName,
            //     FieldAttributes.Private|FieldAttributes.Static|FieldAttributes.Assembly,
            //     targetType.Module.ImportReference(typeof(Delegate)));
            targetType.Fields.Add(sfldInject);
            return sfldInject;
        }
        static void AddInjectionMethod(
            this TypeDefinition targetType,
            MethodDefinition targetMethod,MethodDefinition originalMethod,
            FieldDefinition delegateField,string methodName
        ){
            var HasThis =           targetMethod.HasThis;
            var Parameters =        targetMethod.Parameters;
            // var GenericParameters = targetMethod.GenericParameters;
            // var CustomAttributes =  targetMethod.CustomAttributes;
            var ReturnType =        targetMethod.ReturnType;

            //redirect method
            targetMethod.Body.Instructions.Clear();
            var delegateType = delegateField.FieldType.Resolve();
            var ilProcessor = targetMethod.Body.GetILProcessor();
            var tagOp = Instruction.Create(OpCodes.Nop);
            //check null
            ilProcessor.Append(Instruction.Create(OpCodes.Ldsfld,  delegateField));
            ilProcessor.Append(Instruction.Create(OpCodes.Brtrue_S,tagOp));
            
            //set field
            if(HasThis)
                ilProcessor.Append(Instruction.Create(OpCodes.Ldarg_0));
            else
                ilProcessor.Append(Instruction.Create(OpCodes.Ldnull));
            ilProcessor.Append(Instruction.Create(OpCodes.Ldftn, originalMethod));
            ilProcessor.Append(Instruction.Create(OpCodes.Newobj,delegateType.FindMethod(".ctor")));
            ilProcessor.Append(Instruction.Create(OpCodes.Stsfld,delegateField));

            //invoke
            ilProcessor.Append(tagOp);
            ilProcessor.Append(Instruction.Create(OpCodes.Ldsfld,delegateField));
            var argidx = 0;
            if(HasThis)
                ilProcessor.Append(ilProcessor.createLdarg(argidx++));
            for(var i=0; i<Parameters.Count; i++){
                var pType = Parameters[i].ParameterType;
                ilProcessor.Append(ilProcessor.createLdarg(argidx++));
                // if(pType.IsValueType)
                //     ilProcessor.Append(Instruction.Create(OpCodes.Box,pType));
            }
            ilProcessor.Append(Instruction.Create(OpCodes.Callvirt,
                delegateType.FindMethod("Invoke")));
            if(ReturnType.IsComplexValueType())
                ilProcessor.Append(Instruction.Create(OpCodes.Box,ReturnType));
            
            ilProcessor.Append(Instruction.Create(OpCodes.Nop));
            ilProcessor.Append(Instruction.Create(OpCodes.Ret));
        }
        // static void InjectCctor(this TypeDefinition targetType,FieldDefinition field){
        //     var cctorMethod = targetType.Methods.FirstOrDefault(m=>m.Name==".cctor");
        //     if(cctorMethod is null){
        //         cctorMethod = new MethodDefinition(".cctor",MethodAttributes.Static|MethodAttributes.Private,targetType.Module.TypeSystem.Void);
        //         targetType.Methods.Add(cctorMethod);
        //     }
        //     var ilProcessor = cctorMethod.Body.GetILProcessor();
        //     var bdis = cctorMethod.Body.Instructions;
        //     var insertPoint = bdis[0];
        //     ilProcessor.InsertBefore(insertPoint,Instruction.Create(OpCodes.Ldsfld,field));
        //     ilProcessor.InsertBefore(insertPoint,Instruction.Create(OpCodes.Ldsfld,field));
        // }
        // =>md.GetType(type.ToString(),true);
        static Instruction createLdarg(this ILProcessor ilProcessor, int i)
        {
            if (i < s_ldargs.Length)
            {
                return Instruction.Create(s_ldargs[i]);
            }
            else if (i < 256)
            {
                return ilProcessor.Create(OpCodes.Ldarg_S, (byte)i);
            }
            else
            {
                return ilProcessor.Create(OpCodes.Ldarg, (short)i);
            }
        }

        /// <summary>
        /// Create a clone of the given method definition
        /// </summary>
        public static MethodDefinition Clone(this MethodDefinition source)
        {
            var result = new MethodDefinition(source.Name, source.Attributes, source.ReturnType) {
                ImplAttributes = source.ImplAttributes,
                SemanticsAttributes = source.SemanticsAttributes,
                HasThis = source.HasThis,
                ExplicitThis = source.ExplicitThis,
                CallingConvention = source.CallingConvention
            };
            foreach (var p in source.Parameters)       result.Parameters.Add(p);
            foreach (var p in source.CustomAttributes) result.CustomAttributes.Add(p);
            foreach (var p in source.GenericParameters)result.GenericParameters.Add(p);
            if (source.HasBody)
            {
                result.Body = source.Body.Clone(result);
            }
            return result;
        }

        /// <summary>
        /// Create a clone of the given method body
        /// </summary>
        public static MethodBody Clone(this MethodBody source, MethodDefinition target)
        {
            var result = new MethodBody(target) { InitLocals = source.InitLocals, MaxStackSize = source.MaxStackSize };
            var worker = result.GetILProcessor();
            if(source.HasVariables){
                foreach(var v in source.Variables){
                    result.Variables.Add(v);
                }
            }
            foreach (var i in source.Instructions)
            {
                // Poor mans clone, but sufficient for our needs
                var clone = Instruction.Create(OpCodes.Nop);
                clone.OpCode = i.OpCode;
                clone.Operand = i.Operand;
                worker.Append(clone);
            }
            return result;
        }

        internal static bool IsReturnVoid(this MethodDefinition md)
            => md.ReturnType.ToString()==voidType.ToString();
        internal static bool IsReturnValueType(this MethodDefinition md)
            => !md.IsReturnVoid() && md.ReturnType.IsValueType;
        internal static bool IsComplexValueType(this TypeReference td)
            => td.ToString()!=voidType.ToString() && !td.IsPrimitive;
        internal static Type GetUnderlyingType(this TypeReference td)
            => td.IsPrimitive ? Type.GetType(td.Name) : objType;
        internal static MethodDefinition FindMethod(this TypeDefinition td,string methodName)
            => td.Methods.FirstOrDefault(m=>m.Name==methodName);
        internal static TypeReference ConvertType(this ModuleDefinition md,Type type){
            return new TypeReference(type.Namespace,type.Name,md,md.TypeSystem.CoreLibrary);
        }
        internal static TypeReference ConvertType<T>(this ModuleDefinition md){
            return new TypeReference(typeof(T).Namespace,typeof(T).Name,md,md.TypeSystem.CoreLibrary);
        }
        internal static TypeDefinition CreateDelegateType(this ModuleDefinition assembly,string name,TypeDefinition declaringType,
                TypeReference returnType, IEnumerable<TypeReference> parameters)
        {
            var voidType = assembly.TypeSystem.Void;
            var objectType = assembly.TypeSystem.Object;
            var nativeIntType = assembly.TypeSystem.IntPtr;
            var asyncResultType = assembly.ConvertType<IAsyncResult>();
            var asyncCallbackType = assembly.ConvertType<AsyncCallback>();
            var multicastDelegateType = assembly.ConvertType<MulticastDelegate>();

            var DelegateTypeAttributes = TypeAttributes.NestedPublic | TypeAttributes.Sealed;
            var dt = new TypeDefinition("", name, DelegateTypeAttributes, multicastDelegateType);
            dt.DeclaringType = declaringType;

            // add constructor
            var ConstructorAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            var constructor = new MethodDefinition(".ctor", ConstructorAttributes, voidType);
            constructor.Parameters.Add(new ParameterDefinition("objectInstance", ParameterAttributes.None, objectType));
            constructor.Parameters.Add(new ParameterDefinition("functionPtr", ParameterAttributes.None, nativeIntType));
            constructor.ImplAttributes = MethodImplAttributes.Runtime;
            dt.Methods.Add(constructor);

            // add BeginInvoke
            var DelegateMethodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.VtableLayoutMask;
            var beginInvoke = new MethodDefinition("BeginInvoke", DelegateMethodAttributes, asyncResultType);
            foreach (var p in parameters)
            {
                beginInvoke.Parameters.Add(new ParameterDefinition(p));
            }
            beginInvoke.Parameters.Add(new ParameterDefinition("callback", ParameterAttributes.None, asyncCallbackType));
            beginInvoke.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, objectType));
            beginInvoke.ImplAttributes = MethodImplAttributes.Runtime;
            dt.Methods.Add(beginInvoke);

            // add EndInvoke
            var endInvoke = new MethodDefinition("EndInvoke", DelegateMethodAttributes, returnType);
            endInvoke.Parameters.Add(new ParameterDefinition("result", ParameterAttributes.None, asyncResultType));
            endInvoke.ImplAttributes = MethodImplAttributes.Runtime;
            dt.Methods.Add(endInvoke);

            // add Invoke
            var invoke = new MethodDefinition("Invoke", DelegateMethodAttributes, returnType);
            foreach (var p in parameters)
            {
                invoke.Parameters.Add(new ParameterDefinition(p));
            }
            invoke.ImplAttributes = MethodImplAttributes.Runtime;
            dt.Methods.Add(invoke);

            return dt;
        }
        static Type voidType = typeof(void);
        static Type objType = typeof(object);
        static OpCode[] s_ldargs = new []{OpCodes.Ldarg_0,OpCodes.Ldarg_1,OpCodes.Ldarg_2,OpCodes.Ldarg_3};
    }
}