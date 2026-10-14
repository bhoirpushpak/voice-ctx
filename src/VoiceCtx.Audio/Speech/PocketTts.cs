using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using VoiceCtx.Audio.Dsp;

namespace VoiceCtx.Audio.Speech;

/// <summary>Per-language generation settings (voicectx.json next to the ONNX graphs).</summary>
public sealed class PocketTtsSettings
{
    public string Language { get; init; } = "";
    public string Name { get; init; } = "";
    public float Temperature { get; init; } = 0.3f;
    public int LsdSteps { get; init; } = 1;
    public float EosThreshold { get; init; } = -4f;
    public int? FramesAfterEos { get; init; }
    public bool AppendTerminalPunctuation { get; init; } = true;
    public bool CapitalizeFirstLetter { get; init; } = true;
    public bool RemoveSemicolons { get; init; }
    public bool PadShortInputs { get; init; }
    /// <summary>Strip punctuation before speaking (models trained without it, e.g. Hindi).</summary>
    public bool StripPunctuation { get; init; }
    public Dictionary<string, string> ReplaceCharacters { get; init; } = [];
    public string Credits { get; init; } = "";
    public string License { get; init; } = "";
}

/// <summary>A cloned voice: the conditioning frames one language model computed from a recording.</summary>
public sealed class PocketVoice(float[] frames, int frameCount, string language)
{
    internal float[] Frames { get; } = frames;
    internal int FrameCount { get; } = frameCount;
    public string Language { get; } = language;
}

/// <summary>
/// Kyutai Pocket TTS on ONNX Runtime (CPU): text → speech in a voice cloned from a short recording.
/// Five graphs: text conditioner, Mimi encoder (voice), FlowLM backbone (stateful, KV cache),
/// flow head (LSD sampler) and Mimi decoder (stateful, streaming). Runs on a worker thread.
/// </summary>
public sealed class PocketTts : IDisposable
{
    public const int SampleRate = 24000;
    public const int SamplesPerFrame = 1920;
    private const int LatentDim = 32;
    private const int ModelDim = 1024;
    private const int MaxTokensPerChunk = 50;
    private const int MinFramesBeforeEos = 6;
    private const int DecodeChunkFrames = 10;
    private const double MaxVoiceSeconds = 20;
    private const double TokensPerSecondEstimate = 3.0, GenSecondsPadding = 2.0;

    private static readonly Regex SentenceEnd = new(@"(?<=[.!?…।॥])\s+", RegexOptions.Compiled);
    private static readonly Regex ClauseEnd = new(@"(?<=[,;:،])\s+", RegexOptions.Compiled);
    private static readonly Regex Punctuation = new(@"[\p{P}\p{S}]+", RegexOptions.Compiled);

    private readonly InferenceSession _text, _encoder, _main, _flow, _decoder;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly float[]? _bosBeforeVoice;
    private readonly StateSet _mainState, _decoderState;
    private readonly Random _random = new();
    private readonly object _gate = new();
    private (PocketVoice Voice, byte[][] State)? _voiceSnapshot;

    public PocketTts(string folder, int threads = 2)
    {
        Folder = folder;
        Settings = JsonSerializer.Deserialize<PocketTtsSettings>(
            File.ReadAllText(Path.Combine(folder, "voicectx.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Bad voicectx.json");

        using (var stream = File.OpenRead(Path.Combine(folder, "tokenizer.model")))
            _tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);

        _text = Open("text_conditioner.onnx", threads);
        _encoder = Open("mimi_encoder.onnx", threads, arena: false);
        _main = Open("flow_lm_main_int8.onnx", threads);
        _flow = Open("flow_lm_flow_int8.onnx", 1);
        _decoder = Open("mimi_decoder_int8.onnx", threads, arena: false);
        _mainState = new StateSet(_main);
        _decoderState = new StateSet(_decoder);

        string bos = Path.Combine(folder, "bos_before_voice.npy");
        if (File.Exists(bos)) _bosBeforeVoice = ReadNpyFloat32(bos, ModelDim);
    }

    public string Folder { get; }
    public PocketTtsSettings Settings { get; }

    internal IReadOnlyList<int> Tokenize(string text) => _tokenizer.EncodeToIds(text);

    /// <summary>Folders under <paramref name="root"/> that hold a complete Pocket TTS model.</summary>
    public static bool IsModelFolder(string folder) =>
        new[] { "voicectx.json", "tokenizer.model", "text_conditioner.onnx", "mimi_encoder.onnx",
                "flow_lm_main_int8.onnx", "flow_lm_flow_int8.onnx", "mimi_decoder_int8.onnx" }
            .All(f => File.Exists(Path.Combine(folder, f)));

    private InferenceSession Open(string file, int threads, bool arena = true)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        if (!arena)
        {
            options.EnableCpuMemArena = false;
            options.EnableMemoryPattern = false;
        }
        return new InferenceSession(Path.Combine(Folder, file), options);
    }

