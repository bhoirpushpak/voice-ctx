using System.Collections.Concurrent;

namespace VoiceCtx.Audio.Speech;

/// <summary>
/// Queue of synthesized speech shared by the UI ("Type to speak") and Live translate. The audio
/// thread plays clips into the live mic path, where they flow through AI Voice and effects exactly
/// like your own voice. Also controls how loud your real mic is while (or whenever) speech is used.
/// </summary>
public sealed class SpeechInjector
{
    private readonly ConcurrentQueue<SpeechClip> _queue = new();
    private volatile int _clearGeneration;
    private volatile bool _speaking;
    private volatile float _duckGain;
    private volatile float _baseMicGain = 1f;
    private volatile float _volume = 1f;

    /// <summary>Adds a clip to play after any clips already queued.</summary>
    public void Enqueue(SpeechClip clip)
    {
        if (clip.Samples.Length > 0 && clip.SampleRate > 0) _queue.Enqueue(clip);
    }

    /// <summary>Stops the current clip and drops everything queued.</summary>
    public void Clear() => Interlocked.Increment(ref _clearGeneration);

    /// <summary>True while a clip is playing or waiting to play.</summary>
    public bool IsSpeaking => _speaking || !_queue.IsEmpty;

    /// <summary>Mic gain while speech plays (0 = mute your mic, 1 = leave it). Default mute.</summary>
    public float DuckGain
    {
        get => _duckGain;
        set => _duckGain = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Mic gain at all times (Live translate sets 0 so only translations are heard).</summary>
    public float BaseMicGain
    {
        get => _baseMicGain;
        set => _baseMicGain = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Speech playback volume (0..2).</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 2f);
    }

    internal int ClearGeneration => _clearGeneration;
    internal bool TryDequeue(out SpeechClip clip) => _queue.TryDequeue(out clip!);
    internal void DrainQueue() { while (_queue.TryDequeue(out _)) { } }
    internal void SetSpeaking(bool value) => _speaking = value;
}

/// <summary>Audio-thread player for <see cref="SpeechInjector"/> (one per pipeline / device rate).</summary>
public sealed class SpeechStage(int sampleRate, SpeechInjector injector)
{
    private const double MicRampSeconds = 0.030;
    private const double HangSeconds = 0.250;
    private const double GapSeconds = 0.120;

    private readonly float _micStep = 1f / (float)Math.Max(1, sampleRate * MicRampSeconds);
    private readonly int _hangSamples = (int)(sampleRate * HangSeconds);
    private readonly int _gapSamples = (int)(sampleRate * GapSeconds);
    private SpeechClip? _clip;
    private double _position;
    private double _step;
    private int _hang;
    private int _gap;
    private int _seenGeneration = injector.ClearGeneration;
    private float _micGain = 1f;

    /// <summary>Mixes queued speech into the mic block and applies mic ducking. Allocation-free.</summary>
    public void Process(Span<float> buffer)
    {
        int generation = injector.ClearGeneration;
        if (generation != _seenGeneration)
        {
            _seenGeneration = generation;
            _clip = null;
            injector.DrainQueue();
        }

        float volume = injector.Volume;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (_clip == null && _gap <= 0 && injector.TryDequeue(out var next))
            {
                _clip = next;
                _position = 0;
                _step = (double)next.SampleRate / sampleRate;
            }

            float speech = 0f;
            if (_clip != null)
            {
                speech = Read(_clip.Samples, _position) * volume;
                _position += _step;
                if (_position >= _clip.Samples.Length - 1)
                {
                    _clip = null;
                    _gap = _gapSamples;
                }
                _hang = _hangSamples;
            }
            else if (_gap > 0)
            {
                _gap--;
            }
            else if (_hang > 0)
            {
                _hang--;
            }

            float target = injector.BaseMicGain * (_hang > 0 || _clip != null ? injector.DuckGain : 1f);
            _micGain = target > _micGain ? Math.Min(target, _micGain + _micStep) : Math.Max(target, _micGain - _micStep);
            buffer[i] = Math.Clamp(buffer[i] * _micGain + speech, -1f, 1f);
        }
        injector.SetSpeaking(_clip != null || _gap > 0);
    }

    private static float Read(float[] x, double position)
    {
        int i = (int)position;
        float t = (float)(position - i);
        float y0 = x[Math.Max(0, i - 1)], y1 = x[i], y2 = x[Math.Min(x.Length - 1, i + 1)], y3 = x[Math.Min(x.Length - 1, i + 2)];
        float c1 = 0.5f * (y2 - y0);
        float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
        float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return ((c3 * t + c2) * t + c1) * t + y1;
    }
}
