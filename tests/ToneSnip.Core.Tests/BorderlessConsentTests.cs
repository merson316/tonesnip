using ToneSnip.Core.Capture;
using Xunit;

namespace ToneSnip.Core.Tests;

public class BorderlessConsentTests
{
    [Fact]
    public void A_pending_prompt_is_waited_for_and_given_time_to_close_before_the_capture()
    {
        // The packaged app's first snip asks Windows for a borderless capture, which shows a consent prompt. The wait was
        // capped at 2 s, so the capture ran with the prompt still on screen.
        BorderlessConsent.Plan plan = BorderlessConsent.PlanFor(promptRequired: true);
        Assert.True(plan.AnswerWaitMs >= 30_000);
        Assert.True(plan.SettleMs > 0);
    }

    [Fact]
    public void Without_a_prompt_the_request_is_brief_and_nothing_settles()
    {
        BorderlessConsent.Plan plan = BorderlessConsent.PlanFor(promptRequired: false);
        Assert.True(plan.AnswerWaitMs <= 2_000);
        Assert.Equal(0, plan.SettleMs);
    }
}
