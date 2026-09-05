using StoryForge.Core.Graph;
using StoryForge.Core.Schemas;

namespace StoryForge.Core.AiReference;

// Translates a chapter node's incoming AND/OR/NOT logic chain into a Traditional Chinese sentence an AI
// can read directly as branch conditions, instead of leaving it to infer that from a raw node/edge dump.
// AND/OR/NOT nodes never carry an identity value, so they're walked through transparently and never
// described as a step in their own right — see the AI-reference-export design notes for the full rationale
// (this class implements decisions worked out there, not something to re-derive from scratch).
public static class ChapterConditionDescriber
{
    private const string AndTypeName = "Definition.ProcessNode.AndProcessNode, MainAssemble";
    private const string OrTypeName = "Definition.ProcessNode.OrProcessNode, MainAssemble";
    private const string NotTypeName = "Definition.ProcessNode.NotProcessNode, MainAssemble";
    private const string BoolFlagTypeName = "Definition.ProcessNode.BoolFlagProcessNode, MainAssemble";
    private const string IntFlagTypeName = "Definition.ProcessNode.IntFlagProcessNode, MainAssemble";
    private const string PlayscriptFlagTypeName = "Definition.ProcessNode.PlayscriptFlagProcessNode, MainAssemble";
    private const string PlayscriptProcessTypeName = "Definition.ProcessNode.PlayscriptProcessNode, MainAssemble";

    // A leaf phrase this same class produced elsewhere -> its natural negation, so NOT-of-a-simple-leaf
    // reads as "未開啟" instead of "非（已開啟）". Compound (AND/OR) or cross-chapter-tagged phrases never
    // match here and fall back to the "非（...）" wrapper instead.
    private static readonly (string Positive, string Negative)[] NegationPairs =
    {
        ("已開啟", "未開啟"),
        ("已完成", "未完成"),
        ("已解鎖", "未解鎖"),
        ("可執行", "不可執行"),
    };

    // Null means the node has no input ports at all (nothing to describe) or none of them have any
    // incoming edges (unconditional). Multiple input ports (rare) are joined with "；", each prefixed by
    // its own port name so it's clear which port a given condition belongs to.
    public static string? DescribeEntryCondition(GraphDocumentModel model, GraphNodeVm node, HashSet<string> chapterNodeGuids)
    {
        var schema = model.GetSchema(node.TypeName);
        if (schema == null || schema.Inputs.Count == 0)
            return null;

        var portDescriptions = new List<string>();
        foreach (var input in schema.Inputs)
        {
            var edges = EdgesInto(model, node.Guid, input.FieldName);
            if (edges.Count == 0)
                continue;

            var parts = edges.Select(e => DescribeSource(model, e.FromNodeGuid, e.FromPort, chapterNodeGuids)).ToList();
            var combined = parts.Count == 1 ? parts[0] : string.Join(" 且 ", parts);
            portDescriptions.Add(schema.Inputs.Count > 1 ? $"{input.PortName}：{combined}" : combined);
        }

        return portDescriptions.Count == 0 ? null : string.Join("；", portDescriptions);
    }

    private static List<GraphEdgeVm> EdgesInto(GraphDocumentModel model, string nodeGuid, string port)
    {
        return model.Edges.Where(e => !e.IsMarkedForDeletion && e.ToNodeGuid == nodeGuid && e.ToPort == port).ToList();
    }

    private static string DescribeSource(GraphDocumentModel model, string nodeGuid, string outputPort, HashSet<string> chapterNodeGuids)
    {
        var node = model.FindNode(nodeGuid);
        if (node == null)
            return "條件不明";

        if (!string.IsNullOrEmpty(node.IdentityValue))
        {
            var leaf = DescribeLeaf(model, node, outputPort);
            return chapterNodeGuids.Contains(node.Guid) ? leaf : $"{leaf}（此條件來自其他章節）";
        }

        if (node.TypeName == NotTypeName)
            return DescribeNot(model, node, chapterNodeGuids);

        if (node.TypeName == AndTypeName || node.TypeName == OrTypeName)
        {
            var edges = EdgesInto(model, node.Guid, "input");
            if (edges.Count == 0)
                return "條件不明";

            var parts = edges.Select(e => DescribeSource(model, e.FromNodeGuid, e.FromPort, chapterNodeGuids)).ToList();
            var combined = string.Join(node.TypeName == AndTypeName ? " 且 " : " 或 ", parts);
            return parts.Count > 1 ? $"（{combined}）" : combined;
        }

        // Some other identity-less node type sitting on a boolean edge — not expected for the known node
        // set, but keep walking through its single input the same way AND/OR are walked through, per the
        // "skip logic nodes, don't just give up" rule, rather than stopping cold.
        var schema = model.GetSchema(node.TypeName);
        if (schema is { Inputs.Count: 1 })
        {
            var fallbackEdges = EdgesInto(model, node.Guid, schema.Inputs[0].FieldName);
            if (fallbackEdges.Count == 1)
                return DescribeSource(model, fallbackEdges[0].FromNodeGuid, fallbackEdges[0].FromPort, chapterNodeGuids);
        }

        return "條件不明";
    }

