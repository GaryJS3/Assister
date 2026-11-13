namespace Assister;

// Native handlers return protocol outcomes separately from user-facing wording.
public sealed record NativeModuleResult(string Response, string Outcome);