    // ------------------------------------------------------------------ voice

    /// <summary>Computes this language's conditioning for a voice recording (any rate, mono).</summary>
    public PocketVoice EncodeVoice(float[] samples, int sampleRate)
    {
        var audio = Resample(samples, sampleRate, SampleRate);
        int max = (int)(MaxVoiceSeconds * SampleRate);
        if (audio.Length > max) Array.Resize(ref audio, max);
        float peak = audio.Length == 0 ? 0 : audio.Max(Math.Abs);
        if (peak > 1) for (int i = 0; i < audio.Length; i++) audio[i] /= peak;

        lock (_gate)
        {
            using var input = OrtValue.CreateTensorValueFromMemory(audio, [1, 1, audio.Length]);
            using var outputs = _encoder.Run(new RunOptions(), ["audio"], [input], _encoder.OutputNames);
            var data = outputs[0].GetTensorDataAsSpan<float>();
            int frames = data.Length / ModelDim;
            int bos = _bosBeforeVoice != null ? 1 : 0;
            var result = new float[(frames + bos) * ModelDim];
            _bosBeforeVoice?.CopyTo(result, 0);
            data.CopyTo(result.AsSpan(bos * ModelDim));
            return new PocketVoice(result, frames + bos, Settings.Language);
        }
    }

    // ------------------------------------------------------------------ synthesis

    /// <summary>Speaks <paramref name="text"/> in <paramref name="voice"/>; returns 24 kHz mono audio.</summary>
    public float[] Synthesize(string text, PocketVoice voice, CancellationToken cancellation = default)
    {
        var audio = new List<float>();
        foreach (var chunk in SplitIntoChunks(text))
        {
            cancellation.ThrowIfCancellationRequested();
            var (prepared, framesAfterEosGuess) = Prepare(chunk);
            if (prepared.Length == 0) continue;
            var piece = SynthesizeChunk(prepared, voice, Settings.FramesAfterEos ?? framesAfterEosGuess + 2, cancellation);
            Crossfade(audio, piece);
        }
        return audio.ToArray();
    }

    private float[] SynthesizeChunk(string text, PocketVoice voice, int framesAfterEos, CancellationToken cancellation)
    {
        lock (_gate)
        {
            var ids = _tokenizer.EncodeToIds(text).Select(i => (long)i).ToArray();
            if (ids.Length == 0) return [];
            int maxFrames = (int)Math.Ceiling((ids.Length / TokensPerSecondEstimate + GenSecondsPadding) * 12.5);

            PrimeVoice(voice);
            RunTextConditioning(ids);
            _decoderState.Reset();

            var output = new List<float>(maxFrames * SamplesPerFrame);
            var latents = new List<float>(DecodeChunkFrames * LatentDim);
            var previous = new float[LatentDim];
            Array.Fill(previous, float.NaN);            // NaN = beginning of speech
            var x = new float[LatentDim];
            int? eosStep = null;
            float std = MathF.Sqrt(Settings.Temperature);

            for (int step = 0; step < maxFrames; step++)
            {
                cancellation.ThrowIfCancellationRequested();
                var (conditioning, eosLogit) = RunBackbone(previous);
                if (eosStep == null && eosLogit > Settings.EosThreshold && step >= MinFramesBeforeEos) eosStep = step;
                if (eosStep != null && step >= eosStep + framesAfterEos) break;

                for (int i = 0; i < LatentDim; i++) x[i] = std > 0 ? NextGaussian() * std : 0f;
                RunFlow(conditioning, x);
                x.CopyTo(previous, 0);
                latents.AddRange(x);
                if (latents.Count >= DecodeChunkFrames * LatentDim) Decode(latents, output);
            }
            Decode(latents, output);
            return output.ToArray();
        }
    }

