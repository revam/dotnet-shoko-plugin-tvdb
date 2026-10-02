using System;
using System.Collections.Generic;
using Shoko.Abstractions.Metadata.Enums;

namespace Shoko.Plugin.Tvdb.Mapping;

/// <summary>
/// Where TheTVDB's images live, and how an image's URL becomes a resource ID
/// of the image template.
/// </summary>
public static class TvdbImages
{
    /// <summary>
    /// The default template URL for TheTVDB's images. Every image TheTVDB
    /// serves is under <c>/banners/</c> on its artwork host, and the rest of
    /// the path is the resource ID.
    /// </summary>
    public const string TemplateUrl = "https://artworks.thetvdb.com/banners/{0}";

    /// <summary>
    /// The longest resource ID the core's image table holds.
    /// </summary>
    public const int MaxResourceIDLength = 128;

    private const string BannersPrefix = "/banners/";

    private const string MissingImagePrefix = "images/missing/";

    /// <summary>
    /// The resource ID of an image TheTVDB gave by URL or by path.
    /// </summary>
    /// <remarks>
    /// TheTVDB answers either a full URL on its artwork host or a path under
    /// <c>/banners/</c>. Anything on another host, TheTVDB's placeholders for
    /// a missing image, and a path longer than the image table holds are
    /// dropped rather than truncated, since a truncated ID downloads nothing.
    /// </remarks>
    /// <param name="value">The URL or path.</param>
    /// <returns>The resource ID, or <see langword="null"/> when there is no usable image.</returns>
    public static string? ToResourceID(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        string path;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            if (!string.Equals(uri.Host, "artworks.thetvdb.com", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.StartsWith(BannersPrefix, StringComparison.Ordinal))
                return null;

            path = uri.AbsolutePath[BannersPrefix.Length..];
        }
        else if (trimmed.StartsWith(BannersPrefix, StringComparison.Ordinal))
        {
            path = trimmed[BannersPrefix.Length..];
        }
        else if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            path = trimmed.TrimStart('/');
        }
        else
        {
            return null;
        }

        if (path.Length is 0 || path.Length > MaxResourceIDLength || path.StartsWith(MissingImagePrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return path;
    }

    /// <summary>
    /// The default image an entry's record names, of one type, for the store
    /// to pin.
    /// </summary>
    /// <param name="imageType">What the image is for the entry.</param>
    /// <param name="resourceID">The image's resource ID, or <see langword="null"/> when the record names none.</param>
    /// <returns>The default by its type, or an empty map when the record names none.</returns>
    public static Dictionary<ImageEntityType, string> ToDefaultImages(ImageEntityType imageType, string? resourceID)
        => string.IsNullOrEmpty(resourceID) ? [] : new() { [imageType] = resourceID };

    /// <summary>
    /// The portrait a credit or record names, as a person's or character's
    /// default image.
    /// </summary>
    /// <param name="resourceID">The portrait's resource ID, or <see langword="null"/>.</param>
    /// <returns>
    /// The default, or <see langword="null"/> without a portrait, which keeps
    /// the stored one as the portrait itself is kept.
    /// </returns>
    public static Dictionary<ImageEntityType, string>? ToPortraitDefault(string? resourceID)
        => string.IsNullOrEmpty(resourceID) ? null : new() { [ImageEntityType.Primary] = resourceID };

    /// <summary>
    /// What a kind of TheTVDB artwork is for its show or season.
    /// </summary>
    /// <param name="artworkType">TheTVDB's number for the kind of artwork.</param>
    /// <returns>The image type, or <see cref="ImageEntityType.None"/> for a kind the plugin does not offer.</returns>
    public static ImageEntityType ToImageType(int artworkType)
        => artworkType switch
        {
            1 or 6 => ImageEntityType.Banner,
            2 or 7 => ImageEntityType.Primary,
            3 or 8 => ImageEntityType.Backdrop,
            23 => ImageEntityType.Logo,
            _ => ImageEntityType.None,
        };

    /// <summary>
    /// Whether a kind of TheTVDB artwork belongs to a season rather than the
    /// show.
    /// </summary>
    /// <param name="artworkType">TheTVDB's number for the kind of artwork.</param>
    /// <returns><see langword="true"/> for a season banner, poster or background.</returns>
    public static bool IsSeasonArtwork(int artworkType)
        => artworkType is 6 or 7 or 8;
}
