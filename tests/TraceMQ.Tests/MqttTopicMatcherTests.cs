using Microsoft.Extensions.Logging.Abstractions;
using TraceMQ.Api.Storage;
using TraceMQ.Api.Topics;

namespace TraceMQ.Tests;

public class MqttTopicMatcherTests
{
    /// <summary>
    /// The one table. It runs against the C# matcher here and against the SQL function in
    /// <see cref="SqlFunctionAgreesWithTheMatcher"/>, so the live tail and history can never
    /// drift apart on what a filter means.
    /// </summary>
    public static TheoryData<string, string, bool> Cases => new()
    {
        // exact, no wildcards
        { "codeit/a/b", "codeit/a/b", true },
        { "codeit/a/b", "codeit/a/c", false },
        { "codeit/a/b", "codeit/a", false },
        { "codeit/a/b", "codeit/a/b/c", false },

        // single-level +
        { "sport/+", "sport/tennis", true },
        { "sport/+", "sport/tennis/player1", false },   // + is exactly one level
        { "sport/+", "sport", false },                  // + requires a level to be there
        { "sport/+", "sport/", true },                  // an empty level is still a level
        { "+/tennis/#", "sport/tennis/player1", true },
        { "+/tennis/#", "sport/tennis", true },
        { "+/+", "a/b", true },
        { "+/+", "a/b/c", false },

        // multi-level #
        { "sport/#", "sport/tennis/player1", true },
        { "sport/#", "sport/tennis", true },
        { "sport/#", "sport", true },                   // # matches the parent level too
        { "sport/#", "sports", false },
        { "#", "any/topic/at/all", true },
        { "#", "single", true },
        { "codeit/#", "codeit/boliden/DDATA/odda/foundry/fvl/bundlescan/scanner1/scanner", true },
        { "codeit/#", "plc/conveyor/state", false },

        // $-prefixed system topics are not reachable by a leading wildcard
        { "#", "$SYS/broker/uptime", false },
        { "+/broker/uptime", "$SYS/broker/uptime", false },
        { "$SYS/#", "$SYS/broker/uptime", true },

        // case is significant in topics
        { "codeit/A", "codeit/a", false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesToSpec(string filter, string topic, bool expected)
    {
        Assert.Equal(expected, MqttTopicMatcher.Matches(filter, topic));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SqlFunctionAgreesWithTheMatcher(string filter, string topic, bool expected)
    {
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);

        using var connection = db.Factory.Open();
        SqlFunctions.Register(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT mqtt_match($filter, $topic);";
        cmd.Parameters.AddWithValue("$filter", filter);
        cmd.Parameters.AddWithValue("$topic", topic);

        Assert.Equal(expected, Convert.ToInt32(cmd.ExecuteScalar()) == 1);
    }

    [Theory]
    [InlineData("codeit/#", "codeit/")]
    [InlineData("codeit/a/#", "codeit/a/")]
    [InlineData("#", "")]
    public void PrefixFiltersBecomeAnIndexFriendlyLike(string filter, string expectedPrefix)
    {
        Assert.Equal(expectedPrefix.TrimEnd('/'), MqttTopicMatcher.AsSqlPrefix(filter));
    }

    [Theory]
    [InlineData("codeit/+/b")]
    [InlineData("codeit/#/b")]
    [InlineData("codeit/a/b")]
    [InlineData("")]
    public void OtherFiltersDoNotGetThePrefixShortcut(string filter)
    {
        Assert.Null(MqttTopicMatcher.AsSqlPrefix(filter));
    }

    [Fact]
    public void AFilterWithoutWildcardsIsPlainEquality()
    {
        Assert.False(MqttTopicMatcher.HasWildcards("codeit/a/b"));
        Assert.True(MqttTopicMatcher.HasWildcards("codeit/#"));
        Assert.True(MqttTopicMatcher.HasWildcards("codeit/+/b"));
    }
}
