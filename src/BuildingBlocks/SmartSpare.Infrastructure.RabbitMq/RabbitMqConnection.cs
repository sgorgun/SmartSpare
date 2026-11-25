using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace SmartSpare.Infrastructure.RabbitMq;

/// <summary>
/// The one AMQP connection of this service replica (ADR 0017 §5: the free broker allows 20). Opened on first use and kept;
/// the client's automatic recovery re-establishes it and its topology after a network failure. The publisher and the
/// subscriber each open one channel on it.
/// </summary>
internal sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    /// <summary>True while the connection is open (false before the first use and while it recovers).</summary>
    public bool IsOpen => _connection?.IsOpen == true;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;     // recovering connections are kept: never a second connection per replica
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is null)
            {
                var settings = options.Value;
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(settings.ConnectionString!),
                    ClientProvidedName = settings.ClientName,
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                };
                _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
