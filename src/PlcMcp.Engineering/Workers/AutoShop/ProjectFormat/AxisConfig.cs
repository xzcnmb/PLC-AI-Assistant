using System.Globalization;
using System.Xml.Linq;

namespace PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;

/// <summary>
/// Axis kind of an AutoShop motion-axis definition. Reverse-engineered from AutoShop.exe
/// (V4.12.0.2) code that serializes axis properties — the axis containers are the four wide
/// strings L"AxesGroupName" / L"XAxis" / L"YAxis" / L"ZAxis" / L"AssitAxis" pushed together
/// around file offsets 0x117710a-0x11771f6 (VA 0x1577xxx region of AutoShop.exe).
/// </summary>
public enum AxisKind
{
    X,
    Y,
    Z,
    Assit,
}

/// <summary>
/// One axis definition of the AutoShop axis configuration (xml\axis.xml inside an unpacked
/// project directory).
/// <para>
/// <b>Schema provenance</b> — reverse-engineered, NOT verified against a real vendor file:
/// no .hcpp sample on this machine contains an axis.xml record (verified across all 15
/// template containers, the 连续式立包机 V5.x project, the simulator download packages and the
/// unpacked Template A1 project — the H3U template carries a 2-byte empty Axis.dat instead),
/// and AutoShop's UI input is input-filtered (docs/inovance-autoshop-uia-map.md), so no real
/// sample can be produced offline. The element names below were recovered from AutoShop.exe:
/// the project-XML element-name table (L"AxisName"/L"AxisType"/L"IsContour"/L"IsVirtual"/
/// L"OutputDev"/L"InputDev"/L"slaveName"/L"Mode"/L"EnableLimitSet"/L"ForwardLimitValue"/
/// L"NegativeLimitValue"/L"RotationPeriod"/L"Row") around file offsets 0x117755e-0x117856e,
/// consumed by the axis property-object code (RTTI classes CAxisMgr / CAxisBaseProcess /
/// CAIWebAPICreateAxis...). Types follow the consuming code: AxisName is a string copy,
/// EnableLimitSet is read as a byte (bool), Forward/NegativeLimitValue and RotationPeriod are
/// read as doubles (fld qword ptr [eax+8]), Mode/AxisType are ints.
/// </para>
/// <para>
/// Because the exact element nesting cannot be confirmed, the parser is
/// <b>structure-preserving</b>: the full XML tree is kept verbatim, known fields are exposed
/// as typed views, and every unrecognized element/attribute survives a parse→write round trip
/// byte-for-byte (whitespace apart). When a real sample becomes available, only the
/// <see cref="AxisConfigXml"/> element-name table needs adjusting — not the model.
/// </para>
/// </summary>
public sealed class AxisDefinition
{
    private readonly XElement _element;

    internal AxisDefinition(XElement element, AxisKind kind)
    {
        _element = element;
        Kind = kind;
    }

    /// <summary>The XML element backing this axis (live view — setters mutate it).</summary>
    public XElement Element => _element;

    /// <summary>Axis container kind (XAxis/YAxis/ZAxis/AssitAxis element name).</summary>
    public AxisKind Kind { get; }

    /// <summary>Container element name for this kind (RE: wide strings in AutoShop.exe).</summary>
    public static string ContainerName(AxisKind kind) => kind switch
    {
        AxisKind.X => "XAxis",
        AxisKind.Y => "YAxis",
        AxisKind.Z => "ZAxis",
        _ => "AssitAxis",
    };

    // ---- RE-derived typed fields (live views onto _element) ----------------

    public string? Name
    {
        get => GetText("AxisName");
        set => SetText("AxisName", value);
    }

    public string? AxisType
    {
        get => GetText("AxisType");
        set => SetText("AxisType", value);
    }

    /// <summary>Operating mode (int per the consuming code).</summary>
    public int? Mode
    {
        get => GetInt("Mode");
        set => SetInt("Mode", value);
    }

    public bool? EnableLimitSet
    {
        get => GetBool("EnableLimitSet");
        set => SetBool("EnableLimitSet", value);
    }

