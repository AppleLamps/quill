using System.Text.RegularExpressions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace Quill.Transcription;

/// On-device transcription with whisper.cpp through Whisper.net. Models are
/// GGML files cached under %LOCALAPPDATA%\quill\models and downloaded once on
/// first use; nothing but the model download ever touches the network.
internal sealed partial class WhisperEngine : ITranscriptionEngine
{
    private const int WhisperSampleRate = 16_000;
    private const int ChunkSeconds = 600;
    private const string ModelHost = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    private readonly string _model;
    private WhisperFactory? _factory;

    public WhisperEngine(string model)
    {
        // The model name goes into both a file path and a download URL, and it
        // comes from a config file — keep it to the shape of a real GGML name.
        if (!ModelName().IsMatch(model))
            throw new ArgumentException(
                $"invalid transcription model \"{model}\" — expected something like base.en or large-v3-turbo",
                nameof(model));
        _model = model;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_]{0,63}$")]
    private static partial Regex ModelName();

    public string Name => "whisper";
    public string Model => _model;

    public static string ModelsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "quill", "models");

    public static string ModelPath(string model) => Path.Combine(ModelsDirectory, $"ggml-{model}.bin");

    public static bool ModelIsCached(string model)
    {
        var path = ModelPath(model);
        return File.Exists(path) && new FileInfo(path).Length > 1_000_000;
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (_factory is not null) return;
        var path = await EnsureModelAsync(_model, cancellationToken).ConfigureAwait(false);
        _factory = WhisperFactory.FromPath(path);
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string audioPath, CancellationToken cancellationToken = default)
    {
        if (_factory is null) throw new InvalidOperationException("engine not prepared");

        using var audio = OpenMono16k(audioPath);
        var window = new float[ChunkSeconds * WhisperSampleRate];
        var segments = new List<TranscriptSegment>();
        var offset = TimeSpan.Zero;
        var carried = 0;

        // whisper.cpp wants the samples in memory, so a two-hour meeting read
        // whole would be half a gigabyte of float on top of the model. Feed it
        // in windows instead and shift each window's timestamps back onto the
        // track's clock.
        while (true)
        {
            // Fill() and the decode behind it are synchronous, so a cancel
            // lands at the next chunk boundary rather than immediately.
            cancellationToken.ThrowIfCancellationRequested();

            var filled = Fill(audio, window, carried);
            var last = filled < window.Length;
            // Cut the window at its quietest point rather than at an arbitrary
            // sample, so a chunk boundary doesn't land mid-word; whatever is
            // past the cut starts the next window.
            var cut = last ? filled : QuietestPoint(window, filled);

            // whisper.cpp needs at least one full 1s window; a shorter tail is
            // silence or a truncated track, not a transcript.
            if (cut >= WhisperSampleRate)
            {
                using var processor = _factory.CreateBuilder()
                    .WithLanguage(_model.EndsWith(".en", StringComparison.Ordinal) ? "en" : "auto")
                    .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
                    .Build();

                await foreach (var segment in processor
                                   .ProcessAsync(window.AsMemory(0, cut), cancellationToken)
                                   .ConfigureAwait(false))
                {
                    var text = segment.Text.Trim();
                    if (text.Length > 0)
                        segments.Add(new TranscriptSegment(
                            segment.Start + offset, segment.End + offset, text));
                }
            }
            if (last) break;

            offset += TimeSpan.FromSeconds(cut / (double)WhisperSampleRate);
            carried = filled - cut;
            window.AsSpan(cut, carried).CopyTo(window);
        }
        return segments;
    }

    /// Start of the quietest 100 ms in the last minute of a full window — the
    /// least bad place to split a chunk. Never returns a point in the first
    /// half of the window, so progress is guaranteed.
    private static int QuietestPoint(float[] window, int filled)
    {
        const int frame = WhisperSampleRate / 10;
        var earliest = Math.Max(filled / 2, filled - 60 * WhisperSampleRate);
        var quietest = earliest;
        var quietestEnergy = float.MaxValue;

        for (var start = earliest; start + frame <= filled; start += frame)
        {
            var energy = 0f;
            for (var i = start; i < start + frame; i++) energy += Math.Abs(window[i]);
            if (energy >= quietestEnergy) continue;
            quietestEnergy = energy;
            quietest = start;
        }
        return quietest;
    }

    /// Read from `offset` until the buffer is full or the track ends, and
    /// return the total sample count in it. One Read() call can return a short
    /// count at any resampler boundary.
    private static int Fill(ISampleProvider provider, float[] buffer, int offset)
    {
        var filled = offset;
        int read;
        while (filled < buffer.Length &&
               (read = provider.Read(buffer, filled, buffer.Length - filled)) > 0)
            filled += read;
        return filled;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }

    /// Download the GGML model if it isn't cached yet. The download lands in a
    /// temp file and is renamed into place, so an interrupted download never
    /// leaves a half model that looks cached.
    public static async Task<string> EnsureModelAsync(
        string model, CancellationToken cancellationToken = default)
    {
        var path = ModelPath(model);
        if (ModelIsCached(model)) return path;

        Directory.CreateDirectory(ModelsDirectory);
        var url = $"{ModelHost}/ggml-{model}.bin";
        Console.Error.WriteLine($"downloading whisper model {model} → {path}");

        var temp = path + ".part";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var destination = File.Create(temp);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// Decode any WAV quill wrote (or the user dropped in) as the mono 16 kHz
    /// float stream whisper.cpp expects. The resampler is fully managed, so
    /// this works on Windows installs without Media Foundation.
    private static Mono16kReader OpenMono16k(string path)
    {
        var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels > 1) provider = new DownmixSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != WhisperSampleRate)
            provider = new WdlResamplingSampleProvider(provider, WhisperSampleRate);
        return new Mono16kReader(reader, provider);
    }

    /// Average every channel into one. NAudio's multiplexer *selects* channels
    /// rather than mixing them, which on a 5.1 track would throw away the
    /// centre channel — where dialogue lives.
    private sealed class DownmixSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _interleaved = [];

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var wanted = count * _channels;
            if (_interleaved.Length < wanted) _interleaved = new float[wanted];

            var read = source.Read(_interleaved, 0, wanted);
            var frames = read / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++)
                    sum += _interleaved[frame * _channels + channel];
                buffer[offset + frame] = sum / _channels;
            }
            return frames;
        }
    }

    /// A sample provider that owns the file handle behind it.
    private sealed class Mono16kReader(AudioFileReader file, ISampleProvider provider)
        : ISampleProvider, IDisposable
    {
        public WaveFormat WaveFormat => provider.WaveFormat;
        public int Read(float[] buffer, int offset, int count) => provider.Read(buffer, offset, count);
        public void Dispose() => file.Dispose();
    }
}
