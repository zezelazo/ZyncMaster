using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SyncMaster.Core;

namespace SyncMaster.CalImport;

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

        if (!string.IsNullOrEmpty(description))
        {
            sb.Append("<p>");
            sb.Append(EscapeHtmlPreservingLines(description));
            sb.Append("</p>");
        }

        if (participants.Count > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(RenderBlock(participants));
        }

        return sb.ToString();
    }

    public string MergeIntoExistingBody(string existingBodyHtml, IReadOnlyList<ParticipantRecord> participants)
    {
        if (participants == null) throw new ArgumentNullException(nameof(participants));
        existingBodyHtml ??= "";

        var newBlock = participants.Count > 0 ? RenderBlock(participants) : "";

        if (BlockPattern.IsMatch(existingBodyHtml))
            return BlockPattern.Replace(existingBodyHtml, _ => newBlock);

        if (newBlock.Length == 0)
            return existingBodyHtml;

        return existingBodyHtml.Length == 0
            ? newBlock
            : existingBodyHtml.TrimEnd() + "\n" + newBlock;
    }

    private static string RenderBlock(IReadOnlyList<ParticipantRecord> participants)
    {
        var sb = new StringBuilder();
        // Header lives inside the markers so MergeIntoExistingBody can strip the
        // entire block (header included) when no participants are present.
        sb.Append(StartMarker);
        sb.Append("\n<p><b>Participantes (no invitados, solo referencia):</b></p>\n");
        sb.Append("<ul>\n");
        foreach (var p in participants)
        {
            sb.Append("  <li>");
            sb.Append(WebUtility.HtmlEncode(p.Name));
            if (!string.IsNullOrEmpty(p.Email))
            {
                sb.Append(" &lt;");
                sb.Append(WebUtility.HtmlEncode(p.Email));
                sb.Append("&gt;");
            }
            if (!string.IsNullOrEmpty(p.Type))
            {
                sb.Append(" — ");
                sb.Append(WebUtility.HtmlEncode(p.Type));
            }
            if (!string.IsNullOrEmpty(p.Response))
            {
                sb.Append(" — ");
                sb.Append(WebUtility.HtmlEncode(p.Response));
            }
            sb.Append("</li>\n");
        }
        sb.Append("</ul>\n");
        sb.Append(EndMarker);
        return sb.ToString();
    }

    private static string EscapeHtmlPreservingLines(string text)
    {
        var escaped = WebUtility.HtmlEncode(text);
        return escaped.Replace("\r\n", "<br>\n").Replace("\n", "<br>\n");
    }
}
