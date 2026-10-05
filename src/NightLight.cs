using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace QuietKey
{
    // A small, strict CompactBinary v1 reader. Untouched fields are preserved.
    // Windows' Night Light storage is not a supported public API; unfamiliar data fails closed.
    internal sealed class BondField
    {
        public int Id;
        public int Type;
        public byte[] Value;
        public BondStruct Children;
    }
    internal sealed class BondStruct
    {
        public readonly List<BondField> Fields = new List<BondField>();
        public byte Stop;
        public BondStruct Derived;
        public BondField Find(int id, int type)
        {
            BondField field = Fields.SingleOrDefault(f => f.Id == id);
            if (field == null || field.Type != type) throw new InvalidDataException("Unrecognized Night Light data.");
            return field;
        }
        public void Set(int id, int type, byte[] value)
        {
            BondField old = Fields.SingleOrDefault(f => f.Id == id);
            if (old != null && old.Type != type) throw new InvalidDataException("Unexpected Night Light field type.");
            if (old != null) Fields.Remove(old);
            Fields.Add(new BondField { Id = id, Type = type, Value = value });
        }
        public byte[] Encode()
        {
            using (var output = new MemoryStream())
            {
                foreach (BondField field in Fields.OrderBy(f => f.Id))
                {
                    if (field.Id < 6) output.WriteByte((byte)((field.Id << 5) | field.Type));
                    else if (field.Id <= 255) { output.WriteByte((byte)(0xC0 | field.Type)); output.WriteByte((byte)field.Id); }
                    else { output.WriteByte((byte)(0xE0 | field.Type)); output.WriteByte((byte)field.Id); output.WriteByte((byte)(field.Id >> 8)); }
                    byte[] value = field.Children == null ? field.Value : field.Children.Encode();
                    output.Write(value, 0, value.Length);
                }
                output.WriteByte(Stop);
                if (Derived != null) { byte[] derived = Derived.Encode(); output.Write(derived, 0, derived.Length); }
                return output.ToArray();
            }
        }
    }
    internal sealed class BondReader
    {
        private readonly byte[] data;
        private int position;
        public BondReader(byte[] source) { data = source; }
        private byte Read()
        {
            if (position >= data.Length) throw new InvalidDataException("Truncated Night Light data.");
            return data[position++];
        }
        private void Skip(int count)
        {
            if (count < 0 || count > data.Length - position) throw new InvalidDataException("Invalid Night Light data length.");
            position += count;
        }
        public ulong Number()
        {
            ulong value = 0;
            for (int shift = 0; shift <= 63; shift += 7)
            {
                byte b = Read();
                if (shift == 63 && b > 1) throw new InvalidDataException("Invalid Night Light integer.");
                value |= ((ulong)(b & 127)) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new InvalidDataException("Invalid Night Light integer.");
        }
        public BondStruct Struct(int depth)
        {
            if (depth > 12) throw new InvalidDataException("Night Light data is too deeply nested.");
            var result = new BondStruct();
            while (true)
            {
                byte header = Read();
                int type = header & 31;
                if (type == 0 || type == 1) {
                    result.Stop = (byte)type;
                    if (type == 1) result.Derived = Struct(depth + 1);
                    return result;
                }
                int id = header >> 5;
                if (id == 6) id = Read();
                else if (id == 7) id = Read() | (Read() << 8);
                if (result.Fields.Any(f => f.Id == id)) throw new InvalidDataException("Duplicate Night Light field.");
                int start = position;
                BondStruct children = null;
                switch (type)
                {
                    case 2: case 3: case 14: Skip(1); break;
                    case 4: case 5: case 6: case 15: case 16: case 17: Number(); break;
                    case 7: Skip(4); break;
                    case 8: Skip(8); break;
                    case 9: Skip(checked((int)Number())); break;
                    case 18: Skip(checked((int)Number() * 2)); break;
                    case 10: children = Struct(depth + 1); break;
                    case 11:
                        if (Read() != 14) throw new InvalidDataException("Unknown Night Light collection format.");
                        Skip(checked((int)Number())); break;
                    default: throw new InvalidDataException("Unknown Night Light field format.");
                }
                result.Fields.Add(new BondField { Id = id, Type = type, Children = children, Value = data.Skip(start).Take(position - start).ToArray() });
            }
        }
        public static BondStruct Decode(byte[] source)
        {
            if (source == null || source.Length < 5 || source.Length > 65536 || source[0] != 0x43 || source[1] != 0x42 || source[2] != 1 || source[3] != 0)
                throw new InvalidDataException("Windows Night Light uses an unsupported format.");
            var reader = new BondReader(source);
            reader.position = 4;
            BondStruct result = reader.Struct(0);
            if (reader.position != source.Length) throw new InvalidDataException("Unexpected trailing Night Light data.");
            return result;
        }
        public static byte[] Varint(ulong value)
        {
            var bytes = new List<byte>();
            do { byte b = (byte)(value & 127); value >>= 7; bytes.Add(value == 0 ? b : (byte)(b | 128)); } while (value != 0);
            return bytes.ToArray();
        }
        public static byte[] Marshal(BondStruct value) { return new byte[] { 0x43, 0x42, 1, 0 }.Concat(value.Encode()).ToArray(); }
        public static byte[] ByteList(byte[] value) { return new byte[] { 14 }.Concat(Varint((ulong)value.Length)).Concat(value).ToArray(); }
        public static byte[] GetByteList(byte[] value)
        {
            var reader = new BondReader(value);
            if (reader.Read() != 14) throw new InvalidDataException("Unknown Night Light payload format.");
            int length = checked((int)reader.Number());
            if (length != value.Length - reader.position) throw new InvalidDataException("Incorrect Night Light payload length.");
            return value.Skip(reader.position).ToArray();
        }
    }
    public static class NightLightCodec
    {
        private static BondStruct Payload(BondStruct outer)
        {
            return BondReader.Decode(BondReader.GetByteList(outer.Find(1, 10).Children.Find(1, 10).Children.Find(1, 11).Value));
        }
        public static bool IsOn(byte[] source)
        {
            BondStruct inner = Payload(BondReader.Decode(source));
            BondField usable = inner.Fields.SingleOrDefault(f => f.Id == 30);
            if (usable != null && (usable.Type != 2 || usable.Value.Length != 1 || usable.Value[0] == 0))
                throw new NotSupportedException("Windows reports that Night Light is unavailable for this display.");
            BondField status = inner.Fields.SingleOrDefault(f => f.Id == 0);
            if (status == null) return false;
            if (status.Type != 16 || new BondReader(status.Value).Number() != 0) throw new InvalidDataException("Unknown Night Light state.");
            return true;
        }
        public static byte[] WithState(byte[] source, bool enabled, DateTime utcNow)
        {
            IsOn(source); // Fully validate before making a change.
            BondStruct outer = BondReader.Decode(source);
            BondStruct inner = Payload(outer);
            inner.Fields.RemoveAll(f => f.Id == 0);
            if (enabled) inner.Set(0, 16, BondReader.Varint(0));
            inner.Set(10, 16, BondReader.Varint(2)); // ZigZag 1: manual transition.
            inner.Set(20, 6, BondReader.Varint(checked((ulong)utcNow.ToFileTimeUtc())));
            BondStruct container = outer.Find(1, 10).Children;
            ulong previous = new BondReader(container.Find(0, 6).Value).Number();
            ulong now = checked((ulong)(utcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            container.Set(0, 6, BondReader.Varint(Math.Max(now, checked(previous + 2))));
            container.Find(1, 10).Children.Set(1, 11, BondReader.ByteList(BondReader.Marshal(inner)));
            return BondReader.Marshal(outer);
        }
    }
    public sealed class NightLightService
    {
        private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current";
        private const string SharedName = "windows.data.bluelightreduction.bluelightreductionstate";
        private static List<string> Targets()
        {
            var paths = new List<string>();
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(Root))
            {
                if (root == null) throw new NotSupportedException("Open Windows Night Light settings once to initialize Night Light.");
                string shared = "default$" + SharedName;
                using (RegistryKey key = root.OpenSubKey(shared + "\\" + SharedName)) if (key != null) paths.Add(Root + "\\" + shared + "\\" + SharedName);
                string[] devices = root.GetSubKeyNames().Where(name => name.EndsWith("$" + SharedName + "perdevice", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (devices.Length > 1) throw new NotSupportedException("Several device-specific Night Light states were found. Use Windows Night Light settings to select this device.");
                foreach (string device in devices) {
                    string path = device + "\\" + SharedName + "perdevice";
                    using (RegistryKey key = root.OpenSubKey(path)) if (key != null) paths.Add(Root + "\\" + path);
                }
            }
            if (paths.Count == 0) throw new NotSupportedException("Open Windows Night Light settings once to initialize Night Light.");
            return paths;
        }
        public Dictionary<string, bool> ReadStates()
        {
            var states = new Dictionary<string, bool>();
            foreach (string path in Targets()) using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path)) states[path] = NightLightCodec.IsOn(key.GetValue("Data") as byte[]);
            return states;
        }
        public bool Read()
        {
            var states = ReadStates();
            var device = states.FirstOrDefault(pair => pair.Key.EndsWith("perdevice", StringComparison.OrdinalIgnoreCase));
            return device.Key == null ? states.First().Value : device.Value;
        }
        public void Set(bool enabled, string backupDirectory)
        {
            var states = ReadStates(); // Validate every target before any write.
            foreach (string path in states.Keys) SetState(path, enabled, backupDirectory);
            if (ReadStates().Any(pair => pair.Value != enabled)) throw new IOException("Windows did not retain the Night Light change. Open Windows Night Light settings.");
        }
        public void SetState(string path, bool enabled, string backupDirectory)
        {
            if (!Targets().Contains(path)) throw new InvalidDataException("The saved Night Light device is no longer available.");
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, true))
            {
                if (key == null) throw new NotSupportedException("Night Light data is unavailable. Open its Windows settings first.");
                byte[] original = key.GetValue("Data") as byte[];
                byte[] updated = NightLightCodec.WithState(original, enabled, DateTime.UtcNow);
                Directory.CreateDirectory(backupDirectory);
                string file = path.EndsWith("perdevice", StringComparison.OrdinalIgnoreCase) ? "night-light-device-before-change.bin" : "night-light-before-change.bin";
                File.WriteAllBytes(Path.Combine(backupDirectory, file), original);
                key.SetValue("Data", updated, RegistryValueKind.Binary);
                if (NightLightCodec.IsOn(key.GetValue("Data") as byte[]) != enabled) throw new IOException("Windows did not accept the Night Light change.");
            }
        }
    }
}
