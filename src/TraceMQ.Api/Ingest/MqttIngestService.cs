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
    private readonly CorrelationExtractor _correlation;
    private readonly MessageRing _ring;
    private readonly RecentKeys _recentKeys;
    private readonly ILogger<MqttIngestService> _log;

    public MqttIngestService(
        Channel<LogMessage> channel,
        IOptions<MqttOptions> options,
        CorrelationExtractor correlation,
        MessageRing ring,
        RecentKeys recentKeys,
        ILogger<MqttIngestService> log
    )
    {
        _writer = channel.Writer;
        _options = options.Value;
        _correlation = correlation;
        _ring = ring;
        _recentKeys = recentKeys;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = new MqttClientFactory().CreateMqttClient();

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
                _correlation.Extract(bytes));

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
                    _log.LogInformation("Connected to {Host}:{Port}", _options.Host, _options.Port);

                    foreach (var topic in _options.Topics)
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
                _log.LogWarning(ex, "MQTT connection failed; retrying in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        if (client.IsConnected)
        {
            await client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None);
        }

        _writer.TryComplete();
    }
}