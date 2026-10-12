using Microsoft.ML.OnnxRuntime;

namespace VoiceCtx.Audio.AiVoice;

/// <summary>Converts one block: a fixed 16 kHz context window in, the newest generated audio out.</summary>
public interface IVoiceConverter : IDisposable
{
    /// <summary>16 kHz samples the converter needs per call (context + new block).</summary>
    int ContextSamples16k { get; }

    /// <summary>New 16 kHz samples consumed per call.</summary>
    int BlockSamples16k { get; }

    int OutputSampleRate { get; }

    /// <summary>Samples returned per call: the block plus a leading crossfade margin.</summary>
    int OutputSamples { get; }

    /// <summary>Leading samples of the output that overlap the previous block (for crossfading).</summary>
    int MarginSamples { get; }

    void Convert(ReadOnlySpan<float> context16k, float pitchShiftSemitones, Span<float> output);
}

/// <summary>ONNX Runtime sessions shared by every voice: HuBERT content encoder and RMVPE pitch.</summary>
public sealed class RvcRuntime : IDisposable
{
    private RvcRuntime(InferenceSession encoder, InferenceSession pitch, string deviceName, bool usesGpu)
    {
        Encoder = encoder;
        Pitch = pitch;
        DeviceName = deviceName;
        UsesGpu = usesGpu;
    }

    internal InferenceSession Encoder { get; }
    internal InferenceSession Pitch { get; }
    public string DeviceName { get; }
    public bool UsesGpu { get; }
    internal GpuAdapter? Adapter { get; private init; }

    /// <summary>Loads the shared models, preferring the discrete GPU via DirectML and falling back to CPU.</summary>
    public static RvcRuntime Load(VoiceLibrary library)
    {
        var adapter = GpuSelector.ChooseBest();
        if (adapter != null)
        {
            try
            {
                return new RvcRuntime(
                    new InferenceSession(library.EncoderPath, CreateOptions(adapter)),
                    new InferenceSession(library.PitchPath, CreateOptions(adapter)),
                    adapter.Name, usesGpu: true) { Adapter = adapter };
            }
            catch (OnnxRuntimeException) { /* fall back to CPU below */ }
        }
        return new RvcRuntime(
            new InferenceSession(library.EncoderPath, CreateOptions(null)),
            new InferenceSession(library.PitchPath, CreateOptions(null)),
            "CPU", usesGpu: false);
    }

    internal SessionOptions CreateVoiceOptions() => CreateOptions(UsesGpu ? Adapter : null);

    private static SessionOptions CreateOptions(GpuAdapter? adapter)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (adapter != null)
        {
            options.EnableMemoryPattern = false;           // required by the DirectML provider
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(adapter.Index);
        }
        else
        {
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        }
        return options;
    }

    public void Dispose()
    {
        Encoder.Dispose();
        Pitch.Dispose();
    }
}

/// <summary>
/// RVC v2 streaming conversion: HuBERT features (50 fps, repeated to 100 fps) + RMVPE pitch on the
/// whole context, decoded by the voice generator for the newest frames only. All tensors are
/// pre-allocated and bound once; <see cref="Convert"/> reuses them every call.
/// </summary>
public sealed class RvcConverter : IVoiceConverter
{
    private const int FeatureSize = 768;

    private readonly RvcRuntime _runtime;
    private readonly InferenceSession _generator;
    private readonly MelFrontEnd _mel = new();
    private readonly int _frames;
    private readonly int _tail;
    private readonly int _encoderFrames;
    private readonly int _pitchFrames;
    private readonly int _pitchPadded;

    private readonly float[] _wav;
    private readonly float[] _features;
    private readonly float[] _melBuffer;
    private readonly float[] _salience;
    private readonly float[] _f0;
    private readonly float[] _phone;
    private readonly long[] _pitch;
    private readonly float[] _nsff0;
    private readonly float[] _audio;

    private readonly OrtValue _wavValue, _featuresValue, _melValue, _salienceValue;
    private readonly OrtValue _phoneValue, _pitchValue, _nsff0Value, _sidValue, _noiseValue, _audioValue;
    private readonly RunOptions _runOptions = new();
    private readonly string[] _encoderIn = ["wav16k"], _encoderOut = ["features"];
    private readonly string[] _pitchIn, _pitchOut;
    private readonly string[] _generatorIn = ["phone", "pitch", "nsff0", "sid", "noise_scale"], _generatorOut = ["audio"];

