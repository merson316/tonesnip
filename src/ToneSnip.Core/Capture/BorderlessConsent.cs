namespace ToneSnip.Core.Capture;

/// <summary>
/// How a capture waits on the request for a borderless capture. A packaged app is shown a consent prompt the first time
/// it asks; the snip waits for the answer and for the prompt to leave the screen, or the prompt is in the capture.
/// </summary>
public static class BorderlessConsent
{
    /// <param name="AnswerWaitMs">How long to wait for the request to complete.</param>
    /// <param name="SettleMs">How long to let the prompt's window close before capturing; 0 when none was shown.</param>
    public readonly record struct Plan(int AnswerWaitMs, int SettleMs);

    public static Plan PlanFor(bool promptRequired) => promptRequired ? new Plan(60_000, 400) : new Plan(2_000, 0);
}
