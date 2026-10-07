using System.Threading.Channels;
using Gcs.Application.Abstractions;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.Mavlink.Commands;

public sealed record CommandExchangeOptions
{
    /// <summary>How long to wait for COMMAND_ACK before sending the command again.</summary>
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Resends after the first transmission. 3 means at most 4 transmissions, then the command times out.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// After an IN_PROGRESS answer the vehicle has the command and is working on it (e.g. a long calibration).
    /// Resending would restart it, so we only wait, up to this long, for the final answer.
    /// </summary>
    public TimeSpan InProgressTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The MAVLink command protocol for one COMMAND_LONG:
/// <code>
/// GCS ──COMMAND_LONG(confirmation=0)──►   (lost)
///     ... no COMMAND_ACK within AckTimeout ...
/// GCS ──COMMAND_LONG(confirmation=1)──►
/// GCS ◄──COMMAND_ACK(ACCEPTED)──            ► Accepted after 2 attempts
/// </code>
/// The <c>confirmation</c> field counts retransmissions, so the vehicle can tell a resend from a new command.
/// A refusal (DENIED, FAILED, ...) is final and never retried: asking again does not change the vehicle's mind.
/// Pure logic over a send delegate and an inbox, so tests drive it with a fake clock and a scripted vehicle.
/// </summary>
public sealed class CommandExchange(
    Func<IMavlinkMessage, CancellationToken, ValueTask> send,
    ChannelReader<CommandAckMessage> acks,
    CommandExchangeOptions options,
    TimeProvider time)
{
    public async Task<CommandDelivery> RunAsync(CommandLongMessage command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        for (var attempt = 1; attempt <= options.MaxRetries + 1; attempt++)
        {
            var confirmation = (byte)Math.Min(attempt - 1, byte.MaxValue);
            await send(command with { Confirmation = confirmation }, cancellationToken);

            var ack = await ReceiveAsync(command.Command, options.AckTimeout, cancellationToken);
            if (ack?.Result == MavResult.InProgress)
            {
                ack = await WaitForFinalAsync(command.Command, cancellationToken);
                if (ack is null)
                {
                    return new CommandDelivery(
                        CommandDeliveryStatus.TimedOut, attempt, $"The vehicle reported IN_PROGRESS but no result within {Seconds(options.InProgressTimeout)} s.");
                }
            }

            if (ack is not null)
            {
                return ack.Result == MavResult.Accepted
                    ? new CommandDelivery(CommandDeliveryStatus.Accepted, attempt, null)
                    : new CommandDelivery(CommandDeliveryStatus.Rejected, attempt, $"The vehicle answered {ack.Result}.");
            }
        }

        var attempts = options.MaxRetries + 1;
        return new CommandDelivery(
            CommandDeliveryStatus.TimedOut, attempts, $"No COMMAND_ACK after {attempts} attempts ({Seconds(options.AckTimeout)} s each).");
    }

    private async Task<CommandAckMessage?> WaitForFinalAsync(MavCmd command, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + options.InProgressTimeout;
        while (time.GetUtcNow() < deadline)
        {
            var ack = await ReceiveAsync(command, deadline - time.GetUtcNow(), cancellationToken);
            if (ack is null || ack.Result != MavResult.InProgress)
            {
                return ack;
            }
        }

        return null;
    }

    /// <summary>The next ACK for this command, or null when none arrives in time. ACKs for other commands are skipped.</summary>
    private async Task<CommandAckMessage?> ReceiveAsync(MavCmd command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var expiry = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            while (true)
            {
                var ack = await acks.ReadAsync(linked.Token);
                if (ack.Command == command)
                {
                    return ack;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
