using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

using Utf8Json.Internal;
using Utf8Json.Internal.Emit;

namespace Utf8Json.Formatters
{
    public sealed class DynamicObjectTypeFallbackFormatterAdapter<T> : IJsonFormatter<T>
    {
        private readonly IJsonFormatter<object> formatter;

        public DynamicObjectTypeFallbackFormatterAdapter(IJsonFormatter<object> _formatter)
        {
            formatter = _formatter;
        } 

        public void Serialize(ref JsonWriter writer, T value, IJsonFormatterResolver formatterResolver)
        {
            formatter.Serialize(ref writer, value, formatterResolver);
        }

        public T Deserialize(ref JsonReader reader, IJsonFormatterResolver formatterResolver)
        {
            var obj = formatter.Deserialize(ref reader, formatterResolver);
            return (T)obj;
        }
    }

    public sealed class DynamicObjectTypeFallbackFormatter : IJsonFormatter<object>
    {
        private Dictionary<string, Type> typesByName = new Dictionary<string, Type>();

        delegate void SerializeMethod(object dynamicFormatter, ref JsonWriter writer, object value, IJsonFormatterResolver formatterResolver);
        
        delegate object DeserializeMethod(object dynamicFormatter, ref JsonReader reader, IJsonFormatterResolver formatterResolver);

        readonly ThreadsafeTypeKeyHashTable<KeyValuePair<object, SerializeMethod>> serializers = new ThreadsafeTypeKeyHashTable<KeyValuePair<object, SerializeMethod>>();

        readonly ThreadsafeTypeKeyHashTable<KeyValuePair<object, DeserializeMethod>> deserializers = new ThreadsafeTypeKeyHashTable<KeyValuePair<object, DeserializeMethod>>();

        readonly IJsonFormatterResolver[] innerResolvers;

        public DynamicObjectTypeFallbackFormatter(params IJsonFormatterResolver[] innerResolvers)
        {
            this.innerResolvers = innerResolvers;
        }

        public void Serialize(ref JsonWriter writer, object value, IJsonFormatterResolver formatterResolver)
        {
            if (value == null) { writer.WriteNull(); return; }

            var type = value.GetType();

            if (type == typeof(object))
            {
                // serialize to empty object
                writer.WriteBeginObject();
                writer.WriteEndObject();
                return;
            }

            KeyValuePair<object, SerializeMethod> formatterAndDelegate;
            if (!serializers.TryGetValue(type, out formatterAndDelegate))
            {
                lock (serializers)
                {
                    if (!serializers.TryGetValue(type, out formatterAndDelegate))
                    {
                        object formatter = null;
                        foreach (var innerResolver in innerResolvers)
                        {
                            formatter = innerResolver.GetFormatterDynamic(type);
                            if (formatter != null) break;
                        }
                        if (formatter == null)
                        {
                            throw new FormatterNotRegisteredException(type.FullName + " is not registered in this resolver. resolvers:" + string.Join(", ", innerResolvers.Select(x => x.GetType().Name).ToArray()));
                        }

                        var t = type;
                        {
                            var dm = new DynamicMethod("Serialize", null, new[] { typeof(object), typeof(JsonWriter).MakeByRefType(), typeof(object), typeof(IJsonFormatterResolver) }, type.Module, true);
                            var il = dm.GetILGenerator();

                            // delegate void SerializeMethod(object dynamicFormatter, ref JsonWriter writer, object value, IJsonFormatterResolver formatterResolver);

                            il.EmitLdarg(0);
                            il.Emit(OpCodes.Castclass, typeof(IJsonFormatter<>).MakeGenericType(t));
                            il.EmitLdarg(1);
                            il.EmitLdarg(2);
                            il.EmitUnboxOrCast(t);
                            il.EmitLdarg(3);

                            il.EmitCall(Resolvers.Internal.DynamicObjectTypeBuilder.EmitInfo.Serialize(t));

                            il.Emit(OpCodes.Ret);

                            formatterAndDelegate = new KeyValuePair<object, SerializeMethod>(formatter, (SerializeMethod)dm.CreateDelegate(typeof(SerializeMethod)));
                        }

                        serializers.TryAdd(t, formatterAndDelegate);
                    }
                }
            }

            formatterAndDelegate.Value(formatterAndDelegate.Key, ref writer, value, formatterResolver);
        }

        public object Deserialize(ref JsonReader reader, IJsonFormatterResolver formatterResolver)
        {
            if (reader.ReadIsNull()) return null;

            var typeName = reader.ReadTypeNameWithVerify();
            if (typeName == null) return null;
            var type = GetTypeFast(typeName);
            if (type == null) throw new UnknownTypeException(typeName);

            KeyValuePair<object, DeserializeMethod> formatterAndDelegate;
            if (!deserializers.TryGetValue(type, out formatterAndDelegate))
            {
                lock (deserializers)
                {
                    if (!deserializers.TryGetValue(type, out formatterAndDelegate))
                    {
                        object formatter = null;
                        foreach (var innerResolver in innerResolvers)
                        {
                            formatter = innerResolver.GetFormatterDynamic(type);
                            if (formatter != null) break;
                        }
                        if (formatter == null)
                        {
                            throw new FormatterNotRegisteredException(type.FullName + " is not registered in this resolver. resolvers:" + string.Join(", ", innerResolvers.Select(x => x.GetType().Name).ToArray()));
                        }

                        var t = type;
                        {
                            var dm = new DynamicMethod("Deserialize", typeof(object), new[] { typeof(object), typeof(JsonReader).MakeByRefType(), typeof(IJsonFormatterResolver) }, type.Module, true);
                            var il = dm.GetILGenerator();

                            // delegate object DeserializeMethod(object dynamicFormatter, ref JsonReader reader, IJsonFormatterResolver formatterResolver);

                            il.EmitLdarg(0);
                            il.Emit(OpCodes.Castclass, typeof(IJsonFormatter<>).MakeGenericType(t));
                            il.EmitLdarg(1);
                            il.EmitLdarg(2);

                            il.EmitCall(Resolvers.Internal.DynamicObjectTypeBuilder.EmitInfo.Deserialize(t));

                            il.Emit(OpCodes.Castclass, typeof(object));
                            il.Emit(OpCodes.Ret);

                            formatterAndDelegate = new KeyValuePair<object, DeserializeMethod>(formatter, (DeserializeMethod)dm.CreateDelegate(typeof(DeserializeMethod)));
                        }

                        deserializers.TryAdd(t, formatterAndDelegate);
                    }
                }
            }

            return formatterAndDelegate.Value(formatterAndDelegate.Key, ref reader, formatterResolver);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Type GetTypeFast(string typeName)
        {
            Type type;
            if (typesByName.TryGetValue(typeName, out type)) return type;
            type = Type.GetType(typeName);
            typesByName.Add(typeName, type);
            return type;
        }
    }
    public class UnknownTypeException : Exception
    {
        public UnknownTypeException(string typeName) : base("unknown type " + typeName)
        {
        }
    }
}