using System.Text;
using System.Xml.Linq;
using PlcMcp.AutoShopNativeBridge.Crypto;
using PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;
using Xunit;

namespace PlcMcp.Engineering.Tests;

/// <summary>
/// Offline tests for the AutoShop axis configuration (xml\axis.xml) model, parser, editor and
/// the decrypt→edit→re-encrypt→repackage round trip.
/// <para>
/// All fixtures are SYNTHESIZED in memory: no real vendor axis.xml exists anywhere on this
/// machine (verified across all template .hcpp containers, the 连续式立包机 V5.x project, the
/// simulator download packages and the unpacked Template A1 project — see
/// docs/inovance-autoshop-axis-config.md). The XML schema implemented here is the
/// reverse-engineered element table from AutoShop.exe and is marked as such; the parser is
/// structure-preserving so unknown vendor content would survive unchanged.
/// </para>
/// </summary>
public class AutoShopAxisTests
{
    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static IReadOnlyList<(AxisKind Kind, string Name, string AxisType, bool EnableLimitSet, double ForwardLimit, double NegativeLimit, double RotationPeriod)>
        SampleAxes() => new List<(AxisKind, string, string, bool, double, double, double)>
    {
        (AxisKind.X, "X1", "Pulse", true, 1000.0, -1000.0, 5.0),
        (AxisKind.Y, "Y1", "Pulse", false, 800.5, -800.5, 2.5),
        (AxisKind.Z, "Z1", "Pulse", true, 500.0, -500.0, 10.0),
    };

    private static string SampleAxisXml() => AxisConfigXml.BuildTemplate("G1", SampleAxes());

    /// <summary>Builds a synthetic .hcpp: encrypted main record + two members (axis.xml + MAIN.LD).</summary>
    private static byte[] BuildPackage(string axisXml, string projectXml, string mainLd)
    {
        var blob = AutoShopProjectCrypt.EncryptBlob(MainPlain(projectXml));
        var main = new HcppMainRecordSpec("测试工程.hcp", blob);
        var files = new[]
        {
            new HcppFileSpec("axis.xml", Encoding.UTF8.GetBytes(axisXml)),
            new HcppFileSpec("MAIN.LD", Encoding.ASCII.GetBytes(mainLd)),
        };
        return HcppWriter.Write(main, files);
    }

    private static byte[] MainPlain(string projectXml)
    {
        var xml = Encoding.Unicode.GetBytes(projectXml);
        var plain = new byte[xml.Length + 2];
        plain[0] = 0xFF; // UTF-16LE BOM, the vendor main-blob shape (D3)
        plain[1] = 0xFE;
        xml.CopyTo(plain, 2);
        return plain;
    }

    private const string SampleProjectXml =
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<project>\r\n\t<Version>1</Version>\r\n" +
        "\t<ProjectDesc>合成测试工程</ProjectDesc>\r\n</project>";

    // ---------------------------------------------------------------
    // B1: model + parser
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_SynthesizedTemplate_FindsAxesAndTypedFields()
    {
        var ok = AxisConfigXml.TryParse(SampleAxisXml(), out var doc, out var error);
        Assert.True(ok, error);
        Assert.NotNull(doc);

        Assert.Equal("G1", doc!.AxesGroupName);
        Assert.Equal(3, doc.Axes.Count);

        var x = doc.Axes.Single(a => a.Kind == AxisKind.X);
        Assert.Equal("X1", x.Name);
        Assert.Equal("Pulse", x.AxisType);
        Assert.True(x.EnableLimitSet);
        Assert.Equal(1000.0, x.ForwardLimitValue);
        Assert.Equal(-1000.0, x.NegativeLimitValue);
        Assert.Equal(5.0, x.RotationPeriod);
        Assert.False(x.IsVirtual);

        var y = doc.Axes.Single(a => a.Kind == AxisKind.Y);
        Assert.False(y.EnableLimitSet);
        Assert.Equal(800.5, y.ForwardLimitValue);
    }

    [Fact]
    public void Parse_RejectsGarbage_WithReason()
    {
        Assert.False(AxisConfigXml.TryParse("not xml at all <<<<", out _, out var error));
        Assert.Contains("not well-formed", error);

        Assert.False(AxisConfigXml.TryParse("", out _, out var empty));
        Assert.False(AxisConfigXml.TryParse("   ", out _, out _));
        Assert.NotNull(empty);
    }

    [Fact]
    public void RoundTrip_PreservesUnknownContent_ByteForByte()
    {
        // Structure preservation contract: fields the model does not know about survive a
        // parse→write round trip. This is what makes the editor safe on future REAL samples.
        var xml = SampleAxisXml().Replace(
            "<IsContour>false</IsContour>",
            "<IsContour>false</IsContour><VendorFuture a=\"1\"><inner>保留</inner></VendorFuture>");
        Assert.True(AxisConfigXml.TryParse(xml, out var doc, out var error));
        Assert.True(AxisConfigXml.TryParse(AxisConfigXml.Write(doc!), out var doc2, out error));
        Assert.Equal(doc!.Document.Root!.ToString(SaveOptions.DisableFormatting),
                     doc2!.Document.Root!.ToString(SaveOptions.DisableFormatting));
        Assert.Contains("VendorFuture", doc2.Document.Root.ToString());
        Assert.Contains("保留", doc2.Document.Root.ToString());
    }

