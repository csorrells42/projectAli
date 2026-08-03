namespace Ali.Modules.Coding.RoslynActions;

public sealed record AliRoslynActionApplication(
    bool Success,
    string HandleId,
    string HandleState,
    bool Applied,
    string OutcomeCode,
    string Summary);
