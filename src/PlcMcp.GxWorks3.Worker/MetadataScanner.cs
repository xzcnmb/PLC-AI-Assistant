using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml;

namespace PlcMcp.GxWorks3.Worker
{
    public sealed class MetadataScanner
    {
        private static readonly string[] MonitoredVendorDlls = new string[]
        {
            @"Service\Managed\Melco.GXW3.Controller.Project.Managed.DataOperationController.dll",
            @"Service\Managed\Melco.GXW3.Controller.ExternalData.Managed.PLCFormat.dll",
            @"Service\Managed\Melco.GXW3.Controller.Checker.Managed.dll",
            @"Service\Managed\Melco.GXW3.Controller.ReferenceData.Managed.Label.dll",
            @"Service\Managed\Melco.GXW3.Controller.Monitor.Managed.dll",
            @"Melco.GXW3.ServiceBus.dll"
        };

        private readonly CommandLineOptions _options;
        private readonly string _installDir;

        public MetadataScanner(CommandLineOptions options)
        {
            _options = options;
            _installDir = Path.GetDirectoryName(options.GxExe) ?? string.Empty;
        }

        public Dictionary<string, object> PerformDoctorDiagnosis()
        {
            var checks = new List<Dictionary<string, object>>();
            bool overallHealthy = true;
            bool installed = false;
            string? toolchainVersion = null;

            // 1. GXW3.exe inspection
            var gxw3Evidence = InspectExecutable(out bool gxw3Healthy, checks);
            if (gxw3Evidence.ContainsKey("fileVersion"))
            {
                toolchainVersion = gxw3Evidence["fileVersion"] as string;
            }
            if (gxw3Evidence.ContainsKey("exists") && (bool)gxw3Evidence["exists"])
            {
                installed = true;
            }
            if (!gxw3Healthy)
            {
                overallHealthy = false;
            }

            // 2. Service.config inspection
            var serviceConfigEvidence = InspectServiceConfig(out bool serviceConfigHealthy, checks);
            if (!serviceConfigHealthy)
            {
                overallHealthy = false;
            }

            // 3. Monitored vendor DLLs inspection
            var dllsEvidence = InspectVendorDlls(out bool dllsHealthy, checks);
            if (!dllsHealthy)
            {
                overallHealthy = false;
            }

            // 4. Engineering boundary check
            checks.Add(new Dictionary<string, object>
            {
                { "name", "EngineeringApiBoundary" },
                { "passed", true },
                { "message", "Vendor APIs, COM controllers, ServiceBus, and PLC connectivity remain strictly uninitialized (engineeringApiVerified=false)." }
            });

            // Evidence object
            var evidence = new Dictionary<string, object>
            {
                { "gxw3", gxw3Evidence },
                { "serviceConfig", serviceConfigEvidence },
                { "dlls", dllsEvidence },
                { "engineeringApiVerified", false },
                { "limitation", "ServiceBus, COM, and native/managed engineering controller APIs are not initialized. Worker operates in safe, metadata-only diagnostic mode without code execution or PLC connections." }
            };

            string details = overallHealthy
                ? "GX Works3 core installation and monitored vendor metadata assemblies verified successfully in 32-bit isolated diagnostic mode."
                : "GX Works3 installation diagnostics reported discrepancies or failed checks.";

            return new Dictionary<string, object>
            {
                { "healthy", overallHealthy },
                { "installed", installed },
                { "toolchainPath", _options.GxExe },
                { "toolchainVersion", toolchainVersion ?? string.Empty },
                { "bitness", "32-bit" },
                { "details", details },
                { "checks", checks },
                { "evidence", evidence }
            };
        }

