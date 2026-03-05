namespace Honua.DevOps.Agent.Tests;

internal sealed class TestEnvironmentVariableScope : IDisposable
{
    private readonly Dictionary<string, string?> _originalValues = [];

    internal void Set(string variableName, string? value)
    {
        if (!_originalValues.ContainsKey(variableName))
        {
            _originalValues[variableName] = Environment.GetEnvironmentVariable(variableName);
        }

        Environment.SetEnvironmentVariable(variableName, value);
    }

    public void Dispose()
    {
        foreach ((string variableName, string? value) in _originalValues)
        {
            Environment.SetEnvironmentVariable(variableName, value);
        }
    }
}
