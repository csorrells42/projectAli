using Ali.Modules.Automation.UI;
using Ali.Modules.Attention;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0].Equals("attention-self-test", StringComparison.OrdinalIgnoreCase))
        {
            return RunAttentionSelfTest();
        }

        return UiAutomationProgram.Run(args);
    }

    private static int RunAttentionSelfTest()
    {
        var gate = new AttentionGate(engageFrames: 3, releaseFrames: 2);
        if (gate.Update(true) || gate.Update(true) || !gate.Update(true))
        {
            Console.Error.WriteLine("attention gate failed to require three deliberate frames");
            return 1;
        }

        if (!gate.Update(false) || gate.Update(false))
        {
            Console.Error.WriteLine("attention gate release hysteresis failed");
            return 1;
        }

        var started = DateTimeOffset.Parse("2026-07-21T12:00:00Z");
        var turn = new VoiceTurnDetector(
            trailingSilence: TimeSpan.FromMilliseconds(900),
            noSpeechTimeout: TimeSpan.FromSeconds(6));
        turn.Start(started);
        if (turn.Observe(0.03, started.AddSeconds(1)) != VoiceTurnDecision.Continue
            || turn.Observe(0.001, started.AddSeconds(2)) != VoiceTurnDecision.Send)
        {
            Console.Error.WriteLine("speech turn end detection failed");
            return 1;
        }

        turn.Start(started);
        if (turn.Observe(0.001, started.AddSeconds(7)) != VoiceTurnDecision.Cancel)
        {
            Console.Error.WriteLine("no-speech timeout failed");
            return 1;
        }

        Console.WriteLine("attention-self-test passed: gaze hysteresis, speech end, and no-speech cancellation");
        return 0;
    }
}
