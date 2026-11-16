namespace Skua.Core.Headless;

/// <summary>
/// Account credentials. Only ever read from the environment or a file at run time; <see cref="ToString"/> never
/// shows the password.
/// </summary>
public sealed class AqwCredentials
{
    public const string UserVariable = "AQW_USER";
    public const string PasswordVariable = "AQW_PASS";

    public AqwCredentials(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        Username = username.Trim();
        Password = password;
    }

    public string Username { get; }

    public string Password { get; }

    /// <summary>From <c>AQW_USER</c> / <c>AQW_PASS</c>, or null when either is missing.</summary>
    public static AqwCredentials? FromEnvironment()
    {
        string? user = Environment.GetEnvironmentVariable(UserVariable);
        string? pass = Environment.GetEnvironmentVariable(PasswordVariable);
        return string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass) ? null : new AqwCredentials(user, pass);
    }

    /// <summary>
    /// From a dotenv-style file: <c>AQW_USER=...</c> and <c>AQW_PASS=...</c> lines (an optional <c>export</c> prefix,
    /// surrounding quotes and <c>#</c> comment lines are allowed).
    /// </summary>
    /// <exception cref="InvalidDataException">The file lacks either variable.</exception>
    public static AqwCredentials FromEnvFile(string path)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];
            values[key] = value;
        }

        if (!values.TryGetValue(UserVariable, out string? user) || string.IsNullOrWhiteSpace(user)
            || !values.TryGetValue(PasswordVariable, out string? pass) || string.IsNullOrEmpty(pass))
        {
            throw new InvalidDataException($"The credentials file must define {UserVariable} and {PasswordVariable}.");
        }
        return new AqwCredentials(user, pass);
    }

    public override string ToString()
    {
        return $"AqwCredentials({Username}, <password redacted>)";
    }
}
