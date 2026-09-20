namespace TraceMQ.Api.Ingest;

/// <summary>
/// Whether the MQTT client currently has a session with the broker. The UI shows this, so it
/// has to be the real thing: a troubleshooting tool that always claims "connected" is worse
/// than one that says nothing.
/// </summary>
public sealed class BrokerState
{
    private volatile bool _connected;

    public bool Connected => _connected;
    public string Endpoint { get; private set; } = string.Empty;
    public IReadOnlyList<string> Topics { get; private set; } = [];

    public void Describe(string endpoint, IReadOnlyList<string> topics)
    {
        Endpoint = endpoint;
        Topics = topics;
    }

    public void SetConnected(bool connected) => _connected = connected;
}
