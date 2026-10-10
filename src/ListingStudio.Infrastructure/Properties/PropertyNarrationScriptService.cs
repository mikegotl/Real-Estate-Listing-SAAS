using System.Data;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Properties;

public sealed partial class PropertyNarrationScriptService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage storage,
    TimeProvider timeProvider,
    ILogger<PropertyNarrationScriptService> logger) : IPropertyNarrationScriptService
{
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public async Task<PropertyNarrationScriptItem?> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var script = await dbContext.PropertyNarrationScripts
            .AsNoTracking()
            .Where(script => script.OrganizationId == organizationId && script.PropertyId == propertyId)
            .SingleOrDefaultAsync(cancellationToken);
        return script is null ? null : ToItem(script);
    }

    public async Task<Guid> UploadAsync(
        string userId,
        Guid propertyId,
        PropertyNarrationScriptUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(upload.Content);
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var validated = await ValidateAsync(upload, cancellationToken);
        var uploadId = Guid.NewGuid();
        var blobPath =
            $"organizations/{organizationId:N}/properties/{propertyId:N}/narration-scripts/{uploadId:N}{validated.Extension}";
        string? previousBlobPath = null;
        var stored = false;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            var propertyExists = await dbContext.Properties
                .FromSqlInterpolated($"SELECT * FROM \"Properties\" WHERE \"Id\" = {propertyId} AND \"OrganizationId\" = {organizationId} FOR UPDATE")
                .AnyAsync(property => property.ArchivedAtUtc == null, cancellationToken);
            if (!propertyExists)
            {
                throw new InvalidOperationException("The property is unavailable for narration-script uploads.");
            }

            var script = await dbContext.PropertyNarrationScripts.SingleOrDefaultAsync(candidate =>
                candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId,
                cancellationToken);
            validated.Content.Position = 0;
            await storage.StoreAsync(blobPath, validated.Content, validated.ContentType, cancellationToken);
            stored = true;
            var now = timeProvider.GetUtcNow();
            if (script is null)
            {
                script = PropertyNarrationScript.Create(
                    organizationId,
                    propertyId,
                    blobPath,
                    validated.Filename,
                    validated.ContentType,
                    validated.Content.Length,
                    validated.ExtractedText,
                    now);
                dbContext.PropertyNarrationScripts.Add(script);
            }
            else
            {
                previousBlobPath = script.BlobPath;
                script.Replace(
                    blobPath,
                    validated.Filename,
                    validated.ContentType,
                    validated.Content.Length,
                    validated.ExtractedText,
                    now);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (previousBlobPath is not null)
            {
                await DeleteOrLogAsync(previousBlobPath);
            }

            return script.Id;
        }
        catch (Exception uploadError)
        {
            if (stored)
            {
                try
                {
                    await storage.DeleteAsync(blobPath, CancellationToken.None);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(
                        "Narration-script persistence failed and its stored file could not be cleaned up.",
                        uploadError,
                        cleanupError);
                }
            }

            throw;
        }
        finally
        {
            await validated.Content.DisposeAsync();
        }
    }

    public async Task<bool> SetMarketingUseAcceptedAsync(
        string userId,
        Guid propertyId,
        Guid scriptId,
        bool accepted,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var script = await dbContext.PropertyNarrationScripts.SingleOrDefaultAsync(candidate =>
            candidate.Id == scriptId
            && candidate.PropertyId == propertyId
            && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (script is null)
        {
            return false;
        }

        if (accepted)
        {
            var wordCount = NarrationScriptPolicy.CountWords(script.ExtractedText);
            if (wordCount < 5)
            {
                throw new InvalidOperationException("The narration script must contain at least five words before marketing use can be accepted.");
            }

            if (wordCount > IPropertyNarrationScriptService.MaximumAcceptedWords)
            {
                throw new InvalidOperationException(
                    $"The narration script has {wordCount} words. Long-form narration supports up to {IPropertyNarrationScriptService.MaximumAcceptedWords:N0} words.");
            }
        }

        script.SetMarketingUseAccepted(accepted, userId, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<PropertyNarrationScriptContent?> OpenReadAsync(
        string userId,
        Guid scriptId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var script = await dbContext.PropertyNarrationScripts.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.Id == scriptId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (script is null)
        {
            return null;
        }

        var content = await storage.OpenReadAsync(script.BlobPath, cancellationToken);
        return content is null
            ? null
            : new PropertyNarrationScriptContent(content, script.ContentType, script.OriginalFilename);
    }

    public async Task<bool> DeleteAsync(
        string userId,
        Guid propertyId,
        Guid scriptId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var script = await dbContext.PropertyNarrationScripts.SingleOrDefaultAsync(candidate =>
            candidate.Id == scriptId
            && candidate.PropertyId == propertyId
            && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (script is null)
        {
            return false;
        }

        var blobPath = script.BlobPath;
        dbContext.PropertyNarrationScripts.Remove(script);
        await dbContext.SaveChangesAsync(cancellationToken);
        await DeleteOrLogAsync(blobPath);
        return true;
    }

    private async Task DeleteOrLogAsync(string blobPath)
    {
        try
        {
            await storage.DeleteAsync(blobPath, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogCleanupFailure(exception, blobPath);
        }
    }

    private static async Task<ValidatedScript> ValidateAsync(
        PropertyNarrationScriptUpload upload,
        CancellationToken cancellationToken)
    {
        var filename = Path.GetFileName(upload.OriginalFilename).Trim();
        if (filename.Length is 0 or > 255)
        {
            throw new InvalidDataException("The script filename is required and cannot exceed 255 characters.");
        }

        if (upload.FileSize <= 0 || upload.FileSize > IPropertyNarrationScriptService.MaximumFileSize)
        {
            throw new InvalidDataException(
                $"Narration scripts must be between 1 byte and {IPropertyNarrationScriptService.MaximumFileSize / 1024 / 1024} MB.");
        }

        var extension = Path.GetExtension(filename).ToLowerInvariant();
        var contentType = extension switch
        {
            ".txt" when string.Equals(upload.ContentType, "text/plain", StringComparison.OrdinalIgnoreCase) => "text/plain",
            ".md" when string.Equals(upload.ContentType, "text/markdown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(upload.ContentType, "text/plain", StringComparison.OrdinalIgnoreCase) => "text/markdown",
            ".docx" when string.Equals(upload.ContentType, DocxContentType, StringComparison.OrdinalIgnoreCase) => DocxContentType,
            _ => throw new InvalidDataException("Only TXT, Markdown, and DOCX narration scripts with matching file types are allowed."),
        };

        var content = new MemoryStream((int)upload.FileSize);
        try
        {
            var buffer = new byte[81_920];
            while (content.Length <= upload.FileSize)
            {
                var remaining = upload.FileSize - content.Length + 1;
                var bytesRead = await upload.Content.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                await content.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            if (content.Length != upload.FileSize || content.Length > IPropertyNarrationScriptService.MaximumFileSize)
            {
                throw new InvalidDataException("The uploaded script size did not match the declared file size.");
            }

            content.Position = 0;
            var extracted = extension == ".docx"
                ? ExtractDocx(content)
                : ExtractUtf8(content);
            extracted = NormalizeText(extracted);
            if (extracted.Length == 0)
            {
                throw new InvalidDataException("The uploaded document does not contain readable narration text.");
            }

            if (extracted.Length > IPropertyNarrationScriptService.MaximumExtractedCharacters)
            {
                throw new InvalidDataException(
                    $"The extracted narration cannot exceed {IPropertyNarrationScriptService.MaximumExtractedCharacters:N0} characters.");
            }

            content.Position = 0;
            return new ValidatedScript(filename, extension, contentType, extracted, content);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
    }

    private static string ExtractUtf8(Stream content)
    {
        content.Position = 0;
        try
        {
            using var reader = new StreamReader(
                content,
                new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("TXT and Markdown narration scripts must use UTF-8 text.", exception);
        }
    }

    private static string ExtractDocx(Stream content)
    {
        content.Position = 0;
        try
        {
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.GetEntry("word/document.xml")
                ?? throw new InvalidDataException("The DOCX file is missing its main document content.");
            if (entry.Length > 5 * 1024 * 1024)
            {
                throw new InvalidDataException("The DOCX document content is too large.");
            }

            using var entryStream = entry.Open();
            using var xmlReader = XmlReader.Create(entryStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 5 * 1024 * 1024,
            });
            var document = XDocument.Load(xmlReader, LoadOptions.None);
            XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            return string.Join('\n', document
                .Descendants(word + "p")
                .Select(paragraph => string.Concat(paragraph.Descendants(word + "t").Select(text => text.Value))));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or IOException)
        {
            throw new InvalidDataException("The DOCX narration script is invalid or unreadable.", exception);
        }
    }

    private static string NormalizeText(string value)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(line => line.Length > 0);
        return string.Join('\n', lines).Trim();
    }

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        return organizationId ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }

    private static PropertyNarrationScriptItem ToItem(PropertyNarrationScript script) => new(
        script.Id,
        script.OriginalFilename,
        script.ContentType,
        script.FileSize,
        script.ExtractedText,
        NarrationScriptPolicy.CountWords(script.ExtractedText),
        script.UploadedAtUtc,
        script.MarketingUseAccepted,
        script.MarketingUseAcceptedAtUtc);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A superseded narration-script asset could not be deleted from {BlobPath}")]
    private partial void LogCleanupFailure(Exception exception, string blobPath);

    private sealed record ValidatedScript(
        string Filename,
        string Extension,
        string ContentType,
        string ExtractedText,
        MemoryStream Content);
}
