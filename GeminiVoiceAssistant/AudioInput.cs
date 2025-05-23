using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Speech.V1;
using Google.Protobuf;
using NAudio.Wave; 

namespace GeminiVoiceAssistant
{
    public class AudioInput : IDisposable
    {
        private SpeechClient _speechClient;
        private WaveInEvent _waveInEvent; 
        private SpeechClient.StreamingRecognizeStream _streamingCall;
        private TaskCompletionSource<bool> _responseHandlerCompletionSource;
        private StringBuilder _finalTranscriptBuilder;
        private readonly SpeechToTextSettings _sttSettings;

        private const int DefaultSampleRate = 16000; // Google Speech-to-Text prefers 16000 for Linear16
        private const int Channels = 1;       // Mono audio

        public AudioInput(SpeechToTextSettings sttSettings)
        {
            _sttSettings = sttSettings ?? throw new ArgumentNullException(nameof(sttSettings));
            if (string.IsNullOrEmpty(_sttSettings.LanguageCode))
            {
                throw new ArgumentException("LanguageCode in SpeechToTextSettings cannot be null or empty.", nameof(sttSettings));
            }

            try
            {
                _speechClient = SpeechClient.Create();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating SpeechClient: {ex.Message}");
                Console.WriteLine("Please ensure you have authenticated with Google Cloud and set up Application Default Credentials.");
                throw;
            }
        }