    public double? ForwardLimitValue
    {
        get => GetDouble("ForwardLimitValue");
        set => SetDouble("ForwardLimitValue", value);
    }

    public double? NegativeLimitValue
    {
        get => GetDouble("NegativeLimitValue");
        set => SetDouble("NegativeLimitValue", value);
    }

    /// <summary>Screw period / rotation period (double per the consuming code).</summary>
    public double? RotationPeriod
    {
        get => GetDouble("RotationPeriod");
        set => SetDouble("RotationPeriod", value);
    }

    public bool? IsVirtual
    {
        get => GetBool("IsVirtual");
        set => SetBool("IsVirtual", value);
    }

    public bool? IsContour
    {
        get => GetBool("IsContour");
        set => SetBool("IsContour", value);
    }

    public string? OutputDev
    {
        get => GetText("OutputDev");
        set => SetText("OutputDev", value);
    }

    public string? InputDev
    {
        get => GetText("InputDev");
        set => SetText("InputDev", value);
    }

    /// <summary>Servo slave name (EtherCAT/CANlink axes; element spelled "slaveName" in the binary).</summary>
    public string? SlaveName
    {
        get => GetText("slaveName");
        set => SetText("slaveName", value);
    }

    public int? Row
    {
        get => GetInt("Row");
        set => SetInt("Row", value);
    }

    // ---- view helpers -------------------------------------------------------

    private string? GetText(string name) =>
        _element.Elements(name).FirstOrDefault()?.Value;

    private void SetText(string name, string? value)
    {
        var el = _element.Element(name);
        if (value is null)
        {
            el?.Remove();
            return;
        }
        if (el is null)
        {
            el = new XElement(name);
            _element.Add(el);
        }
        el.Value = value;
    }

