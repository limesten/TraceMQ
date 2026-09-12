namespace TraceMQ.Api.Ingest;

public sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "tracemq";
    public string[] Topics { get; set; } = ["codeit/#"];
}