using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Runtime.Serialization;

namespace Utf8Json.Internal.Emit
{
    public static class NonPublicFieldAccessor
    {
        public static readonly List<Func<object, object>> GetterDelegates = new List<Func<object, object>>(32768);
        public static readonly List<Action<object, object>> SetterDelegates = new List<Action<object, object>>(32768);

        public static FieldInfo GetterDelegatesFieldInfo;
        public static FieldInfo SetterDelegatesFieldInfo;

        static NonPublicFieldAccessor()
        {
            GetterDelegatesFieldInfo = typeof(NonPublicFieldAccessor).GetField("GetterDelegates", BindingFlags.Static | BindingFlags.Public);
            SetterDelegatesFieldInfo = typeof(NonPublicFieldAccessor).GetField("SetterDelegates", BindingFlags.Static | BindingFlags.Public);
        }

        public static int AddGetterDelegate(Delegate del)
        {
            lock (GetterDelegates)
            {
                GetterDelegates.Add((Func<object, object>)del);
                return GetterDelegates.Count - 1;
            }
        }

        public static int AddSetterDelegate(Delegate del)
        {
            lock (SetterDelegates)
            {
                SetterDelegates.Add((Action<object, object>)del);
                return SetterDelegates.Count - 1;
            }
        }
    }

    internal class MetaMember
    {
        public string Name { get; private set; }
        public string MemberName { get; private set; }

        public bool IsProperty { get { return PropertyInfo != null; } }
        public bool IsField { get { return FieldInfo != null; } }
        public bool IsWritable { get; private set; }
        public bool IsReadable { get; private set; }
        public bool IsPublic { get; private set; }
        public Type Type { get; private set; }
        public FieldInfo FieldInfo { get; private set; }
        public PropertyInfo PropertyInfo { get; private set; }
        public MethodInfo ShouldSerializeMethodInfo { get; private set; }
        public Type ParentType { get;private set; }

        MethodInfo getMethod;
        MethodInfo setMethod;

        protected MetaMember(Type type, string name, string memberName, bool isWritable, bool isReadable)
        {
            this.Name = name;
            this.MemberName = memberName;
            this.Type = type;
            this.IsWritable = isWritable;
            this.IsReadable = isReadable;
        }

        public MetaMember(Type parentType, FieldInfo info, string name, bool allowPrivate)
        {
            this.ParentType = parentType;
            
            this.Name = name;
            this.MemberName = info.Name;
            this.FieldInfo = info;
            this.Type = info.FieldType;
            this.IsReadable = allowPrivate || info.IsPublic;
            this.IsWritable = allowPrivate || (info.IsPublic && !info.IsInitOnly);
            this.IsPublic = info.IsPublic;
            this.ShouldSerializeMethodInfo = GetShouldSerialize(info);
        }

        public MetaMember(Type parentType, PropertyInfo info, string name, bool allowPrivate)
        {
            this.ParentType = parentType;

            this.getMethod = GetGetMethod(parentType, info);
            this.setMethod = GetSetMethod(parentType, info);
 
            this.Name = name;
            this.MemberName = info.Name;
            this.PropertyInfo = info;
            this.Type = info.PropertyType;
            this.IsReadable = (getMethod != null) && (allowPrivate || getMethod.IsPublic) && !getMethod.IsStatic;
            this.IsWritable = (setMethod != null) && (allowPrivate || setMethod.IsPublic) && !setMethod.IsStatic;
            this.ShouldSerializeMethodInfo = GetShouldSerialize(info);
        }