    private int? GetInt(string name)
    {
        var v = GetText(name);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    private void SetInt(string name, int? value) =>
        SetText(name, value?.ToString(CultureInfo.InvariantCulture));

    private bool? GetBool(string name)
    {
        var v = GetText(name);
        if (v is null) return null;
        if (bool.TryParse(v, out var b)) return b;
        if (v == "1") return true;
        if (v == "0") return false;
        return null;
    }

    private void SetBool(string name, bool? value) =>
        SetText(name, value?.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());

    private double? GetDouble(string name)
    {
        var v = GetText(name);
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private void SetDouble(string name, double? value) =>
        SetText(name, value?.ToString("R", CultureInfo.InvariantCulture));
}

/// <summary>
/// Parsed AutoShop axis configuration (xml\axis.xml). Holds the whole XML tree verbatim plus
/// typed views of the axis definitions found under the RE-derived container elements
/// (AxesGroupName + XAxis/YAxis/ZAxis/AssitAxis). See <see cref="AxisDefinition"/> for the
/// schema provenance and its "no real sample exists" caveat.
/// </summary>
public sealed class AxisConfigDocument
{
    private readonly XDocument _document;

    internal AxisConfigDocument(XDocument document)
    {
        _document = document;
        Axes = BuildAxisList(document);
    }

    /// <summary>The live XML tree (structure-preserving; setters mutate this).</summary>
    public XDocument Document => _document;

    /// <summary>Axes found under AxesGroupName and the XAxis/YAxis/ZAxis/AssitAxis containers.</summary>
    public IReadOnlyList<AxisDefinition> Axes { get; }

    /// <summary>Value of the AxesGroupName element (RE: group name string pushed before the axis containers).</summary>
    public string? AxesGroupName
    {
        get => _document.Root?
            .DescendantsAndSelf("AxesGroupName")
            .FirstOrDefault()?.Value;
        set
        {
            var el = _document.Root?
                .DescendantsAndSelf("AxesGroupName")
                .FirstOrDefault();
            if (el is not null && value is not null)
                el.Value = value;
        }
    }

    private static IReadOnlyList<AxisDefinition> BuildAxisList(XDocument document)
    {
        var axes = new List<AxisDefinition>();
        var root = document.Root;
        if (root is null)
            return axes;
        foreach (AxisKind kind in new[] { AxisKind.X, AxisKind.Y, AxisKind.Z, AxisKind.Assit })
        {
            var container = root.DescendantsAndSelf(AxisDefinition.ContainerName(kind))
                .FirstOrDefault();
            if (container is null)
                continue;
            // Axis definitions are either the container element itself (attributes/children on
            // XAxis) or child elements of it — support both without guessing which is real.
            var self = LooksLikeAxisElement(container)
                ? new[] { container }
                : Array.Empty<XElement>();
            foreach (var el in self.Concat(container.Elements()))
            {
                if (LooksLikeAxisElement(el))
                    axes.Add(new AxisDefinition(el, kind));
            }
        }
        return axes;
    }

    private static bool LooksLikeAxisElement(XElement el) =>
        el.Elements("AxisName").Any() || el.Attribute("AxisName") is not null ||
        el.Elements("AxisType").Any() || el.Attribute("AxisType") is not null;
}

/// <summary>
/// Parse/serialize for the AutoShop axis configuration XML (xml\axis.xml).
/// Structure-preserving: parse keeps the whole tree, write serializes it back.
/// <b>Schema caveat: no real vendor axis.xml sample exists on this machine</b> — the element
/// names are RE-derived from AutoShop.exe (see <see cref="AxisDefinition"/>). The parser
/// therefore never rejects unknown content and round-trips it verbatim.
/// </summary>
public static class AxisConfigXml
{
    /// <summary>Parse an axis.xml text. Returns false with a reason instead of throwing.</summary>
    public static bool TryParse(string xml, out AxisConfigDocument? document, out string? error)
    {
        document = null;
        error = null;
        if (string.IsNullOrWhiteSpace(xml))
        {
            error = "axis xml is empty.";
            return false;
        }
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException ex)
        {
            error = $"axis xml is not well-formed: {ex.Message}";
            return false;
        }
        if (doc.Root is null)
        {
            error = "axis xml has no root element.";
            return false;
        }
        document = new AxisConfigDocument(doc);
        return true;
    }

    /// <summary>Serializes the (possibly edited) document back to text, keeping the XML declaration.</summary>
    public static string Write(AxisConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var decl = document.Document.Declaration is null
            ? string.Empty
            : document.Document.Declaration + Environment.NewLine;
        return decl + document.Document.Root!.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Builds a schema-conformant sample axis.xml per the RE-derived element table. This is a
    /// SYNTHESIZED template (fixture/documentation aid) — no real vendor sample exists; see
    /// <see cref="AxisDefinition"/>. Layout: axes → AxesGroupName → one container element per
    /// kind holding that kind's axis definitions.
    /// </summary>
    public static string BuildTemplate(
        string groupName,
        IReadOnlyList<(AxisKind Kind, string Name, string AxisType, bool EnableLimitSet,
            double ForwardLimit, double NegativeLimit, double RotationPeriod)> axes)
    {
        var root = new XElement("Axes");
        root.Add(new XElement("AxesGroupName", groupName));
        foreach (var kind in new[] { AxisKind.X, AxisKind.Y, AxisKind.Z, AxisKind.Assit })
        {
            var container = new XElement(AxisDefinition.ContainerName(kind));
            foreach (var a in axes.Where(a => a.Kind == kind))
            {
                container.Add(new XElement("Axis",
                    new XElement("AxisName", a.Name),
                    new XElement("AxisType", a.AxisType),
                    new XElement("Mode", "0"),
                    new XElement("EnableLimitSet", a.EnableLimitSet ? "true" : "false"),
                    new XElement("ForwardLimitValue", a.ForwardLimit.ToString("R", CultureInfo.InvariantCulture)),
                    new XElement("NegativeLimitValue", a.NegativeLimit.ToString("R", CultureInfo.InvariantCulture)),
                    new XElement("RotationPeriod", a.RotationPeriod.ToString("R", CultureInfo.InvariantCulture)),
                    new XElement("IsVirtual", "false"),
                    new XElement("IsContour", "false"),
                    new XElement("OutputDev", string.Empty),
                    new XElement("InputDev", string.Empty)));
            }
            if (container.HasElements || kind == AxisKind.X)
                root.Add(container);
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root).ToString();
    }
}
