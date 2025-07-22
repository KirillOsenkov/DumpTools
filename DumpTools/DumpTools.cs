using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace DumpTools
{
    public class DumpAnalysis
    {
        private Dictionary<string, List<ulong>> stringUsages = new Dictionary<string, List<ulong>>();
        private Dictionary<ClrType, List<ulong>> instances = new Dictionary<ClrType, List<ulong>>();
        private ulong stringsTotalSize = 0;
        private ulong stringCount = 0;
        private ulong instancesTotalSize = 0;
        private ulong instanceCount = 0;
        private ClrHeap heap;
        private IEnumerable<ClrObject> objects;

        static void Main(string[] args)
        {
            if (args.Length == 0 || args.Length > 3)
            {
                PrintUsage();
                return;
            }

            string dumpFilePath = args[0];
            string mscordacwksFilePath = args.Length >= 2 ? args[1] : "";
            string symbolsPath = args.Length >= 3 ? args[2] : "";

            if (!File.Exists(dumpFilePath))
            {
                Console.WriteLine("Dump file " + dumpFilePath + " doesn't exist");
                return;
            }

            if (!File.Exists(mscordacwksFilePath))
            {
                Console.WriteLine("mscordacwks.dll could not be found: " + mscordacwksFilePath);
                return;
            }

            new DumpAnalysis().Analyze(dumpFilePath, mscordacwksFilePath, symbolsPath);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: DumpTools.exe <path-to-dump.dmp> <path-to-mscordacwks.dll> <symbol-path>");
        }

        public struct Span
        {
            public ulong Start;
            public ulong Length;

            public Span(ulong start, int length) : this()
            {
                this.Start = start;
                this.Length = (ulong)length;
            }

            public Span(ulong start, ulong length) : this()
            {
                this.Start = start;
                this.Length = length;
            }
        }

        private void Analyze(string dumpFilePath, string mscordacwks, string symbolsPath)
        {
            using (var dataTarget = DataTarget.LoadDump(dumpFilePath))
            {
                //dataTarget.AppendSymbolPath(symbolsPath);
                var runtime = dataTarget.ClrVersions[0].CreateRuntime(mscordacwks);

                //DumpModules(dumpFilePath, dataTarget, runtime);

                heap = runtime.Heap;
                objects = heap.EnumerateObjects();
                ProcessHeap();
                //WriteReport();
            }
        }

        private static void DumpModules(string dumpFilePath, DataTarget dataTarget, ClrRuntime runtime)
        {
            string modulesFolder = Path.Combine(Path.GetDirectoryName(dumpFilePath), "Assemblies");
            Directory.CreateDirectory(modulesFolder);

            foreach (var appDomain in runtime.AppDomains)
            {
                foreach (var module in appDomain.Modules.Skip(1).Take(1))
                {
                    if (module.Name == null)
                    {
                        continue;
                    }

                    var bytes1 = ReadMemory(dataTarget, new Span(module.ImageBase, 960));
                    var bytes2 = ReadMemory(dataTarget, new Span(module.MetadataAddress, module.MetadataLength));
                    var bytes = bytes1.Concat(bytes2).ToArray();

                    string outputFilePath = Path.Combine(modulesFolder, Path.GetFileName(module.AssemblyName));
                    File.WriteAllBytes(outputFilePath, bytes);
                }
            }

            foreach (var nativeModule in dataTarget.EnumerateModules())
            {
                var bytes = ReadMemory(dataTarget, new Span(nativeModule.ImageBase, (int)nativeModule.ImageSize));
                string outputFilePath = Path.Combine(modulesFolder, Path.GetFileName(nativeModule.FileName));
                File.WriteAllBytes(outputFilePath, bytes);
            }
        }

        public static byte[] ReadMemory(DataTarget dataTarget, Span span)
        {
            byte[] bytes = new byte[span.Length];
            var byteSpan = new Span<byte>(bytes);
            if (dataTarget.DataReader.Read(span.Start, byteSpan) > 0)
            {
                return bytes;
            }

            return null;
        }

        private void WriteReport()
        {
            using (var stream = new StreamWriter("report.txt"))
            {
                stream.WriteLine("Total instances: " + this.instanceCount.ToString("N0"));
                stream.WriteLine("Total instance size: " + this.instancesTotalSize.ToString("N0"));
                stream.WriteLine("Total strings: " + this.stringCount.ToString("N0"));
                stream.WriteLine("Total string size: " + this.stringsTotalSize.ToString("N0"));
                stream.WriteLine();

                const int stringCount = 10;
                var sorted = stringUsages.OrderByDescending(kvp => kvp.Value.Count * kvp.Key.Length).Take(stringCount);
                stream.WriteLine($"TOP {stringCount} STRINGS that take most space ===========================================");
                int i = 0;
                foreach (var kvp in sorted)
                {
                    var msg = "Count: " +
                        kvp.Value.Count +
                        "\r\n" +
                        string.Join(",", kvp.Value.Take(3).Select(ul => Convert.ToString((long)ul, 16))) +
                        "\r\n\r\n" +
                        kvp.Key.Substring(0, Math.Min(kvp.Key.Length, 500)) +
                        "\r\n\r\n";
                    stream.WriteLine(msg);
                    File.WriteAllText($"StringInstance{i++}.txt", kvp.Key);
                }

                const int instanceCount = 5;
                var topInstances = instances.OrderByDescending(kvp => kvp.Value.Count).Take(instanceCount);
                stream.WriteLine($"TOP {instanceCount} INSTANCES that take most space ===========================================");
                foreach (var kvp in topInstances)
                {
                    var msg = kvp.Key.Name + " (" + kvp.Value.Count + " instances): " + string.Join(",", kvp.Value.Take(3).Select(ul => Convert.ToString((long)ul, 16))) + "\r\n";
                    stream.WriteLine(msg);
                }
            }
        }

        private void ProcessHeap()
        {
            foreach (var instance in objects)
            {
                var type = heap.GetObjectType(instance);
                if (type != null)
                {
                    if (type.IsString)
                    {
                        ProcessString(instance, type);
                    }
                    else
                    {
                        ProcessInstance(instance, type);
                    }
                }
                else
                {
                }
            }
        }

        private void ProcessInstance(ClrObject instance, ClrType type)
        {
            if (type.IsString)
            {
                var text = instance.AsString();
                if (text.Equals(@"C:\temp\mstest3\bin\Debug\net8.0\TestRunner.Core.dll", StringComparison.OrdinalIgnoreCase))
                {
                }

                return;
            }

            if (type.Name == "Microsoft.Win32.SafeHandles.SafeFileHandle")
            {
            }

            foreach (var field in type.Fields)
            {
                if (field.IsObjectReference)
                {
                    continue;
                }

                if (field.ElementType == ClrElementType.NativeInt ||
                    field.ElementType == ClrElementType.NativeUInt ||
                    field.ElementType == ClrElementType.Int64 ||
                    field.ElementType == ClrElementType.UInt64 ||
                    field.ElementType == ClrElementType.Pointer)
                {
                    IntPtr pointer = field.Read<IntPtr>(instance.Address, false);
                    long value = pointer.ToInt64();
                    if (value == 7216 ||
                        value == 11340 ||
                        value == 15996)
                    {
                    }
                }
            }
        }

        private void ProcessInstanceOld(ulong instance, ClrType type)
        {
            instanceCount++;
            instancesTotalSize += (ulong)type.ComponentSize;

            List<ulong> bucket = null;
            if (!instances.TryGetValue(type, out bucket))
            {
                bucket = new List<ulong>(1);
                instances[type] = bucket;
            }

            bucket.Add(instance);
        }

        private void ProcessString(ulong instance, ClrType type)
        {
            //var value = (string)type.ToString(instance);

            //stringCount++;
            //stringsTotalSize += type.GetSize(instance);

            //List<ulong> usages = null;
            //if (!stringUsages.TryGetValue(value, out usages))
            //{
            //    usages = new List<ulong>(1);
            //    stringUsages[value] = usages;
            //}

            //usages.Add(instance);
        }
    }
}
