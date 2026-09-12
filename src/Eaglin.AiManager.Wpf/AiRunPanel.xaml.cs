using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Eaglin.AiManager.Wpf;

/// <summary>
/// A streaming output box with Stop and Copy: drop it in a window, set <see cref="Hub"/>, and
/// call <see cref="RunAsync"/> with a request. Text appears as it is generated; the footer
/// shows elapsed time and, when done, tokens and estimated cost.
/// </summary>
public partial class AiRunPanel : UserControl
{
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _sw = new();

    public AiHub? Hub { get; set; }

    public bool IsRunning => _cts is not null;

    /// <summary>The text shown, whether finished, failed or stopped part-way.</summary>
    public string Text => Output.Text;

    /// <summary>The finished response of the last run, or null if it failed or was stopped.</summary>
    public AiResponse? LastResponse { get; private set; }

    /// <summary>The failure of the last run, if any.</summary>
    public AiException? LastError { get; private set; }

    public AiRunPanel()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => StatusText.Text = $"Generating… {_sw.Elapsed.TotalSeconds:0.0}s";
    }

    public void Clear()
    {
        Output.Clear();
        StatusText.Text = "Ready.";
        StatusText.Foreground = (Brush)FindResource("Ai.Muted");
        CopyButton.IsEnabled = false;
        LastResponse = null;
        LastError = null;
    }

    /// <summary>
    /// Streams the request into the box. Returns the response, or null when the user stopped it
    /// or the provider failed (see <see cref="LastError"/>). Never throws for those two cases.
    /// </summary>
    public async Task<AiResponse?> RunAsync(AiRequest request, CancellationToken outer = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Hub is null) throw new InvalidOperationException("Set AiRunPanel.Hub before running.");
        if (IsRunning) throw new InvalidOperationException("A run is already in progress.");

        Clear();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        StopButton.IsEnabled = true;
        _sw.Restart();
        _clock.Start();

        try
        {
            var response = await Hub.StreamAsync(request, chunk =>
            {
                Output.AppendText(chunk.Text);
                Output.ScrollToEnd();
            }, _cts.Token);

            LastResponse = response;
            StatusText.Text = $"Done in {response.Elapsed.TotalSeconds:0.0}s · {response.Provider}/{response.Model} · in {response.InputTokens:N0} · out {response.OutputTokens:N0}" +
                              (response.EstimatedCostUsd is { } c ? $" · est. ${c:0.0000}" : "") +
                              (response.StopReason is "max_tokens" or "length" or "MAX_TOKENS" ? "  — output was cut off at the token limit" : "");
            return response;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Stopped after {_sw.Elapsed.TotalSeconds:0.0}s. The partial text above is what arrived.";
            return null;
        }
        catch (AiException ex)
        {
            LastError = ex;
            StatusText.Text = ex.Message;
            StatusText.Foreground = (Brush)FindResource("Ai.Bad");
            return null;
        }
        finally
        {
            _clock.Stop();
            _sw.Stop();
            StopButton.IsEnabled = false;
            CopyButton.IsEnabled = Output.Text.Length > 0;
            _cts.Dispose();
            _cts = null;
        }
    }

    public void Stop() => _cts?.Cancel();

    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Output.Text); } catch { /* clipboard busy */ }
    }
}
