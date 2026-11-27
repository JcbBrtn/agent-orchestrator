using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentOrchestrator.Core.Models;
using Microsoft.Extensions.AI;

namespace AgentOrchestrator.Core.Tools;

/// <summary>Built-in agent tools. File access is unrestricted; relative paths resolve against the run's working directory.</summary>
public class AgentToolbox(string workingDirectory, HttpClient? http = null)
{
    public const int MaxOutputChars = 30_000;
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", "bin", "obj", "node_modules", "packages" };

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    public string WorkingDirectory { get; } = workingDirectory;

    public IReadOnlyList<AIFunction> CreateFunctions(IEnumerable<string> enabled)
    {
        var set = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        var all = new (string Name, Delegate Impl, string Description)[]
        {
            (ToolNames.ReadFile, ReadFile, "Read a text file. Optionally restrict to a 1-based inclusive line range."),
            (ToolNames.CreateFile, CreateFile, "Create a new file (or overwrite if overwrite=true) with the given content. Parent directories are created."),
            (ToolNames.EditFile, EditFile, "Replace an exact string in a file. old_str must match exactly once unless replace_all=true. Line endings (CRLF/LF) are matched automatically."),
            (ToolNames.ListDirectory, ListDirectory, "List files and folders in a directory (non-recursive unless recursive=true)."),
            (ToolNames.SearchFiles, SearchFiles, "Search file contents with a regular expression under a directory. Returns path:line: text matches."),
            (ToolNames.WebRequest, WebRequest, "Make an HTTP request. Returns status code and body (HTML converted to text when as_text=true)."),
            (ToolNames.RunPowerShell, RunPowerShell, "Run a PowerShell script and return exit code, stdout and stderr."),
        };
        return all.Where(t => set.Contains(t.Name))
            .Select(t => AIFunctionFactory.Create(t.Impl, t.Name, t.Description))
            .ToList();
    }

