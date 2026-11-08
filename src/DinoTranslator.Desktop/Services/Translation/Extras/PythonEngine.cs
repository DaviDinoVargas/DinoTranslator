using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DinoTranslator.Desktop.Services.Translation.Extras;

public sealed class PythonEngineException(string message) : Exception(message);

/// <summary>
/// O motor auxiliar (Motor/motor.py) rodando no Python dos extras: faster-whisper e CTranslate2.
/// Conversa só pela entrada e saída padrão (uma mensagem JSON por linha), em modo isolado do Python
/// (sem variáveis PYTHON* nem pacotes do usuário). Se o app fechar, a entrada fecha e ele termina.
/// </summary>
public sealed class PythonEngine : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentQueue<string> _errors = new();
    private readonly HashSet<string> _loaded = [];
    private readonly Lock _gate = new();
    private long _next;

    private PythonEngine(Process process)
    {
        _process = process;
        _ = Task.Run(ReadAnswersAsync);
        _ = Task.Run(ReadErrorsAsync);
    }

    /// <summary>A placa de vídeo da NVIDIA respondeu (CUDA).</summary>
    public bool Cuda { get; private set; }

    public bool Running => !_process.HasExited;

    public static async Task<PythonEngine> StartAsync(ExtrasFolder extras, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(extras.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(extras.MotorScript)!,
        };
        foreach (var argument in new[] { "-I", "-X", "utf8", "-u", extras.MotorScript })
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var name in new[] { "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "PYTHONUSERBASE" })
        {
            start.Environment.Remove(name);
        }
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["HF_HUB_OFFLINE"] = "1";

        var process = Process.Start(start) ?? throw new PythonEngineException("O motor auxiliar não abriu.");
        var engine = new PythonEngine(process);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartTimeout);
            var hello = await engine.SendAsync(new JsonObject { ["op"] = "hello" }, timeout.Token);
            engine.Cuda = hello["cuda"]?.GetValue<bool>() == true;
            return engine;
        }
        catch
        {
            await engine.DisposeAsync();
            throw;
        }
    }

    /// <summary>Carrega um modelo (uma vez); "kind" = whisper, nllb ou marian.</summary>
    public async Task LoadAsync(string key, string kind, string path, bool gpu, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_loaded.Contains(key))
            {
                return;
            }
        }
        await SendAsync(new JsonObject
        {
            ["op"] = "load",
            ["key"] = key,
            ["kind"] = kind,
            ["path"] = path,
            ["device"] = gpu ? "cuda" : "cpu",
            ["compute"] = gpu ? "int8_float16" : "int8",
        }, cancellationToken);
        lock (_gate)
        {
            _loaded.Add(key);
        }
    }

    /// <summary>Descarrega o que não faz parte da combinação atual (libera a memória da placa).</summary>
    public async Task KeepOnlyAsync(IReadOnlySet<string> keys)
    {
        string[] unused;
        lock (_gate)
        {
            unused = _loaded.Where(k => !keys.Contains(k)).ToArray();
            _loaded.ExceptWith(unused);
        }
        foreach (var key in unused)
        {
            await SendAsync(new JsonObject { ["op"] = "unload", ["key"] = key }, CancellationToken.None);
        }
    }

    /// <summary>Roda um modelo carregado; "lane" separa o reconhecimento ("asr") da tradução ("mt").</summary>
    public Task<JsonObject> RunAsync(string lane, string key, JsonObject arguments, CancellationToken cancellationToken)
    {
        arguments["op"] = "run";
        arguments["lane"] = lane;
        arguments["key"] = key;
        return SendAsync(arguments, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(wait.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Já terminou.
            }
        }
        _process.Dispose();
        _write.Dispose();
        Fail("O motor auxiliar foi fechado.");
    }

    private async Task<JsonObject> SendAsync(JsonObject request, CancellationToken cancellationToken)
    {
        if (_process.HasExited)
        {
            throw new PythonEngineException(LastError("O motor auxiliar parou."));
        }
        var id = Interlocked.Increment(ref _next);
        request["id"] = id;
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var abandoned))
            {
                abandoned.TrySetCanceled(cancellationToken);
            }
        });
        var line = request.ToJsonString();
        await _write.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            _pending.TryRemove(id, out _);
            throw new PythonEngineException(LastError("O motor auxiliar parou."));
        }
        finally
        {
            _write.Release();
        }
        return await answer.Task;
    }

    private async Task ReadAnswersAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }
                if (message?["id"]?.GetValue<long>() is not { } id || !_pending.TryRemove(id, out var answer))
                {
                    continue;
                }
                if (message["ok"]?.GetValue<bool>() == true)
                {
                    answer.TrySetResult(message);
                }
                else
                {
                    answer.TrySetException(new PythonEngineException(message["error"]?.GetValue<string>() ?? "erro desconhecido"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Processo encerrado.
        }
        Fail(LastError("O motor auxiliar parou."));
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                _errors.Enqueue(line);
                while (_errors.Count > 20)
                {
                    _errors.TryDequeue(out _);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Processo encerrado.
        }
    }

    private void Fail(string message)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var answer))
            {
                answer.TrySetException(new PythonEngineException(message));
            }
        }
    }

    /// <summary>A última linha de erro do Python ajuda a entender a falha (sem caminhos do usuário na tela).</summary>
    private string LastError(string fallback) =>
        _errors.LastOrDefault(l => l.Contains("Error", StringComparison.Ordinal)) is { } error ? $"{fallback} ({error.Trim()})" : fallback;
}
