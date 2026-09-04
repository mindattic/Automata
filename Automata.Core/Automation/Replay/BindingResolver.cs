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

    public static (string? Value, string? Error) Resolve(BindingRef binding, ReplayRunState state)
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
                    return (null, $"'{binding.OutputField}' has not been produced yet — the step that publishes it must run first");
                }
                break;

            case BindingKind.DatasetColumn:
                if (string.IsNullOrWhiteSpace(binding.ColumnName))
                    return (null, "no column name set");
                if (!state.Variables.TryGetValue(binding.ColumnName, out core))
                    return (null, $"no value for '{binding.ColumnName}' here — this binding needs an enclosing for-each over a dataset");
                break;

            case BindingKind.TaskInput:
                if (string.IsNullOrWhiteSpace(binding.ParameterName))
                    return (null, "no input name set");
                core = state.Input(binding.ParameterName);
                if (core == null)
                    return (null, $"nothing supplied the input '{binding.ParameterName}', and it has no default");
                break;

            case BindingKind.EnvVar:
                if (string.IsNullOrWhiteSpace(binding.EnvVarName))
                    return (null, "no environment variable name set");
                core = Environment.GetEnvironmentVariable(binding.EnvVarName);
                if (core == null) return (null, $"environment variable '{binding.EnvVarName}' is not set");
                break;

            default:
                return (null, $"{binding.Kind} bindings are not supported yet");
        }

        return ((binding.Prefix ?? "") + core + (binding.Suffix ?? ""), null);
    }
}
