using Microsoft.Extensions.Configuration;
using TraceMQ.Api.Ingest;

namespace TraceMQ.Tests;

public class MqttOptionsTests
{
    private static MqttOptions Bind(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new MqttOptions();
        configuration.GetSection("Mqtt").Bind(options);
        return options;
    }

    [Fact]
    public void ConfiguringTheDefaultTopicDoesNotSubscribeTwice()
    {
        // The regression: the binder appends to a collection property that already has
        // items, so a default of ["codeit/#"] plus the same value in appsettings produced two
        // subscriptions to one filter and the broker delivered every message twice.
        var options = Bind(new() { ["Mqtt:Topics:0"] = "codeit/#" });

        Assert.Equal(["codeit/#"], options.EffectiveTopics());
    }

    [Fact]
    public void DuplicatesInConfigurationAreCollapsed()
    {
        var options = Bind(new()
        {
            ["Mqtt:Topics:0"] = "codeit/#",
            ["Mqtt:Topics:1"] = "codeit/#",
            ["Mqtt:Topics:2"] = "plc/#",
        });

        Assert.Equal(["codeit/#", "plc/#"], options.EffectiveTopics());
    }

    [Fact]
    public void NoConfiguredTopicsFallsBackToTheDefault()
    {
        Assert.Equal(MqttOptions.DefaultTopics, Bind(new()).EffectiveTopics());
    }

    [Fact]
    public void BlankEntriesAreIgnored()
    {
        var options = Bind(new() { ["Mqtt:Topics:0"] = "  ", ["Mqtt:Topics:1"] = " codeit/# " });

        Assert.Equal(["codeit/#"], options.EffectiveTopics());
    }

    [Fact]
    public void HostAndPortStillBind()
    {
        var options = Bind(new() { ["Mqtt:Host"] = "10.4.12.30", ["Mqtt:Port"] = "8883" });

        Assert.Equal("10.4.12.30", options.Host);
        Assert.Equal(8883, options.Port);
    }
}
