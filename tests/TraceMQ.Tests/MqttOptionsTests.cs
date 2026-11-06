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
    public void ExcludeTopicsBindTrimmedAndDeduplicated()
    {
        var options = Bind(new()
        {
            ["Mqtt:ExcludeTopics:0"] = " codeit/heartbeat/# ",
            ["Mqtt:ExcludeTopics:1"] = "codeit/heartbeat/#",
            ["Mqtt:ExcludeTopics:2"] = "",
        });

        Assert.Equal(["codeit/heartbeat/#"], options.EffectiveExcludeTopics());
    }

    [Fact]
    public void NoExcludeTopicsMeansNothingIsExcluded()
    {
        var excluded = Bind(new()).EffectiveExcludeTopics();

        Assert.Empty(excluded);
        Assert.False(MqttIngestService.IsExcluded(excluded, "codeit/anything"));
    }

    [Theory]
    [InlineData("codeit/heartbeat", true)]
    [InlineData("codeit/heartbeat/line1", true)]
    [InlineData("codeit/line1/status", true)]
    [InlineData("codeit/plc/raw", true)]
    [InlineData("codeit/plc/raw/more", false)]
    [InlineData("codeit/line1/events", false)]
    public void ExcludeFiltersUseMqttWildcards(string topic, bool expected)
    {
        string[] excluded = ["codeit/heartbeat/#", "codeit/+/status", "codeit/plc/raw"];

        Assert.Equal(expected, MqttIngestService.IsExcluded(excluded, topic));
    }

    [Fact]
    public void HostAndPortStillBind()
    {
        var options = Bind(new() { ["Mqtt:Host"] = "10.4.12.30", ["Mqtt:Port"] = "8883" });

        Assert.Equal("10.4.12.30", options.Host);
        Assert.Equal(8883, options.Port);
    }
}
