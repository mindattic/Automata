using System.Text.Json.Serialization;

namespace Automata.Core.Automation.Model;

/// <summary>
/// How many times to attempt a step, and how long to wait between attempts.
/// <para>
/// A record rather than a class so <see cref="ResolvedSettings"/> gets real value equality —
/// with a plain class, two identically-configured resolutions compare unequal by reference,
/// which quietly breaks any "did this scope actually change anything?" comparison.
/// </para>
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>Total attempts including the first. 1 = no retry, which is the floor everywhere.</summary>
    public int MaxAttempts { get; set; } = 1;

    /// <summary>Delay before the second attempt, in milliseconds.</summary>
    public int DelayMs { get; set; } = 2000;

    /// <summary>Each further attempt waits this multiple of the previous delay. 1.0 = fixed.</summary>
    public double BackoffMultiplier { get; set; } = 1.0;
}

/// <summary>What a failure in one lane does to the lanes running beside it.</summary>
public enum FailureIsolation
{
    /// <summary>Let sibling lanes finish. Matches today's "continue past a failed task".</summary>
    IsolateLane,

    /// <summary>Cancel every other lane in the same run.</summary>
    FailFast,
}

/// <summary>
/// Engine settings for one scope in the global → collection → task → step chain.
/// <para>
/// EVERY property is nullable, and null means "inherit". The absence of a value is the whole
/// point: an entity carries only what it actually overrides, so a collection or task written
/// before this type existed gains nothing on disk until someone changes something.
/// </para>
/// </summary>
public sealed class EngineSettingsOverride
{
    /// <summary>Resolve/settle budget per step. <see cref="Step.TimeoutMs"/> still wins over this
    /// for one specific step.</summary>
    public int? DefaultStepTimeoutMs { get; set; }

    public bool? SelfHeal { get; set; }
    public bool? AllowLlmRepair { get; set; }
    public RetryPolicy? Retry { get; set; }

    /// <summary>
    /// Keep running the remaining SIBLING steps after one fails. A failed step's own children are
    /// always skipped regardless — its post-condition did not hold, so its substeps have no
    /// footing to run on.
    /// </summary>
    public bool? ContinueOnStepError { get; set; }

    /// <summary>Keep running the remaining tasks in a collection after one fails.</summary>
    public bool? ContinueOnTaskError { get; set; }

    public FailureIsolation? Isolation { get; set; }

    /// <summary>
    /// Ceiling on concurrent browser lanes. A deeper scope may only LOWER what it inherits, never
    /// raise it, so one task can never starve the machine by out-declaring the global setting.
    /// </summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>Named browser profile: the same name means the same userDataFolder, and therefore
    /// shared cookies and logins. Different names are fully isolated.</summary>
    public string? BrowserProfile { get; set; }

    public bool? ScreenshotOnFailure { get; set; }

    /// <summary>"claude" | "openai" | "gemini" | "kimi"; falls back to the global provider.</summary>
    public string? LlmProvider { get; set; }

    /// <summary>
    /// True when this scope overrides nothing. Callers use it to drop the object entirely rather
    /// than persisting a settings node that says nothing — which is what keeps an untouched task's
    /// JSON byte-identical to how it was written before scoped settings existed.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty =>
        DefaultStepTimeoutMs is null && SelfHeal is null && AllowLlmRepair is null &&
        Retry is null && ContinueOnStepError is null && ContinueOnTaskError is null &&
        Isolation is null && MaxConcurrency is null && ScreenshotOnFailure is null &&
        string.IsNullOrEmpty(BrowserProfile) && string.IsNullOrEmpty(LlmProvider);
}

/// <summary>
/// The flattened result of resolving the four scopes: every value present, nothing to inherit.
/// This is what the engine actually reads.
/// </summary>
public sealed record ResolvedSettings(
    int DefaultStepTimeoutMs,
    bool SelfHeal,
    bool AllowLlmRepair,
    RetryPolicy Retry,
    bool ContinueOnStepError,
    bool ContinueOnTaskError,
    FailureIsolation Isolation,
    int MaxConcurrency,
    string BrowserProfile,
    bool ScreenshotOnFailure,
    string LlmProvider);
