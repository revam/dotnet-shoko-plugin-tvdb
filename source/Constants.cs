namespace Shoko.Plugin.Tvdb;

/// <summary>
/// Build-time constants. The value here is a placeholder in the tree and is
/// rewritten by CI for official builds.
/// </summary>
internal static class Constants
{
    /// <summary>
    /// The licensed TvDB v4 project key official builds ship with,
    /// substituted by CI from the <c>TVDB_PROJECT_KEY</c> secret. For a build
    /// from source the placeholder stays put, and the plugin then needs an API
    /// key from the settings before it will fetch anything.
    /// </summary>
    /// <remarks>
    /// TvDB issues keys per project, not to individual users. A licensed
    /// key authenticates on its own, so an official build needs neither a key
    /// nor a subscriber PIN from its user; a PIN only matters for a
    /// user-supported key, which authenticates as the subscriber it comes
    /// with. A key embedded in a binary can be read back out of it, so this is
    /// kept out of the tree, not out of the DLL.
    /// <br/><br/>
    /// The comparison against the placeholder deliberately lives in
    /// <c>TvdbApiClient</c> rather than here, because CI rewrites this file and
    /// would rewrite the thing being compared against along with it.
    /// </remarks>
    public const string ProjectApiKey = "TVDB_PROJECT_KEY_GOES_HERE";
}
