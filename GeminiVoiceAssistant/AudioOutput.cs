using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.TextToSpeech.V1;
using NAudio.Wave;

namespace GeminiVoiceAssistant
{
    public class AudioOutput : IDisposable
    {
        private TextToSpeechClient _textToSpeechClient;
        private WaveOutEvent _waveOutEvent; 
        private TaskCompletionSource<bool> _playbackFinishedTcs;
        private readonly TextToSpeechSettings _ttsSettings;

        // Default sample rate if not specified, especially for voices like WaveNet.
        // cs-CZ-Wavenet-A typically defaults to 24000 Hz if not specified in AudioConfig.
        private const int DefaultSampleRateForWaveNet = 24000; 

        public AudioOutput(TextToSpeechSettings ttsSettings)
        {
            _ttsSettings = ttsSettings ?? throw new ArgumentNullException(nameof(ttsSettings));
            if (string.IsNullOrEmpty(_ttsSettings.LanguageCode))
                throw new ArgumentException("LanguageCode in TextToSpeechSettings cannot be null or empty.", nameof(ttsSettings));
            if (string.IsNullOrEmpty(_ttsSettings.VoiceName))
                throw new ArgumentException("VoiceName in TextToSpeechSettings cannot be null or empty.", nameof(ttsSettings));

            try
            {
                _textToSpeechClient = TextToSpeechClient.Create();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating TextToSpeechClient: {ex.Message}");
                Console.WriteLine("Please ensure you have authenticated with Google Cloud, set GOOGLE_APPLICATION_CREDENTIALS,");
                Console.WriteLine("and that the Text-to-Speech API is enabled for your project.");
                throw;
            }
        }

        public async Task SpeakAsync(string textToSpeak, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(textToSpeak))
            {
                Console.WriteLine("AudioOutput: Text to speak is null or empty. Nothing to play.");
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("AudioOutput: SpeakAsync called with a CancellationToken already cancelled.");
                return;
            }

            Console.WriteLine($"AudioOutput: Requesting speech for: \"{textToSpeak}\"");

            var input = new SynthesisInput { Text = textToSpeak };
            var voiceSelection = new VoiceSelectionParams
            {
                LanguageCode = _ttsSettings.LanguageCode,
                Name = _ttsSettings.VoiceName
            };

            var audioConfig = new AudioConfig { AudioEncoding = AudioEncoding.Linear16 };
            // If SampleRateHertz is specified in settings, use it. 
            // Otherwise, the API will use the optimal/default for the selected voice.
            if (_ttsSettings.SampleRateHertz.HasValue && _ttsSettings.SampleRateHertz > 0)
            {
                // audioConfig.SampleRateHertz = _ttsSettings.SampleRateHertz.Value; // This is how you'd set it if you want to override API default
                // However, for Linear16, it's often better to let API decide and then use that for playback.
                // The SynthesizeSpeechResponse includes the effective sample rate if not specified in request,
                // but for LINEAR16, it's usually what the voice model is trained on (e.g. 24kHz for Wavenet).
            }


            SynthesizeSpeechResponse response;
            try
            {
                response = await _textToSpeechClient.SynthesizeSpeechAsync(new SynthesizeSpeechRequest
                {
                    Input = input,
                    Voice = voiceSelection,
                    AudioConfig = audioConfig
                });

                if (cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("AudioOutput: Cancellation requested after speech synthesis, before playback.");
                    return;
                }
                Console.WriteLine("AudioOutput: Speech synthesized successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioOutput: Error during text-to-speech synthesis: {ex.Message}");
                return; 
            }

            _waveOutEvent?.Dispose(); 
            _waveOutEvent = new WaveOutEvent();
            _playbackFinishedTcs = new TaskCompletionSource<bool>();

            using (var cancellationRegistration = cancellationToken.Register(() =>
            {
                Console.WriteLine("AudioOutput: Cancellation token triggered. Stopping playback.");
                _waveOutEvent?.Stop();
                _playbackFinishedTcs?.TrySetCanceled(cancellationToken);
            }))
            {
                MemoryStream memoryStream = null;
                RawSourceWaveStream waveStream = null;

                try
                {
                    memoryStream = new MemoryStream(response.AudioContent.ToByteArray());
                    
                    // Determine sample rate for NAudio:
                    // 1. Use SampleRateHertz from settings if provided and valid.
                    // 2. Otherwise, default to a known good value for the voice type (e.g., 24000 for WaveNet).
                    int sampleRateForPlayback = _ttsSettings.SampleRateHertz.HasValue && _ttsSettings.SampleRateHertz.Value > 0 
                                                ? _ttsSettings.SampleRateHertz.Value 
                                                : DefaultSampleRateForWaveNet;
                    // Note: Google TextToSpeech API for Linear16 output usually gives 24kHz for Wavenet voices.
                    // If using a standard voice, it might be 22050Hz or 16000Hz.
                    // It's safer to configure this in appsettings.json per voice if deviating from common defaults.

                    waveStream = new RawSourceWaveStream(memoryStream, new WaveFormat(sampleRateForPlayback, 16, 1)); // 16-bit, 1 channel (mono)

                    _waveOutEvent.Init(waveStream);
                    _waveOutEvent.PlaybackStopped += (sender, args) =>
                    {
                        if (args.Exception != null) _playbackFinishedTcs?.TrySetException(args.Exception);
                        else _playbackFinishedTcs?.TrySetResult(true);
                    };
                    
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine("AudioOutput: Cancellation requested just before starting playback.");
                        _playbackFinishedTcs?.TrySetCanceled(cancellationToken);
                    }
                    else
                    {
                        _waveOutEvent.Play();
                        Console.WriteLine($"AudioOutput: Playing audio ({response.AudioContent.Length} bytes, {sampleRateForPlayback} Hz)...");
                    }

                    await _playbackFinishedTcs.Task; 

                    if (cancellationToken.IsCancellationRequested) Console.WriteLine("AudioOutput: Playback was cancelled.");
                    else Console.WriteLine("AudioOutput: Playback finished naturally.");
                }
                catch (OperationCanceledException) { Console.WriteLine("AudioOutput: Playback task was cancelled."); }
                catch (Exception ex)
                {
                    Console.WriteLine($"AudioOutput: Error during playback: {ex.Message}");
                    if (_playbackFinishedTcs?.Task != null && !_playbackFinishedTcs.Task.IsCompleted)
                         _playbackFinishedTcs?.TrySetException(ex); 
                    if (!_playbackFinishedTcs.Task.IsCompleted) throw; 
                }
                finally
                {
                    _waveOutEvent?.Dispose(); // Ensure this is disposed before waveStream and memoryStream
                    _waveOutEvent = null;
                    waveStream?.Dispose();
                    memoryStream?.Dispose();
                    Console.WriteLine("AudioOutput: Playback resources disposed.");
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _waveOutEvent?.Stop(); 
                _waveOutEvent?.Dispose();
                _waveOutEvent = null;
<<<<<<< HEAD
                // TextToSpeechClient doesn't implement IDisposable in current version
=======
>>>>>>> cf126aa509fcfc425bd424be1918b9ee5280d417
            }
        }
    }
}

// Helper class for RpcException if not defined elsewhere
// This is also defined in AudioInput.cs, consider moving to a shared file if project grows
// public class RpcException : Exception
// {
//     public int StatusCode { get; }
//     public RpcException(string message, int statusCode) : base(message)
//     {
//         StatusCode = statusCode;
//     }
// }
