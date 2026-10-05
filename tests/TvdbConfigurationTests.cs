using System.ComponentModel.DataAnnotations;
using System.Linq;
using Shoko.Plugin.Tvdb;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// What the configuration promises: sensible defaults, and nothing marked
/// required.
/// </summary>
public class TvdbConfigurationTests
{
    [Fact]
    public void TheDefaults_AreUsableWithoutAnyoneTouchingThem()
    {
        var configuration = new TvdbConfiguration();

        Assert.True(configuration.AutoDownloadAlternateOrderings);
        Assert.Equal(50, configuration.PersonDetailsLimit);
        Assert.False(configuration.ConsiderExistingOtherLinks);
        Assert.Equal(10, configuration.SearchResultLimit);
        Assert.Null(configuration.ApiKey);
        Assert.Null(configuration.SubscriberPin);
    }

    [Fact]
    public void NothingIsMarkedRequired()
    {
        // A required-but-unset property fails configuration validation, which
        // stops the whole plugin from loading. The missing credential is
        // checked for at runtime instead, so the plugin stays loaded and only
        // the fetching stops.
        var required = typeof(TvdbConfiguration)
            .GetProperties()
            .Where(property => property.GetCustomAttributes(typeof(RequiredAttribute), inherit: true).Length is not 0)
            .Select(property => property.Name);

        Assert.Empty(required);
    }

    [Fact]
    public void BothCredentials_AreMarkedAsPasswordsSoTheWebUIMasksThem()
    {
        foreach (var name in new[] { nameof(TvdbConfiguration.ApiKey), nameof(TvdbConfiguration.SubscriberPin) })
        {
            var attribute = typeof(TvdbConfiguration).GetProperty(name)!
                .GetCustomAttributes(typeof(DataTypeAttribute), inherit: true)
                .Cast<DataTypeAttribute>()
                .SingleOrDefault();

            Assert.Equal(DataType.Password, attribute?.DataType);
        }
    }
}
