namespace AmazonMusicSmtc;

internal sealed record TrackInfo(string Title, string Artist, string Album)
{
    /// <summary>
    /// Local copy of the cover art taken at notification time, or null when the
    /// artwork could not be captured. Owned by the consumer.
    /// </summary>
    public string? ArtworkPath { get; init; }

    /// <summary>
    /// Remote cover art, as reported by the renderer. Amazon serves these from a
    /// public CDN, so no cookies are needed to fetch one.
    /// </summary>
    public string? ArtworkUrl { get; init; }

    /// <summary>
    /// Exact track length when the source knows it. The notification path does not,
    /// and falls back to <see cref="DurationLookup"/>.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    public bool SameTrackAs(TrackInfo? other) =>
        other is not null &&
        string.Equals(Title, other.Title, StringComparison.Ordinal) &&
        string.Equals(Artist, other.Artist, StringComparison.Ordinal) &&
        string.Equals(Album, other.Album, StringComparison.Ordinal);

    public override string ToString() => $"{Artist} - {Title} [{Album}]";
}
