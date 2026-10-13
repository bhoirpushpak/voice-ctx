namespace VoiceCtx.Audio.Translate;

/// <summary>
/// Cuts a 16 kHz stream into utterances using per-frame speech probabilities (hysteresis +
/// minimum silence). Keeps a short pre-roll so the first syllable isn't clipped, and force-splits
/// very long speech. Pure logic: no models, no threads.
/// </summary>
public sealed class SpeechSegmenter
{
    public const int SampleRate = 16000;

    private readonly int _frameSize;
    private readonly int _minSilenceFrames;
    private readonly int _minSpeechFrames;
    private readonly int _maxSamples;
    private readonly float[] _preRoll;
    private int _preRollCount;
    private readonly List<float> _current = new(SampleRate * 10);
    private bool _inSpeech;
    private int _silentFrames;
    private int _speechFrames;

    /// <param name="frameSize">Samples per probability frame (Silero uses 512 at 16 kHz).</param>
    public SpeechSegmenter(int frameSize = 512, double minSilenceSeconds = 0.6, double minSpeechSeconds = 0.25,
        double maxSeconds = 20, double preRollSeconds = 0.2)
    {
        _frameSize = frameSize;
        _minSilenceFrames = Math.Max(1, (int)Math.Ceiling(minSilenceSeconds * SampleRate / frameSize));
        _minSpeechFrames = Math.Max(1, (int)Math.Ceiling(minSpeechSeconds * SampleRate / frameSize));
        _maxSamples = (int)(maxSeconds * SampleRate);
        _preRoll = new float[(int)(preRollSeconds * SampleRate)];
    }

    public float StartThreshold { get; init; } = 0.5f;
    public float EndThreshold { get; init; } = 0.35f;

    /// <summary>True while an utterance is being collected.</summary>
    public bool InSpeech => _inSpeech;

    /// <summary>Feeds one frame and its speech probability. Returns a finished utterance, or null.</summary>
    public float[]? Push(ReadOnlySpan<float> frame, float speechProbability)
    {
        if (!_inSpeech)
        {
            if (speechProbability >= StartThreshold)
            {
                _inSpeech = true;
                _silentFrames = 0;
                _speechFrames = 1;
                _current.Clear();
                AppendPreRoll();
                Append(frame);
            }
            else
            {
                AddToPreRoll(frame);
            }
            return null;
        }

        Append(frame);
        if (speechProbability >= EndThreshold)
        {
            _silentFrames = 0;
            _speechFrames++;
        }
        else
        {
            _silentFrames++;
        }

        if (_silentFrames >= _minSilenceFrames || _current.Count >= _maxSamples)
        {
            _inSpeech = false;
            bool longEnough = _speechFrames >= _minSpeechFrames;
            int trailing = Math.Min(_current.Count, Math.Max(0, (_silentFrames - 3) * _frameSize));
            var result = longEnough ? _current.GetRange(0, _current.Count - trailing).ToArray() : null;
            _current.Clear();
            _preRollCount = 0;
            return result;
        }
        return null;
    }

    public void Reset()
    {
        _inSpeech = false;
        _current.Clear();
        _preRollCount = 0;
    }

    private void Append(ReadOnlySpan<float> frame)
    {
        foreach (float s in frame) _current.Add(s);
    }

    private void AddToPreRoll(ReadOnlySpan<float> frame)
    {
        if (_preRoll.Length == 0) return;
        if (frame.Length >= _preRoll.Length)
        {
            frame[^_preRoll.Length..].CopyTo(_preRoll);
            _preRollCount = _preRoll.Length;
            return;
        }
        int keep = Math.Min(_preRollCount, _preRoll.Length - frame.Length);
        Array.Copy(_preRoll, _preRollCount - keep, _preRoll, 0, keep);
        frame.CopyTo(_preRoll.AsSpan(keep));
        _preRollCount = keep + frame.Length;
    }

    private void AppendPreRoll()
    {
        for (int i = 0; i < _preRollCount; i++) _current.Add(_preRoll[i]);
        _preRollCount = 0;
    }
}