        private Dictionary<string, object> InspectExecutable(out bool healthy, List<Dictionary<string, object>> checks)
        {
            var dict = new Dictionary<string, object>
            {
                { "path", _options.GxExe }
            };

            healthy = true;

            if (!File.Exists(_options.GxExe))
            {
                dict["exists"] = false;
                healthy = false;
                checks.Add(new Dictionary<string, object>
                {
                    { "name", "ExecutableExistence" },
                    { "passed", false },
                    { "message", "GXW3.exe does not exist at path: " + _options.GxExe }
                });
                return dict;
            }

            dict["exists"] = true;

            try
            {
                var fi = new FileInfo(_options.GxExe);
                dict["sizeBytes"] = fi.Length;

                string sha = ComputeFileSha256(_options.GxExe, 200 * 1024 * 1024);
                dict["sha256"] = sha;

                var ver = FileVersionInfo.GetVersionInfo(_options.GxExe);
                string fv = ver.FileVersion ?? string.Empty;
                string pv = ver.ProductVersion ?? string.Empty;
                dict["fileVersion"] = fv;
                dict["productVersion"] = pv;

                var pe = PeMetadataReader.Read(_options.GxExe);
                dict["peMachine"] = pe.MachineHex;
                dict["peBitness"] = pe.Bitness;

                // Version match check
                bool versionMatches = string.Equals(fv.Trim(), _options.ExpectedVersion.Trim(), StringComparison.OrdinalIgnoreCase);
                if (!versionMatches)
                {
                    healthy = false;
                    checks.Add(new Dictionary<string, object>
                    {
                        { "name", "FileVersionMatch" },
                        { "passed", false },
                        { "message", string.Format("FileVersion mismatch: expected '{0}', got '{1}'.", _options.ExpectedVersion, fv) }
                    });
                }
                else
                {
                    checks.Add(new Dictionary<string, object>
                    {
                        { "name", "FileVersionMatch" },
                        { "passed", true },
                        { "message", string.Format("FileVersion '{0}' matches expected '{1}'.", fv, _options.ExpectedVersion) }
                    });
                }

                // PE bitness check
                if (pe.Machine != 0x014c)
                {
                    healthy = false;
                    checks.Add(new Dictionary<string, object>
                    {
                        { "name", "PeHeaderMachine" },
                        { "passed", false },
                        { "message", string.Format("PE Machine expected 0x014c (x86), got {0} ({1}).", pe.MachineHex, pe.Bitness) }
                    });
                }
                else
                {
                    checks.Add(new Dictionary<string, object>
                    {
                        { "name", "PeHeaderMachine" },
                        { "passed", true },
                        { "message", "PE Machine is 0x014c (x86 32-bit architecture verified)." }
                    });
                }
            }
            catch (Exception ex)
            {
                healthy = false;
                dict["error"] = ex.Message;
                checks.Add(new Dictionary<string, object>
                {
                    { "name", "ExecutableInspection" },
                    { "passed", false },
                    { "message", "Executable inspection error: " + ex.Message }
                });
            }

            return dict;
        }

        private Dictionary<string, object> InspectServiceConfig(out bool healthy, List<Dictionary<string, object>> checks)
        {
            var dict = new Dictionary<string, object>();
            healthy = true;

            string configPath = Path.Combine(_installDir, "Service", "Service.config");
            dict["path"] = configPath;

            // Security sanity on path
            if (!IsPathSafeWithinDirectory(configPath, _installDir))
            {
                healthy = false;
                dict["error"] = "Path traverses outside allowed installation directory.";
                checks.Add(new Dictionary<string, object>
                {
                    { "name", "ServiceConfigPathSafety" },
                    { "passed", false },
                    { "message", "Service.config path escapes installation directory." }
                });
                return dict;
            }

            if (!File.Exists(configPath))
            {
                healthy = false;
                dict["exists"] = false;
                checks.Add(new Dictionary<string, object>
                {
                    { "name", "ServiceConfigExistence" },
                    { "passed", false },
                    { "message", "Service.config not found at: " + configPath }
                });
                return dict;
            }

            dict["exists"] = true;

            try
            {
                var fi = new FileInfo(configPath);
                dict["sizeBytes"] = fi.Length;

                if (fi.Length > 5 * 1024 * 1024)
                {
                    healthy = false;
                    dict["error"] = "Service.config file exceeds 5MB limit.";
                    checks.Add(new Dictionary<string, object>
                    {
                        { "name", "ServiceConfigSize" },
                        { "passed", false },
                        { "message", "Service.config size (" + fi.Length + " bytes) exceeds 5MB safety limit." }
                    });
                    return dict;
                }

                dict["sha256"] = ComputeFileSha256(configPath, 5 * 1024 * 1024);

                // DTD-prohibited safe XML reader
                var xmlSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 5 * 1024 * 1024
                };

                dict["dtdProhibited"] = true;

                int totalCount = 0;
                int managedCount = 0;
                int nativeCount = 0;
                var managedKeys = new List<string>();
                var nativeKeys = new List<string>();

                using (var fs = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = XmlReader.Create(fs, xmlSettings))
                {
                    var doc = new XmlDocument();
                    doc.XmlResolver = null;
                    doc.Load(reader);

                    var nodes = doc.SelectNodes("//Service");
                    if (nodes != null)
                    {
                        totalCount = nodes.Count;
                        foreach (XmlNode node in nodes)
                        {
                            if (node.Attributes == null) continue;
                            var keyAttr = node.Attributes["Key"];
                            var platAttr = node.Attributes["Platform"];
                            string key = keyAttr != null ? keyAttr.Value : string.Empty;
                            string plat = platAttr != null ? platAttr.Value : string.Empty;

                            if (string.Equals(plat, "Managed", StringComparison.OrdinalIgnoreCase))
                            {
                                managedCount++;
                                if (!string.IsNullOrEmpty(key)) managedKeys.Add(key);
                            }
                            else if (string.Equals(plat, "Native", StringComparison.OrdinalIgnoreCase))
                            {
                                nativeCount++;
                                if (!string.IsNullOrEmpty(key)) nativeKeys.Add(key);
                            }
                        }
                    }
                }

                dict["totalServiceCount"] = totalCount;
                dict["managedServiceCount"] = managedCount;
                dict["nativeServiceCount"] = nativeCount;
                dict["managedServiceKeys"] = managedKeys;

                checks.Add(new Dictionary<string, object>
                {
                    { "name", "ServiceConfigParsing" },
                    { "passed", true },
                    { "message", string.Format("Safely parsed {0} services ({1} managed, {2} native) with DTD prohibited.", totalCount, managedCount, nativeCount) }
                });
            }
            catch (Exception ex)
            {
                healthy = false;
                dict["error"] = ex.Message;
                checks.Add(new Dictionary<string, object>
                {
                    { "name", "ServiceConfigParsing" },
                    { "passed", false },
                    { "message", "Failed to parse Service.config: " + ex.Message }
                });
            }

