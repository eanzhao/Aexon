using Aexon.Core.Messages;
using Aexon.Core.Storage;

namespace Aexon.Core.Tests.Storage;

/// <summary>
/// Contains tests for session Resume Loader.
/// </summary>
public sealed class SessionResumeLoaderTests
{
    [Fact]
    public async Task LoadAsync_UsesLatestSessionWhenRequested()
    {
        var session = CreateSession("session-latest");
        var projection = BuildProjection(session, StorageTestData.UserText("user-1", "hello"));
        var store = new StubTranscriptStore
        {
            LatestSession = session,
            Projection = projection,
        };
        var recovery = new StubRecovery();
        var loader = new SessionResumeLoader(store, recovery);

        var result = await loader.LoadAsync(ResumeSource.Latest());

        Assert.NotNull(result);
        Assert.Same(session, result!.Session);
        Assert.Same(projection, store.LastProjectionRequested);
        Assert.Same(projection, recovery.LastProjection);
        Assert.Equal(1, store.GetLatestSessionCalls);
        Assert.Null(store.LastFindRequest);
        Assert.NotNull(recovery.LastResult);
    }

    [Fact]
    public async Task LoadAsync_UsesSpecificSessionAndReturnsNullWhenMissing()
    {
        var store = new StubTranscriptStore();
        var recovery = new StubRecovery();
        var loader = new SessionResumeLoader(store, recovery);

        var missing = await loader.LoadAsync(ResumeSource.Session("/tmp/missing-session"));

        Assert.Null(missing);
        Assert.Equal("/tmp/missing-session", store.LastFindRequest);
        Assert.Null(store.LastProjectionRequested);
        Assert.Null(recovery.LastProjection);
    }

    private static TranscriptSession CreateSession(string sessionId) =>
        new()
        {
            SessionId = sessionId,
            SessionDirectory = $"/tmp/{sessionId}",
            TranscriptPath = $"/tmp/{sessionId}/transcript.jsonl",
            ManifestPath = $"/tmp/{sessionId}/manifest.json",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = "/work/project",
            Model = "sonnet",
            Metadata = new ConversationSessionMetadata(),
        };

    private static TranscriptProjection BuildProjection(
        TranscriptSession session,
        ConversationMessage message)
    {
        return new TranscriptProjection
        {
            Session = session,
            MessagesById =
                new Dictionary<string, StoredTranscriptMessage>(StringComparer.Ordinal)
                {
                    [
                        message.Id
                    ] = new StoredTranscriptMessage(message, null, 1),
                },
            MetadataEntries = [],
        };
    }

    private sealed class StubTranscriptStore : ITranscriptStore
    {
        public TranscriptSession? LatestSession { get; init; }
        public TranscriptProjection? Projection { get; init; }
        public string? LastFindRequest { get; private set; }
        public TranscriptProjection? LastProjectionRequested { get; private set; }
        public int GetLatestSessionCalls { get; private set; }

        public Task<TranscriptSession> CreateSessionAsync(
            string workingDirectory,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TranscriptSession>> ListSessionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TranscriptSession>>([]);

        public Task<TranscriptSession?> GetLatestSessionAsync(
            CancellationToken cancellationToken = default)
        {
            GetLatestSessionCalls++;
            return Task.FromResult(LatestSession);
        }

        public Task<TranscriptSession?> FindSessionAsync(
            string sessionIdOrPath,
            CancellationToken cancellationToken = default)
        {
            LastFindRequest = sessionIdOrPath;
            return Task.FromResult<TranscriptSession?>(null);
        }

        public Task AppendMessageAsync(
            TranscriptSession session,
            ConversationMessage message,
            string? parentMessageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AppendMetadataAsync(
            TranscriptSession session,
            TranscriptMetadataEntry entry,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateSessionAsync(
            TranscriptSession session,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TranscriptProjection> LoadProjectionAsync(
            TranscriptSession session,
            TranscriptLoadOptions options,
            CancellationToken cancellationToken = default)
        {
            LastProjectionRequested = Projection;
            return Task.FromResult(Projection!);
        }
    }

    private sealed class StubRecovery : IConversationRecovery
    {
        public TranscriptProjection? LastProjection { get; private set; }
        public ResumeLoadResult? LastResult { get; private set; }

        public ResumeLoadResult Recover(TranscriptProjection projection)
        {
            LastProjection = projection;
            return LastResult = new ResumeLoadResult
            {
                Session = projection.Session,
                Messages = [],
                TotalUsage = TokenUsage.Empty,
                Metadata = projection.Session.Metadata.Clone(),
            };
        }
    }
}