        public async Task<string> RecognizeSpeechAsync(CancellationToken cancellationToken, int maxDurationSeconds = 10)
        {
            _finalTranscriptBuilder = new StringBuilder();
            _responseHandlerCompletionSource = new TaskCompletionSource<bool>();
            bool recordingStarted = false;
            long bytesSent = 0;

            if (WaveIn.DeviceCount == 0)
            {
                Console.Error.WriteLine("No audio recording devices found.");
                return "Error: No audio recording devices found.";
            }
            
            _waveInEvent?.Dispose(); 
            _waveInEvent = new WaveInEvent
            {
                WaveFormat = new WaveFormat(DefaultSampleRate, Channels)
            };

            _streamingCall = _speechClient.StreamingRecognize();

            await _streamingCall.WriteAsync(new StreamingRecognizeRequest
            {
                StreamingConfig = new StreamingRecognitionConfig
                {
                    Config = new RecognitionConfig
                    {
                        Encoding = RecognitionConfig.Types.AudioEncoding.Linear16,
                        SampleRateHertz = DefaultSampleRate,
                        LanguageCode = _sttSettings.LanguageCode,
                        EnableAutomaticPunctuation = true,
                    },
                    InterimResults = true,
                }
            });

            var responseHandlerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var response in _streamingCall.GetResponseStream().WithCancellation(cancellationToken))
                    {
                        // WithCancellation will throw OperationCanceledException if token is cancelled.
                        if (response.Error != null)
                        {
                            Console.WriteLine($"API Error: {response.Error.Code} - {response.Error.Message}");
                            _responseHandlerCompletionSource.TrySetException(new System.ApplicationException($"Speech API Error: {response.Error.Message} (Code: {response.Error.Code})"));
                            return;
                        }

                        foreach (var result in response.Results)
                        {
                            if (result.IsFinal)
                            {
                                var transcript = result.Alternatives[0].Transcript;
                                _finalTranscriptBuilder.Append(transcript);
                                Console.WriteLine($"Final transcript segment: {transcript}");
                            }
                            else
                            {
                                Console.WriteLine($"Interim transcript: {result.Alternatives[0].Transcript}");
                            }
                        }
                    }
                    _responseHandlerCompletionSource.TrySetResult(true); 
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"AudioInput ResponseHandler: Operation cancelled. ");
                    _responseHandlerCompletionSource.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in response handler: {ex.Message}");
                    _responseHandlerCompletionSource.TrySetException(ex);
                }
            }, cancellationToken); // Pass token to Task.Run

            _waveInEvent.DataAvailable += async (sender, args) =>
            {
                if (cancellationToken.IsCancellationRequested) return; 
                if (args.BytesRecorded > 0 && _streamingCall != null && !_streamingCall.IsWriteNeeded) // Check if stream is still expecting data
                {
                    try
                    {
                        await _streamingCall.WriteAsync(new StreamingRecognizeRequest
                        {
                            AudioContent = ByteString.CopyFrom(args.Buffer, 0, args.BytesRecorded)
                        });
                        Interlocked.Add(ref bytesSent, args.BytesRecorded);
                    }
                    catch (Exception ex)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            Console.WriteLine($"Error sending audio data: {ex.Message}");
                        }
                    }
                }
            };

            _waveInEvent.RecordingStopped += (sender, args) =>
            {
                Console.WriteLine("AudioInput: NAudio recording stopped.");
            };
            
            Console.WriteLine("AudioInput: Starting audio capture for speech recognition...");
            try
            {
                _waveInEvent.StartRecording();
                recordingStarted = true;
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"Error starting NAudio recording: {ex.Message}");
                 _responseHandlerCompletionSource.TrySetException(ex); 
                 return $"Error starting audio capture: {ex.Message}";
            }

            var recordingStopTime = DateTime.UtcNow.AddSeconds(maxDurationSeconds);
            
            while (DateTime.UtcNow < recordingStopTime && !cancellationToken.IsCancellationRequested && !_responseHandlerCompletionSource.Task.IsCompleted)
            {
                try
                {
                    await Task.Delay(100, cancellationToken); 
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("AudioInput: Recording loop delay cancelled.");
                    break; 
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("AudioInput: Cancellation requested during recording loop.");
            }

            Console.WriteLine("AudioInput: Stopping audio capture...");
            if (recordingStarted)
            {
                _waveInEvent.StopRecording(); 
            }

            try
            {
                if (_streamingCall != null && _streamingCall.IsWriteNeeded) // Check if it's appropriate to call WriteCompleteAsync
                {
                    await _streamingCall.WriteCompleteAsync(); 
                    Console.WriteLine($"AudioInput: Finished sending audio. Total bytes sent: {bytesSent}");
                }
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"AudioInput: Error calling WriteCompleteAsync: {ex.Message}");
            }
            
            try
            {
                 await responseHandlerTask; // This now correctly awaits the task which is also observing the CancellationToken
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("AudioInput: Speech recognition task was cancelled (outer await).");
                return string.Empty; 
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioInput: An error occurred in response handler task (outer await): {ex.Message}");
            }

            if (_responseHandlerCompletionSource.Task.IsFaulted && _responseHandlerCompletionSource.Task.Exception != null)
            {
                 var aggEx = _responseHandlerCompletionSource.Task.Exception.Flatten();
                 foreach(var ex in aggEx.InnerExceptions)
                 {
                    Console.WriteLine($"AudioInput: Recognition failed with error: {ex.Message}");
                 }
                 return $"Error during recognition: {aggEx.InnerExceptions[0].Message}";
            }
            
            if (_finalTranscriptBuilder.Length == 0 && bytesSent > 0 && !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("AudioInput: No final transcript received, though audio was sent.");
                return "No speech detected or recognized.";
            }
            else if (bytesSent == 0 && !cancellationToken.IsCancellationRequested)
            {
                 Console.WriteLine("AudioInput: No audio data was sent to the API.");
                return "No audio data captured.";
            }

            return _finalTranscriptBuilder.ToString();
        }

        public static void ListAudioDevices()
        {
            for (int n = -1; n < WaveIn.DeviceCount; n++)
            {
                var caps = WaveIn.GetCapabilities(n);
                Console.WriteLine($"{n}: {caps.ProductName}");
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
                _waveInEvent?.StopRecording(); 
                _waveInEvent?.Dispose();
                _waveInEvent = null;
                _streamingCall = null; // gRPC call object management
                _speechClient?.Dispose(); 
                _speechClient = null;
            }
        }
    }
}