        private MethodInfo GetGetMethod(Type type, PropertyInfo info)
        {
            MethodInfo result;
            do
            {
                result = info.GetGetMethod(true);
                if (result != null) return result;
                type = type.BaseType;
                info = type.GetProperty(info.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            while (info != null);

            return null;
        }

        private MethodInfo GetSetMethod(Type type, PropertyInfo info)
        {
            MethodInfo result;
            do
            {
                result = info.GetSetMethod(true);
                if (result != null) return result;
                type = type.BaseType;
                info = type.GetProperty(info.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            while (info != null);

            return null;
        }

        static MethodInfo GetShouldSerialize(MemberInfo info)
        {
            var shouldSerialize = "ShouldSerialize" + info.Name;

            // public only
            return info.DeclaringType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(x => x.Name == shouldSerialize && x.ReturnType == typeof(bool) && x.GetParameters().Length == 0)
                .FirstOrDefault();
        }

        public T GetCustomAttribute<T>(bool inherit) where T : Attribute
        {
            if (IsProperty)
            {
                return PropertyInfo.GetCustomAttribute<T>(inherit);
            }
            else if (FieldInfo != null)
            {
                return FieldInfo.GetCustomAttribute<T>(inherit);
            }
            else
            {
                return null;
            }
        }

        public virtual void EmitLoadValue(ILGenerator il)
        {
            if (IsProperty)
            {
                if (getMethod.IsPublic)
                {
                    il.EmitCall(getMethod);
                }
                else
                {
                    // generate dynamic method to call nonpublic get method
                    var varType = getMethod.ReturnType;
                    var dynMethod = new DynamicMethod("Get" + getMethod.Name, typeof(object), new[] { typeof(object) }, ParentType, true);
                    var ilGen = dynMethod.GetILGenerator();

                    ilGen.Emit(OpCodes.Ldarg_0);
                    ilGen.Emit(OpCodes.Call, getMethod);
                    ilGen.EmitBoxOrDoNothing(varType);
                    ilGen.Emit(OpCodes.Ret);

                    EmitLoadValueWithDelegate(il, dynMethod, varType);
                }
            }
            else
            {
                if (IsPublic)
                {
                    il.Emit(OpCodes.Ldfld, FieldInfo);
                }
                else
                {
                    // generate dynamic method to get nonpublic field value
                    var dynMethod = new DynamicMethod("Get" + FieldInfo.Name, typeof(object), new[] { typeof(object) }, ParentType, true);
                    var ilGen = dynMethod.GetILGenerator();

                    ilGen.Emit(OpCodes.Ldarg_0);
                    ilGen.Emit(OpCodes.Ldfld, FieldInfo);
                    ilGen.EmitBoxOrDoNothing(FieldInfo.FieldType);
                    ilGen.Emit(OpCodes.Ret);

                    EmitLoadValueWithDelegate(il, dynMethod, FieldInfo.FieldType);
                }
            }
        }

        private void EmitLoadValueWithDelegate(ILGenerator il, DynamicMethod dynMethod, Type varType)
        {
            var obj = il.DeclareLocal(ParentType);
            il.EmitStloc(obj);

            var index = NonPublicFieldAccessor.AddGetterDelegate(dynMethod.CreateDelegate(typeof(Func<object, object>)));

            il.Emit(OpCodes.Ldsfld, NonPublicFieldAccessor.GetterDelegatesFieldInfo);
            il.EmitLdc_I4(index);
            il.EmitCall(NonPublicFieldAccessor.GetterDelegatesFieldInfo.FieldType.GetMethod("get_Item", BindingFlags.Instance | BindingFlags.Public));

            il.EmitLdloc(obj);
            var invokeMethod = typeof(Func<object, object>).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
            il.EmitCall(invokeMethod);

            il.EmitUnboxOrCast(varType);
        }

        public virtual void EmitStoreValue(ILGenerator il)
        {
            if (IsProperty)
            {
                if (setMethod.IsPublic)
                {
                    il.EmitCall(setMethod);
                }
                else
                {
                    // generate dynamic method to call nonpublic set method
                    var varType = setMethod.GetParameters()[0].ParameterType;
                    var dynMethod = new DynamicMethod("Set" + setMethod.Name, null, new[] { typeof(object), typeof(object) }, ParentType, true);
                    var ilGen = dynMethod.GetILGenerator();

                    ilGen.Emit(OpCodes.Ldarg_0);
                    ilGen.Emit(OpCodes.Ldarg_1);
                    ilGen.EmitUnboxOrCast(varType);
                    ilGen.Emit(OpCodes.Call, setMethod);
                    ilGen.Emit(OpCodes.Ret);

                    EmitSaveValueWithDelegate(il, dynMethod, varType);
                }
            }
            else
            {
                if (IsPublic)
                {
                    il.Emit(OpCodes.Stfld, FieldInfo);
                }
                else
                {
                    // generate dynamic method to set nonpublic field value
                    var dynMethod = new DynamicMethod("Set" + FieldInfo.Name, null, new[] { typeof(object), typeof(object) }, ParentType, true);
                    var ilGen = dynMethod.GetILGenerator();

                    ilGen.Emit(OpCodes.Ldarg_0);
                    ilGen.Emit(OpCodes.Ldarg_1);
                    ilGen.EmitUnboxOrCast(FieldInfo.FieldType);
                    ilGen.Emit(OpCodes.Stfld, FieldInfo);
                    ilGen.Emit(OpCodes.Ret);

                    EmitSaveValueWithDelegate(il, dynMethod, FieldInfo.FieldType);
                }
            }
        }

        private void EmitSaveValueWithDelegate(ILGenerator il, DynamicMethod dynMethod, Type varType)
        {
            var index = NonPublicFieldAccessor.AddSetterDelegate(dynMethod.CreateDelegate(typeof(Action<object, object>)));

            il.EmitBoxOrDoNothing(varType);
            var obj = il.DeclareLocal(ParentType);
            var val = il.DeclareLocal(ParentType);
            il.EmitStloc(val);
            il.EmitStloc(obj);

            il.Emit(OpCodes.Ldsfld, NonPublicFieldAccessor.SetterDelegatesFieldInfo);
            il.EmitLdc_I4(index);
            il.EmitCall(NonPublicFieldAccessor.SetterDelegatesFieldInfo.FieldType.GetMethod("get_Item", BindingFlags.Instance | BindingFlags.Public));

            il.EmitLdloc(obj);
            il.EmitLdloc(val);

            var invokeMethod = typeof(Action<object, object>).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
            il.EmitCall(invokeMethod);
        }
    }

    // used for serialize exception...
    internal class StringConstantValueMetaMember : MetaMember
    {
        readonly string constant;

        public StringConstantValueMetaMember(string name, string constant)
            : base(typeof(String), name, name, false, true)
        {
            this.constant = constant;
        }

        public override void EmitLoadValue(ILGenerator il)
        {
            il.Emit(OpCodes.Pop); // pop load instance
            il.Emit(OpCodes.Ldstr, constant);
        }

        public override void EmitStoreValue(ILGenerator il)
        {
            throw new NotSupportedException();
        }
    }

    // used for serialize exception...
    internal class InnerExceptionMetaMember : MetaMember
    {
        static readonly MethodInfo getInnerException = ExpressionUtility.GetPropertyInfo((Exception ex) => ex.InnerException).GetGetMethod();
        static readonly MethodInfo nongenericSerialize = ExpressionUtility.GetMethodInfo<JsonWriter>(writer => JsonSerializer.NonGeneric.Serialize(ref writer, default(object), default(IJsonFormatterResolver)));

        // set after...
        internal ArgumentField argWriter;
        internal ArgumentField argValue;
        internal ArgumentField argResolver;

        public InnerExceptionMetaMember(string name)
            : base(typeof(Exception), name, name, false, true)
        {
        }

        public override void EmitLoadValue(ILGenerator il)
        {
            il.Emit(OpCodes.Callvirt, getInnerException);
        }

        public override void EmitStoreValue(ILGenerator il)
        {
            throw new NotSupportedException();
        }

        public void EmitSerializeDirectly(ILGenerator il)
        {
            // JsonSerializer.NonGeneric.Serialize(ref writer, value.InnerException, formatterResolver);
            argWriter.EmitLoad();
            argValue.EmitLoad();
            il.Emit(OpCodes.Callvirt, getInnerException);
            argResolver.EmitLoad();
            il.EmitCall(nongenericSerialize);
        }
    }
}