    private static string DescribeNot(GraphDocumentModel model, GraphNodeVm notNode, HashSet<string> chapterNodeGuids)
    {
        var edges = EdgesInto(model, notNode.Guid, "input");
        if (edges.Count == 0)
            return "條件不明";

        var inner = DescribeSource(model, edges[0].FromNodeGuid, edges[0].FromPort, chapterNodeGuids);
        return TryNegateSimplePhrase(inner) ?? $"非（{inner}）";
    }

    private static string? TryNegateSimplePhrase(string phrase)
    {
        if (phrase.Contains("（此條件來自其他章節）") || phrase.Contains(" 且 ") || phrase.Contains(" 或 "))
            return null;

        foreach (var (positive, negative) in NegationPairs)
        {
            if (phrase.EndsWith(positive, StringComparison.Ordinal))
                return phrase[..^positive.Length] + negative;
            if (phrase.EndsWith(negative, StringComparison.Ordinal))
                return phrase[..^negative.Length] + positive;
        }

        return null;
    }

    private static string DescribeLeaf(GraphDocumentModel model, GraphNodeVm node, string outputPort)
    {
        var identity = node.IdentityValue ?? node.TypeName;

        if (node.TypeName == BoolFlagTypeName)
        {
            return outputPort switch
            {
                "isOn" => $"Flag『{identity}』已開啟",
                "isOff" => $"Flag『{identity}』未開啟",
                _ => GenericLeaf(model, node, identity, outputPort),
            };
        }

        if (node.TypeName == PlayscriptFlagTypeName || node.TypeName == PlayscriptProcessTypeName)
        {
            return outputPort switch
            {
                "isFinish" => $"劇本『{identity}』已完成",
                "notFinish" => $"劇本『{identity}』未完成",
                "isUnlock" => $"劇本『{identity}』已解鎖",
                "notUnlock" => $"劇本『{identity}』未解鎖",
                "isPlayable" => $"劇本『{identity}』可執行",
                "isNotPlayable" => $"劇本『{identity}』不可執行",
                _ => GenericLeaf(model, node, identity, outputPort),
            };
        }

        if (node.TypeName == IntFlagTypeName)
            return DescribeIntFlagLeaf(node, identity, outputPort);

        return GenericLeaf(model, node, identity, outputPort);
    }

    private static string DescribeIntFlagLeaf(GraphNodeVm node, string identity, string outputPort)
    {
        var compareValue = node.Fields.TryGetValue("compareValue", out var v) ? v?.ToString() : "?";
        var symbol = outputPort switch
        {
            "isLargerThan" => "＞",
            "isLargerOrEquals" => "≧",
            "isEquals" => "＝",
            "isNotEquals" => "≠",
            "isLessThan" => "＜",
            "isLessOrEquals" => "≦",
            _ => outputPort,
        };
        return $"變數『{identity}』{symbol}{compareValue}";
    }

    // Fallback for any node type this describer doesn't have specific phrasing for — uses the schema's own
    // (already human-readable) display name and port name rather than raw field names.
    private static string GenericLeaf(GraphDocumentModel model, GraphNodeVm node, string identity, string outputPort)
    {
        var schema = model.GetSchema(node.TypeName);
        var portName = schema?.Outputs.FirstOrDefault(o => o.FieldName == outputPort)?.PortName ?? outputPort;
        var typeLabel = schema?.DisplayName ?? node.TypeName;
        return $"{typeLabel}『{identity}』{portName}";
    }
}