    /// <summary>Feeds the voice into a fresh backbone state (cached per voice, then restored).</summary>
    private void PrimeVoice(PocketVoice voice)
    {
        if (_voiceSnapshot is { } snap && ReferenceEquals(snap.Voice, voice))
        {
            _mainState.Restore(snap.State);
            return;
        }
        _mainState.Reset();
        using var sequence = Empty([1, 0, LatentDim]);
        using var frames = OrtValue.CreateTensorValueFromMemory(voice.Frames, [1, voice.FrameCount, ModelDim]);
        RunMain(sequence, frames).Dispose();
        _voiceSnapshot = (voice, _mainState.Snapshot());
    }

    private void RunTextConditioning(long[] ids)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
        using var outputs = _text.Run(new RunOptions(), ["token_ids"], [input], _text.OutputNames);
        var embeddings = outputs[0].GetTensorDataAsSpan<float>().ToArray();
        using var sequence = Empty([1, 0, LatentDim]);
        using var text = OrtValue.CreateTensorValueFromMemory(embeddings, [1, ids.Length, ModelDim]);
        RunMain(sequence, text).Dispose();
    }

    private (float[] Conditioning, float EosLogit) RunBackbone(float[] previous)
    {
        using var sequence = OrtValue.CreateTensorValueFromMemory(previous, [1, 1, LatentDim]);
        using var text = Empty([1, 0, ModelDim]);
        using var outputs = RunMain(sequence, text);
        return (outputs[0].GetTensorDataAsSpan<float>().ToArray(), outputs[1].GetTensorDataAsSpan<float>()[0]);
    }

    private OutputBatch RunMain(OrtValue sequence, OrtValue text)
    {
        var inputs = new Dictionary<string, OrtValue> { ["sequence"] = sequence, ["text_embeddings"] = text };
        return _mainState.Run(_main, inputs, ["conditioning", "eos_logit"]);
    }

    private readonly float[] _s = new float[1], _t = new float[1];

    private void RunFlow(float[] conditioning, float[] x)
    {
        int steps = Math.Max(1, Settings.LsdSteps);
        for (int i = 0; i < steps; i++)
        {
            _s[0] = (float)i / steps;
            _t[0] = (float)(i + 1) / steps;
            using var c = OrtValue.CreateTensorValueFromMemory(conditioning, [ModelDim]);
            using var s = OrtValue.CreateTensorValueFromMemory(_s, [1, 1]);
            using var t = OrtValue.CreateTensorValueFromMemory(_t, [1, 1]);
            using var xv = OrtValue.CreateTensorValueFromMemory(x, [1, LatentDim]);
            using var outputs = _flow.Run(new RunOptions(), ["c", "s", "t", "x"], [c, s, t, xv], ["flow_dir"]);
            var dir = outputs[0].GetTensorDataAsSpan<float>();
            for (int j = 0; j < LatentDim; j++) x[j] += dir[j] / steps;
        }
    }

    private void Decode(List<float> latents, List<float> output)
    {
        if (latents.Count == 0) return;
        var data = latents.ToArray();
        latents.Clear();
        using var latent = OrtValue.CreateTensorValueFromMemory(data, [1, data.Length / LatentDim, LatentDim]);
        using var outputs = _decoderState.Run(_decoder, new Dictionary<string, OrtValue> { ["latent"] = latent }, ["audio_frame"]);
        foreach (float v in outputs[0].GetTensorDataAsSpan<float>()) output.Add(v);
    }

    // ------------------------------------------------------------------ text

    /// <summary>Splits text into sentence groups the model can say in one go (≤ 50 tokens).</summary>
    internal List<string> SplitIntoChunks(string text)
    {
        var pieces = new List<string>();
        foreach (var sentence in SentenceEnd.Split(text.Trim()))
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;
            if (CountTokens(sentence) <= MaxTokensPerChunk) { pieces.Add(sentence.Trim()); continue; }
            foreach (var clause in ClauseEnd.Split(sentence))
                pieces.AddRange(SplitByWords(clause.Trim()));
        }

        var chunks = new List<string>();
        string current = "";
        int currentTokens = 0;
        foreach (var piece in pieces)
        {
            int tokens = CountTokens(piece);
            if (current.Length > 0 && currentTokens + tokens > MaxTokensPerChunk)
            {
                chunks.Add(current);
                current = "";
                currentTokens = 0;
            }
            current = current.Length == 0 ? piece : current + " " + piece;
            currentTokens += tokens;
        }
        if (current.Length > 0) chunks.Add(current);
        return chunks;
    }

    private IEnumerable<string> SplitByWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string current = "";
        foreach (var word in words)
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && CountTokens(candidate) > MaxTokensPerChunk - 2)
            {
                yield return current;
                current = word;
            }
            else current = candidate;
        }
        if (current.Length > 0) yield return current;
    }

    private int CountTokens(string text) => _tokenizer.CountTokens(Prepare(text).Text);

    /// <summary>Port of pocket_tts prepare_text_prompt (plus punctuation stripping for Hindi).</summary>
    internal (string Text, int FramesAfterEosGuess) Prepare(string raw) => PrepareText(raw, Settings);

    internal static (string Text, int FramesAfterEosGuess) PrepareText(string raw, PocketTtsSettings settings)
    {
        string text = raw.Trim();
        if (settings.ReplaceCharacters.Count > 0)
        {
            foreach (var (from, to) in settings.ReplaceCharacters) text = text.Replace(from, to);
            text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            text = Regex.Replace(text, @"([.!?…])\s*[,;:]", "$1");
        }
        if (settings.StripPunctuation)
            text = string.Join(' ', Punctuation.Replace(text, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        text = text.Replace('\n', ' ').Replace('\r', ' ').Replace("  ", " ").Trim();
        if (text.Length == 0) return ("", 0);
        if (settings.RemoveSemicolons) text = text.Replace(';', ',');

        int words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        int framesAfterEos = words <= 4 ? 3 : 1;
        if (settings.CapitalizeFirstLetter && char.IsLower(text[0])) text = char.ToUpperInvariant(text[0]) + text[1..];
        if (settings.AppendTerminalPunctuation) text = EnsureTerminalPunctuation(text);
        if (settings.PadShortInputs && words < 5) text = new string(' ', 8) + text;
        return (text, framesAfterEos);
    }

    private static string EnsureTerminalPunctuation(string text)
    {
        const string closers = "\"'”’)]»";
        string core = text.TrimEnd((closers + " ").ToCharArray());
        string tail = text[core.Length..].Trim();
        if (core.Length == 0 || ".!?…".Contains(core[^1])) return text;
        if (",;:-–—".Contains(core[^1])) return core.TrimEnd(",;:-–— ".ToCharArray()) + "." + tail;
        return text + ".";
    }

    // ------------------------------------------------------------------ helpers

    private float NextGaussian()
    {
        double u1 = 1.0 - _random.NextDouble(), u2 = _random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }

    private static OrtValue Empty(long[] shape) =>
        OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float, shape);

    private static void Crossfade(List<float> audio, float[] next)
    {
        int fade = Math.Min(240, Math.Min(audio.Count, next.Length));
        int start = audio.Count - fade;
        for (int i = 0; i < fade; i++)
        {
            float t = (float)i / fade;
            audio[start + i] = audio[start + i] * (1 - t) + next[i] * t;
        }
        for (int i = fade; i < next.Length; i++) audio.Add(next[i]);
    }

    internal static float[] Resample(float[] input, int from, int to)
    {
        if (from == to) return (float[])input.Clone();
        double ratio = (double)from / to;
        var resampler = new StreamResampler(input.Length);
        var output = new float[StreamResampler.MaxOutputLength(input.Length, ratio) + 8];
        int produced = resampler.Process(input, output, ratio);
        Array.Resize(ref output, produced);
        return output;
    }

    private static float[] ReadNpyFloat32(string path, int count)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 10 || bytes[0] != 0x93 || bytes[1] != (byte)'N') throw new InvalidDataException("Not a .npy file: " + path);
        int headerLength = bytes[6] == 1 ? BitConverter.ToUInt16(bytes, 8) : BitConverter.ToInt32(bytes, 8);
        int offset = (bytes[6] == 1 ? 10 : 12) + headerLength;
        string header = System.Text.Encoding.ASCII.GetString(bytes, bytes[6] == 1 ? 10 : 12, headerLength);
        if (!header.Contains("'<f4'") || bytes.Length - offset != count * 4) throw new InvalidDataException("Unexpected BOS tensor: " + path);
        var result = new float[count];
        Buffer.BlockCopy(bytes, offset, result, 0, count * 4);
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _mainState.Dispose();
            _decoderState.Dispose();
            _text.Dispose();
            _encoder.Dispose();
            _main.Dispose();
            _flow.Dispose();
            _decoder.Dispose();
        }
    }

    // ------------------------------------------------------------------ state plumbing

    /// <summary>Outputs of one run; disposes the non-state outputs.</summary>
    private sealed class OutputBatch(List<OrtValue> values) : IDisposable
    {
        public OrtValue this[int index] => values[index];
        public void Dispose() { foreach (var v in values) v.Dispose(); }
    }

    /// <summary>
    /// The recurrent state of a stateful graph ("state_N" in, "out_state_N" out), double-buffered:
    /// each run reads one set of preallocated tensors and writes the other.
    /// </summary>
    private sealed class StateSet : IDisposable
    {
        private readonly string[] _inputs, _outputs;
        private readonly OrtValue[][] _buffers = new OrtValue[2][];
        private int _current;

        public StateSet(InferenceSession session)
        {
            _inputs = session.InputNames.Where(n => n.StartsWith("state_", StringComparison.Ordinal))
                .OrderBy(n => int.Parse(n.AsSpan(6))).ToArray();
            _outputs = _inputs.Select(n => "out_" + n).ToArray();
            for (int b = 0; b < 2; b++)
            {
                _buffers[b] = _inputs.Select(n =>
                {
                    var meta = session.InputMetadata[n];
                    long[] shape = meta.Dimensions.Select(d => (long)Math.Max(d, 0)).ToArray();
                    return OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, meta.ElementDataType, shape);
                }).ToArray();
            }
            Reset();
        }

        public void Reset()
        {
            _current = 0;
            foreach (var set in _buffers)
                foreach (var v in set) v.GetTensorMutableRawData().Clear();
        }

        public byte[][] Snapshot() => _buffers[_current].Select(v => v.GetTensorMutableRawData().ToArray()).ToArray();

        public void Restore(byte[][] snapshot)
        {
            _current = 0;
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].CopyTo(_buffers[0][i].GetTensorMutableRawData());
        }

        public OutputBatch Run(InferenceSession session, Dictionary<string, OrtValue> inputs, string[] outputNames)
        {
            using var binding = session.CreateIoBinding();
            foreach (var (name, value) in inputs) binding.BindInput(name, value);
            var read = _buffers[_current];
            var write = _buffers[1 - _current];
            for (int i = 0; i < _inputs.Length; i++)
            {
                binding.BindInput(_inputs[i], read[i]);
                binding.BindOutput(_outputs[i], write[i]);
            }
            var memory = OrtMemoryInfo.DefaultInstance;
            foreach (var name in outputNames) binding.BindOutputToDevice(name, memory);
            using var options = new RunOptions();
            session.RunWithBinding(options, binding);
            var results = binding.GetOutputValues();
            _current = 1 - _current;

            // GetOutputValues returns every bound output in binding order; keep only the named ones.
            var named = new List<OrtValue>();
            int stateOutputs = _inputs.Length;
            for (int i = 0; i < results.Count; i++)
            {
                if (i >= stateOutputs) named.Add(results[i]);
                else results[i].Dispose();
            }
            return new OutputBatch(named);
        }

        public void Dispose()
        {
            foreach (var set in _buffers)
                foreach (var v in set) v.Dispose();
        }
    }
}
