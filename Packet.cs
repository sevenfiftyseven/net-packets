using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Ticker
{
    // realistically packets shouldn't be very big. They should be small and frequent.
    public class Packet
    {
        /*
         *  In order to standardize packet sizes
         *  We sizelock the number of bytes in a string 
         *  1024 (1KB) is the max. This can be adjusted if necessary, but this should be plenty.
         *  Because of this, we should try to keep packets that use strings self-contained
         *  a single packet using three strings will be at least 3kb
        */
        public const int STANDARD_STRING_SIZE = 1024;
        public static Encoding STRING_ENCODING = Encoding.UTF8;
        /*
         * Because we need to have standard sizes for datatypes, 
         * we must limit the size of arrays.
         * 256 becomes rather small
         * however, for a 32bit integer, this should be roughly
         * the same as a string at 256 indices (scaling based on size of datatype)
         * at defaults, this means the size of a string[] would be 256kb, and should not be used lightly
         */

        // this also accounts for dictionaries
        public const int MAX_ARRAY_INDICES = 256;

        public Packet()
        {

        }

        public static int PacketSize(Type packetType)
        {
            int sum = 0;
            foreach (var field in packetType.GetProperties())
            {
                sum += field.PropertyType.Size(field);
            }
            return sum;
        }

        public virtual byte[] Serialize()
        {
            List<byte> data = new List<byte>();
            data.AddRange(this.GetType().GUID.ToByteArray());

            foreach (var field in this.GetType().GetProperties())
            {
                data.AddRange(field.GetValue(this).Serialize(field));
            }

            return data.ToArray();
        }

        public static void Build(ref object packet, byte[] data)
        {
            int cPos = 0;
            foreach (var field in packet.GetType().GetProperties())
            {
                var size = field.PropertyType.Size(field);
                field.SetValue(packet, data.Skip(cPos).Take(size).Deserialize(field.PropertyType));
                cPos += size;
            }
        }
    }
    public class PacketHandler
    {
        Dictionary<Type, Action<Packet>> PacketEventHandlers { get; set; } = new Dictionary<Type, Action<Packet>>();
        static Dictionary<Guid, Type> PacketRegistry = new Dictionary<Guid, Type>();

        // turns out the guid of the type doesn't change based on it's definition but based on its Fully Qualified Name (Namespace.ClassName) (Stuff.Packets.ClientAuthentication.Success)
        // so it would be nice to use some kind of checksum on the class to verify it matches from client/server
        // albeit not necessary. We can continue to use the Guid (Fully Qualified Name would be more readable during debugging)
        // I'm just going to leave this as is and verify the Version info on the shared DLLs and Client

        public void Recieve(Type packetType, Packet packet)
        {
            if (PacketEventHandlers.ContainsKey(packetType))
            {
                PacketEventHandlers[packetType](packet);
            }
        }
        public void Register(Type packetType, Action<Packet> action)
        {
            if (!PacketRegistry.ContainsKey(packetType.GUID))
            {
                PacketRegistry.Add(packetType.GUID, packetType);
            }
            PacketEventHandlers.Add(packetType, action);
        }

        public void Register<T>(Action<T> action)
            where T : Packet, new()
        {
            Register(typeof(T), new Action<Packet>(
                p => //we have to surrogate wrap this. Which is lame and does have cost
                {
                    action(p as T);
                }
            ));
        }

        public void ProcessStream(NetworkStream stream)
        {
            try
            {
                var bytes = new byte[16];
                stream.Read(bytes, 0, 16);
                var packetGuid = new Guid(bytes);
                if (PacketRegistry.ContainsKey(packetGuid))
                {
                    var packetType = PacketRegistry[packetGuid];
                    var size = Packet.PacketSize(packetType);
                    var buffer = new byte[size];
                    if (size > 0) // don't try to read if packet has no data
                    {
                        stream.Read(buffer, 0, size);
                    }
                    var packet = packetType.GetConstructor(new Type[0]).Invoke(new object[0]);
                    Packet.Build(ref packet, buffer);
                    Recieve(packetType, (Packet)packet);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                stream.Close();
            }
        }
    }

    /// <summary>
    /// Sets the maximum length of variable size fields for network serialization. Currently supports arrays, dictionaries, and strings.
    /// </summary>
    public class MaxLengthAttribute : Attribute
    {
        public MaxLengthAttribute(int max)
        {
            MaxLength = max;
        }
        public int MaxLength { get; set; }
    }

    public static class Extensions
    {
        static Type[] bitConversions = new Type[]
        {
            typeof(byte),
            typeof(char),
            typeof(short),
            typeof(ushort),
            typeof(int),
            typeof(uint),
            typeof(double),
            typeof(float),
            typeof(long),
            typeof(ulong),
            typeof(bool),
        };

        public static string sha256(this string randomString)
        {
            var crypt = new SHA256Managed();
            string hash = String.Empty;
            byte[] crypto = crypt.ComputeHash(Encoding.UTF8.GetBytes(randomString));
            foreach (byte theByte in crypto)
            {
                hash += theByte.ToString("x2");
            }
            return hash;
        }

        public static int Size(this Type type, MemberInfo source = null)
        {
            //eventually we may want to use this with the OUT parameter, in a separate function.
            var MAX_STR = Packet.STANDARD_STRING_SIZE;
            var MAX_IND = Packet.MAX_ARRAY_INDICES;

            if (source != null)
            {
                var mlAttr = source.GetCustomAttribute<MaxLengthAttribute>();
                if (mlAttr != null)
                {
                    MAX_STR = mlAttr.MaxLength;
                    MAX_IND = mlAttr.MaxLength;
                }
            }


            if (type == typeof(bool))
                return 1;
            else if (bitConversions.Contains(type))
                return Marshal.SizeOf(type);
            else if (type == typeof(string))
                return MAX_STR + 4;
            else if (type.IsSubclassOf(typeof(Array)))
                return (type.GetElementType().Size() * MAX_IND) + 4;
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                return (type.GenericTypeArguments[0].Size() + type.GenericTypeArguments[1].Size()) * MAX_IND;
            else
            {
                // grab all properties we can set
                //  By nature, this means we can only use properties where the Set value = Get Value
                var properties = type.GetProperties();

                var sum = 0;

                foreach (var prop in properties)
                    sum += prop.PropertyType.Size(prop);

                return sum;
            }
        }

        /// <summary>
        /// Generic method to serialize objects for network transfer.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="source">Used to allow attributes to be provided to serialization system.</param>
        /// <returns></returns>
        public static byte[] Serialize(this object obj, MemberInfo source = null)
        {
            if (obj == null)
                throw new Exception("Null values are not currently supported.");

            var MAX_STR = Packet.STANDARD_STRING_SIZE;
            var MAX_IND = Packet.MAX_ARRAY_INDICES;

            if (source != null)
            {
                var mlAttr = source.GetCustomAttribute<MaxLengthAttribute>();
                if (mlAttr != null)
                {
                    MAX_STR = mlAttr.MaxLength;
                    MAX_IND = mlAttr.MaxLength;
                }
            }

            List<byte> bytes = new List<byte>();
            if (obj.GetType() == typeof(byte))
            {
                bytes.Add((byte)obj);
            }
            else if (bitConversions.Contains(obj.GetType()))
            { // we will ned to dynamically invoke the bitconverter so we don't have to tell it a type
                var convert = typeof(BitConverter).GetMethod("GetBytes", new Type[] { obj.GetType() });
                bytes.AddRange((byte[])convert.Invoke(null, new object[] { obj }));
            }
            else if (obj is string str)
            {
                bytes.AddRange(str.Length.Serialize());
                bytes.AddRange(Packet.STRING_ENCODING.GetBytes(str));

                var empty = MAX_STR - (bytes.Count - 4); // -4 is to account for integer at the beginning

                // Check if string is too big to be serialized

                if (empty < 0)
                    throw new Exception("STRING IS TOO BIG - MAX SIZE IS " + MAX_STR);

                for (int i = 0; i < empty; i++)
                    bytes.Add(0);
            }
            else if (obj.GetType().IsSubclassOf(typeof(Array)))
            {
                if (obj.GetType().GetArrayRank() > 1)
                    throw new Exception("Array serialization may not exceed a single dimension.");

                var a = obj as Array;
                bytes.AddRange(a.Length.Serialize());
                foreach (var e in a)
                    bytes.AddRange(e.Serialize());

                // file empty bytes
                var maxlength = 4 + (obj.GetType().GetElementType().Size() * MAX_IND);
                if (bytes.Count < maxlength)
                    for (int i = 0; i < maxlength - bytes.Count; i++)
                        bytes.Add(0);
            }
            else if (obj.GetType().IsGenericType && obj.GetType().GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                // how do we handle null values?

                var type = obj.GetType();

                var count = (int)type.GetProperty("Count").GetValue(obj);

                bytes.AddRange(count.Serialize());

                // how to populate?
                var keys = (System.Collections.IEnumerable)type.GetProperty("Keys").GetValue(obj);
                var values = (System.Collections.IEnumerable)type.GetProperty("Values").GetValue(obj);

                foreach (var key in keys)
                    bytes.AddRange(key.Serialize());
                foreach (var val in values)
                    if (val == null)
                        throw new Exception("Null values are currently unsupported in serialization");
                    else
                        bytes.AddRange(val.Serialize());
            }
            else
            {
                // grab all properties we can set
                //  By nature, this means we can only use properties where the Set value = Get Value
                var properties = obj.GetType().GetProperties().Where(p => p.SetMethod != null);
                var fields = obj.GetType().GetProperties();

                // let's serialize fields first
                foreach (var field in fields)
                    bytes.AddRange(field.GetValue(obj).Serialize(field));
                foreach (var prop in properties)
                    bytes.AddRange(prop.GetValue(obj).Serialize(prop));
            }
            return bytes.ToArray();
        }
        public static object Deserialize(this byte[] bytes, Type type, MemberInfo source = null)
        {
            // this doesn't care about size really, so we'll just keep source here for now incase we need it later
            // since we only currently have the MaxLength attribute


            if (bitConversions.Contains(type))
            {
                if (type == typeof(int))
                    return BitConverter.ToInt32(bytes, 0);
                else if (type == typeof(uint))
                    return BitConverter.ToUInt32(bytes, 0);
                else if (type == typeof(byte))
                    return bytes[0];
                else if (type == typeof(char))
                    return BitConverter.ToChar(bytes, 0);
                else if (type == typeof(short))
                    return BitConverter.ToInt16(bytes, 0);
                else if (type == typeof(ushort))
                    return BitConverter.ToUInt16(bytes, 0);
                else if (type == typeof(double))
                    return BitConverter.ToDouble(bytes, 0);
                else if (type == typeof(float))
                    return BitConverter.ToSingle(bytes, 0);
                else if (type == typeof(long))
                    return BitConverter.ToInt64(bytes, 0);
                else if (type == typeof(ulong))
                    return BitConverter.ToUInt64(bytes, 0);
                else if (type == typeof(bool))
                    return BitConverter.ToBoolean(bytes, 0);
            }
            else if (type == typeof(string))
            {
                var length = bytes.Take(4).Deserialize<int>();
                return Packet.STRING_ENCODING.GetString(bytes.Skip(4).Take(length).ToArray());
            }
            else if (type.IsSubclassOf(typeof(Array)))
            {
                if (type.GetArrayRank() > 1)
                    throw new Exception("Array serialization may not exceed a single dimension.");

                var length = (int)bytes.Take(4).Deserialize<int>();
                var result = (Array)type.GetConstructor(new Type[] { typeof(int) }).Invoke(new object[] { length });

                var eType = type.GetElementType();
                var eSize = eType.Size();

                for (int i = 0; i < length; i++)
                {
                    result.SetValue(bytes.Skip(4 + (i * eSize)).Take(eSize).Deserialize(eType), i);
                }

                return result;
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var count = bytes.Take(4).Deserialize<int>();
                var types = type.GetGenericArguments();
                var result = type.GetConstructor(new Type[] { }).Invoke(null);
                var addMethod = type.GetMethod("Add", types);

                var keySize = types[0].Size();
                var valSize = types[1].Size();

                for (int i = 0; i < count; i++)
                { // we shouldn't need to iterate more than once
                    addMethod.Invoke(result, new object[] {
                        bytes.Skip(4 + (i * keySize)).Take(keySize).Deserialize(types[0]), // key
                        bytes.Skip(4 + (count * keySize) + (i * valSize)).Take(valSize).Deserialize(types[1])
                    });
                }

                return result;
            }
            else // this should handle all custom data types (Class, Race, Character, Etc..)
            {
                var constructor = type.GetConstructor(new Type[] { }); // CLASS MUST HAVE EMPTY CONSTRUCTOR
                if (constructor == null)
                    throw new Exception("Attempt to deserialize type that doesn't have parameterless constructor.");

                var result = type.GetConstructor(new Type[] { }).Invoke(null);
                var pos = 0;

                foreach (var field in type.GetProperties())
                {
                    var size = field.PropertyType.Size();
                    field.SetValue(result, bytes.Skip(pos).Take(size).Deserialize(field.PropertyType));
                    pos += size;
                }

                return result;
            }

            return null;
        }
        public static object Deserialize(this IEnumerable<byte> bytes, Type type, MemberInfo source = null)
        {
            return bytes.ToArray().Deserialize(type, source);
        }
        public static T Deserialize<T>(this IEnumerable<byte> bytes, MemberInfo source = null)
        {
            return (T)bytes.ToArray().Deserialize<T>(source);
        }
        public static T Deserialize<T>(this byte[] bytes, MemberInfo source = null)
        {
            return (T)bytes.Deserialize(typeof(T), source);
        }
    }
}