            return dict;
        }

        private List<Dictionary<string, object>> InspectVendorDlls(out bool healthy, List<Dictionary<string, object>> checks)
        {
            var list = new List<Dictionary<string, object>>();
            healthy = true;
            int foundCount = 0;

            foreach (var relPath in MonitoredVendorDlls)
            {
                string fullPath = Path.Combine(_installDir, relPath);
                var dllDict = new Dictionary<string, object>
                {
                    { "relativePath", relPath },
                    { "name", Path.GetFileName(relPath) }
                };

                if (!IsPathSafeWithinDirectory(fullPath, _installDir))
                {
                    dllDict["exists"] = false;
                    dllDict["metadataReadStatus"] = "SecurityViolation: Path escapes install directory";
                    list.Add(dllDict);
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    dllDict["exists"] = false;
                    dllDict["metadataReadStatus"] = "FileNotFound";
                    list.Add(dllDict);
                    continue;
                }

                dllDict["exists"] = true;
                foundCount++;

                try
                {
                    var fi = new FileInfo(fullPath);
                    dllDict["sizeBytes"] = fi.Length;
                    dllDict["sha256"] = ComputeFileSha256(fullPath, 50 * 1024 * 1024);

                    var ver = FileVersionInfo.GetVersionInfo(fullPath);
                    dllDict["fileVersion"] = ver.FileVersion ?? string.Empty;
                    dllDict["productVersion"] = ver.ProductVersion ?? string.Empty;

                    var pe = PeMetadataReader.Read(fullPath);
                    dllDict["peMachine"] = pe.MachineHex;
                    dllDict["peBitness"] = pe.Bitness;

                    // Static ReflectionOnly metadata reading (no code execution)
                    ReadAssemblyReflectionOnlyMetadata(fullPath, dllDict);
                }
                catch (Exception ex)
                {
                    dllDict["metadataReadStatus"] = "InspectionError: " + ex.GetType().Name + ": " + ex.Message;
                }

                list.Add(dllDict);
            }

            checks.Add(new Dictionary<string, object>
            {
                { "name", "VendorDllsInspection" },
                { "passed", true },
                { "message", string.Format("Inspected {0}/{1} monitored vendor assemblies via static ReflectionOnly metadata.", foundCount, MonitoredVendorDlls.Length) }
            });

            return list;
        }

        private static void ReadAssemblyReflectionOnlyMetadata(string fullPath, Dictionary<string, object> dllDict)
        {
            try
            {
                var asm = Assembly.ReflectionOnlyLoadFrom(fullPath);
                dllDict["imageRuntimeVersion"] = asm.ImageRuntimeVersion;

                string targetFramework = "Unknown";
                foreach (var attr in CustomAttributeData.GetCustomAttributes(asm))
                {
                    if (attr.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute")
                    {
                        if (attr.ConstructorArguments.Count > 0)
                        {
                            targetFramework = Convert.ToString(attr.ConstructorArguments[0].Value);
                        }
                    }
                }

                dllDict["targetFramework"] = targetFramework;
                dllDict["metadataReadStatus"] = "Success";
            }
            catch (BadImageFormatException ex)
            {
                dllDict["metadataReadStatus"] = "BadImageFormatException (Native/C++ assembly or architecture mismatch): " + ex.Message;
            }
            catch (FileNotFoundException ex)
            {
                dllDict["metadataReadStatus"] = "FileNotFoundException during ReflectionOnly load: " + ex.Message;
            }
            catch (FileLoadException ex)
            {
                dllDict["metadataReadStatus"] = "FileLoadException during ReflectionOnly load: " + ex.Message;
            }
            catch (Exception ex)
            {
                dllDict["metadataReadStatus"] = "ReflectionOnlyFailed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static bool IsPathSafeWithinDirectory(string path, string parentDir)
        {
            try
            {
                string normPath = Path.GetFullPath(path);
                string normParent = Path.GetFullPath(parentDir);

                if (!normParent.EndsWith(Path.DirectorySeparatorChar.ToString()))
                {
                    normParent += Path.DirectorySeparatorChar;
                }

                return normPath.StartsWith(normParent, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeFileSha256(string filePath, long maxBytes)
        {
            var fi = new FileInfo(filePath);
            if (fi.Length > maxBytes)
            {
                throw new InvalidOperationException("File size exceeds hashing limit of " + maxBytes + " bytes.");
            }

            using (var sha = SHA256.Create())
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
