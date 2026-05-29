using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ZyncMaster.Core;

namespace ZyncMaster.Graph;

public sealed class ParticipantBodyRenderer : IParticipantRenderer
{
    private const string StartMarker = "<!-- calimport:participants:start -->";
    private const string EndMarker   = "<!-- calimport:participants:end -->";

    private static readonly Regex BlockPattern = new Regex(
        @"<!--\s*calimport:participants:start\s*-->.*?<!--\s*calimport:participants:end\s*-->",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string BuildBodyForCreate(string description, IReadOnlyList<ParticipantRecord> participants)
    {
        if (participants == null) throw new ArgumentNullException(nameof(participants));

        var sb = new StringBuilder();

        // Participants go first so they are visible at the top of the event body.
        if (participants.Count > 0)
            sb.Append(RenderBlock(participants));

        if (!string.IsNullOrEmpty(description))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("<p>");
            sb.Append(EscapeHtmlPreservingLines(description));
            sb.Append("</p>");
        }

        return sb.ToString();
    }

    public string MergeIntoExistingBody(string existingBodyHtml, IReadOnlyList<ParticipantRecord> participants)
    {
        if (participants == null) throw new ArgumentNullException(nameof(participants));
        existingBodyHtml ??= "";

        var newBlock = participants.Count > 0 ? RenderBlock(participants) : "";

        // Replace the existing block in place (preserving whatever the user edited around it).
        if (BlockPattern.IsMatch(existingBodyHtml))
            return BlockPattern.Replace(existingBodyHtml, _ => newBlock);

        if (newBlock.Length == 0)
            return existingBodyHtml;

        // No markers yet: prepend so participants stay at the top, matching create order.
        return existingBodyHtml.Length == 0
            ? newBlock
            : newBlock + "\n" + existingBodyHtml.TrimStart();
    }

    private static string RenderBlock(IReadOnlyList<ParticipantRecord> participants)
    {
        var sb = new StringBuilder();
        // Header lives inside the markers so MergeIntoExistingBody can strip the
        // entire block (header included) when no participants are present.
        sb.Append(StartMarker);
        sb.Append("\n<p><b>Participants (reference only — not invited):</b></p>\n");
        sb.Append("<table border=\"1\" cellpadding=\"4\" cellspacing=\"0\">\n");
        sb.Append("<tr><th>Name</th><th>Email</th><th>Type</th><th>Response</th></tr>\n");
        foreach (var p in participants)
        {
            sb.Append("<tr><td>");
            sb.Append(WebUtility.HtmlEncode(p.Name));
            sb.Append("</td><td>");
            sb.Append(WebUtility.HtmlEncode(p.Email));
            sb.Append("</td><td>");
            sb.Append(WebUtility.HtmlEncode(p.Type));
            sb.Append("</td><td>");
            sb.Append(WebUtility.HtmlEncode(p.Response));
            sb.Append("</td></tr>\n");
        }
        sb.Append("</table>\n");
        sb.Append(EndMarker);
        return sb.ToString();
    }

    private static string EscapeHtmlPreservingLines(string text)
    {
        var escaped = WebUtility.HtmlEncode(text);
        return escaped.Replace("\r\n", "<br>\n").Replace("\n", "<br>\n");
    }
}
