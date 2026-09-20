using System.Buffers;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using TraceMQ.Api.Correlation;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;

namespace TraceMQ.Api.Ingest;

public sealed class MqttIngestService : BackgroundService
{
    private readonly ChannelWriter<LogMessage> _writer;
    private readonly MqttOptions _options;
    private readonly CorrelationSettings _correlation;
    private readonly MessageRing _ring;
    private readonly RecentKeys _recentKeys;
    private readonly BrokerState _brokerState;
    private readonly ILogger<MqttIngestService> _log;

    public MqttIngestService(
        Channel<LogMessage> channel,
        IOptions<MqttOptions> options,
        CorrelationSettings correlation,
        MessageRing ring,
        RecentKeys recentKeys,
        BrokerState brokerState,
        ILogger<MqttIngestService> log
    )
    {
        _writer = channel.Writer;
        _options = options.Value;
        _correlation = correlation;
        _ring = ring;
        _recentKeys = recentKeys;
        _brokerState = brokerState;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        var topics = _options.EffectiveTopics();
        _brokerState.Describe($"mqtt://{_options.Host}:{_options.Port}", topics);
        client.DisconnectedAsync += _ =>
        {
            _brokerState.SetConnected(false);
            return Task.CompletedTask;
        };

        client.ApplicationMessageReceivedAsync += e =>
        {
            ReadOnlySequence<byte> payload = e.ApplicationMessage.Payload;
            var bytes = payload.ToArray();

            var msg = new LogMessage(
                _ring.NextId(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                e.ApplicationMessage.Topic,
                bytes,
                (byte)e.ApplicationMessage.QualityOfServiceLevel,
                e.ApplicationMessage.Retain,
                _correlation.Extractor.Extract(bytes));

            // The ring first: the live pane must see the message even if the writer is behind
            // or the channel drops it.
            _ring.Add(msg);
            if (msg.CorrelationKey is { Length: > 0 } key)
            {
                _recentKeys.Record(key, msg.TimestampMs);
            }
            _writer.TryWrite(msg);

            return Task.CompletedTask;
        };

        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(_options.ClientId)
            .Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    await client.ConnectAsync(clientOptions, stoppingToken);
                    _brokerState.SetConnected(true);
                    _log.LogInformation("Connected to {Host}:{Port}", _options.Host, _options.Port);

                    foreach (var topic in topics)
                    {
                        await client.SubscribeAsync(
                            new MqttTopicFilterBuilder()
                                .WithTopic(topic)
                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                                .Build(),
                            stoppingToken);

                        _log.LogInformation("Subscribed to {Topic}", topic);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _brokerState.SetConnected(false);
                _log.LogWarning(ex, "MQTT connection failed; retrying in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _brokerState.SetConnected(false);
        if (client.IsConnected)
        {
            await client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None);
        }

        _writer.TryComplete();
    }
}