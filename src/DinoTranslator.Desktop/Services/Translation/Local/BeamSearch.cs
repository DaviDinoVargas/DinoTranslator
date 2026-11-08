namespace DinoTranslator.Desktop.Services.Translation.Local;

/// <summary>
/// Busca em feixe da tradução: em vez de escolher a palavra mais provável de cada vez (que pode
/// levar a uma frase ruim por causa de uma escolha boa só no começo), acompanha os <see cref="Width"/>
/// caminhos mais prováveis e, no fim, fica com o melhor — com a pontuação dividida pelo tamanho,
/// para não favorecer frases curtas demais.
/// </summary>
public sealed class BeamSearch(int width, int maxTokens)
{
    private readonly List<(List<long> Tokens, double Score)> _finished = [];
    private List<(List<long> Tokens, double Score)> _alive = [([], 0)];
    private int _steps;

    public int Width { get; } = width;

    /// <summary>Acabou: os caminhos terminados já superam os que ainda estão abertos, ou chegou ao limite.</summary>
    public bool Done { get; private set; }

    /// <summary>A última palavra de cada caminho aberto: a entrada do próximo passo.</summary>
    public long[] LastTokens() => _alive.Select(a => a.Tokens[^1]).ToArray();

    /// <summary>
    /// Avança um passo com as pontuações do modelo (<c>logits</c> [caminhos, posições, vocabulário]).
    /// Devolve, para cada caminho novo, de qual caminho anterior ele veio (para reordenar o cache).
    /// No primeiro passo há um caminho só; depois, sempre <see cref="Width"/>.
    /// </summary>
    public int[] Advance(ReadOnlySpan<float> logits, int positions, int vocabulary)
    {
        var candidates = new List<(int Parent, long Token, double Score)>(_alive.Count * 2 * Width);
        Span<long> tokens = stackalloc long[2 * Width];
        Span<double> logProbs = stackalloc double[2 * Width];
        for (var beam = 0; beam < _alive.Count; beam++)
        {
            var row = logits.Slice((((beam + 1) * positions) - 1) * vocabulary, vocabulary);
            var found = TopLogProbs(row, tokens, logProbs);
            for (var i = 0; i < found; i++)
            {
                candidates.Add((beam, tokens[i], _alive[beam].Score + logProbs[i]));
            }
        }
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var next = new List<(List<long> Tokens, double Score)>(Width);
        var parents = new List<int>(Width);
        for (var rank = 0; rank < candidates.Count && next.Count < Width; rank++)
        {
            var (parent, token, score) = candidates[rank];
            var path = _alive[parent].Tokens;
            if (token == NllbVocabulary.EndOfSentence)
            {
                // Fim de frase só conta entre os melhores deste passo.
                if (rank < Width)
                {
                    _finished.Add((path, score / (path.Count + 1)));
                }
                continue;
            }
            var extended = new List<long>(path.Count + 1);
            extended.AddRange(path);
            extended.Add(token);
            if (NllbVocabulary.IsLooping(extended))
            {
                continue;
            }
            next.Add((extended, score));
            parents.Add(parent);
        }
        // O lote do modelo tem sempre o mesmo tamanho: faltando caminhos, repete o último sem chance de vencer.
        while (next.Count is > 0 && next.Count < Width)
        {
            next.Add((next[^1].Tokens, double.NegativeInfinity));
            parents.Add(parents[^1]);
        }

        _alive = next;
        _steps++;
        Done = _alive.Count == 0
            || _steps >= maxTokens
            || (_finished.Count >= Width && _finished.Max(f => f.Score) >= _alive.Max(a => a.Score / a.Tokens.Count));
        return [.. parents];
    }

    /// <summary>O melhor caminho, terminado ou não (sem o fim de frase).</summary>
    public IReadOnlyList<long> Best() =>
        _finished
            .Concat(_alive.Where(a => a.Tokens.Count > 0).Select(a => (a.Tokens, Score: a.Score / a.Tokens.Count)))
            .MaxBy(p => p.Score).Tokens ?? [];

    /// <summary>
    /// As 2×largura palavras mais prováveis da linha, com a probabilidade em log. Só contam o fim de
    /// frase e as palavras de texto (nem os códigos de idioma, nem a desconhecida).
    /// </summary>
    private static int TopLogProbs(ReadOnlySpan<float> row, Span<long> tokens, Span<double> logProbs)
    {
        var limit = (int)Math.Min(row.Length, NllbVocabulary.FirstLanguage);
        var first = (int)NllbVocabulary.Unknown + 1;

        var max = row[(int)NllbVocabulary.EndOfSentence];
        for (var id = first; id < limit; id++)
        {
            max = Math.Max(max, row[id]);
        }
        double sum = MathF.Exp(row[(int)NllbVocabulary.EndOfSentence] - max);
        for (var id = first; id < limit; id++)
        {
            sum += MathF.Exp(row[id] - max);
        }
        var logSum = max + Math.Log(sum);

        var count = 0;
        Insert((int)NllbVocabulary.EndOfSentence, row, tokens, logProbs, ref count);
        for (var id = first; id < limit; id++)
        {
            if (count < tokens.Length || row[id] > logProbs[count - 1])
            {
                Insert(id, row, tokens, logProbs, ref count);
            }
        }
        for (var i = 0; i < count; i++)
        {
            logProbs[i] -= logSum;
        }
        return count;
    }

    /// <summary>Insere na lista ordenada (maior primeiro) de tamanho fixo; logProbs guarda o valor bruto até o fim.</summary>
    private static void Insert(int id, ReadOnlySpan<float> row, Span<long> tokens, Span<double> values, ref int count)
    {
        var value = row[id];
        var position = count < tokens.Length ? count : tokens.Length - 1;
        while (position > 0 && values[position - 1] < value)
        {
            if (position < tokens.Length)
            {
                tokens[position] = tokens[position - 1];
                values[position] = values[position - 1];
            }
            position--;
        }
        tokens[position] = id;
        values[position] = value;
        if (count < tokens.Length)
        {
            count++;
        }
    }
}
