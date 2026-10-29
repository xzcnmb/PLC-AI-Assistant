using System;
using System.IO;

namespace PlcMcp.GxWorks3.Worker
{
    public sealed class PeMetadata
    {
        public bool IsValid { get; }
        public ushort Machine { get; }
        public string MachineHex { get; }
        public string Bitness { get; }
        public string? ErrorMessage { get; }

        public PeMetadata(bool isValid, ushort machine, string machineHex, string bitness, string? errorMessage)
        {
            IsValid = isValid;
            Machine = machine;
            MachineHex = machineHex;
            Bitness = bitness;
            ErrorMessage = errorMessage;
        }

        public static PeMetadata Read(string filePath, long maxSizeBytes = 100 * 1024 * 1024)
        {
            return PeMetadataReader.Read(filePath, maxSizeBytes);
        }
    }

    public static class PeMetadataReader
    {
        public static PeMetadata Read(string filePath, long maxSizeBytes = 100 * 1024 * 1024)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return new PeMetadata(false, 0, "0x0000", "Unknown", "File not found: " + filePath);
            }

            try
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length < 0x40)
                {
                    return new PeMetadata(false, 0, "0x0000", "Unknown", "File size too small for PE header (" + fileInfo.Length + " bytes).");
                }

                if (fileInfo.Length > maxSizeBytes)
                {
                    return new PeMetadata(false, 0, "0x0000", "Unknown", "File size exceeds maximum allowed threshold of " + maxSizeBytes + " bytes.");
                }

                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fs))
                {
                    // DOS Header check 'MZ'
                    ushort dosMagic = reader.ReadUInt16();
                    if (dosMagic != 0x5A4D) // 'MZ'
                    {
                        return new PeMetadata(false, 0, "0x0000", "Unknown", "Invalid DOS header magic: 0x" + dosMagic.ToString("X4"));
                    }

                    // Seek to e_lfanew at 0x3C
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    int peHeaderOffset = reader.ReadInt32();

                    if (peHeaderOffset <= 0 || peHeaderOffset > fileInfo.Length - 24)
                    {
                        return new PeMetadata(false, 0, "0x0000", "Unknown", "Invalid e_lfanew offset: " + peHeaderOffset);
                    }

                    // Seek to PE Signature
                    fs.Seek(peHeaderOffset, SeekOrigin.Begin);
                    uint peSignature = reader.ReadUInt32();
                    if (peSignature != 0x00004550) // "PE\0\0"
                    {
                        return new PeMetadata(false, 0, "0x0000", "Unknown", "Invalid PE signature: 0x" + peSignature.ToString("X8"));
                    }

                    // Read Machine from COFF header
                    ushort machine = reader.ReadUInt16();
                    string hex = "0x" + machine.ToString("x4");
                    string bitness;

                    switch (machine)
                    {
                        case 0x014c: // IMAGE_FILE_MACHINE_I386
                            bitness = "32-bit";
                            break;
                        case 0x8664: // IMAGE_FILE_MACHINE_AMD64
                            bitness = "64-bit";
                            break;
                        case 0x0200: // IMAGE_FILE_MACHINE_IA64
                            bitness = "64-bit (IA64)";
                            break;
                        case 0xaa64: // IMAGE_FILE_MACHINE_ARM64
                            bitness = "64-bit (ARM64)";
                            break;
                        default:
                            bitness = "Unknown (" + hex + ")";
                            break;
                    }

                    return new PeMetadata(true, machine, hex, bitness, null);
                }
            }
            catch (Exception ex)
            {
                return new PeMetadata(false, 0, "0x0000", "Unknown", "PE read failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
