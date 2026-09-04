using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Replay;

/// <summary>
/// Turns a <see cref="BindingRef"/> into the string a step should actually use.
/// <para>
/// Shared by the replay engine and the workflow engine so there is one definition of what a
/// binding means. A binding that cannot be resolved is reported as an error rather than falling
/// back to the literal beside it — a silent fallback looks like it worked.
/// </para>
/// </summary>
internal static class BindingResolver
{
    /// <summary>The value and URL a step should use, after applying any bindings over its literals.</summary>
    public static (string? Value, string? Url, string? Error) ResolveValues(Step step, ReplayRunState state)
    {
        var value = step.Value;
        var url = step.Url;
        if (step.Bindings is not { Count: > 0 }) return (value, url, null);

        foreach (var (field, binding) in step.Bindings)
        {
            if (binding == null) continue;
            var (resolved, error) = Resolve(binding, state);
            if (error != null) return (null, null, $"binding for {field}: {error}");
            if (string.Equals(field, "Url", StringComparison.OrdinalIgnoreCase)) url = resolved;
            else value = resolved;
        }
        return (value, url, null);
    }

    /// <summary>
    /// The value, or the reason there is none — turning an ABSENT value into an error, which is
    /// what every caller but a presence test wants. A column that is not in the row is nearly
    /// always a mis-typed column name, and reporting it as an empty string would type nothing into
    /// a field and call the step a success.
    /// </summary>
    public static (string? Value, string? Error) Resolve(BindingRef binding, ReplayRunState state)
    {
        var (found, value, error) = Lookup(binding, state);
        if (error != null) return (null, error);
        if (!found) return (null, Missing(binding, state));
        return (value, null);
    }

    /// <summary>
    /// Three answers, not two: the value, or "legitimately absent", or "this binding is broken".
    /// <para>
    /// Only a presence test wants the middle one — see <see cref="Model.ConditionOp.Exists"/>.
    /// Keeping it separate is what lets a ragged dataset be branched on without also making a
    /// typo'd column name look like an empty one.
    /// </para>
    /// </summary>
    public static (bool Found, string? Value, string? Error) Lookup(BindingRef binding, ReplayRunState state)
    {
        string? core;
        switch (binding.Kind)
        {
            case BindingKind.Literal:
                core = binding.Literal ?? "";
                break;

            case BindingKind.StepOutput:
                // Keyed by step id, which is a GUID, so a source in another task resolves the same
                // way as one in this task as long as it has already run.
                if (!state.Outputs.TryGetValue(
                        ReplayRunState.OutputKey(binding.SourceStepId, binding.OutputField), out core))
                {
                    return (false, null, null);
                }
                break;

            case BindingKind.DatasetColumn:
                if (string.IsNullOrWhiteSpace(binding.ColumnName))
                    return (false, null, "no column name set");
                if (!state.Variables.TryGetValue(binding.ColumnName, out core))
                    return (false, null, null);
                break;

            case BindingKind.TaskInput:
                if (string.IsNullOrWhiteSpace(binding.ParameterName))
                    return (false, null, "no input name set");
                core = state.Input(binding.ParameterName);
                if (core == null) return (false, null, null);
                break;

            case BindingKind.EnvVar:
                if (string.IsNullOrWhiteSpace(binding.EnvVarName))
                    return (false, null, "no environment variable name set");
                core = Environment.GetEnvironmentVariable(binding.EnvVarName);
                if (core == null) return (false, null, null);
                break;

            default:
                return (false, null, $"{binding.Kind} bindings are not supported yet");
        }

        return (true, (binding.Prefix ?? "") + core + (binding.Suffix ?? ""), null);
    }

    /// <summary>
    /// Why a value that is not there is not there — worded for the mistake it usually is.
    /// <para>
    /// A dataset column gets two different messages, because they are two different mistakes. With
    /// no enclosing loop the binding is in the wrong place entirely; inside one, the row simply
    /// does not carry that column, and the fix is either the column's name or an <c>exists</c>
    /// guard in front of it.
    /// </para>
    /// </summary>
    private static string Missing(BindingRef binding, ReplayRunState state) => binding.Kind switch
    {
        BindingKind.StepOutput =>
            $"'{binding.OutputField}' has not been produced yet — the step that publishes it must run first",
        BindingKind.TaskInput =>
            $"nothing supplied the input '{binding.ParameterName}', and it has no default",
        BindingKind.DatasetColumn when !state.InRowScope =>
            $"no value for '{binding.ColumnName}' here — this binding needs an enclosing for-each over a dataset",
        BindingKind.DatasetColumn =>
            $"this row has no '{binding.ColumnName}' — check the column name, or guard the step with 'exists'",
        BindingKind.EnvVar => $"environment variable '{binding.EnvVarName}' is not set",
        _ => $"{binding.Kind} has no value here",
    };
}
