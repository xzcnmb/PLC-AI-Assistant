using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using PlcMcp.Engineering.Models;

namespace PlcMcp.Engineering.Plcopen;

public interface IPlcopenXmlParser
{
    ParsedProject Parse(string xmlContent, string? sourceSha256 = null);
    ParsedProject ParseFile(string filePath);
    ProjectDiff Compare(ParsedProject original, ParsedProject target);
}

public sealed class PlcopenXmlParser : IPlcopenXmlParser
{
    private static readonly XmlReaderSettings SafeSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0
    };

    public ParsedProject ParseFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        using var sha256 = SHA256.Create();
        using var fileStream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(sha256.ComputeHash(fileStream)).ToLowerInvariant();
        fileStream.Seek(0, SeekOrigin.Begin);

        using var reader = new StreamReader(fileStream);
        var content = reader.ReadToEnd();
        return Parse(content, hash);
    }

    public ParsedProject Parse(string xmlContent, string? sourceSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            throw new ArgumentException("XML content cannot be empty.", nameof(xmlContent));

        // Enforce safe XML reading: Prohibit DTD and resolve no external entities (Anti-XXE)
        XDocument doc;
        using (var stringReader = new StringReader(xmlContent))
        using (var xmlReader = XmlReader.Create(stringReader, SafeSettings))
        {
            doc = XDocument.Load(xmlReader);
        }

        var root = doc.Root;
        if (root == null)
            throw new InvalidOperationException("XML root element is missing.");

        var ns = root.GetDefaultNamespace();

        // Project / Content header
        var contentHeader = root.Element(ns + "contentHeader");
        string projectName = contentHeader?.Attribute("name")?.Value
                             ?? root.Element(ns + "fileHeader")?.Attribute("companyName")?.Value
                             ?? "PlcopenProject";

        var symbols = new List<EngineeringSymbol>();
        var pous = new List<EngineeringPou>();

        // Extract global variables: <types><globalVars> or <project><types><globalVars>
        var globalVarsElements = root.Descendants(ns + "globalVars");
        foreach (var gv in globalVarsElements)
        {
            foreach (var variable in gv.Elements(ns + "variable"))
            {
                var sym = ParseVariable(variable, ns, "Global");
                if (sym != null) symbols.Add(sym);
            }
        }

        // Extract POUs: <types><pous><pou>
        var pouElements = root.Descendants(ns + "pou");
        foreach (var pouElem in pouElements)
        {
            var pou = ParsePou(pouElem, ns);
            if (pou != null) pous.Add(pou);
        }

        var sha = sourceSha256 ?? ComputeContentSha256(xmlContent);

        return new ParsedProject(
            ProjectName: projectName,
            Format: "PLCopen-XML",
            Symbols: symbols,
            Pous: pous,
            SourceSha256: sha);
    }

    public ProjectDiff Compare(ParsedProject original, ParsedProject target)
    {
        var origPouNames = original.Pous.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        var targetPouNames = target.Pous.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        var addedPous = targetPouNames.Keys.Except(origPouNames.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var removedPous = origPouNames.Keys.Except(targetPouNames.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var commonPous = origPouNames.Keys.Intersect(targetPouNames.Keys, StringComparer.OrdinalIgnoreCase).ToList();

        var modifiedPous = new List<string>();
        foreach (var name in commonPous)
        {
            var p1 = origPouNames[name];
            var p2 = targetPouNames[name];

            bool bodyDiff = !string.Equals(p1.BodyText?.Trim(), p2.BodyText?.Trim(), StringComparison.Ordinal);
            bool typeDiff = p1.PouType != p2.PouType;
            bool langDiff = !string.Equals(p1.Language, p2.Language, StringComparison.OrdinalIgnoreCase);
            bool varCountDiff = p1.Variables.Count != p2.Variables.Count;

            if (bodyDiff || typeDiff || langDiff || varCountDiff)
            {
                modifiedPous.Add(name);
            }
        }

        var origSymbols = original.Symbols.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        var targetSymbols = target.Symbols.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

        var addedSymbols = targetSymbols.Keys.Except(origSymbols.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var removedSymbols = origSymbols.Keys.Except(targetSymbols.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var commonSymbols = origSymbols.Keys.Intersect(targetSymbols.Keys, StringComparer.OrdinalIgnoreCase).ToList();

        var modifiedSymbols = new List<string>();
        foreach (var name in commonSymbols)
        {
            var s1 = origSymbols[name];
            var s2 = targetSymbols[name];

            if (!string.Equals(s1.Address, s2.Address, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(s1.DataType, s2.DataType, StringComparison.OrdinalIgnoreCase))
            {
                modifiedSymbols.Add(name);
            }
        }

        bool hasDifferences = addedPous.Count > 0 || removedPous.Count > 0 || modifiedPous.Count > 0 ||
                             addedSymbols.Count > 0 || removedSymbols.Count > 0 || modifiedSymbols.Count > 0;

        string summary = hasDifferences
            ? $"Diff: POUs [+{addedPous.Count} -{removedPous.Count} ~{modifiedPous.Count}], Symbols [+{addedSymbols.Count} -{removedSymbols.Count} ~{modifiedSymbols.Count}]."
            : "No differences found between projects.";

        return new ProjectDiff(
            AddedPous: addedPous,
            RemovedPous: removedPous,
            ModifiedPous: modifiedPous,
            AddedSymbols: addedSymbols,
            RemovedSymbols: removedSymbols,
            ModifiedSymbols: modifiedSymbols,
            HasDifferences: hasDifferences,
            Summary: summary);
    }

    private static EngineeringSymbol? ParseVariable(XElement variableElem, XNamespace ns, string scope)
    {
        string? name = variableElem.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(name)) return null;

        string? address = variableElem.Attribute("address")?.Value;
        string dataType = "UNKNOWN";

        var typeElem = variableElem.Element(ns + "type");
        if (typeElem != null)
        {
            var firstChild = typeElem.Elements().FirstOrDefault();
            if (firstChild != null)
            {
                dataType = firstChild.Name.LocalName;
                if (dataType == "derived" && firstChild.Attribute("name") != null)
                {
                    dataType = firstChild.Attribute("name")!.Value;
                }
            }
        }

        var comment = variableElem.Element(ns + "documentation")?.Element(ns + "xhtml")?.Value?.Trim()
                     ?? variableElem.Element(ns + "documentation")?.Value?.Trim();

        return new EngineeringSymbol(
            Name: name,
            Address: address,
            DataType: dataType,
            Comment: comment,
            Scope: scope);
    }

    private static EngineeringPou? ParsePou(XElement pouElem, XNamespace ns)
    {
        string? name = pouElem.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(name)) return null;

        string rawType = pouElem.Attribute("pouType")?.Value ?? "program";
        var pouType = rawType.ToLowerInvariant() switch
        {
            "function" => PouType.Function,
            "functionblock" => PouType.FunctionBlock,
            _ => PouType.Program
        };

        var vars = new List<EngineeringSymbol>();
        var interfaceElem = pouElem.Element(ns + "interface");
        if (interfaceElem != null)
        {
            foreach (var group in interfaceElem.Elements())
            {
                string scope = group.Name.LocalName; // localVars, inputVars, outputVars, inOutVars
                foreach (var v in group.Elements(ns + "variable"))
                {
                    var sym = ParseVariable(v, ns, scope);
                    if (sym != null) vars.Add(sym);
                }
            }
        }

        // Body and Language
        string language = "UNKNOWN";
        string? bodyText = null;

        var bodyElem = pouElem.Element(ns + "body");
        if (bodyElem != null)
        {
            var langElem = bodyElem.Elements().FirstOrDefault();
            if (langElem != null)
            {
                language = langElem.Name.LocalName.ToUpperInvariant(); // ST, FBD, LD, IL, SFC
                if (language == "ST")
                {
                    bodyText = langElem.Element(ns + "xhtml")?.Value
                               ?? langElem.Value;
                }
                else
                {
                    bodyText = langElem.ToString(SaveOptions.DisableFormatting);
                }
            }
        }

        return new EngineeringPou(
            Name: name,
            PouType: pouType,
            Language: language,
            BodyText: bodyText,
            Variables: vars);
    }

    private static string ComputeContentSha256(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
    }
}