    public RvcConverter(RvcRuntime runtime, VoiceInfo voice)
    {
        _runtime = runtime;
        Voice = voice;
        _frames = voice.Frames;
        _tail = voice.Tail;
        ContextSamples16k = (voice.BlockMs + voice.ContextMs) * 16;
        BlockSamples16k = voice.BlockMs * 16;
        OutputSampleRate = voice.SampleRate;
        int hop = voice.SampleRate / 100;
        OutputSamples = _tail * hop;
        MarginSamples = voice.MarginFrames * hop;

        _generator = new InferenceSession(voice.ModelPath, runtime.CreateVoiceOptions());
        ValidateGenerator();

        _encoderFrames = (ContextSamples16k - 400) / 320 + 1;
        _pitchFrames = MelFrontEnd.FrameCount(ContextSamples16k);
        _pitchPadded = MelFrontEnd.PaddedFrameCount(_pitchFrames);
        if (_encoderFrames * 2 != _frames || _pitchFrames < _frames)
            throw new InvalidOperationException($"Voice '{voice.Name}' does not match its streaming configuration.");

        _wav = new float[ContextSamples16k];
        _features = new float[_encoderFrames * FeatureSize];
        _melBuffer = new float[MelFrontEnd.Mels * _pitchPadded];
        _salience = new float[_pitchPadded * PitchMath.SalienceBins];
        _f0 = new float[_pitchPadded];
        _phone = new float[_frames * FeatureSize];
        _pitch = new long[_frames];
        _nsff0 = new float[_tail];
        _audio = new float[OutputSamples];

        _wavValue = OrtValue.CreateTensorValueFromMemory(_wav, [1, ContextSamples16k]);
        _featuresValue = OrtValue.CreateTensorValueFromMemory(_features, [1, _encoderFrames, FeatureSize]);
        _melValue = OrtValue.CreateTensorValueFromMemory(_melBuffer, [1, MelFrontEnd.Mels, _pitchPadded]);
        _salienceValue = OrtValue.CreateTensorValueFromMemory(_salience, [1, _pitchPadded, PitchMath.SalienceBins]);
        _phoneValue = OrtValue.CreateTensorValueFromMemory(_phone, [1, _frames, FeatureSize]);
        _pitchValue = OrtValue.CreateTensorValueFromMemory(_pitch, [1, _frames]);
        _nsff0Value = OrtValue.CreateTensorValueFromMemory(_nsff0, [1, _tail]);
        _sidValue = OrtValue.CreateTensorValueFromMemory(new long[] { 0 }, [1]);
        _noiseValue = OrtValue.CreateTensorValueFromMemory(new[] { 0.66666f }, Array.Empty<long>());
        _audioValue = OrtValue.CreateTensorValueFromMemory(_audio, [1, OutputSamples]);

        _pitchIn = [runtime.Pitch.InputMetadata.Keys.First()];
        _pitchOut = [runtime.Pitch.OutputMetadata.Keys.First()];
    }

    public VoiceInfo Voice { get; }
    public int ContextSamples16k { get; }
    public int BlockSamples16k { get; }
    public int OutputSampleRate { get; }
    public int OutputSamples { get; }
    public int MarginSamples { get; }

    public void Convert(ReadOnlySpan<float> context16k, float pitchShiftSemitones, Span<float> output)
    {
        context16k[..ContextSamples16k].CopyTo(_wav);

        _runtime.Encoder.Run(_runOptions, _encoderIn, [_wavValue], _encoderOut, [_featuresValue]);
        for (int f = 0; f < _frames; f++)
            _features.AsSpan((f / 2) * FeatureSize, FeatureSize).CopyTo(_phone.AsSpan(f * FeatureSize));

        _mel.Compute(_wav, _melBuffer);
        _runtime.Pitch.Run(_runOptions, _pitchIn, [_melValue], _pitchOut, [_salienceValue]);
        PitchMath.DecodeSalience(_salience, _pitchFrames, _f0);

        float ratio = MathF.Pow(2f, pitchShiftSemitones / 12f);
        int offset = _pitchFrames - _frames;
        for (int f = 0; f < _frames; f++)
        {
            float hz = _f0[offset + f] * ratio;
            _pitch[f] = PitchMath.Coarse(hz);
            if (f >= _frames - _tail) _nsff0[f - (_frames - _tail)] = hz;
        }

        _generator.Run(_runOptions, _generatorIn, [_phoneValue, _pitchValue, _nsff0Value, _sidValue, _noiseValue], _generatorOut, [_audioValue]);
        _audio.CopyTo(output);
    }

    private void ValidateGenerator()
    {
        var phone = _generator.InputMetadata.GetValueOrDefault("phone");
        var nsff0 = _generator.InputMetadata.GetValueOrDefault("nsff0");
        if (phone == null || nsff0 == null || phone.Dimensions[1] != _frames || nsff0.Dimensions[1] != _tail)
            throw new InvalidOperationException($"Voice model '{Voice.Name}' does not match its description.");
    }

    public void Dispose()
    {
        foreach (var value in new[] { _wavValue, _featuresValue, _melValue, _salienceValue, _phoneValue, _pitchValue, _nsff0Value, _sidValue, _noiseValue, _audioValue })
            value.Dispose();
        _runOptions.Dispose();
        _generator.Dispose();
    }
}
