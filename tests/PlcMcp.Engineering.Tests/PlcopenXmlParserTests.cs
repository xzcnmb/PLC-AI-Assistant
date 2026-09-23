using System.Xml;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Plcopen;

namespace PlcMcp.Engineering.Tests;

public class PlcopenXmlParserTests
{
    private readonly PlcopenXmlParser _parser = new();

    [Fact]
    public void Parse_ValidPlcopenXml_ExtractsPousAndSymbols()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <project xmlns="http://www.plcopen.org/xml/tc6_0201">
              <fileHeader companyName="TestAutomation" />
              <contentHeader name="TestStation1" />
              <types>
                <globalVars>
                  <variable name="g_StartButton" address="%IX0.0">
                    <type><BOOL /></type>
                    <documentation><xhtml xmlns="http://www.w3.org/1999/xhtml">Main start push button</xhtml></documentation>
                  </variable>
                  <variable name="g_MotorRun" address="%QX0.0">
                    <type><BOOL /></type>
                  </variable>
                </globalVars>
                <pous>
                  <pou name="MotorControl" pouType="program">
                    <interface>
                      <localVars>
                        <variable name="timer1">
                          <type><derived name="TON" /></type>
                        </variable>
                      </localVars>
                    </interface>
                    <body>
                      <ST>
                        <xhtml xmlns="http://www.w3.org/1999/xhtml">
                          IF g_StartButton THEN
                              g_MotorRun := TRUE;
                          END_IF;
                        </xhtml>
                      </ST>
                    </body>
                  </pou>
                </pous>
              </types>
            </project>
            """;

        var project = _parser.Parse(xml);

        Assert.NotNull(project);
        Assert.Equal("TestStation1", project.ProjectName);
        Assert.Equal("PLCopen-XML", project.Format);
        Assert.Equal(2, project.Symbols.Count);
        Assert.Contains(project.Symbols, s => s.Name == "g_StartButton" && s.Address == "%IX0.0" && s.DataType == "BOOL");
        Assert.Contains(project.Symbols, s => s.Name == "g_MotorRun" && s.Address == "%QX0.0");

        Assert.Single(project.Pous);
        var pou = project.Pous[0];
        Assert.Equal("MotorControl", pou.Name);
        Assert.Equal(PouType.Program, pou.PouType);
        Assert.Equal("ST", pou.Language);
        Assert.Contains("g_MotorRun := TRUE", pou.BodyText);
        Assert.Single(pou.Variables);
        Assert.Equal("timer1", pou.Variables[0].Name);
        Assert.Equal("TON", pou.Variables[0].DataType);
    }

    [Fact]
    public void Parse_XxeInjectionAttempt_ThrowsXmlExceptionDueToProhibitedDtd()
    {
        // Malicious XML attempting external entity injection (XXE)
        var maliciousXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE foo [
              <!ELEMENT foo ANY >
              <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini" >
            ]>
            <project xmlns="http://www.plcopen.org/xml/tc6_0201">
              <contentHeader name="&xxe;" />
            </project>
            """;

        Assert.Throws<XmlException>(() => _parser.Parse(maliciousXml));
    }

    [Fact]
    public void Compare_DetectsAddedRemovedAndModifiedPousAndSymbols()
    {
        var projA = new ParsedProject(
            ProjectName: "ProjA",
            Format: "PLCopen-XML",
            Symbols:
            [
                new EngineeringSymbol("Sym1", "%MW100", "INT", null),
                new EngineeringSymbol("SymCommon", "%MW102", "INT", null)
            ],
            Pous:
            [
                new EngineeringPou("PouOld", PouType.Program, "ST", "a := 1;", Array.Empty<EngineeringSymbol>()),
                new EngineeringPou("PouShared", PouType.Program, "ST", "b := 1;", Array.Empty<EngineeringSymbol>())
            ],
            SourceSha256: "hashA");

        var projB = new ParsedProject(
            ProjectName: "ProjB",
            Format: "PLCopen-XML",
            Symbols:
            [
                new EngineeringSymbol("SymCommon", "%MW200", "DINT", null), // modified address and type
                new EngineeringSymbol("SymNew", "%MW104", "REAL", null)     // added
            ],
            Pous:
            [
                new EngineeringPou("PouShared", PouType.Program, "ST", "b := 999;", Array.Empty<EngineeringSymbol>()), // modified body
                new EngineeringPou("PouNew", PouType.Function, "ST", "c := 1;", Array.Empty<EngineeringSymbol>())       // added
            ],
            SourceSha256: "hashB");

        var diff = _parser.Compare(projA, projB);

        Assert.True(diff.HasDifferences);
        Assert.Contains("PouNew", diff.AddedPous);
        Assert.Contains("PouOld", diff.RemovedPous);
        Assert.Contains("PouShared", diff.ModifiedPous);

        Assert.Contains("SymNew", diff.AddedSymbols);
        Assert.Contains("Sym1", diff.RemovedSymbols);
        Assert.Contains("SymCommon", diff.ModifiedSymbols);
    }
}
