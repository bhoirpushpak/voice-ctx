using Microsoft.ML.OnnxRuntime;

namespace VoiceCtx.Audio.Translate;

/// <summary>Silero VAD v5/v6 (ONNX, CPU): speech probability for each 512-sample frame at 16 kHz.</summary>
public sealed class SileroVad : IDisposable
{
    public const int FrameSize = 512;
    private const int ContextSize = 64;

    private readonly InferenceSession _session;
    private readonly float[] _input = new float[ContextSize + FrameSize];
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly float[] _stateOut = new float[2 * 1 * 128];
    private readonly float[] _output = new float[1];
    private readonly OrtValue _inputValue, _stateValue, _srValue, _outputValue, _stateOutValue;
    private readonly RunOptions _run = new();
    private static readonly string[] InputNames = ["input", "state", "sr"];
    private static readonly string[] OutputNames = ["output", "stateN"];

    public SileroVad(string modelPath)
    {
        var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new InferenceSession(modelPath, options);
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, _input.Length]);
        _stateValue = OrtValue.CreateTensorValueFromMemory(_state, [2, 1, 128]);
        _srValue = OrtValue.CreateTensorValueFromMemory(new long[] { 16000 }, Array.Empty<long>());
        _outputValue = OrtValue.CreateTensorValueFromMemory(_output, [1, 1]);
        _stateOutValue = OrtValue.CreateTensorValueFromMemory(_stateOut, [2, 1, 128]);
    }

    /// <summary>Probability (0..1) that <paramref name="frame"/> (exactly 512 samples) contains speech.</summary>
    public float Process(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSize) throw new ArgumentException($"VAD frames must be {FrameSize} samples.", nameof(frame));
        frame.CopyTo(_input.AsSpan(ContextSize));
        _session.Run(_run, InputNames, [_inputValue, _stateValue, _srValue], OutputNames, [_outputValue, _stateOutValue]);
        _stateOut.CopyTo(_state, 0);
        _input.AsSpan(_input.Length - ContextSize).CopyTo(_input);   // last 64 samples become next context
        return _output[0];
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_input);
    }

    public void Dispose()
    {
        foreach (var v in new[] { _inputValue, _stateValue, _srValue, _outputValue, _stateOutValue }) v.Dispose();
        _run.Dispose();
        _session.Dispose();
    }
}
