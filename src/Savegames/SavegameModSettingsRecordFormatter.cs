using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MessagePack;
using MessagePack.Formatters;

namespace APIShared
{
    /// <summary>Explicit wire format; avoids MessagePack's unavailable dynamic formatter dependency.</summary>
    public sealed class SavegameModSettingsRecordFormatter : IMessagePackFormatter<SavegameModSettingsRecord>
    {
        /// <summary>Writes the bounded record without dynamic object formatters.</summary>
        public void Serialize(ref MessagePackWriter writer, SavegameModSettingsRecord value,
            MessagePackSerializerOptions options)
        {
            if (value == null) { writer.WriteNil(); return; }
            writer.WriteArrayHeader(7);
            writer.Write(value.Version);
            writer.Write(value.Kind);
            writer.Write(value.Variant);
            WriteValues(ref writer, value.Mods);
            writer.Write(value.LockedByConflict);
            WriteRules(ref writer, value.CreatorRules);
            writer.Write(value.TrailCustomizeAllowed);
        }

        /// <summary>Reads the versioned record with explicit bounds before allocating entries.</summary>
        public SavegameModSettingsRecord Deserialize(ref MessagePackReader reader,
            MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil()) return null;
            if (reader.ReadArrayHeader() != 7) return null;
            var value = new SavegameModSettingsRecord
            {
                Version = reader.ReadInt32(), Kind = reader.ReadInt32(),
                Variant = reader.ReadInt32(), Mods = ReadValues(ref reader),
                LockedByConflict = reader.ReadBoolean(), CreatorRules = ReadRules(ref reader),
                TrailCustomizeAllowed = reader.ReadBoolean(),
            };
            return value;
        }

        private static void WriteValues(ref MessagePackWriter writer,
            Dictionary<string, Dictionary<string, byte[]>> values)
        {
            writer.WriteMapHeader(values?.Count ?? 0);
            if (values == null) return;
            foreach (var mod in values.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.Write(mod.Key);
                writer.WriteMapHeader(mod.Value?.Count ?? 0);
                if (mod.Value == null) continue;
                foreach (var property in mod.Value.OrderBy(item => item.Key, StringComparer.Ordinal))
                { writer.Write(property.Key); writer.Write(property.Value); }
            }
        }

        private static Dictionary<string, Dictionary<string, byte[]>> ReadValues(ref MessagePackReader reader)
        {
            int count = reader.ReadMapHeader();
            if (count > 1024) throw new InvalidDataException("Too many savegame mods.");
            var values = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string mod = reader.ReadString();
                int properties = reader.ReadMapHeader();
                if (properties > 2048) throw new InvalidDataException("Too many savegame properties.");
                var stored = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                for (int j = 0; j < properties; j++)
                {
                    string name = reader.ReadString();
                    byte[] data = reader.ReadBytes()?.ToArray();
                    if (name == null || data == null || data.Length > 1024 * 1024 || stored.ContainsKey(name))
                        throw new InvalidDataException("Invalid savegame property.");
                    stored.Add(name, data);
                }
                if (mod == null || values.ContainsKey(mod)) throw new InvalidDataException("Duplicate savegame mod.");
                values.Add(mod, stored);
            }
            return values;
        }

        private static void WriteRules(ref MessagePackWriter writer,
            Dictionary<string, Dictionary<string, TrailCreatorRule>> rules)
        {
            writer.WriteMapHeader(rules?.Count ?? 0);
            if (rules == null) return;
            foreach (var mod in rules.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.Write(mod.Key);
                writer.WriteMapHeader(mod.Value?.Count ?? 0);
                if (mod.Value == null) continue;
                foreach (var property in mod.Value.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    writer.Write(property.Key);
                    writer.WriteArrayHeader(2);
                    writer.Write(property.Value.Mode);
                    writer.Write(property.Value.FixedValue);
                }
            }
        }

        private static Dictionary<string, Dictionary<string, TrailCreatorRule>> ReadRules(ref MessagePackReader reader)
        {
            int count = reader.ReadMapHeader();
            if (count > 1024) throw new InvalidDataException("Too many Trail rule mods.");
            var rules = new Dictionary<string, Dictionary<string, TrailCreatorRule>>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string mod = reader.ReadString();
                int properties = reader.ReadMapHeader();
                if (properties > 2048) throw new InvalidDataException("Too many Trail rules.");
                var stored = new Dictionary<string, TrailCreatorRule>(StringComparer.Ordinal);
                for (int j = 0; j < properties; j++)
                {
                    string name = reader.ReadString();
                    if (reader.ReadArrayHeader() != 2) throw new InvalidDataException("Invalid Trail rule.");
                    var rule = new TrailCreatorRule { Mode = reader.ReadInt32(), FixedValue = reader.ReadBytes()?.ToArray() };
                    if (name == null || stored.ContainsKey(name)) throw new InvalidDataException("Duplicate Trail rule.");
                    stored.Add(name, rule);
                }
                if (mod == null || rules.ContainsKey(mod)) throw new InvalidDataException("Duplicate Trail rule mod.");
                rules.Add(mod, stored);
            }
            return rules;
        }
    }

}
