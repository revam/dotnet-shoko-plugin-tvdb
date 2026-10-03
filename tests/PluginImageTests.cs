using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The thumbnail and icon the plugin names ship in its assembly.
/// </summary>
public class PluginImageTests
{
    [Fact]
    public void ThePluginShipsTheImagesItNames()
    {
        var plugin = new Plugin();
        var resources = typeof(Plugin).Assembly.GetManifestResourceNames();

        Assert.Contains(plugin.EmbeddedThumbnailResourceName, resources);
        Assert.Contains(plugin.EmbeddedIconResourceName, resources);
    }
}
