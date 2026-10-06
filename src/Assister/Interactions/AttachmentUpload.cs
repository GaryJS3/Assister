using Assister.Contracts;

namespace Assister.Interactions;

public static class AttachmentUpload
{
    public static async Task<IResult> ReceiveAsync(HttpContext Http, InteractionStore Store, CancellationToken Token)
    {
        var Name = Http.Request.Query["name"].ToString();
        var Source = Http.Request.Query["source"].ToString();
        if (Source.Length == 0) Source = "user";
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128 || Name.IndexOfAny(['/', '\\', '\r', '\n']) >= 0 || Source.Length > 64)
            return Results.BadRequest(new ProtocolError("invalid_attachment", "Supply a filename of at most 128 characters and a source of at most 64 characters."));
        const int MaximumBytes = 1024 * 1024;
        if (Http.Request.ContentLength > MaximumBytes) return TooLarge();
        var MimeType = Http.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? "";
        if (MimeType is not ("text/plain" or "text/markdown" or "application/json" or "image/png" or "image/jpeg" or "audio/pcm"))
            return Results.BadRequest(new ProtocolError("attachment_type_unsupported", "Supported uploads are UTF-8 text, Markdown, JSON, PNG, JPEG and fixed-format PCM audio."));
        using var Content = new MemoryStream();
        var Buffer = new byte[8192];
        int Read;
        while ((Read = await Http.Request.Body.ReadAsync(Buffer, Token)) > 0)
        {
            if (Content.Length + Read > MaximumBytes) return TooLarge();
            Content.Write(Buffer, 0, Read);
        }
        var Bytes = Content.ToArray();
        if (Bytes.Length == 0) return Results.BadRequest(new ProtocolError("invalid_attachment", "The attachment is empty."));
        string? Text = null;
        if (MimeType == "audio/pcm")
        {
            if (Bytes.Length % 2 != 0) return Results.BadRequest(new ProtocolError("invalid_audio", "PCM must be 16 kHz mono signed 16-bit little-endian samples."));
        }
        else if (MimeType.StartsWith("image/", StringComparison.Ordinal))
        {
            if (MimeType == "image/png" && !Bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || MimeType == "image/jpeg" && !Bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }))
                return Results.BadRequest(new ProtocolError("invalid_attachment", "The image signature does not match its content type."));
        }
        else
        {
            try { Text = new System.Text.UTF8Encoding(false, true).GetString(Bytes).TrimStart('\uFEFF'); }
            catch (System.Text.DecoderFallbackException) { return Results.BadRequest(new ProtocolError("invalid_attachment", "Text attachments must contain valid UTF-8.")); }
            if (Text.Contains('\0')) return Results.BadRequest(new ProtocolError("invalid_attachment", "Binary data is not a text attachment."));
            if (Text.Length > 12000) return Results.BadRequest(new ProtocolError("attachment_context_too_large", "Text attachments are limited to 12,000 characters."));
        }
        try { return Results.Ok(Store.SaveAttachment((string)Http.Items["ClientOwner"]!, Name, MimeType, Source, Bytes, Text, (string)Http.Items["ClientId"]!)); }
        catch (InvalidOperationException Error) { return Results.Conflict(new ProtocolError(Error.Message, "The attachment quota of 100 uploads has been reached.")); }
    }
    private static IResult TooLarge() => Results.Json(new ProtocolError("attachment_too_large", "Attachments are limited to 1 MiB."), statusCode: 413);
}
