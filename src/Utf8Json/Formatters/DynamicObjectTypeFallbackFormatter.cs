using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Utf8Json.Internal;
using Utf8Json.Internal.Emit;
using Utf8Json.Resolvers;

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
        static readonly Regex SubtractFullNameRegex = new Regex(@", Version=\d+.\d+.\d+.\d+, Culture=\w+, PublicKeyToken=\w+", RegexOptions.Compiled);

        private readonly Dictionary<string, Type> typesByName = new Dictionary<string, Type>();

        delegate void SerializeMethod(object dynamicFormatter, ref JsonWriter writer, object value, IJsonFormatterResolver formatterResolver);
        
        delegate object DeserializeMethod(object dynamicFormatter, ref JsonReader reader, IJsonFormatterResolver formatterResolver);

        readonly ThreadsafeTypeKeyHashTable<KeyValuePair<object, Tuple<SerializeMethod, bool>>> serializers = new ThreadsafeTypeKeyHashTable<KeyValuePair<object, Tuple<SerializeMethod, bool>>>();

        readonly ThreadsafeTypeKeyHashTable<KeyValuePair<object, Tuple<DeserializeMethod, bool>>> deserializers = new ThreadsafeTypeKeyHashTable<KeyValuePair<object, Tuple<DeserializeMethod, bool>>>();

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

            KeyValuePair<object, Tuple<SerializeMethod, bool>> formatterAndDelegate;
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

                            var writeTypeName = BuiltinResolver.HasFormatter(type);
                            formatterAndDelegate = new KeyValuePair<object, Tuple<SerializeMethod, bool>>(formatter, Tuple.Create((SerializeMethod)dm.CreateDelegate(typeof(SerializeMethod)), writeTypeName));
                        }

                        serializers.TryAdd(t, formatterAndDelegate);
                    }
                }
            }

            if (formatterAndDelegate.Value.Item2)
            {
                writer.WriteBeginObject();
                writer.WritePropertyName("$type");
                var typeName = SubtractFullNameRegex.Replace(type.AssemblyQualifiedName, "");
                writer.WriteString(typeName);
                writer.WriteValueSeparator();
                writer.WritePropertyName("$value");
            }
            
            formatterAndDelegate.Value.Item1(formatterAndDelegate.Key, ref writer, value, formatterResolver);

            if (formatterAndDelegate.Value.Item2)
            {
                writer.WriteEndObject();
            }
        }

        public object Deserialize(ref JsonReader reader, IJsonFormatterResolver formatterResolver)
        {
            if (reader.ReadIsNull()) return null;

            var typeName = reader.ReadTypeNameWithVerify();
            if (typeName == null) return null;
            var type = GetTypeFast(typeName);
            if (type == null) throw new UnknownTypeException(typeName);

            KeyValuePair<object, Tuple<DeserializeMethod, bool>> formatterAndDelegate;
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

                            il.EmitBoxOrDoNothing(type);

                            il.Emit(OpCodes.Ret);

                            var writeTypeName = BuiltinResolver.HasFormatter(type);
                            formatterAndDelegate = new KeyValuePair<object, Tuple<DeserializeMethod, bool>>(formatter, Tuple.Create((DeserializeMethod)dm.CreateDelegate(typeof(DeserializeMethod)), writeTypeName));
                        }

                        deserializers.TryAdd(t, formatterAndDelegate);
                    }
                }
            }

            if (formatterAndDelegate.Value.Item2)
            {
                reader.ReadIsBeginObjectWithVerify();
                if (reader.ReadPropertyName() != "$type") throw new JsonParsingException("$type missing");
                reader.ReadString();
                reader.ReadIsValueSeparatorWithVerify();
                if (reader.ReadPropertyName() != "$value") throw new JsonParsingException("$value missing");
            }

            var value = formatterAndDelegate.Value.Item1(formatterAndDelegate.Key, ref reader, formatterResolver);

            if (formatterAndDelegate.Value.Item2)
            {
                reader.ReadIsEndObject();
            }

            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Type GetTypeFast(string typeName)
        {
            lock (typesByName)
            {
                Type type;
                if (typesByName.TryGetValue(typeName, out type)) return type;
                type = Type.GetType(typeName);
                typesByName.Add(typeName, type);
                return type;
            }
        }
    }
    public class UnknownTypeException : Exception
    {
        public UnknownTypeException(string typeName) : base("unknown type " + typeName)
        {
        }
    }
}