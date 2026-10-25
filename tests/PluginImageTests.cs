using System.Linq;
using System.Reflection;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The thumbnail and icon the build stamps into the plugin ship in its assembly.
/// </summary>
public class PluginImageTests
{
    [Theory]
    [InlineData("PackageThumbnail")]
    [InlineData("PackageIcon")]
    public void ThePluginShipsTheImagesItNames(string key)
    {
        var assembly = typeof(Plugin).Assembly;
        var resourceName = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;

        Assert.NotNull(resourceName);
        Assert.Contains(resourceName, assembly.GetManifestResourceNames());
    }
}
