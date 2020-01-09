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
        private static readonly List<Func<object, object>> getterDelegates = new List<Func<object, object>>();
        private static readonly List<Action<object, object>> setterDelegates = new List<Action<object, object>>();

        public static readonly List<string> GetterFieldNames = new List<string>();
        public static readonly List<string> SetterFieldNames = new List<string>();

        public static MethodInfo GetNonPublicFieldMethod;

        public static MethodInfo SetNonPublicFieldMethod;

        static NonPublicFieldAccessor()
        {
            GetNonPublicFieldMethod = typeof(NonPublicFieldAccessor).GetMethod("GetNonPublicField", BindingFlags.Static | BindingFlags.Public);
            SetNonPublicFieldMethod = typeof(NonPublicFieldAccessor).GetMethod("SetNonPublicField", BindingFlags.Static | BindingFlags.Public);
        }

        public static int AddGetterDelegate(Delegate del)
        {
            getterDelegates.Add((Func<object, object>)del);
            return getterDelegates.Count - 1;
        }

        public static int AddSetterDelegate(Delegate del)
        {
            setterDelegates.Add((Action<object, object>)del);
            return setterDelegates.Count - 1;
        }

        public static object GetNonPublicField(object obj, int delegateIdx)
        {
            return getterDelegates[delegateIdx].Invoke(obj);
        }

        public static void SetNonPublicField(object obj, object value, int delegateIdx)
        {
            setterDelegates[delegateIdx].Invoke(obj, value);
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

            this.getMethod = info.GetGetMethod(true);
            this.setMethod = info.GetSetMethod(true);

            this.Name = name;
            this.MemberName = info.Name;
            this.PropertyInfo = info;
            this.Type = info.PropertyType;
            this.IsReadable = (getMethod != null) && (allowPrivate || getMethod.IsPublic) && !getMethod.IsStatic;
            this.IsWritable = (setMethod != null) && (allowPrivate || setMethod.IsPublic) && !setMethod.IsStatic;
            this.ShouldSerializeMethodInfo = GetShouldSerialize(info);
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
                il.EmitCall(getMethod);
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
                    ilGen.Emit(OpCodes.Ret);

                    var idx = NonPublicFieldAccessor.AddGetterDelegate(dynMethod.CreateDelegate(typeof(Func<object, object>)));
                    NonPublicFieldAccessor.GetterFieldNames.Add(FieldInfo.Name);

                    il.EmitLdc_I4(idx);
                    il.Emit(OpCodes.Call, NonPublicFieldAccessor.GetNonPublicFieldMethod);
                }
            }
        }

        public virtual void EmitStoreValue(ILGenerator il)
        {
            if (IsProperty)
            {
                il.EmitCall(setMethod);
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

                    var idx = NonPublicFieldAccessor.AddSetterDelegate(dynMethod.CreateDelegate(typeof(Action<object, object>)));
                    NonPublicFieldAccessor.SetterFieldNames.Add(FieldInfo.Name);

                    il.EmitBoxOrDoNothing(FieldInfo.FieldType);
                    il.EmitLdc_I4(idx);
                    il.Emit(OpCodes.Call, NonPublicFieldAccessor.SetNonPublicFieldMethod);
                }
            }
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