    public string Resolve(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path));

    public static bool IsRisky(string toolName, IDictionary<string, object?>? args, string workingDirectory)
    {
        switch (toolName)
        {
            case ToolNames.RunPowerShell:
                return true;
            case ToolNames.CreateFile:
            case ToolNames.EditFile:
                var path = args is not null && args.TryGetValue("path", out var p) ? p?.ToString() : null;
                if (string.IsNullOrWhiteSpace(path))
                    return true;
                var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
                var root = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return !full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            case ToolNames.WebRequest:
                var method = args is not null && args.TryGetValue("method", out var m) ? m?.ToString() : null;
                return !string.IsNullOrEmpty(method) && !method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    && !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    public string ReadFile(
        [Description("File path (absolute or relative to the working directory).")] string path,
        [Description("Optional first line (1-based).")] int? start_line = null,
        [Description("Optional last line (1-based, inclusive).")] int? end_line = null)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
            return $"Error: file not found: {full}";
        var lines = File.ReadAllLines(full);
        var start = Math.Max(1, start_line ?? 1);
        var end = Math.Min(lines.Length, end_line ?? lines.Length);
        var sb = new StringBuilder();
        for (var i = start; i <= end; i++)
            sb.Append(i).Append(". ").AppendLine(lines[i - 1]);
        return Truncate(sb.ToString());
    }

    public string CreateFile(
        [Description("File path (absolute or relative to the working directory).")] string path,
        [Description("Full file content.")] string content,
        [Description("Overwrite if the file already exists.")] bool overwrite = false)
    {
        var full = Resolve(path);
        if (File.Exists(full) && !overwrite)
            return $"Error: file already exists: {full}. Use edit_file or set overwrite=true.";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return $"Created {full} ({content.Length} chars).";
    }

    public string EditFile(
        [Description("File path (absolute or relative to the working directory).")] string path,
        [Description("Exact text to replace.")] string old_str,
        [Description("Replacement text.")] string new_str,
        [Description("Replace every occurrence instead of requiring exactly one.")] bool replace_all = false)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
            return $"Error: file not found: {full}";
        if (string.IsNullOrEmpty(old_str))
            return "Error: old_str must not be empty.";
        var raw = File.ReadAllBytes(full);
        using var reader = new StreamReader(new MemoryStream(raw), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var encoding = reader.CurrentEncoding;

        var fileEol = text.Contains("\r\n") ? "\r\n" : "\n";
        var match = old_str;
        var replacement = new_str;
        var count = CountOccurrences(text, match);
        if (count == 0)
        {
            match = ToEol(old_str, fileEol);
            replacement = ToEol(new_str, fileEol);
            count = CountOccurrences(text, match);
        }
        if (count == 0)
            return NotFoundMessage(text, old_str);
        if (count > 1 && !replace_all)
            return $"Error: old_str matches {count} times; add more context or set replace_all=true.";

        var updated = encoding.GetBytes(text.Replace(match, replacement, StringComparison.Ordinal));
        var preamble = encoding.GetPreamble();
        if (preamble.Length > 0 && raw.AsSpan().StartsWith(preamble))
            updated = [.. preamble, .. updated];
        File.WriteAllBytes(full, updated);
        return $"Edited {full} ({count} replacement(s)).";
    }

    private static string ToEol(string value, string eol) =>
        value.Replace("\r\n", "\n").Replace("\n", eol);

    private static string NotFoundMessage(string text, string oldStr)
    {
        var target = oldStr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var lines = text.Split('\n');
        int best = -1, bestScore = 0;
        for (var i = 0; i < lines.Length && target.Length > 0; i++)
        {
            var line = lines[i].Trim();
            var score = line.Contains(target, StringComparison.Ordinal) ? target.Length : CommonPrefixLength(line, target);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        var hint = best < 0 ? "" : $" Closest line {best + 1}: {Truncate(lines[best].Trim(), 200)}";
        return $"Error: old_str not found in file.{hint} Match indentation and punctuation exactly.";
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return n;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public string ListDirectory(
        [Description("Directory path (absolute or relative). Defaults to the working directory.")] string? path = null,
        [Description("List recursively (max 500 entries).")] bool recursive = false)
    {
        var full = Resolve(string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!Directory.Exists(full))
            return $"Error: directory not found: {full}";
        var entries = recursive ? Walk(full).Take(500) : Directory.EnumerateFileSystemEntries(full).Take(500);
        var sb = new StringBuilder();
        foreach (var e in entries)
            sb.AppendLine(Directory.Exists(e) ? Path.GetRelativePath(full, e) + Path.DirectorySeparatorChar : Path.GetRelativePath(full, e));
        return sb.Length == 0 ? "(empty)" : Truncate(sb.ToString());
    }

    public string SearchFiles(
        [Description("Regular expression to search for.")] string pattern,
        [Description("Directory to search (defaults to working directory).")] string? path = null,
        [Description("File name glob, e.g. *.cs (default *).")] string? file_glob = null,
        [Description("Case-insensitive search.")] bool ignore_case = true)
    {
        var root = Resolve(string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!Directory.Exists(root))
            return $"Error: directory not found: {root}";
        var regex = new Regex(pattern, ignore_case ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(2));
        var glob = string.IsNullOrWhiteSpace(file_glob) ? "*" : file_glob;
        var sb = new StringBuilder();
        var matches = 0;
        foreach (var file in Walk(root).Where(File.Exists))
        {
            if (!FileSystemName(Path.GetFileName(file), glob))
                continue;
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { continue; }
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                sb.Append(Path.GetRelativePath(root, file)).Append(':').Append(i + 1).Append(": ").AppendLine(lines[i].Trim());
                if (++matches >= 200)
                    return Truncate(sb.Append("... (match limit reached)").ToString());
            }
        }
        return matches == 0 ? "No matches." : Truncate(sb.ToString());
    }

    public async Task<string> WebRequest(
        [Description("Absolute URL.")] string url,
        [Description("HTTP method (GET, POST, PUT, PATCH, DELETE, HEAD). Default GET.")] string method = "GET",
        [Description("Optional JSON object of request headers, e.g. {\"Accept\":\"application/json\"}.")] string? headers_json = null,
        [Description("Optional request body.")] string? body = null,
        [Description("Content type for the body (default application/json).")] string? content_type = null,
        [Description("Convert HTML responses to plain text.")] bool as_text = true,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url);
        if (!string.IsNullOrWhiteSpace(headers_json))
        {
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(headers_json) ?? [])
                request.Headers.TryAddWithoutValidation(k, v);
        }
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, content_type ?? "application/json");
        if (!request.Headers.UserAgent.Any())
            request.Headers.UserAgent.ParseAdd("AgentOrchestrator/1.0");

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (as_text && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            text = HtmlToText(text);
        return Truncate($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\nContent-Type: {mediaType}\n\n{text}");
    }

    public async Task<string> RunPowerShell(
        [Description("PowerShell script to run.")] string script,
        [Description("Working directory (defaults to the run working directory).")] string? working_directory = null,
        [Description("Timeout in seconds (default 120, max 1800).")] int timeout_seconds = 120,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellExe.Value,
            WorkingDirectory = Resolve(string.IsNullOrWhiteSpace(working_directory) ? "." : working_directory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'\n" + script)));

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeout_seconds, 1, 1800)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            if (cancellationToken.IsCancellationRequested)
                throw;
            return $"Error: timed out after {timeout_seconds}s.\nSTDOUT:\n{Truncate(await stdout)}";
        }
        return Truncate($"Exit code: {process.ExitCode}\nSTDOUT:\n{await stdout}\nSTDERR:\n{await stderr}");
    }

    private static readonly Lazy<string> PowerShellExe = new(() =>
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "pwsh.exe")))
                    return Path.Combine(dir, "pwsh.exe");
            }
            catch { /* bad PATH entry */ }
        }
        return "powershell.exe";
    });

    private static IEnumerable<string> Walk(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); } catch { continue; }
            foreach (var e in entries)
            {
                yield return e;
                if (Directory.Exists(e) && !SkippedDirs.Contains(Path.GetFileName(e)))
                    stack.Push(e);
            }
        }
    }

    private static bool FileSystemName(string name, string glob) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob, name, ignoreCase: true);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }
        return count;
    }

    internal static string HtmlToText(string html)
    {
        var text = Regex.Replace(html, @"<(script|style|noscript)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br|/p|/div|/h\d|/li|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        return Regex.Replace(text, @"\n\s*\n+", "\n\n").Trim();
    }

    private static string Truncate(string s) =>
        s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + $"\n... (truncated, {s.Length - MaxOutputChars} more chars)";
}
