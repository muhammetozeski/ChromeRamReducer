using System.Diagnostics;

namespace ChromeRamReducer.Core;

/// <summary>
/// Target types worth attaching to, and the outcome of one trim pass.
/// </summary>
public sealed record TrimResult(
    MemorySnapshot Before,
    MemorySnapshot After,
    int TargetsVisited,
    int TargetsFailed,
    int WorkingSetsEmptied,
    TimeSpan Duration,
    string? Error)
{
    public bool Succeeded => Error is null;

    /// <summary>Committed memory actually handed back to Windows.</summary>
    public double ReleasedMb => Before.PrivateMb - After.PrivateMb;

    /// <summary>Drop in the figure Task Manager shows. Includes pages merely moved to standby.</summary>
    public double WorkingSetDropMb => Before.WorkingSetMb - After.WorkingSetMb;
}

public sealed class ChromeTrimmer(AppSettings settings)
{
    private static readonly string[] AttachableTypes =
    [
        "page",
        "iframe",
        "webview",
        "worker",
        "shared_worker",
        "service_worker",
        "background_page",
    ];

    /// <summary>
    /// Runs V8 garbage collection across every Chrome target, then applies the optional
    /// working-set trim. Settling delays let the renderers hand pages back before the second read.
    /// </summary>
    public async Task<TrimResult> TrimAsync(int port, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        MemorySnapshot before = MemorySnapshot.Capture();

        int visited = 0;
        int failed = 0;
        int emptied = 0;
        string? error = null;

        try
        {
            await using CdpConnection connection = await CdpConnection.ConnectAsync(port, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report($"Connected to {connection.BrowserVersion}.");

            IReadOnlyList<CdpTarget> targets = await connection.GetTargetsAsync(cancellationToken)
                .ConfigureAwait(false);

            CdpTarget[] attachable = [.. targets.Where(t => AttachableTypes.Contains(t.Type, StringComparer.Ordinal))];

            progress?.Report($"{attachable.Length} of {targets.Count} targets are attachable.");

            foreach (CdpTarget target in attachable)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? sessionId = await connection.AttachAsync(target.TargetId, cancellationToken)
                    .ConfigureAwait(false);

                if (sessionId is null)
                {
                    failed++;
                    continue;
                }

                try
                {
                    await connection.CollectGarbageAsync(sessionId, cancellationToken).ConfigureAwait(false);

                    if (settings.PurgeJavaScriptMemory)
                    {
                        await connection.PurgeJavaScriptMemoryAsync(sessionId, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    visited++;
                    progress?.Report($"Collected: {Describe(target)}");
                }
                catch (CdpException)
                {
                    failed++;
                }
                finally
                {
                    try
                    {
                        await connection.DetachAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (CdpException)
                    {
                        // The target may already be gone.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // Renderers release pages asynchronously once the collection finishes.
        await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);

        if (settings.EmptyWorkingSets)
        {
            emptied = MemorySnapshot.EmptyAllChromeWorkingSets();
            progress?.Report($"Working set emptied on {emptied} processes (cosmetic).");
            await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);
        }

        MemorySnapshot after = MemorySnapshot.Capture();
        stopwatch.Stop();

        return new TrimResult(before, after, visited, failed, emptied, stopwatch.Elapsed, error);
    }

    private static string Describe(CdpTarget target)
    {
        string label = target.Title.Length > 0 ? target.Title : target.Url;

        if (label.Length > 58)
        {
            label = string.Concat(label.AsSpan(0, 55), "...");
        }

        return $"[{target.Type}] {label}";
    }
}
