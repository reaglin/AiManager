using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Eaglin.AiManager.Wpf;

/// <summary>
/// The prompt console: the box first holds the request itself, editable, so the author can
/// change it before sending (<see cref="ShowPrompt"/>, <see cref="PromptText"/>). On
/// <see cref="RunAsync"/> it empties, shows "Running prompt…" with a timer, and fills with the
/// answer as it is generated. The footer then reports time, tokens and estimated cost, and says
/// plainly — in red — when the answer was cut off at the token limit.
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

    /// <summary>True while the box holds the request rather than an answer.</summary>
    public bool IsShowingPrompt { get; private set; }

    /// <summary>The request text as the author has it now (what <see cref="ShowPrompt"/> put there, plus any edits).</summary>
    public string PromptText => IsShowingPrompt ? Output.Text : _prompt;

    private string _prompt = string.Empty;

    /// <summary>True when the last answer stopped because it hit the token ceiling.</summary>
    public bool LastWasCutOff { get; private set; }

    /// <summary>
    /// Puts the request in the box for the author to read and edit before sending. Call again to
    /// bring the request back after an answer has replaced it.
    /// </summary>
    public void ShowPrompt(string promptText)
    {
        _prompt = promptText ?? string.Empty;
        IsShowingPrompt = true;
        Output.IsReadOnly = false;
        Output.FontFamily = new FontFamily("Consolas");
        Output.FontSize = 12.5;
        Output.Text = _prompt;
        Output.CaretIndex = 0;
        StatusText.Text = "This is the request. Edit it if you want, then Send Prompt.";
        StatusText.Foreground = (Brush)FindResource("Ai.Muted");
        StatusText.FontWeight = FontWeights.Normal;
        CopyButton.IsEnabled = _prompt.Length > 0;
    }

    /// <summary>The finished response of the last run, or null if it failed or was stopped.</summary>
    public AiResponse? LastResponse { get; private set; }

    /// <summary>The failure of the last run, if any.</summary>
    public AiException? LastError { get; private set; }

    public AiRunPanel()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => StatusText.Text = $"Running prompt…  {_sw.Elapsed.TotalSeconds:0.0}s";
    }

    public void Clear()
    {
        Output.Clear();
        Output.IsReadOnly = true;
        IsShowingPrompt = false;
        StatusText.Text = "Ready.";
        StatusText.Foreground = (Brush)FindResource("Ai.Muted");
        StatusText.FontWeight = FontWeights.Normal;
        CopyButton.IsEnabled = false;
        LastResponse = null;
        LastError = null;
        LastWasCutOff = false;
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

        if (IsShowingPrompt) _prompt = Output.Text;                 // keep the author's edits for a re-send
        Clear();
        Output.FontFamily = new FontFamily("Segoe UI");
        Output.FontSize = 13.5;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        StopButton.IsEnabled = true;
        StatusText.FontWeight = FontWeights.SemiBold;
        StatusText.Foreground = (Brush)FindResource("Ai.Accent");
        StatusText.Text = "Running prompt…  0.0s";
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
            LastWasCutOff = response.StopReason is "max_tokens" or "length" or "MAX_TOKENS";
            StatusText.Text = $"Done in {response.Elapsed.TotalSeconds:0.0}s · {response.Provider}/{response.Model} · in {response.InputTokens:N0} · out {response.OutputTokens:N0}" +
                              (response.EstimatedCostUsd is { } c ? $" · est. ${c:0.0000}" : "") +
                              (LastWasCutOff ? $"   ⚠ CUT OFF at the {response.OutputTokens:N0}-token limit — the answer is incomplete. Raise \"max tokens\" for this prompt in AI › Prompt Library, or ask for less." : "");
            StatusText.Foreground = (Brush)FindResource(LastWasCutOff ? "Ai.Bad" : "Ai.Muted");
            StatusText.FontWeight = LastWasCutOff ? FontWeights.SemiBold : FontWeights.Normal;
            return response;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Stopped after {_sw.Elapsed.TotalSeconds:0.0}s. The partial text above is what arrived.";
            StatusText.Foreground = (Brush)FindResource("Ai.Muted");
            StatusText.FontWeight = FontWeights.Normal;
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
