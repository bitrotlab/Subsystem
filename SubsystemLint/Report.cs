namespace SubsystemLint;

internal sealed class Report
{
    private readonly List<(string Path, string Message)> _errors = [];
    private readonly List<(string Path, string Message)> _notes = [];

    public int Errors => _errors.Count;

    public void Error(string path, string message) => _errors.Add((path, message));
    public void Note(string path, string message) => _notes.Add((path, message));

    public void Print()
    {
        foreach (var (path, message) in _errors)
            Console.WriteLine($"ERROR  {path}\n       {message}\n");

        foreach (var (path, message) in _notes)
            Console.WriteLine($"note   {path}: {message}");

        Console.WriteLine(_errors.Count == 0
            ? "OK — no problems found."
            : $"{_errors.Count} problem(s) found.");
    }
}
