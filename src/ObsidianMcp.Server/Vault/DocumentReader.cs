using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;

namespace ObsidianMcp.Server.Vault;

public sealed record AttachmentInfo(string Path, string Name, string Extension, string MimeType, long Size, DateTime Modified, string Sha256);

/// <summary>Reads attachments: metadata, checksum, and text extraction for PDF, DOCX and plain-text formats.</summary>
public sealed class DocumentReader(VaultService vault)
{
    public const long MaxAttachmentBytes = 50 * 1024 * 1024;
    public const long MaxImageBytes = 4 * 1024 * 1024;

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".md"] = "text/markdown", [".txt"] = "text/plain", [".csv"] = "text/csv", [".json"] = "application/json",
        [".html"] = "text/html", [".xml"] = "application/xml",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".txt", ".csv", ".json", ".html", ".xml", ".canvas" };

    public static bool IsImage(string ext) => Mime.TryGetValue(ext, out var m) && m.StartsWith("image/");

    public string Resolve(string reference, string? relativeTo = null) =>
        vault.ResolveAttachment(reference, relativeTo) ?? throw new VaultException($"Attachment not found: '{reference}'.");

    public AttachmentInfo Info(string rel)
    {
        var full = vault.ToFullPath(rel);
        var fi = new FileInfo(full);
        if (!fi.Exists) throw new VaultException($"Attachment not found: '{rel}'.");
        if (fi.Length > MaxAttachmentBytes) throw new VaultException($"'{rel}' is too large ({fi.Length:N0} bytes).");
        string hash;
        using (var s = fi.OpenRead()) hash = Convert.ToHexStringLower(SHA256.HashData(s));
        return new AttachmentInfo(rel, fi.Name, fi.Extension.ToLowerInvariant(),
            Mime.GetValueOrDefault(fi.Extension, "application/octet-stream"), fi.Length, fi.LastWriteTimeUtc, hash);
    }

    public byte[] ReadBytes(string rel) => File.ReadAllBytes(vault.ToFullPath(rel));

    /// <summary>Extracts text; returns null if the format is not supported.</summary>
    public string? ExtractText(string rel)
    {
        var full = vault.ToFullPath(rel);
        var ext = Path.GetExtension(full);
        if (TextExtensions.Contains(ext)) return File.ReadAllText(full);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return Pdf(full);
        if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)) return Docx(full);
        return null;
    }

    private static string Pdf(string full)
    {
        var sb = new StringBuilder();
        try
        {
            using var doc = PdfDocument.Open(full);
            foreach (var page in doc.GetPages())
            {
                sb.Append("\n\n--- Page ").Append(page.Number).Append(" ---\n");
                sb.Append(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page));
            }
        }
        catch (Exception ex) when (ex is not VaultException)
        {
            throw new VaultException($"Could not read PDF: {ex.Message}");
        }
        var text = sb.ToString().Trim();
        return text.Length > 0 ? text : "(No extractable text. The PDF may be a scanned image that needs OCR.)";
    }

    private static string Docx(string full)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(full, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null) return "";
            var sb = new StringBuilder();
            foreach (var p in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
                sb.AppendLine(p.InnerText);
            return sb.ToString().Trim();
        }
        catch (Exception ex) when (ex is not VaultException)
        {
            throw new VaultException($"Could not read DOCX: {ex.Message}");
        }
    }
}
