using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using PlcMcp.Engineering.Workers.Omron;

namespace PlcMcp.Engineering.Tests;

public class OmronExchangeTests : IDisposable
{
    private readonly string _testRoot;
    private readonly OmronExchangeService _service;

    public OmronExchangeTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PlcMcp_OmronExchangeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _service = new OmronExchangeService(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void Inspect_ValidIec61131_10Xml_ParsesSuccessfully()
    {
        var xmlContent = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:smcext="https://www.ia.omron.com/Smc" schemaVersion="1" xmlns="www.iec.ch/public/TC65SC65BWG7TF10">
              <FileHeader companyName="OMRON Corporation" productName="Sysmac Studio" productVersion="1.60.0" />
              <ContentHeader name="TestStationNJ" creationDateTime="2026-09-24T10:00:00">
                <AddData>
                  <Data name="OmronDeviceInfo" handleUnknown="discard">
                    <smcext:DeviceInfo modelName="NJ501-1500" version="1.48" />
                  </Data>
                </AddData>
              </ContentHeader>
              <Types>
                <GlobalNamespace>
                  <DataTypeDecl name="TankRecord">
                    <UserDefinedTypeSpec xsi:type="StructTypeSpec">
                      <Member name="Level">
                        <Type><TypeName>REAL</TypeName></Type>
                      </Member>
                      <Member name="InletOpen">
                        <Type><TypeName>BOOL</TypeName></Type>
                      </Member>
                    </UserDefinedTypeSpec>
                  </DataTypeDecl>
                  <Program name="MainControl">
                    <Vars accessSpecifier="private">
                      <Variable name="TankA">
                        <Type><TypeName>TankRecord</TypeName></Type>
                      </Variable>
                      <Variable name="AutoMode">
                        <Type><TypeName>BOOL</TypeName></Type>
                      </Variable>
                    </Vars>
                    <MainBody>
                      <BodyContent xsi:type="smcext:LdSection" name="Section0">
                        <Rung evaluationOrder="1">
                          <CommonObject xsi:type="Comment">
                            <Content xsi:type="SimpleText">Main logic rung</Content>
                          </CommonObject>
                        </Rung>
                        <Rung evaluationOrder="2" />
                      </BodyContent>
                    </MainBody>
                  </Program>
                </GlobalNamespace>
              </Types>
              <Configuration name="Config0">
                <Resource name="Res0" resourceTypeName="">
                  <GlobalVars retain="true">
                    <Variable name="g_EmergencyStop">
                      <Type><TypeName>BOOL</TypeName></Type>
                      <Address address="%d0.0" />
                      <Documentation xsi:type="SimpleText">Safety stop switch</Documentation>
                    </Variable>
                  </GlobalVars>
                </Resource>
              </Configuration>
            </Project>
            """;

        var filePath = Path.Combine(_testRoot, "NJ_Sample.xml");
        File.WriteAllText(filePath, xmlContent, Encoding.UTF8);

        var report = _service.Inspect(filePath);

        Assert.NotNull(report);
        Assert.Equal("TestStationNJ", report.ProjectName);
        Assert.Equal("IEC-61131-10-XML", report.Format);
        Assert.Equal("NJ501-1500 (v1.48)", report.TargetDevice);
        Assert.Equal("structural", report.ValidationLevel);
        Assert.False(report.IsVendorCompiler);
        Assert.NotEmpty(report.SourceSha256);

        // Verify POU
        Assert.Single(report.Pous);
        var pou = report.Pous[0];
        Assert.Equal("MainControl", pou.Name);
        Assert.Equal("Program", pou.PouType);
        Assert.Equal("LD", pou.Language);
        Assert.Equal(2, pou.VariableCount);
        Assert.Equal("2 LD rung(s)", pou.BodySummary);
        Assert.NotEmpty(pou.RawContentHash);

        // Verify Data Types
        Assert.Single(report.DataTypes);
        var dt = report.DataTypes[0];
        Assert.Equal("TankRecord", dt.Name);
        Assert.Equal("Struct", dt.Kind);
        Assert.Equal(2, dt.MemberCount);
        Assert.Contains("Level: REAL", dt.Members);

        // Verify Global Symbols
        Assert.Single(report.Symbols);
        var sym = report.Symbols[0];
        Assert.Equal("g_EmergencyStop", sym.Name);
        Assert.Equal("BOOL", sym.DataType);
        Assert.Equal("%d0.0", sym.Address);
        Assert.True(sym.IsRetain);
        Assert.Equal("Safety stop switch", sym.Comment);

        // Limitations and Diagnostics
        Assert.NotEmpty(report.Limitations);
        Assert.Contains(report.Limitations, l => l.Contains("structural only", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Diagnostics, d => d.Code == "OMR_001");
    }

    [Fact]
    public void Inspect_ValidAutomationMl_ParsesTopology()
    {
        var amlContent = """
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile FileName="PlantCell.aml" SchemaVersion="2.15" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <AdditionalInformation AutomationMLVersion="2.0">
                <WriterHeader>
                  <WriterProjectTitle>CellProjectA</WriterProjectTitle>
                </WriterHeader>
              </AdditionalInformation>
              <InstanceHierarchy Name="OmronCell">
                <InternalElement Name="MainCabinet" ID="1111-2222-3333">
                  <InternalElement Name="CPU01" ID="4444-5555-6666">
                    <Attribute Name="DeviceItemType" AttributeDataType="xs:string">
                      <Value>CPU</Value>
                    </Attribute>
                    <Attribute Name="TypeIdentifier" AttributeDataType="xs:string">
                      <Value>OrderNumber:NX102-1200</Value>
                    </Attribute>
                    <Attribute Name="Manufacturer" AttributeDataType="xs:string">
                      <Value>OMRON</Value>
                    </Attribute>
                    <Attribute Name="FirmwareVersion" AttributeDataType="xs:string">
                      <Value>1.50</Value>
                    </Attribute>
                    <InternalElement Name="EtherCATMaster" ID="7777-8888-9999">
                      <Attribute Name="TypeIdentifier" AttributeDataType="xs:string">
                        <Value>Master</Value>
                      </Attribute>
                    </InternalElement>
                  </InternalElement>
                </InternalElement>
              </InstanceHierarchy>
            </CAEXFile>
            """;

        var filePath = Path.Combine(_testRoot, "PlantCell.aml");
        File.WriteAllText(filePath, amlContent, Encoding.UTF8);

        var report = _service.Inspect(filePath);

        Assert.NotNull(report);
        Assert.Equal("AutomationML-CAEX", report.Format);
        Assert.Equal("CellProjectA", report.ProjectName);
        Assert.Equal("NX102-1200 (FW 1.50)", report.TargetDevice);
        Assert.Equal(3, report.Objects.Count);

        var cpuObj = report.Objects.FirstOrDefault(o => o.Name == "CPU01");
        Assert.NotNull(cpuObj);
        Assert.Equal("OmronCell/MainCabinet/CPU01", cpuObj.Path);
        Assert.Equal("CPU", cpuObj.DeviceType);
        Assert.Equal("NX102-1200", cpuObj.Model);
        Assert.Equal("1.50", cpuObj.FirmwareVersion);
        Assert.Equal(1, cpuObj.ChildCount); // EtherCATMaster
    }

    [Fact]
    public void Inspect_ValidPlcopenXml_ParsesSuccessfully()
    {
        var xmlContent = """
            <?xml version="1.0" encoding="utf-8"?>
            <project xmlns="http://www.plcopen.org/xml/tc6_0201">
              <fileHeader companyName="OmronPartner" />
              <contentHeader name="SubStation" />
              <types>
                <globalVars>
                  <variable name="Heartbeat" address="%QX0.1">
                    <type><BOOL /></type>
                  </variable>
                </globalVars>
                <pous>
                  <pou name="Blinker" pouType="functionBlock">
                    <interface>
                      <inputVars>
                        <variable name="PeriodMs"><type><DINT /></type></variable>
                      </inputVars>
                    </interface>
                    <body>
                      <ST>
                        Heartbeat := NOT Heartbeat;
                      </ST>
                    </body>
                  </pou>
                </pous>
              </types>
            </project>
            """;

        var filePath = Path.Combine(_testRoot, "PlcopenSample.xml");
        File.WriteAllText(filePath, xmlContent, Encoding.UTF8);

        var report = _service.Inspect(filePath);

        Assert.NotNull(report);
        Assert.Equal("PLCopen-XML", report.Format);
        Assert.Equal("SubStation", report.ProjectName);
        Assert.Single(report.Symbols);
        Assert.Equal("Heartbeat", report.Symbols[0].Name);
        Assert.Single(report.Pous);
        Assert.Equal("Blinker", report.Pous[0].Name);
        Assert.Equal("ST", report.Pous[0].Language);
    }

    [Fact]
    public void Security_PathSandbox_RejectsPathOutsideAllowedRoot()
    {
        var outsideFile = Path.Combine(Path.GetTempPath(), "outside_" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(outsideFile, "<Project />");

        try
        {
            var ex = Assert.Throws<UnauthorizedAccessException>(() => _service.Inspect(outsideFile));
            Assert.Contains("outside allowed root", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(outsideFile)) File.Delete(outsideFile);
        }
    }

    [Fact]
    public void Security_PathSandbox_RejectsRelativePath()
    {
        Assert.Throws<ArgumentException>(() => _service.Inspect("relative/path/Sample.xml"));
    }

    [Fact]
    public void Security_PathSandbox_RejectsDirectoryTraversalEscape()
    {
        var escapedPath = Path.Combine(_testRoot, @"..\..\secret.xml");
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _service.Inspect(escapedPath));
        Assert.Contains("outside allowed root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Security_RejectsBroadAllowedRoot()
    {
        string rootPath = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.Throws<ArgumentException>(() => new OmronExchangeService(rootPath));
    }

    [Fact]
    public void Security_RejectsFileSizeExceeding4MiB()
    {
        var largeFile = Path.Combine(_testRoot, "TooLarge.xml");
        // Create 4.1 MiB file
        byte[] buffer = new byte[4 * 1024 * 1024 + 1024];
        File.WriteAllBytes(largeFile, buffer);

        var ex = Assert.Throws<InvalidOperationException>(() => _service.Inspect(largeFile));
        Assert.Contains("exceeds maximum allowed size of 4 MiB", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Security_DtdProcessingProhibited_ThrowsOnDtdDeclaration()
    {
        var xxePayload = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE Project [
              <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">
            ]>
            <Project xmlns="www.iec.ch/public/TC65SC65BWG7TF10">
              <ContentHeader name="&xxe;" />
            </Project>
            """;

        var dtdFile = Path.Combine(_testRoot, "XXE_Attack.xml");
        File.WriteAllText(dtdFile, xxePayload, Encoding.UTF8);

        Assert.Throws<XmlException>(() => _service.Inspect(dtdFile));
    }

    [Fact]
    public void Security_RejectsExceedingMaxXmlDepth()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        for (int i = 0; i < 70; i++)
        {
            sb.Append($"<Project depth=\"{i}\" xmlns=\"www.iec.ch/public/TC65SC65BWG7TF10\">\n");
        }
        for (int i = 0; i < 70; i++)
        {
            sb.Append("</Project>\n");
        }

        var deepFile = Path.Combine(_testRoot, "DeepNested.xml");
        File.WriteAllText(deepFile, sb.ToString(), Encoding.UTF8);

        var ex = Assert.Throws<InvalidOperationException>(() => _service.Inspect(deepFile));
        Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Security_RejectsUnknownRootElement()
    {
        var unknownXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <RandomPayload id="123">
              <Data>Malicious or unsupported content</Data>
            </RandomPayload>
            """;

        var file = Path.Combine(_testRoot, "Unknown.xml");
        File.WriteAllText(file, unknownXml, Encoding.UTF8);

        var ex = Assert.Throws<InvalidOperationException>(() => _service.Inspect(file));
        Assert.Contains("Unrecognized or unsupported root element", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compare_IdenticalFiles_ReturnsNoDifferences()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="www.iec.ch/public/TC65SC65BWG7TF10">
              <ContentHeader name="Station" />
              <Types>
                <Program name="Main">
                  <Vars accessSpecifier="private">
                    <Variable name="Var1"><Type><TypeName>BOOL</TypeName></Type></Variable>
                  </Vars>
                </Program>
              </Types>
            </Project>
            """;

        var file1 = Path.Combine(_testRoot, "StationA.xml");
        var file2 = Path.Combine(_testRoot, "StationB.xml");
        File.WriteAllText(file1, xml, Encoding.UTF8);
        File.WriteAllText(file2, xml, Encoding.UTF8);

        var diff = _service.Compare(file1, file2);

        Assert.False(diff.HasDifferences);
        Assert.Contains("Identical", diff.Summary);
        Assert.Empty(diff.AddedPous);
        Assert.Empty(diff.RemovedPous);
        Assert.Empty(diff.ModifiedPous);
    }

    [Fact]
    public void Compare_StructuralDifferences_DetectedAndSortedDeterministically()
    {
        var xml1 = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="www.iec.ch/public/TC65SC65BWG7TF10">
              <ContentHeader name="Station" />
              <Types>
                <Program name="Z_Program">
                  <Vars accessSpecifier="private">
                    <Variable name="v1"><Type><TypeName>INT</TypeName></Type></Variable>
                  </Vars>
                </Program>
                <Program name="A_Program" />
              </Types>
              <Configuration name="Cfg">
                <Resource name="R">
                  <GlobalVars>
                    <Variable name="Speed"><Type><TypeName>REAL</TypeName></Type></Variable>
                  </GlobalVars>
                </Resource>
              </Configuration>
            </Project>
            """;

        var xml2 = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="www.iec.ch/public/TC65SC65BWG7TF10">
              <ContentHeader name="Station" />
              <Types>
                <Program name="Z_Program">
                  <Vars accessSpecifier="private">
                    <Variable name="v1"><Type><TypeName>DINT</TypeName></Type></Variable>
                  </Vars>
                </Program>
                <Program name="B_Program" />
              </Types>
              <Configuration name="Cfg">
                <Resource name="R">
                  <GlobalVars>
                    <Variable name="Speed"><Type><TypeName>DINT</TypeName></Type></Variable>
                    <Variable name="Acceleration"><Type><TypeName>REAL</TypeName></Type></Variable>
                  </GlobalVars>
                </Resource>
              </Configuration>
            </Project>
            """;

        var file1 = Path.Combine(_testRoot, "Left.xml");
        var file2 = Path.Combine(_testRoot, "Right.xml");
        File.WriteAllText(file1, xml1, Encoding.UTF8);
        File.WriteAllText(file2, xml2, Encoding.UTF8);

        var diff = _service.Compare(file1, file2);

        Assert.True(diff.HasDifferences);
        Assert.Equal(new[] { "B_Program" }, diff.AddedPous);
        Assert.Equal(new[] { "A_Program" }, diff.RemovedPous);
        Assert.Single(diff.ModifiedPous);
        Assert.Equal("Z_Program", diff.ModifiedPous[0].Name);

        Assert.Equal(new[] { "Acceleration" }, diff.AddedSymbols);
        Assert.Empty(diff.RemovedSymbols);
        Assert.Single(diff.ModifiedSymbols);
        Assert.Equal("Speed", diff.ModifiedSymbols[0].Name);
        Assert.Equal("REAL", diff.ModifiedSymbols[0].LeftType);
        Assert.Equal("DINT", diff.ModifiedSymbols[0].RightType);
    }

    [Fact]
    public void Compare_ProprietaryExtensionChange_DetectedWithoutFakeSemanticEquivalence()
    {
        // High-level POU name, program type, and symbols are identical,
        // but vendor AddData extension content differs.
        var xml1 = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns:smcext="https://www.ia.omron.com/Smc" xmlns="www.iec.ch/public/TC65SC65BWG7TF10">
              <ContentHeader name="Station" />
              <Types>
                <Program name="Worker">
                  <AddData>
                    <smcext:CustomMeta tag="1.0.0" />
                  </AddData>
                </Program>
              </Types>
            </Project>
            """;

        var xml2 = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns:smcext="https://www.ia.omron.com/Smc" xmlns="www.iec.ch/public/TC65SC65BWG7TF10">
              <ContentHeader name="Station" />
              <Types>
                <Program name="Worker">
                  <AddData>
                    <smcext:CustomMeta tag="2.0.0_modified" />
                  </AddData>
                </Program>
              </Types>
            </Project>
            """;

        var file1 = Path.Combine(_testRoot, "Ext1.xml");
        var file2 = Path.Combine(_testRoot, "Ext2.xml");
        File.WriteAllText(file1, xml1, Encoding.UTF8);
        File.WriteAllText(file2, xml2, Encoding.UTF8);

        var diff = _service.Compare(file1, file2);

        Assert.True(diff.HasDifferences);
        Assert.NotEmpty(diff.RawExtensionDifferences);
        // Does not claim fake equivalence!
        Assert.Contains("Worker", diff.ModifiedPous.Select(p => p.Name));
    }

    [Fact]
    public void JsonSerialization_AdheresToCamelCaseNaming()
    {
        var report = new OmronExchangeReport(
            SourcePath: "D:\\test.xml",
            SourceSha256: "abc",
            ByteLength: 100,
            Format: "IEC-61131-10-XML",
            ProjectName: "TestProj",
            TargetDevice: "NJ501",
            Pous: Array.Empty<OmronPouInfo>(),
            Symbols: Array.Empty<OmronSymbolInfo>(),
            DataTypes: Array.Empty<OmronDataTypeInfo>(),
            Objects: Array.Empty<OmronCaexObjectInfo>(),
            Diagnostics: Array.Empty<OmronDiagnosticInfo>(),
            Limitations: new[] { "Limit1" },
            RawExtensionHashes: new Dictionary<string, string>());

        string json = JsonSerializer.Serialize(report);

        Assert.Contains("\"sourcePath\":", json);
        Assert.Contains("\"sourceSha256\":", json);
        Assert.Contains("\"validationLevel\":", json);
        Assert.Contains("\"isVendorCompiler\":", json);
        Assert.Contains("\"targetDevice\":", json);
        Assert.DoesNotContain("\"SourcePath\":", json);
        Assert.DoesNotContain("\"ValidationLevel\":", json);
    }
}
