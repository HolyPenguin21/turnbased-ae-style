using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Cached Unity API references contain managed throw bodies marked InternalCall. Mono can
// inspect those; CoreCLR rejects the containing type before any method runs. In TEMPORARY
// runtime copies only, retain the placeholder body and remove its incompatible InternalCall bit.
// This provides no implementation of Unity native-engine behavior.
static class ReferenceAdapter
{
    static void Main(string[] files)
    {
        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            using (var pe = new PEReader(new MemoryStream(bytes)))
            {
                var reader = pe.GetMetadataReader();
                int table = pe.PEHeaders.MetadataStartOffset + reader.GetTableMetadataOffset(TableIndex.MethodDef);
                int size = reader.GetTableRowSize(TableIndex.MethodDef);
                for (int row = 0; row < reader.GetTableRowCount(TableIndex.MethodDef); row++)
                {
                    int offset = table + row * size;
                    int rva = BitConverter.ToInt32(bytes, offset);
                    ushort flags = BitConverter.ToUInt16(bytes, offset + 4);
                    ushort methodFlags=BitConverter.ToUInt16(bytes,offset+6);
                    if(rva!=0&&(methodFlags&(ushort)MethodAttributes.Abstract)!=0)
                    {
                        ushort managed=(ushort)(methodFlags&~(ushort)MethodAttributes.Abstract);
                        bytes[offset+6]=(byte)managed;bytes[offset+7]=(byte)(managed>>8);
                    }
                    if (rva != 0 && (flags & (ushort)MethodImplAttributes.InternalCall) != 0)
                    {
                        ushort managed = (ushort)(flags & ~(ushort)MethodImplAttributes.InternalCall);
                        bytes[offset + 4] = (byte)managed;
                        bytes[offset + 5] = (byte)(managed >> 8);
                    }
                }
            }
            File.WriteAllBytes(file, bytes);
        }
    }
}
