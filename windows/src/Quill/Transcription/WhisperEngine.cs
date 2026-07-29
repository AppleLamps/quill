using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace Quill.Transcription;

/// On-device transcription with whisper.cpp through Whisper.net. Models are
/// GGML files cached under %LOCALAPPDATA%\quill\models and downloaded once on
/// first use; nothing but the model download ever touches the network.
internal sealed class WhisperEngine : ITranscriptionEngine
{
    private const int WhisperSampleRate = 16_000;
    private const string ModelHost = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    private readonly string _model;
    private WhisperFactory? _factory;

    public WhisperEngine(string model) => _model = model;

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

        var samples = ReadMono16k(audioPath);
        // whisper.cpp needs at least one full 1s window; anything shorter is
        // an empty or truncated track, not a transcript.
        if (samples.Length < WhisperSampleRate) return [];

        using var processor = _factory.CreateBuilder()
            .WithLanguage(_model.EndsWith(".en", StringComparison.Ordinal) ? "en" : "auto")
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            .Build();

        var segments = new List<TranscriptSegment>();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken)
                           .ConfigureAwait(false))
        {
            var text = segment.Text.Trim();
            if (text.Length > 0) segments.Add(new TranscriptSegment(segment.Start, segment.End, text));
        }
        return segments;
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

    /// Decode any WAV quill wrote (or the user dropped in) to the mono 16 kHz
    /// float stream whisper.cpp expects. The resampler is fully managed, so
    /// this works on Windows installs without Media Foundation.
    private static float[] ReadMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels > 1)
            provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (provider.WaveFormat.SampleRate != WhisperSampleRate)
            provider = new WdlResamplingSampleProvider(provider, WhisperSampleRate);

        var samples = new List<float>();
        var buffer = new float[WhisperSampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        return samples.ToArray();
    }
}
