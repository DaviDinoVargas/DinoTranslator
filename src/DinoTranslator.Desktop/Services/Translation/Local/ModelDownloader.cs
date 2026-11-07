using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace DinoTranslator.Desktop.Services.Translation.Local;

public sealed class ModelDownloadException(string message) : Exception(message);

/// <param name="Downloaded">Bytes já baixados de todos os arquivos.</param>
public readonly record struct DownloadProgress(long Downloaded, long Total, string FileName);

/// <summary>
/// Baixa os modelos por HTTPS para um arquivo temporário, calculando o SHA-256 no caminho.
/// Só move para o lugar definitivo se tamanho e hash baterem com o catálogo.
/// </summary>
public sealed class ModelDownloader(HttpClient http, ModelFolder folder)
{
    private const int BufferSize = 1024 * 1024;

    public async Task DownloadAsync(IReadOnlyList<ModelFile> files, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder.Path);
        var total = files.Sum(f => f.Size);
        long done = files.Where(folder.IsInstalled).Sum(f => f.Size);

        foreach (var file in files.Where(f => !folder.IsInstalled(f)))
        {
            await DownloadFileAsync(file, downloaded => progress?.Report(new DownloadProgress(done + downloaded, total, file.FileName)), cancellationToken);
            done += file.Size;
        }
        progress?.Report(new DownloadProgress(total, total, string.Empty));
    }

    private async Task DownloadFileAsync(ModelFile file, Action<long> report, CancellationToken cancellationToken)
    {
        if (file.Source.Scheme != Uri.UriSchemeHttps)
        {
            throw new ModelDownloadException("Endereço de modelo sem HTTPS.");
        }

        var target = folder.PathOf(file);
        var partial = target + ".part";
        try
        {
            using var response = await http.GetAsync(file.Source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ModelDownloadException($"Não foi possível baixar {file.FileName} (HTTP {(int)response.StatusCode}).");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long downloaded = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    downloaded += read;
                    if (downloaded > file.Size)
                    {
                        throw new ModelDownloadException($"{file.FileName} veio maior que o esperado e foi descartado.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    report(downloaded);
                }
                if (downloaded != file.Size)
                {
                    throw new ModelDownloadException($"{file.FileName} veio incompleto. Tente de novo.");
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(file.Sha256)))
            {
                throw new ModelDownloadException($"{file.FileName} não confere com a assinatura esperada e foi descartado.");
            }
            File.Move(partial, target, overwrite: true);
        }
        catch (HttpRequestException)
        {
            throw new ModelDownloadException($"Sem conexão para baixar {file.FileName}.");
        }
        finally
        {
            File.Delete(partial);
        }
    }
}
