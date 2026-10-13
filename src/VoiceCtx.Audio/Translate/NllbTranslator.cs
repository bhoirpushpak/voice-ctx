using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace VoiceCtx.Audio.Translate;

/// <summary>
/// NLLB-200 translation (ONNX, int8, CPU) with a SentencePiece tokenizer and greedy decoding over
/// the merged decoder's key/value cache. Cache tensors returned by one step are fed straight into
/// the next (no copies). Text is translated sentence by sentence, which NLLB handles far better
/// than long multi-sentence inputs.
/// </summary>
public sealed partial class NllbTranslator : IDisposable
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly SentencePieceTokenizer _spm;
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<int, string> _reverse;
    private readonly int _eos, _unk, _decoderStart;
    private readonly int _heads, _headDim;
    private readonly string[] _pastNames;
    private readonly string[] _decoderInputNames;
    private readonly string[] _decoderOutputNames;
    private readonly bool[] _isEncoderPast;
    private readonly RunOptions _run = new();

    /// <param name="folder">NLLB model folder.</param>
    /// <param name="threads">CPU threads (0 = automatic).</param>
    public NllbTranslator(string folder, int threads = 0)
    {
        SessionOptions Options() => new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = threads > 0 ? threads : DefaultThreads,
        };

        _encoder = new InferenceSession(Path.Combine(folder, "encoder_model.onnx"), Options());
        _decoder = new InferenceSession(Path.Combine(folder, "decoder_model_merged.onnx"), Options());

        using (var model = File.OpenRead(Path.Combine(folder, "sentencepiece.bpe.model")))
            _spm = SentencePieceTokenizer.Create(model, addBeginningOfSentence: false, addEndOfSentence: false);

        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "vocab.json")));
        _vocab = meta.RootElement.GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        _reverse = _vocab.ToDictionary(kv => kv.Value, kv => kv.Key);
        _eos = meta.RootElement.GetProperty("eos").GetInt32();
        _unk = meta.RootElement.GetProperty("unk").GetInt32();
        _decoderStart = meta.RootElement.GetProperty("decoderStart").GetInt32();

        _pastNames = _decoder.InputMetadata.Keys.Where(k => k.StartsWith("past_key_values.", StringComparison.Ordinal)).ToArray();
        _isEncoderPast = _pastNames.Select(n => n.Contains(".encoder.", StringComparison.Ordinal)).ToArray();
        var dims = _decoder.InputMetadata[_pastNames[0]].Dimensions;
        _heads = dims[1];
        _headDim = dims[3];
        _decoderInputNames = ["input_ids", "encoder_attention_mask", "encoder_hidden_states", "use_cache_branch", .. _pastNames];
        _decoderOutputNames = ["logits", .. _pastNames.Select(n => n.Replace("past_key_values.", "present."))];
    }

    /// <summary>Thread count used when none is given.</summary>
    public static int DefaultThreads { get; } = Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>True when the NLLB vocabulary contains this language token (e.g. "hin_Deva").</summary>
    public bool Supports(string nllbCode) => _vocab.ContainsKey(nllbCode);

    public string Translate(string text, string sourceNllb, string targetNllb)
    {
        if (!_vocab.TryGetValue(sourceNllb, out int srcLang) || !_vocab.TryGetValue(targetNllb, out int tgtLang))
            throw new ArgumentException($"Unsupported language pair {sourceNllb} → {targetNllb}.");

        var sentences = SentenceBreak().Split(text.Trim()).Where(s => s.Any(char.IsLetterOrDigit)).ToList();
        string separator = targetNllb is "zho_Hans" or "zho_Hant" or "jpn_Jpan" or "tha_Thai" ? "" : " ";
        return string.Join(separator, sentences.Select(s => TranslateSentence(s, srcLang, tgtLang)).Where(s => s.Length > 0));
    }

    private string TranslateSentence(string sentence, int srcLang, int tgtLang)
    {
        // Source: [src_lang] tokens [</s>]
        var ids = new List<long> { srcLang };
        foreach (var token in _spm.EncodeToTokens(sentence, out _, addBeginningOfSentence: false, addEndOfSentence: false))
            ids.Add(_vocab.TryGetValue(token.Value, out int id) ? id : _unk);
        ids.Add(_eos);
        int srcLen = ids.Count;

        using var inputIds = OrtValue.CreateTensorValueFromMemory(ids.ToArray(), [1, srcLen]);
        using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, srcLen).ToArray(), [1, srcLen]);
        using var encoderOut = _encoder.Run(_run, ["input_ids", "attention_mask"], [inputIds, mask], ["last_hidden_state"]);
        var hidden = encoderOut[0];

        var nextToken = new long[1];
        var useCache = new bool[1];
        using var tokenValue = OrtValue.CreateTensorValueFromMemory(nextToken, [1, 1]);
        using var useCacheValue = OrtValue.CreateTensorValueFromMemory(useCache, [1]);

        var past = new OrtValue[_pastNames.Length];
        for (int i = 0; i < past.Length; i++)
            past[i] = OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float, [1, _heads, 0, _headDim]);

        var output = new List<int>();
        int limit = srcLen * 3 + 10;
        nextToken[0] = _decoderStart;     // decoder input: [</s>, tgt_lang, ...]
        try
        {
            for (int step = 0; step <= limit; step++)
            {
                var inputs = new OrtValue[_decoderInputNames.Length];
                inputs[0] = tokenValue;
                inputs[1] = mask;
                inputs[2] = hidden;
                inputs[3] = useCacheValue;
                Array.Copy(past, 0, inputs, 4, past.Length);

                var results = _decoder.Run(_run, _decoderInputNames, inputs, _decoderOutputNames);
                long chosen;
                using (var logits = results[0])
                    chosen = step == 0 ? tgtLang : ArgMax(logits.GetTensorDataAsSpan<float>());

                // Decoder self-attention cache: always the newest. Cross-attention cache: from step 0 only.
                for (int i = 0; i < past.Length; i++)
                {
                    var present = results[i + 1];
                    if (step == 0 || !_isEncoderPast[i])
                    {
                        past[i].Dispose();
                        past[i] = present;
                    }
                    else
                    {
                        present.Dispose();
                    }
                }
                useCache[0] = true;

                if (chosen == _eos) break;
                if (chosen != tgtLang) output.Add((int)chosen);
                nextToken[0] = chosen;
            }
        }
        finally
        {
            foreach (var p in past) p.Dispose();
        }
        return Detokenize(output);
    }

    private static long ArgMax(ReadOnlySpan<float> logits)
    {
        int best = 0;
        for (int i = 1; i < logits.Length; i++) if (logits[i] > logits[best]) best = i;
        return best;
    }

    private string Detokenize(List<int> ids)
    {
        var sb = new StringBuilder();
        foreach (int id in ids)
        {
            if (!_reverse.TryGetValue(id, out var piece)) continue;
            if (piece.StartsWith('<') && piece.EndsWith('>')) continue;
            if (piece.Length == 8 && piece[3] == '_') continue;   // language tokens
            sb.Append(piece);
        }
        return sb.ToString().Replace('▁', ' ').Trim();
    }

    [GeneratedRegex(@"(?<=[.!?।。！？])\s+")]
    private static partial Regex SentenceBreak();

    public void Dispose()
    {
        _run.Dispose();
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
