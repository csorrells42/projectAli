namespace Ali.Modules.Orchestration.Activity;

public sealed record ActivityTransition
{
    public ActivityTransition(string justDid, string next)
    {
        JustDid = string.IsNullOrWhiteSpace(justDid)
            ? throw new ArgumentException("The completed activity description is required.", nameof(justDid))
            : justDid;
        Next = string.IsNullOrWhiteSpace(next)
            ? throw new ArgumentException("The next activity description is required.", nameof(next))
            : next;
    }

    public string JustDid { get; }

    public string Next { get; }
}

public static class ActivityNarrator
{
    public static string Render(ActivityTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return $"{transition.JustDid.Trim()} -> {transition.Next.Trim()}";
    }
}
