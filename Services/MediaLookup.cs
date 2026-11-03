using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Models;

namespace MovieHub.Services;

public sealed record StreamRow(int Index, StreamKind Kind, string? Codec, bool IsDefault);
public sealed record MediaFileInfo(int Id, string Url, IReadOnlyList<StreamRow> Streams);
public sealed record SubtitleInfo(int Id, string Url, string Format);

/// <summary>Data access for the playback endpoints, kept behind an interface so the endpoints can be tested without a database.</summary>
public interface IMediaLookup
{
    Task<MediaFileInfo?> GetFileAsync(int fileId, CancellationToken ct);
    Task<SubtitleInfo?> GetSubtitleAsync(int subtitleId, CancellationToken ct);
}

public sealed class EfMediaLookup : IMediaLookup
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public EfMediaLookup(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<MediaFileInfo?> GetFileAsync(int fileId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var file = await db.MediaFiles.AsNoTracking().Include(f => f.Streams).FirstOrDefaultAsync(f => f.Id == fileId, ct);
        return file == null ? null
            : new MediaFileInfo(file.Id, file.FileUrl, file.Streams.Select(s => new StreamRow(s.StreamIndex, s.StreamType, s.Codec, s.IsDefault)).ToList());
    }

    public async Task<SubtitleInfo?> GetSubtitleAsync(int subtitleId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var s = await db.Subtitles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == subtitleId, ct);
        return s == null ? null : new SubtitleInfo(s.Id, s.FileUrl, s.Format);
    }
}