    // ---------------------------------------------------------------
    // B2: edit + repackage round trip
    // ---------------------------------------------------------------

    [Fact]
    public void EditAxis_Repackage_VerifyShowsEditAndPreservesMain()
    {
        var pkg = BuildPackage(SampleAxisXml(), SampleProjectXml, "LD-content-baseline");

        var ok = AxisPackageEditor.TryEditAxisAndRepackage(
            pkg,
            doc =>
            {
                var x = doc.Axes.Single(a => a.Name == "X1");
                x.EnableLimitSet = false;
                x.ForwardLimitValue = 4321.5;
            },
            out var repacked,
            out var error);
        Assert.True(ok, error);
        Assert.NotNull(repacked);

        Assert.True(AxisPackageEditor.TryVerify(repacked!, out var axisXml, out var mainXml, out error));
        Assert.True(AxisConfigXml.TryParse(axisXml!, out var reread, out error));
        var x2 = reread!.Axes.Single(a => a.Name == "X1");
        Assert.False(x2.EnableLimitSet);          // edit landed
        Assert.Equal(4321.5, x2.ForwardLimitValue);
        var z2 = reread.Axes.Single(a => a.Name == "Z1");
        Assert.True(z2.EnableLimitSet);           // untouched axis intact

        // main blob: byte-identical container passthrough + decryptable
        Assert.Contains("合成测试工程", mainXml);
        Assert.True(HcppReader.TryRead(repacked!, out var before, out _));
        Assert.True(HcppReader.TryRead(pkg, out var after, out _));
        Assert.Equal(
            Convert.ToHexString(before!.MainRecord!.MainBlob!),
            Convert.ToHexString(after!.MainRecord!.MainBlob!));
    }

    [Fact]
    public void EditMainXml_Reencrypt_RoundTripsThroughDecrypt()
    {
        var pkg = BuildPackage(SampleAxisXml(), SampleProjectXml, "LD-content-baseline");

        var ok = AxisPackageEditor.TryEditMainXmlAndRepackage(
            pkg,
            xml => xml.Replace("<ProjectDesc>合成测试工程</ProjectDesc>",
                               "<ProjectDesc>已修改描述</ProjectDesc>"),
            out var repacked,
            out var error);
        Assert.True(ok, error);

        Assert.True(AxisPackageEditor.TryVerify(repacked!, out var axisXml, out var mainXml, out error));
        Assert.Contains("已修改描述", mainXml);          // decrypt→edit→encrypt loop closed
        Assert.Contains("axis.xml", string.Join(",", HcppReader.Read(repacked!).Files.Select(f => f.Name)));

        // the axis member content is untouched by the main-XML path
        Assert.Contains("<AxesGroupName>G1</AxesGroupName>", axisXml);
    }

    [Fact]
    public void ContainerWithoutAxisMember_ReportsAbsentWithoutError()
    {
        var pkg = HcppWriter.Write(
            new HcppMainRecordSpec("p.hcp", AutoShopProjectCrypt.EncryptBlob(MainPlain(SampleProjectXml))),
            new[] { new HcppFileSpec("MAIN.LD", Encoding.ASCII.GetBytes("ld")) });

        var ok = AxisPackageEditor.TryLoad(pkg, out var doc, out var hasAxis, out var error);
        Assert.True(ok, error);
        Assert.False(hasAxis);
        Assert.Null(doc);
    }

    [Fact]
    public void TryLoad_RoundTripsSyntheticPackage()
    {
        var pkg = BuildPackage(SampleAxisXml(), SampleProjectXml, "ld");
        var ok = AxisPackageEditor.TryLoad(pkg, out var doc, out var hasAxis, out var error);
        Assert.True(ok, error);
        Assert.True(hasAxis);
        Assert.NotNull(doc);
        Assert.Equal(3, doc!.Axes.Count);
        Assert.Equal("G1", doc.AxesGroupName);
    }

    [Fact]
    public void Crypt_SelfTest_Gate()
    {
        // version gate for the crypto layer the round trip depends on (D3 known-answer anchor)
        Assert.True(AutoShopProjectCrypt.SelfTest());
    }

    // ---------------------------------------------------------------
    // Real-sample evidence (env-gated, skipped in CI)
    // ---------------------------------------------------------------

    [Fact]
    public void RealTemplate_NoAxisMember_Evidence()
    {
        // Documents the "no real axis.xml sample exists" finding against a real vendor
        // container when one is available locally. Skipped unless PLCMCP_LOCAL_AUTOSHOP_DIR
        // points at the AutoShop install root (never set in CI).
        var dir = Environment.GetEnvironmentVariable("PLCMCP_LOCAL_AUTOSHOP_DIR");
        if (string.IsNullOrEmpty(dir))
            return; // skip silently (env-gated)
        var path = Path.Combine(dir, "Template_Project", "English", "Library_Template",
            "Template A1", "Template A1.hcpp");
        if (!File.Exists(path))
            return;

        var bytes = File.ReadAllBytes(path);
        Assert.True(HcppReader.TryRead(bytes, out var container, out var error));
        var axis = AxisPackageEditor.FindAxisFile(container!);
        Assert.Null(axis); // H3U template carries no axis.xml member (measured fact)
    }
}
