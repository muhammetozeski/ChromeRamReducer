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

        Log($"Trim started on port {port}. Before: {before.ProcessCount} processes, "
            + $"committed {before.PrivateMb:N0} MB, working set {before.WorkingSetMb:N0} MB. "
            + $"PurgeJavaScriptMemory={settings.PurgeJavaScriptMemory}, EmptyWorkingSets={settings.EmptyWorkingSets}",
            LogLevel.Info);

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

            Log($"Targets reported by Chrome ({targets.Count}):\n  "
                + string.Join("\n  ", targets.Select(t => $"[{t.Type}] {t.Title} <- {t.Url}")), LogLevel.Debug);

            progress?.Report($"{attachable.Length} of {targets.Count} targets are attachable.");

            foreach (CdpTarget target in attachable)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? sessionId = await connection.AttachAsync(target.TargetId, cancellationToken)
                    .ConfigureAwait(false);

                if (sessionId is null)
                {
                    Log($"Attach refused by {Describe(target)}.", LogLevel.Warning);
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
                catch (CdpException ex)
                {
                    Log($"Collection failed on {Describe(target)}: {ex.Message}", LogLevel.Warning);
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
            Log($"Trim aborted: {ex}", LogLevel.Error);
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

        TrimResult result = new(before, after, visited, failed, emptied, stopwatch.Elapsed, error);

        Log($"Trim finished in {result.Duration.TotalSeconds:F1}s. "
            + $"Collected {visited}, skipped {failed}. "
            + $"Committed {before.PrivateMb:N0} -> {after.PrivateMb:N0} MB (released {result.ReleasedMb:N0} MB). "
            + $"Working set {before.WorkingSetMb:N0} -> {after.WorkingSetMb:N0} MB.",
            result.Succeeded ? LogLevel.Info : LogLevel.Error);

        return result;
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
