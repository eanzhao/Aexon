using System.Text.Json;
using Aexon.Core.Compaction;
using Aexon.Core.Messages;
using Aexon.Core.Query;

namespace Aexon.Core.Storage;

/// <summary>
/// Represents transcript session.
/// </summary>
public sealed class TranscriptSession
{
    public required string SessionId { get; init; }
    public required string SessionDirectory { get; init; }
    public required string TranscriptPath { get; init; }
    public required string ManifestPath { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public required string WorkingDirectory { get; set; }
    public required string Model { get; set; }
    public string? Provider { get; set; }
    public ConversationSessionMetadata Metadata { get; set; } = new();
    public string? CurrentLeafMessageId { get; set; }
}

/// <summary>
/// Represents transcript metadata entry.
/// </summary>
public sealed record TranscriptMetadataEntry(
    string EventType,
    JsonElement? Payload = null,
    DateTimeOffset? RecordedAt = null);

/// <summary>
/// Represents stored transcript message.
/// </summary>
public sealed record StoredTranscriptMessage(
    ConversationMessage Message,
    string? ParentMessageId,
    long Sequence);

/// <summary>
/// Represents transcript projection.
/// </summary>
public sealed class TranscriptProjection
{
    public required TranscriptSession Session { get; init; }
    public required IReadOnlyDictionary<string, StoredTranscriptMessage> MessagesById { get; init; }
    public required IReadOnlyList<TranscriptMetadataEntry> MetadataEntries { get; init; }
}

/// <summary>
/// Represents options for transcript load.
/// </summary>
public sealed record TranscriptLoadOptions;

/// <summary>
/// Defines the contract for transcript store.
/// </summary>
public interface ITranscriptStore
{
    Task<TranscriptSession> CreateSessionAsync(
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TranscriptSession>> ListSessionsAsync(
        CancellationToken cancellationToken = default);

    Task<TranscriptSession?> GetLatestSessionAsync(
        CancellationToken cancellationToken = default);

    Task<TranscriptSession?> FindSessionAsync(
        string sessionIdOrPath,
        CancellationToken cancellationToken = default);

    Task AppendMessageAsync(
        TranscriptSession session,
        ConversationMessage message,
        string? parentMessageId,
        CancellationToken cancellationToken = default);

    Task AppendMetadataAsync(
        TranscriptSession session,
        TranscriptMetadataEntry entry,
        CancellationToken cancellationToken = default);

    Task UpdateSessionAsync(
        TranscriptSession session,
        CancellationToken cancellationToken = default);

    Task<TranscriptProjection> LoadProjectionAsync(
        TranscriptSession session,
        TranscriptLoadOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Defines the contract for conversation journal.
/// </summary>
public interface IConversationJournal
{
    string SessionId { get; }
    string TranscriptPath { get; }
    ConversationSessionMetadata Metadata { get; }

    Task AppendMessageAsync(
        ConversationMessage message,
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default);

    Task UpdateSessionInfoAsync(
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default);

    Task UpdateMetadataAsync(
        Action<ConversationSessionMetadata> update,
        CancellationToken cancellationToken = default);

    Task AppendMetadataEntryAsync(
        TranscriptMetadataEntry entry,
        CancellationToken cancellationToken = default);

    Task SeedAsync(
        IReadOnlyList<ConversationMessage> messages,
        ConversationSessionMetadata metadata,
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default);

    Task RecordConversationCheckpointAsync(
        ConversationMessage summaryMessage,
        IReadOnlyList<ConversationMessage> activeMessages,
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default);

    Task RecordMicrocompactAsync(
        IReadOnlyList<MicrocompactEdit> edits,
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default);

    Task DeleteMessageAsync(
        string messageId,
        string workingDirectory,
        string model,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task ResetHeadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents conversation journal.
/// </summary>
public sealed class ConversationJournal : IConversationJournal
{
    private readonly ITranscriptStore _store;
    private readonly TranscriptSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConversationJournal(ITranscriptStore store, TranscriptSession session)
    {
        _store = store;
        _session = session;
    }

    public string SessionId => _session.SessionId;

    public string TranscriptPath => _session.TranscriptPath;

    public ConversationSessionMetadata Metadata => _session.Metadata.Clone();

    public async Task AppendMessageAsync(
        ConversationMessage message,
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _session.WorkingDirectory = workingDirectory;
            _session.Model = model;

            await _store.AppendMessageAsync(
                _session,
                message,
                _session.CurrentLeafMessageId,
                cancellationToken);

            _session.CurrentLeafMessageId = message.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetHeadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _store.AppendMetadataAsync(
                _session,
                new TranscriptMetadataEntry("reset-head"),
                cancellationToken);

            _session.CurrentLeafMessageId = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateSessionInfoAsync(
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _session.WorkingDirectory = workingDirectory;
            _session.Model = model;
            _session.UpdatedAt = DateTimeOffset.UtcNow;
            await _store.UpdateSessionAsync(_session, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateMetadataAsync(
        Action<ConversationSessionMetadata> update,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var before = _session.Metadata.Clone();
            update(_session.Metadata);

            foreach (var entry in BuildMetadataDelta(before, _session.Metadata))
                await _store.AppendMetadataAsync(_session, entry, cancellationToken);

            await _store.UpdateSessionAsync(_session, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendMetadataEntryAsync(
        TranscriptMetadataEntry entry,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _store.AppendMetadataAsync(_session, entry, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SeedAsync(
        IReadOnlyList<ConversationMessage> messages,
        ConversationSessionMetadata metadata,
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _session.WorkingDirectory = workingDirectory;
            _session.Model = model;

            foreach (var message in messages)
            {
                await _store.AppendMessageAsync(
                    _session,
                    message,
                    _session.CurrentLeafMessageId,
                    cancellationToken);

                _session.CurrentLeafMessageId = message.Id;
            }

            var before = new ConversationSessionMetadata();
            _session.Metadata = metadata.Clone();
            foreach (var entry in BuildMetadataDelta(before, _session.Metadata))
                await _store.AppendMetadataAsync(_session, entry, cancellationToken);

            await _store.UpdateSessionAsync(_session, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordConversationCheckpointAsync(
        ConversationMessage summaryMessage,
        IReadOnlyList<ConversationMessage> activeMessages,
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default)
    {
        if (activeMessages.Count == 0)
            throw new ArgumentException("Active message list cannot be empty.", nameof(activeMessages));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _session.WorkingDirectory = workingDirectory;
            _session.Model = model;

            await _store.AppendMessageAsync(
                _session,
                summaryMessage,
                _session.CurrentLeafMessageId,
                cancellationToken);

            var checkpoint = new ConversationCheckpoint
            {
                ActiveMessageIds = activeMessages.Select(message => message.Id).ToArray(),
            };

            await _store.AppendMetadataAsync(
                _session,
                new TranscriptMetadataEntry(
                    "conversation-checkpoint",
                    checkpoint.ToMetadataPayload(),
                    DateTimeOffset.UtcNow),
                cancellationToken);

            _session.CurrentLeafMessageId = activeMessages[^1].Id;
            await _store.UpdateSessionAsync(_session, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteMessageAsync(
        string messageId,
        string workingDirectory,
        string model,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _session.WorkingDirectory = workingDirectory;
            _session.Model = model;

            var tombstone = new TombstoneMessage
            {
                DeletedMessageId = messageId,
                Reason = reason,
            };

            await _store.AppendMessageAsync(
                _session,
                tombstone,
                _session.CurrentLeafMessageId,
                cancellationToken);

            _session.CurrentLeafMessageId = tombstone.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordMicrocompactAsync(
        IReadOnlyList<MicrocompactEdit> edits,
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default)
    {
        if (edits.Count == 0)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _session.WorkingDirectory = workingDirectory;
            _session.Model = model;

            var record = new MicrocompactRecord
            {
                Edits = edits,
            };

            await _store.AppendMetadataAsync(
                _session,
                new TranscriptMetadataEntry(
                    MicrocompactRecord.EventType,
                    record.ToMetadataPayload(),
                    DateTimeOffset.UtcNow),
                cancellationToken);

            await _store.UpdateSessionAsync(_session, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IEnumerable<TranscriptMetadataEntry> BuildMetadataDelta(
        ConversationSessionMetadata before,
        ConversationSessionMetadata after)
    {
        if (!string.Equals(before.Title, after.Title, StringComparison.Ordinal))
        {
            yield return new TranscriptMetadataEntry(
                "custom-title",
                after.Title is null
                    ? null
                    : JsonSerializer.SerializeToElement(new { title = after.Title }),
                DateTimeOffset.UtcNow);
        }

        if (before.Mode != after.Mode)
        {
            yield return new TranscriptMetadataEntry(
                "mode",
                after.Mode is null
                    ? null
                    : JsonSerializer.SerializeToElement(new { mode = after.Mode.ToString() }),
                DateTimeOffset.UtcNow);
        }

        if (before.Effort != after.Effort)
        {
            yield return new TranscriptMetadataEntry(
                "effort",
                after.Effort is null
                    ? null
                    : JsonSerializer.SerializeToElement(new { effort = after.Effort.ToString() }),
                DateTimeOffset.UtcNow);
        }

        foreach (var removedTag in before.Tags.Except(after.Tags, StringComparer.OrdinalIgnoreCase))
        {
            yield return new TranscriptMetadataEntry(
                "tag-remove",
                JsonSerializer.SerializeToElement(new { tag = removedTag }),
                DateTimeOffset.UtcNow);
        }

        foreach (var addedTag in after.Tags.Except(before.Tags, StringComparer.OrdinalIgnoreCase))
        {
            yield return new TranscriptMetadataEntry(
                "tag-add",
                JsonSerializer.SerializeToElement(new { tag = addedTag }),
                DateTimeOffset.UtcNow);
        }

        if (before.AwayEnteredAt != after.AwayEnteredAt)
        {
            if (after.AwayEnteredAt.HasValue)
            {
                yield return new TranscriptMetadataEntry(
                    "away-enter",
                    JsonSerializer.SerializeToElement(new
                    {
                        trigger = after.AwayTriggerReason ?? string.Empty,
                        entered_at = after.AwayEnteredAt.Value,
                    }),
                    DateTimeOffset.UtcNow);
            }
            else
            {
                yield return new TranscriptMetadataEntry(
                    "away-exit",
                    JsonSerializer.SerializeToElement(new
                    {
                        exited_at = DateTimeOffset.UtcNow,
                    }),
                    DateTimeOffset.UtcNow);
            }
        }

        foreach (var removedId in before.Attachments.Keys.Except(after.Attachments.Keys, StringComparer.Ordinal))
        {
            yield return new TranscriptMetadataEntry(
                "attachment-remove",
                JsonSerializer.SerializeToElement(new { attachmentId = removedId }),
                DateTimeOffset.UtcNow);
        }

        foreach (var addedId in after.Attachments.Keys.Except(before.Attachments.Keys, StringComparer.Ordinal))
        {
            var a = after.Attachments[addedId];
            yield return new TranscriptMetadataEntry(
                "attachment-add",
                JsonSerializer.SerializeToElement(new
                {
                    id = a.Id,
                    fileName = a.FileName,
                    mimeType = a.MimeType,
                    sizeBytes = a.SizeBytes,
                    source = a.Source.ToString(),
                    sourcePath = a.SourcePath,
                }),
                DateTimeOffset.UtcNow);
        }
    }
}

/// <summary>
/// Provides jsonl transcript store.
/// </summary>
public sealed class JsonlTranscriptStore : ITranscriptStore
{
    private const string SessionsDirectoryName = "sessions";
    private const string TranscriptFileName = "transcript.jsonl";
    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _rootDirectory;

    public JsonlTranscriptStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? GetDefaultRootDirectory();
        Directory.CreateDirectory(GetSessionsDirectory());
    }

    public async Task<TranscriptSession> CreateSessionAsync(
        string workingDirectory,
        string model,
        CancellationToken cancellationToken = default)
    {
        var sessionId = CreateSessionId();
        var sessionDirectory = Path.Combine(GetSessionsDirectory(), sessionId);
        Directory.CreateDirectory(sessionDirectory);

        var transcriptPath = Path.Combine(sessionDirectory, TranscriptFileName);
        var manifestPath = Path.Combine(sessionDirectory, ManifestFileName);
        await using (File.Create(transcriptPath))
        {
        }

        var now = DateTimeOffset.UtcNow;
        var session = new TranscriptSession
        {
            SessionId = sessionId,
            SessionDirectory = sessionDirectory,
            TranscriptPath = transcriptPath,
            ManifestPath = manifestPath,
            CreatedAt = now,
            UpdatedAt = now,
            WorkingDirectory = workingDirectory,
            Model = model,
            Metadata = new ConversationSessionMetadata(),
            CurrentLeafMessageId = null,
        };

        await SaveManifestAsync(session, cancellationToken);
        return session;
    }

    public async Task<TranscriptSession?> GetLatestSessionAsync(
        CancellationToken cancellationToken = default)
    {
        return (await ListSessionsAsync(cancellationToken))
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<TranscriptSession>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var sessionsDirectory = GetSessionsDirectory();
        if (!Directory.Exists(sessionsDirectory))
            return [];

        var sessions = new List<TranscriptSession>();
        foreach (var manifestPath in Directory.EnumerateFiles(
                     sessionsDirectory,
                     ManifestFileName,
                     SearchOption.AllDirectories))
        {
            var session = await LoadManifestAsync(manifestPath, cancellationToken);
            if (session != null)
                sessions.Add(session);
        }

        return sessions
            .OrderByDescending(session => session.UpdatedAt)
            .ThenByDescending(session => session.CreatedAt)
            .ToArray();
    }

    public async Task<TranscriptSession?> FindSessionAsync(
        string sessionIdOrPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionIdOrPath))
            return null;

        var candidate = sessionIdOrPath.Trim();

        if (Directory.Exists(candidate))
        {
            var manifestPath = Path.Combine(candidate, ManifestFileName);
            return await LoadManifestAsync(manifestPath, cancellationToken);
        }

        if (File.Exists(candidate))
        {
            if (Path.GetFileName(candidate).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                return await LoadManifestAsync(candidate, cancellationToken);

            if (Path.GetFileName(candidate).Equals(TranscriptFileName, StringComparison.OrdinalIgnoreCase))
                return await LoadManifestAsync(
                    Path.Combine(Path.GetDirectoryName(candidate)!, ManifestFileName),
                    cancellationToken);
        }

        var sessionDirectory = Path.Combine(GetSessionsDirectory(), candidate);
        return await LoadManifestAsync(
            Path.Combine(sessionDirectory, ManifestFileName),
            cancellationToken);
    }

    public async Task AppendMessageAsync(
        TranscriptSession session,
        ConversationMessage message,
        string? parentMessageId,
        CancellationToken cancellationToken = default)
    {
        var record = new TranscriptRecord
        {
            RecordType = "message",
            ParentMessageId = parentMessageId,
            RecordedAt = DateTimeOffset.UtcNow,
            Message = PersistedConversationMessage.FromDomain(message),
        };

        await AppendRecordAsync(session.TranscriptPath, record, cancellationToken);
        session.CurrentLeafMessageId = message.Id;
        session.UpdatedAt = record.RecordedAt;
        await SaveManifestAsync(session, cancellationToken);
    }

    public async Task AppendMetadataAsync(
        TranscriptSession session,
        TranscriptMetadataEntry entry,
        CancellationToken cancellationToken = default)
    {
        var record = new TranscriptRecord
        {
            RecordType = "metadata",
            RecordedAt = entry.RecordedAt ?? DateTimeOffset.UtcNow,
            Metadata = entry,
        };

        if (string.Equals(entry.EventType, "reset-head", StringComparison.OrdinalIgnoreCase))
            session.CurrentLeafMessageId = null;
        else
            ApplyMetadataEntry(session.Metadata, entry);

        await AppendRecordAsync(session.TranscriptPath, record, cancellationToken);
        session.UpdatedAt = record.RecordedAt;
        await SaveManifestAsync(session, cancellationToken);
    }

    public async Task UpdateSessionAsync(
        TranscriptSession session,
        CancellationToken cancellationToken = default)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveManifestAsync(session, cancellationToken);
    }

    public async Task<TranscriptProjection> LoadProjectionAsync(
        TranscriptSession session,
        TranscriptLoadOptions options,
        CancellationToken cancellationToken = default)
    {
        var messages = new Dictionary<string, StoredTranscriptMessage>(StringComparer.Ordinal);
        var metadata = new List<TranscriptMetadataEntry>();
        long sequence = 0;
        var metadataProjection = session.Metadata.Clone();

        if (File.Exists(session.TranscriptPath))
        {
            using var stream = File.OpenRead(session.TranscriptPath);
            using var reader = new StreamReader(stream);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                TranscriptRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<TranscriptRecord>(line, SerializerOptions);
                }
                catch
                {
                    continue;
                }

                if (record == null)
                    continue;

                switch (record.RecordType)
                {
                    case "message" when record.Message != null:
                        var domainMessage = record.Message.ToDomain();
                        messages[domainMessage.Id] = new StoredTranscriptMessage(
                            domainMessage,
                            record.ParentMessageId,
                            ++sequence);
                        break;

                    case "metadata" when record.Metadata != null:
                        metadata.Add(record.Metadata);
                        ApplyMetadataEntry(metadataProjection, record.Metadata);
                        break;
                }
            }
        }

        session.Metadata = metadataProjection;

        return new TranscriptProjection
        {
            Session = session,
            MessagesById = messages,
            MetadataEntries = metadata,
        };
    }

    private async Task AppendRecordAsync(
        string transcriptPath,
        TranscriptRecord record,
        CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(record, SerializerOptions);
        await File.AppendAllTextAsync(
            transcriptPath,
            line + Environment.NewLine,
            cancellationToken);
    }

    private async Task SaveManifestAsync(
        TranscriptSession session,
        CancellationToken cancellationToken)
    {
        var manifest = new TranscriptManifestRecord
        {
            SessionId = session.SessionId,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            WorkingDirectory = session.WorkingDirectory,
            Model = session.Model,
            Provider = session.Provider,
            Title = session.Metadata.Title,
            Tags = session.Metadata.Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToArray(),
            Mode = session.Metadata.Mode?.ToString(),
            Effort = session.Metadata.Effort?.ToString(),
            CurrentLeafMessageId = session.CurrentLeafMessageId,
            AwayEnteredAt = session.Metadata.AwayEnteredAt,
            AwayTriggerReason = session.Metadata.AwayTriggerReason,
        };

        var json = JsonSerializer.Serialize(manifest, ManifestSerializerOptions);
        await File.WriteAllTextAsync(session.ManifestPath, json, cancellationToken);
    }

    private async Task<TranscriptSession?> LoadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<TranscriptManifestRecord>(json, SerializerOptions);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.SessionId))
                return null;

            var sessionDirectory = Path.GetDirectoryName(manifestPath)!;
            return new TranscriptSession
            {
                SessionId = manifest.SessionId,
                SessionDirectory = sessionDirectory,
                TranscriptPath = Path.Combine(sessionDirectory, TranscriptFileName),
                ManifestPath = manifestPath,
                CreatedAt = manifest.CreatedAt,
                UpdatedAt = manifest.UpdatedAt,
                WorkingDirectory = manifest.WorkingDirectory,
                Model = manifest.Model,
                Provider = manifest.Provider,
                Metadata = CreateMetadataFromManifest(manifest),
                CurrentLeafMessageId = manifest.CurrentLeafMessageId,
            };
        }
        catch
        {
            return null;
        }
    }

    private string GetSessionsDirectory() => Path.Combine(_rootDirectory, SessionsDirectoryName);

    private static string CreateSessionId() => Guid.NewGuid().ToString("N");

    private static string GetDefaultRootDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.CurrentDirectory;

        return Path.Combine(home, ".aexon");
    }

    private static ConversationSessionMetadata CreateMetadataFromManifest(
        TranscriptManifestRecord manifest)
    {
        var metadata = new ConversationSessionMetadata
        {
            Title = manifest.Title,
            Mode = ParsePermissionMode(manifest.Mode),
            Effort = ParseEffort(manifest.Effort),
            AwayEnteredAt = manifest.AwayEnteredAt,
            AwayTriggerReason = manifest.AwayTriggerReason,
        };

        foreach (var tag in manifest.Tags ?? [])
            metadata.Tags.Add(tag);

        return metadata;
    }

    private static void ApplyMetadataEntry(
        ConversationSessionMetadata metadata,
        TranscriptMetadataEntry entry)
    {
        switch (entry.EventType)
        {
            case "custom-title":
                metadata.Title = TryReadString(entry.Payload, "title");
                break;

            case "mode":
                metadata.Mode = ParsePermissionMode(TryReadString(entry.Payload, "mode"));
                break;

            case "effort":
                metadata.Effort = ParseEffort(TryReadString(entry.Payload, "effort"));
                break;

            case "tag-add":
                {
                    var tag = TryReadString(entry.Payload, "tag");
                    if (!string.IsNullOrWhiteSpace(tag))
                        metadata.Tags.Add(tag);
                    break;
                }

            case "tag-remove":
                {
                    var tag = TryReadString(entry.Payload, "tag");
                    if (!string.IsNullOrWhiteSpace(tag))
                        metadata.Tags.Remove(tag);
                    break;
                }

            case "away-enter":
                {
                    var enteredAtStr = TryReadString(entry.Payload, "entered_at");
                    metadata.AwayEnteredAt = DateTimeOffset.TryParse(enteredAtStr, out var parsed)
                        ? parsed
                        : entry.RecordedAt ?? DateTimeOffset.UtcNow;
                    metadata.AwayTriggerReason = TryReadString(entry.Payload, "trigger");
                    break;
                }

            case "away-exit":
                metadata.AwayEnteredAt = null;
                metadata.AwayTriggerReason = null;
                break;

            case "attachment-add":
                {
                    var attachment = TryDeserializeAttachment(entry.Payload);
                    if (attachment != null)
                        metadata.Attachments[attachment.Id] = attachment;
                    break;
                }

            case "attachment-remove":
                {
                    var id = TryReadString(entry.Payload, "attachmentId");
                    if (!string.IsNullOrWhiteSpace(id))
                        metadata.Attachments.Remove(id);
                    break;
                }
        }
    }

    private static string? TryReadString(JsonElement? payload, string propertyName)
    {
        if (payload is not JsonElement element ||
            element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static Attachment? TryDeserializeAttachment(JsonElement? payload)
    {
        if (payload is not JsonElement element || element.ValueKind != JsonValueKind.Object)
            return null;

        var id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var fileName = element.TryGetProperty("fileName", out var fnProp) ? fnProp.GetString() : null;
        var mimeType = element.TryGetProperty("mimeType", out var mtProp) ? mtProp.GetString() : null;
        var sizeBytes = element.TryGetProperty("sizeBytes", out var sbProp) && sbProp.TryGetInt64(out var sz) ? sz : 0L;
        var sourceStr = element.TryGetProperty("source", out var srcProp) ? srcProp.GetString() : null;
        var sourcePath = element.TryGetProperty("sourcePath", out var spProp) ? spProp.GetString() : null;

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fileName))
            return null;

        _ = Enum.TryParse<AttachmentSource>(sourceStr, true, out var source);

        return new Attachment
        {
            Id = id,
            FileName = fileName,
            MimeType = mimeType ?? "application/octet-stream",
            SizeBytes = sizeBytes,
            Source = source,
            SourcePath = sourcePath,
        };
    }

    private static Permissions.PermissionMode? ParsePermissionMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return null;

        return Enum.TryParse<Permissions.PermissionMode>(mode, true, out var parsed)
            ? parsed
            : null;
    }

    private static QueryEffortLevel? ParseEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return null;

        return Enum.TryParse<QueryEffortLevel>(effort, true, out var parsed)
            ? parsed
            : null;
    }

    private sealed class TranscriptRecord
    {
        public required string RecordType { get; init; }
        public string? ParentMessageId { get; init; }
        public DateTimeOffset RecordedAt { get; init; }
        public PersistedConversationMessage? Message { get; init; }
        public TranscriptMetadataEntry? Metadata { get; init; }
    }

    private sealed class TranscriptManifestRecord
    {
        public required string SessionId { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
        public required string WorkingDirectory { get; init; }
        public required string Model { get; init; }
        public string? Provider { get; init; }
        public string? Title { get; init; }
        public string[]? Tags { get; init; }
        public string? Mode { get; init; }
        public string? Effort { get; init; }
        public string? CurrentLeafMessageId { get; init; }
        public DateTimeOffset? AwayEnteredAt { get; init; }
        public string? AwayTriggerReason { get; init; }
    }

    private sealed class PersistedConversationMessage
    {
        public required string Kind { get; init; }
        public required string Id { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public List<PersistedContentBlock>? Content { get; init; }
        public string? ToolUseResult { get; init; }
        public bool? IsMeta { get; init; }
        public string? StopReason { get; init; }
        public TokenUsage? Usage { get; init; }
        public string? ApiError { get; init; }
        public string? SystemContent { get; init; }
        public string? Subtype { get; init; }
        public DateTimeOffset? AwayEnteredAt { get; init; }
        public DateTimeOffset? AwayExitedAt { get; init; }
        public string? TriggerReason { get; init; }
        public string? SummaryText { get; init; }
        public string? DeletedMessageId { get; init; }
        public string? Reason { get; init; }
        public JsonElement? SystemData { get; init; }

        public static PersistedConversationMessage FromDomain(ConversationMessage message) =>
            message switch
            {
                UserMessage user => new PersistedConversationMessage
                {
                    Kind = "user",
                    Id = user.Id,
                    Timestamp = user.Timestamp,
                    Content = user.Content.Select(PersistedContentBlock.FromDomain).ToList(),
                    ToolUseResult = user.ToolUseResult,
                    IsMeta = user.IsMeta,
                },
                AssistantMessage assistant => new PersistedConversationMessage
                {
                    Kind = "assistant",
                    Id = assistant.Id,
                    Timestamp = assistant.Timestamp,
                    Content = assistant.Content.Select(PersistedContentBlock.FromDomain).ToList(),
                    StopReason = assistant.StopReason,
                    Usage = assistant.Usage,
                    ApiError = assistant.ApiError,
                },
                SystemMessage system => new PersistedConversationMessage
                {
                    Kind = "system",
                    Id = system.Id,
                    Timestamp = system.Timestamp,
                    SystemContent = system.Content,
                    Subtype = system.Subtype,
                    SystemData = StructuredSystemMessageCodec.Serialize(system) is { } payload
                        ? payload.Clone()
                        : null,
                },
                SystemAwaySummaryMessage away => new PersistedConversationMessage
                {
                    Kind = "system_away_summary",
                    Id = away.Id,
                    Timestamp = away.Timestamp,
                    AwayEnteredAt = away.AwayEnteredAt,
                    AwayExitedAt = away.AwayExitedAt,
                    TriggerReason = away.TriggerReason,
                    SummaryText = away.SummaryText,
                },
                TombstoneMessage tombstone => new PersistedConversationMessage
                {
                    Kind = "tombstone",
                    Id = tombstone.Id,
                    Timestamp = tombstone.Timestamp,
                    DeletedMessageId = tombstone.DeletedMessageId,
                    Reason = tombstone.Reason,
                },
                _ => throw new InvalidOperationException($"Unsupported message type: {message.GetType().Name}"),
            };

        public ConversationMessage ToDomain() =>
            Kind switch
            {
                "user" => new UserMessage
                {
                    Id = Id,
                    Timestamp = Timestamp,
                    Content = (Content ?? []).Select(block => block.ToDomain()).ToList(),
                    ToolUseResult = ToolUseResult,
                    IsMeta = IsMeta ?? false,
                },
                "assistant" => new AssistantMessage
                {
                    Id = Id,
                    Timestamp = Timestamp,
                    Content = (Content ?? []).Select(block => block.ToDomain()).ToList(),
                    StopReason = StopReason,
                    Usage = Usage,
                    ApiError = ApiError,
                },
                "system" => StructuredSystemMessageCodec.Deserialize(
                    Id,
                    Timestamp,
                    SystemContent ?? string.Empty,
                    Subtype,
                    SystemData is { } payload ? payload.Clone() : null),
                "system_away_summary" => new SystemAwaySummaryMessage
                {
                    Id = Id,
                    Timestamp = Timestamp,
                    AwayEnteredAt = AwayEnteredAt ?? Timestamp,
                    AwayExitedAt = AwayExitedAt ?? Timestamp,
                    TriggerReason = TriggerReason ?? string.Empty,
                    SummaryText = SummaryText ?? string.Empty,
                },
                "tombstone" => new TombstoneMessage
                {
                    Id = Id,
                    Timestamp = Timestamp,
                    DeletedMessageId = DeletedMessageId ?? string.Empty,
                    Reason = Reason,
                },
                _ => throw new InvalidOperationException($"Unsupported persisted message kind: {Kind}"),
            };
    }

    private sealed class PersistedContentBlock
    {
        public required string Type { get; init; }
        public string? Text { get; init; }
        public string? ToolUseId { get; init; }
        public string? Name { get; init; }
        public JsonElement? Input { get; init; }
        public string? Content { get; init; }
        public bool? IsError { get; init; }
        public string? Signature { get; init; }
        public string? AttachmentId { get; init; }
        public string? FileName { get; init; }
        public string? MimeType { get; init; }
        public long? SizeBytes { get; init; }

        public static PersistedContentBlock FromDomain(ContentBlock block) =>
            block switch
            {
                TextBlock text => new PersistedContentBlock
                {
                    Type = "text",
                    Text = text.Text,
                },
                ToolUseBlock toolUse => new PersistedContentBlock
                {
                    Type = "tool_use",
                    ToolUseId = toolUse.ToolUseId,
                    Name = toolUse.Name,
                    Input = toolUse.Input.Clone(),
                },
                ToolResultBlock result => new PersistedContentBlock
                {
                    Type = "tool_result",
                    ToolUseId = result.ToolUseId,
                    Content = result.Content,
                    IsError = result.IsError,
                },
                ThinkingBlock thinking => new PersistedContentBlock
                {
                    Type = "thinking",
                    Text = thinking.Text,
                    Signature = thinking.Signature,
                },
                AttachmentBlock attachment => new PersistedContentBlock
                {
                    Type = "attachment",
                    AttachmentId = attachment.AttachmentId,
                    FileName = attachment.FileName,
                    MimeType = attachment.MimeType,
                    SizeBytes = attachment.SizeBytes,
                },
                _ => throw new InvalidOperationException($"Unsupported content block type: {block.GetType().Name}"),
            };

        public ContentBlock ToDomain() =>
            Type switch
            {
                "text" => new TextBlock(Text ?? string.Empty),
                "tool_use" => new ToolUseBlock
                {
                    ToolUseId = ToolUseId ?? string.Empty,
                    Name = Name ?? string.Empty,
                    Input = (Input ?? JsonDocument.Parse("{}").RootElement).Clone(),
                },
                "tool_result" => new ToolResultBlock(
                    ToolUseId ?? string.Empty,
                    Content ?? string.Empty,
                    IsError ?? false),
                "thinking" => new ThinkingBlock(Text ?? string.Empty, Signature),
                "attachment" => new AttachmentBlock
                {
                    AttachmentId = AttachmentId ?? string.Empty,
                    FileName = FileName ?? string.Empty,
                    MimeType = MimeType ?? string.Empty,
                    SizeBytes = SizeBytes ?? 0,
                },
                _ => throw new InvalidOperationException($"Unsupported persisted content block type: {Type}"),
            };
    }
}
