using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace StructuredRAG.Core.Services;

/// <summary>
/// <see cref="ILlmClient"/> backed by the official OpenAI Codex CLI in headless mode
/// (`codex exec`). Authentication comes from the CLI's own ChatGPT login
/// (`codex login`, stored under ~/.codex), so a ChatGPT subscription can power the
/// offline compilation without managing an API key. Intended for the compiler only —
/// per-call latency is much higher than a raw HTTP endpoint.
/// </summary>
public class CodexCliService : ILlmClient
{
    private readonly ILogger<CodexCliService> _logger;
    private readonly string _command;
    private readonly string? _model;
    private readonly string[] _extraArgs;
    private readonly TimeSpan _timeout;

    public CodexCliService(IConfiguration configuration, ILogger<CodexCliService> logger)
    {
        _logger = logger;
        _command = configuration["CodexCli:Command"] ?? "codex";
        _model = configuration["CodexCli:Model"];
        _extraArgs = (configuration["CodexCli:ExtraArgs"] ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _timeout = TimeSpan.FromSeconds(configuration.GetValue("CodexCli:TimeoutSeconds", 600));
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default, string? system = null)
    {
        // --output-last-message is the stable contract for scripting codex exec:
        // the agent's final message lands in this file, regardless of what the CLI
        // prints to stdout (banners, progress, event logs).
        var outputFile = Path.Combine(Path.GetTempPath(), $"codex-last-message-{Guid.NewGuid():N}.txt");

        var startInfo = new ProcessStartInfo
        {
            FileName = _command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add("read-only");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        if (!string.IsNullOrWhiteSpace(_model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(_model);
        }
        foreach (var arg in _extraArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }
        startInfo.ArgumentList.Add("--output-last-message");
        startInfo.ArgumentList.Add(outputFile);
        startInfo.ArgumentList.Add("-"); // read the prompt from stdin

        try
        {
            Process process;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Starting '{_command}' returned no process handle");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not start '{_command}' ({ex.Message}). Is the Codex CLI installed (npm i -g @openai/codex) and on PATH, or set CodexCli:Command?", ex);
            }
            using var _ = process;

            // Drain stdout/stderr concurrently so the process can't block on a full pipe.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            var fullPrompt = string.IsNullOrWhiteSpace(system) ? prompt : $"{system}\n\n{prompt}";
            await process.StandardInput.WriteAsync(fullPrompt.AsMemory(), cancellationToken);
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"codex exec did not finish within {_timeout.TotalSeconds:0}s");
            }

            var stderr = await stderrTask;
            await stdoutTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"codex exec failed with exit code {process.ExitCode}: {Tail(stderr, 800)}");
            }

            if (!File.Exists(outputFile))
            {
                throw new InvalidOperationException(
                    $"codex exec succeeded but wrote no output file (is the CLI version too old for --output-last-message?). stderr: {Tail(stderr, 400)}");
            }

            var result = (await File.ReadAllTextAsync(outputFile, cancellationToken)).Trim();
            if (result.Length == 0)
            {
                throw new InvalidOperationException($"codex exec returned an empty message. stderr: {Tail(stderr, 400)}");
            }

            _logger.LogDebug("codex exec returned {Length} chars", result.Length);
            return result;
        }
        finally
        {
            try { File.Delete(outputFile); } catch { /* best effort */ }
        }
    }

    private static string Tail(string text, int maxLength) =>
        text.Length <= maxLength ? text : "…" + text[^maxLength..];
}
