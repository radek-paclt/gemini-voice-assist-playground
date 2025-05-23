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
            Console.WriteLine($"AudioInput: Starting speech recognition (max duration: {maxDurationSeconds}s)");
            _finalTranscriptBuilder = new StringBuilder();
            _responseHandlerCompletionSource = new TaskCompletionSource<bool>();
            bool recordingStarted = false;
            bool streamCompleted = false;
            long bytesSent = 0;

            if (NAudio.Wave.WaveInEvent.DeviceCount == 0)
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

            Console.WriteLine($"AudioInput: Configuring speech recognition for language: {_sttSettings.LanguageCode}");
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
                        MaxAlternatives = 1,
                        ProfanityFilter = false,
                        EnableWordTimeOffsets = false,
                        EnableWordConfidence = true, // Enable confidence scores
                        AudioChannelCount = 1,
                        EnableSeparateRecognitionPerChannel = false,
                        UseEnhanced = true, // Use enhanced model for better accuracy
                        // Add speech contexts for better Czech recognition
                        SpeechContexts = {
                            new SpeechContext
                            {
                                Phrases = { "haló", "slyším", "ano", "ne", "děkuji", "prosím" }
                            }
                        }
                    },
                    InterimResults = true,
                    SingleUtterance = false // Allow multiple utterances in one stream
                }
            });
            Console.WriteLine("AudioInput: Speech recognition stream initialized");

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
                            Console.WriteLine($"[DEBUG] Received result - IsFinal: {result.IsFinal}, Stability: {result.Stability}, ResultEndTime: {result.ResultEndTime}");
                            if (result.IsFinal)
                            {
                                var transcript = result.Alternatives[0].Transcript;
                                var confidence = result.Alternatives[0].Confidence;
                                _finalTranscriptBuilder.Append(transcript);
                                Console.WriteLine($"[FINAL] Final transcript segment: '{transcript}' (confidence: {confidence})");
                                
                                // If we have a final result, we can complete successfully even if cancelled later
                                if (!_responseHandlerCompletionSource.Task.IsCompleted)
                                {
                                    _responseHandlerCompletionSource.TrySetResult(true);
                                }
                            }
                            else
                            {
                                var transcript = result.Alternatives[0].Transcript;
                                Console.WriteLine($"[INTERIM] Interim transcript: '{transcript}' (stability: {result.Stability})");
                            }
                        }
                    }
                    // Only set result if not already completed (e.g., by final transcript above)
                    if (!_responseHandlerCompletionSource.Task.IsCompleted)
                    {
                        _responseHandlerCompletionSource.TrySetResult(true);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // If we have final results, don't treat cancellation as an error
                    if (_finalTranscriptBuilder.Length > 0)
                    {
                        if (!_responseHandlerCompletionSource.Task.IsCompleted)
                        {
                            _responseHandlerCompletionSource.TrySetResult(true);
                        }
                    }
                    else
                    {
                        _responseHandlerCompletionSource.TrySetCanceled(cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in response handler: {ex.Message}");
                    _responseHandlerCompletionSource.TrySetException(ex);
                }
            }, cancellationToken); // Pass token to Task.Run

            _waveInEvent.DataAvailable += async (sender, args) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (streamCompleted)
                {
                    return;
                }
                if (args.BytesRecorded > 0 && _streamingCall != null) // Check if stream is still active
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
                if (_streamingCall != null) // Check if stream is still active
                {
                    streamCompleted = true; // Set flag BEFORE calling WriteCompleteAsync
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
                Console.WriteLine("AudioInput: Speech recognition task was cancelled.");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioInput: An error occurred in response handler task: {ex.Message}");
            }

            if (_responseHandlerCompletionSource.Task.IsFaulted && _responseHandlerCompletionSource.Task.Exception != null)
            {
                 var aggEx = _responseHandlerCompletionSource.Task.Exception.Flatten();
                 // Only report as error if we don't have any final transcript
                 if (_finalTranscriptBuilder.Length == 0)
                 {
                     foreach(var ex in aggEx.InnerExceptions)
                     {
                        Console.WriteLine($"AudioInput: Recognition failed with error: {ex.Message}");
                     }
                     return $"Error during recognition: {aggEx.InnerExceptions[0].Message}";
                 }
                 else
                 {
                     Console.WriteLine($"AudioInput: Task faulted but we have final transcript, proceeding with result");
                 }
            }
            
            var finalResult = _finalTranscriptBuilder.ToString().Trim();
            
            if (finalResult.Length == 0 && bytesSent > 0 && !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("AudioInput: No final transcript received, though audio was sent.");
                return "No speech detected or recognized.";
            }
            else if (bytesSent == 0 && !cancellationToken.IsCancellationRequested)
            {
                 Console.WriteLine("AudioInput: No audio data was sent to the API.");
                return "No audio data captured.";
            }

            return finalResult;
        }

        public static void ListAudioDevices()
        {
            for (int n = -1; n < NAudio.Wave.WaveInEvent.DeviceCount; n++)
            {
                var caps = NAudio.Wave.WaveInEvent.GetCapabilities(n);
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
                // SpeechClient doesn't implement IDisposable in current version
                _speechClient = null;
            }
        }
    }
}
