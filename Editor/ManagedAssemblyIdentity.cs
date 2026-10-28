using System;
using System.IO;
using System.Text;

namespace AIBridge.Runtime.Editor
{
    /// <summary>直接读取 Player PE 元数据的 Module MVID，避免把目标程序集加载进 Editor。</summary>
    internal static class ManagedAssemblyIdentity
    {
        public static bool TryReadMvid(string path, out Guid mvid)
        {
            mvid = Guid.Empty;
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    mvid = ReadMvid(reader);
                    return mvid != Guid.Empty;
                }
            }
            catch (BadImageFormatException) { return false; }
            catch (EndOfStreamException) { return false; }
            catch (ArgumentOutOfRangeException) { return false; }
            catch (OverflowException) { return false; }
        }

        private static Guid ReadMvid(BinaryReader reader)
        {
            var stream = reader.BaseStream;
            if (reader.ReadUInt16() != 0x5a4d)
                throw new BadImageFormatException("Missing PE header.");
            stream.Position = 0x3c;
            var pe = reader.ReadUInt32();
            stream.Position = pe;
            if (reader.ReadUInt32() != 0x00004550)
                throw new BadImageFormatException("Invalid PE signature.");
            reader.ReadUInt16();
            var sections = reader.ReadUInt16();
            stream.Position += 12;
            var optionalSize = reader.ReadUInt16();
            reader.ReadUInt16();
            var optional = stream.Position;
            var magic = reader.ReadUInt16();
            if (magic != 0x10b && magic != 0x20b)
                throw new BadImageFormatException("Unknown optional PE header.");
            var directories = optional + (magic == 0x20b ? 112 : 96);
            if (optionalSize < directories - optional + 15 * 8)
                throw new BadImageFormatException("Missing CLI data directory.");
            stream.Position = directories + 14 * 8;
            var cliRva = reader.ReadUInt32();
            var sectionTable = optional + optionalSize;
            stream.Position = RvaToOffset(reader, cliRva, sectionTable, sections) + 8;
            var metadataRva = reader.ReadUInt32();
            var metadata = RvaToOffset(reader, metadataRva, sectionTable, sections);
            return ReadMetadataMvid(reader, metadata);
        }

        private static long RvaToOffset(BinaryReader reader, uint rva, long table, int count)
        {
            for (var index = 0; index < count; index++)
            {
                reader.BaseStream.Position = table + index * 40 + 8;
                var virtualSize = reader.ReadUInt32();
                var address = reader.ReadUInt32();
                var rawSize = reader.ReadUInt32();
                var rawOffset = reader.ReadUInt32();
                if (rva >= address && (long)rva - address < Math.Max((long)virtualSize, rawSize))
                    return checked((long)rawOffset + rva - address);
            }
            throw new BadImageFormatException("PE RVA is outside image sections.");
        }

        private static Guid ReadMetadataMvid(BinaryReader reader, long metadata)
        {
            var stream = reader.BaseStream;
            stream.Position = metadata;
            if (reader.ReadUInt32() != 0x424a5342)
                throw new BadImageFormatException("Missing CLR metadata.");
            stream.Position = metadata + 12;
            var versionLength = reader.ReadUInt32();
            stream.Position = Align4(metadata + 16 + versionLength);
            reader.ReadUInt16();
            var streamCount = reader.ReadUInt16();
            long tables = 0;
            long guidHeap = 0;
            uint guidHeapSize = 0;
            for (var index = 0; index < streamCount; index++)
            {
                var offset = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                var name = ReadStreamName(reader);
                if (name == "#~" || name == "#-") tables = metadata + offset;
                if (name == "#GUID") { guidHeap = metadata + offset; guidHeapSize = size; }
            }
            if (tables == 0 || guidHeap == 0)
                throw new BadImageFormatException("Module metadata streams are missing.");
            return ReadModuleGuid(reader, tables, guidHeap, guidHeapSize);
        }

        private static string ReadStreamName(BinaryReader reader)
        {
            var bytes = new byte[32];
            var length = 0;
            while (length < bytes.Length)
            {
                var value = reader.ReadByte();
                if (value == 0)
                {
                    reader.BaseStream.Position = Align4(reader.BaseStream.Position);
                    return Encoding.ASCII.GetString(bytes, 0, length);
                }
                bytes[length++] = value;
            }
            throw new BadImageFormatException("Invalid metadata stream name.");
        }

        private static Guid ReadModuleGuid(BinaryReader reader, long tables, long guidHeap, uint guidHeapSize)
        {
            reader.BaseStream.Position = tables + 6;
            var heapSizes = reader.ReadByte();
            reader.ReadByte();
            var valid = reader.ReadUInt64();
            reader.ReadUInt64();
            uint moduleRows = 0;
            for (var index = 0; index < 64; index++)
            {
                if ((valid & (1UL << index)) == 0) continue;
                var rows = reader.ReadUInt32();
                if (index == 0) moduleRows = rows;
            }
            if (moduleRows == 0)
                throw new BadImageFormatException("Module table is missing.");
            reader.BaseStream.Position += 2 + ((heapSizes & 1) == 0 ? 2 : 4);
            var guidIndex = (heapSizes & 2) == 0 ? reader.ReadUInt16() : reader.ReadUInt32();
            var offset = checked(((long)guidIndex - 1) * 16);
            if (guidIndex == 0 || offset + 16 > guidHeapSize)
                throw new BadImageFormatException("Module MVID is outside the GUID heap.");
            reader.BaseStream.Position = guidHeap + offset;
            var bytes = reader.ReadBytes(16);
            if (bytes.Length != 16) throw new EndOfStreamException();
            return new Guid(bytes);
        }

        private static long Align4(long value)
        {
            return checked((value + 3) & ~3L);
        }
    }
}
