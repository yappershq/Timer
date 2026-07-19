using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Timer.RequestManager.Scheduling;

/// <summary>
/// Score recalculation request.
/// </summary>
internal readonly record struct RecalcRequest(ulong MapId, int Style, ushort Track, int Tier, int BasePot, double StyleFactor);

/// <summary>
/// Debounced score recalculation scheduler.
/// Uses a Channel with a single consumer for background processing, 5-second debounce delay, deduplicating by (MapId, Style, Track).
/// </summary>
internal sealed class ScoreRecalcScheduler : IDisposable
{
    // Unbounded with a single reader: the consumer immediately deduplicates every drained request by
    // (MapId, Style, Track) into a Dictionary, so memory stays bounded by the number of distinct live
    // keys regardless of burst size. A bounded channel + TryWrite silently DROPPED requests once full
    // (e.g. the admin `recalc all` enqueues one per (style,track) faster than the 5s-debounced consumer
    // drains), leaving those tracks' points permanently stale until something re-enqueued the same key.
    private readonly Channel<RecalcRequest> _channel =
        Channel.CreateUnbounded<RecalcRequest>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumer;
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(5);
    private readonly ILogger? _logger;

    public ScoreRecalcScheduler(Func<RecalcRequest, Task> recalcAction, ILogger? logger = null)
    {
        _logger = logger;
        _consumer = Task.Run(() => ConsumeLoop(recalcAction, _cts.Token));
    }

    /// <summary>
    /// Enqueue a recalculation request (fire-and-forget).
    /// </summary>
    public void Enqueue(RecalcRequest request)
    {
        // TryWrite cannot fail on an unbounded channel that has not completed, but log defensively so a
        // future change back to a bounded channel can never silently lose score recalculations.
        if (!_channel.Writer.TryWrite(request))
        {
            _logger?.LogWarning("Dropped score recalc request for Map={MapId}, Style={Style}, Track={Track}",
                                request.MapId, request.Style, request.Track);
        }
    }

    private async Task ConsumeLoop(Func<RecalcRequest, Task> recalcAction, CancellationToken ct)
    {
        var pending = new Dictionary<(ulong, int, ushort), RecalcRequest>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Block until the first message arrives
                if (!await _channel.Reader.WaitToReadAsync(ct))
                {
                    break;
                }

                // Drain all queued messages, deduplicating by key
                while (_channel.Reader.TryRead(out var req))
                {
                    pending[(req.MapId, req.Style, req.Track)] = req;
                }

                // Wait to allow subsequent requests to coalesce
                await Task.Delay(_debounceDelay, ct);

                // Drain again (new arrivals within the debounce window)
                while (_channel.Reader.TryRead(out var req))
                {
                    pending[(req.MapId, req.Style, req.Track)] = req;
                }

                // Execute each deduplicated recalculation
                foreach (var req in pending.Values)
                {
                    try
                    {
                        await recalcAction(req);
                    }
                    catch (Exception ex)
                    {
                        // Log and continue; don't block other tracks
                        _logger?.LogError(ex, "Error recalculating scores for MapId={MapId}, Style={Style}, Track={Track}",
                            req.MapId, req.Style, req.Track);
                    }
                }

                pending.Clear();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error in score recalc consumer loop");
            }
        }
    }

    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            _consumer.Wait(ShutdownDrainTimeout);
        }
        catch
        {
            // Ignore timeout or cancellation exceptions during shutdown
        }

        if (_channel.Reader.CanCount && _channel.Reader.Count > 0)
        {
            _logger?.LogWarning("Discarding {count} pending score-recalc request(s) on shutdown; "
                              + "affected boards recalc on the next qualifying finish.",
                                _channel.Reader.Count);
        }

        _cts.Dispose();
    }
}
