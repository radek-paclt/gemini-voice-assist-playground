using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace GeminiVoiceAssistant
{
    class Program
    {
        private static AppSettings _appSettings;

        static async Task Main(string[] args)
        {
            // Load Configuration
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            IConfigurationRoot configuration = builder.Build();
            _appSettings = configuration.Get<AppSettings>();

            Console.WriteLine("Gemini Voice Assistant - Main Loop");
            Console.WriteLine("----------------------------------------");

            // 1. Setup and Configuration Check
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: The GOOGLE_APPLICATION_CREDENTIALS environment variable is not set.");
                Console.WriteLine("Please set it to the path of your Google Cloud service account key JSON file.");
                Console.ResetColor();
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
                return;
            }

            if (string.IsNullOrEmpty(_appSettings?.GoogleCloud?.ProjectId) || _appSettings.GoogleCloud.ProjectId == "your-project-id")
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: The GoogleCloud.ProjectId in appsettings.json is not configured or is still set to 'your-project-id'.");
                Console.WriteLine("Please update it with your actual Google Cloud Project ID.");
                Console.ResetColor();
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
                return;
            }
            
            Console.WriteLine($"Using Project ID: {_appSettings.GoogleCloud.ProjectId}");
            Console.WriteLine($"Using Region: {_appSettings.GoogleCloud.Region}");
            Console.WriteLine($"Using Gemini Model: {_appSettings.Gemini.ModelId}");


            AudioInput audioInput = null;
            AudioOutput audioOutput = null;
            GeminiClient geminiClient = null;

            try
            {
                Console.WriteLine("Initializing audio and AI clients...");
                // Pass the relevant settings to the constructors
                audioInput = new AudioInput(_appSettings.SpeechToText);
                audioOutput = new AudioOutput(_appSettings.TextToSpeech);
                geminiClient = new GeminiClient(_appSettings.GoogleCloud, _appSettings.Gemini);
                Console.WriteLine("Clients initialized successfully.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error during client initialization: {ex.Message}");
                Console.WriteLine("Ensure Google Cloud APIs (Speech-to-Text, Text-to-Speech, Vertex AI) are enabled and authentication is correct.");
                Console.ResetColor();
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
                return;
            }
            
            CancellationTokenSource appCts = new CancellationTokenSource();

            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                Console.WriteLine("\nCtrl+C detected. Shutting down...");
                eventArgs.Cancel = true; 
                appCts.Cancel(); // Signal all operations to cancel
            };
            
            string currentInput = null; 

            // 2. Main Interaction Loop
            while (!appCts.IsCancellationRequested)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(currentInput)) 
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("\nListening for your command... (Ctrl+C to exit)");
                        Console.ResetColor();
                        currentInput = await audioInput.RecognizeSpeechAsync(appCts.Token, 15);

                        if (appCts.IsCancellationRequested) break;

                        if (string.IsNullOrWhiteSpace(currentInput) || currentInput.StartsWith("Error:") || currentInput.StartsWith("No speech detected") || currentInput.StartsWith("No audio data captured"))
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine("No valid speech detected or input was empty. Listening again.");
                            Console.ResetColor();
                            currentInput = null; 
                            continue;
                        }
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"You said: {currentInput}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.WriteLine($"Processing carried-over input: {currentInput}");
                        Console.ResetColor();
                    }

                    Console.WriteLine("Sending to Gemini...");
                    string geminiResponse = await geminiClient.GenerateContentAsync(currentInput);
                    currentInput = null; 

                    if (appCts.IsCancellationRequested) break;

                    if (string.IsNullOrWhiteSpace(geminiResponse) || geminiResponse.StartsWith("Error generating content:"))
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Gemini did not provide a valid response. Try again.");
                        Console.ResetColor();
                        continue;
                    }
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Console.WriteLine($"Gemini: {geminiResponse}");
                    Console.ResetColor();

                    using (var aiSpeakingCts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token))
                    {
                        string nextUserInputFromInterruption = null;

                        Console.WriteLine("AI is speaking. Listening for interruption...");
                        Task speakTask = audioOutput.SpeakAsync(geminiResponse, aiSpeakingCts.Token);
                        
                        Task listenForInterruptionTask = Task.Run(async () =>
                        {
                            try
                            {
                                string interruption = await audioInput.RecognizeSpeechAsync(aiSpeakingCts.Token, 60); 
                                if (!string.IsNullOrWhiteSpace(interruption) && 
                                    !interruption.StartsWith("Error:") && 
                                    !interruption.StartsWith("No speech detected") && 
                                    !interruption.StartsWith("No audio data captured") &&
                                    !aiSpeakingCts.IsCancellationRequested)
                                {
                                    Console.ForegroundColor = ConsoleColor.Red;
                                    Console.WriteLine($"--- Interruption detected: {interruption} ---");
                                    Console.ResetColor();
                                    nextUserInputFromInterruption = interruption;
                                    if (!aiSpeakingCts.IsCancellationRequested) aiSpeakingCts.Cancel(); 
                                }
                            }
                            catch (OperationCanceledException) { Console.WriteLine("Interruption listener task was cancelled (expected)."); }
                            catch (Exception ex) { Console.WriteLine($"Error in interruption listener: {ex.Message}"); }
                        }, aiSpeakingCts.Token); // Pass token to Task.Run as well if supported or handle within

                        await Task.WhenAny(speakTask, listenForInterruptionTask);

                        if (!aiSpeakingCts.IsCancellationRequested)
                        {
                            Console.WriteLine("AI finished speaking naturally. Cancelling interruption listener.");
                            aiSpeakingCts.Cancel();
                        }
                        
                        try { await speakTask; Console.WriteLine("SpeakAsync task completed."); }
                        catch (OperationCanceledException) { Console.WriteLine("AI speaking was interrupted or cancelled."); }
                        catch (Exception ex) { Console.WriteLine($"Error during SpeakAsync: {ex.Message}");}

                        try { await listenForInterruptionTask; Console.WriteLine("ListenForInterruptionTask task completed."); }
                        catch (OperationCanceledException) { Console.WriteLine("Interruption listener task was cancelled (as expected after WhenAny or explicit cancel).");}
                        catch (Exception ex) { Console.WriteLine($"Error in listenForInterruptionTask (outer): {ex.Message}");}

                        if (!string.IsNullOrWhiteSpace(nextUserInputFromInterruption))
                        {
                            currentInput = nextUserInputFromInterruption; 
                            Console.ForegroundColor = ConsoleColor.Magenta;
                            Console.WriteLine("Processing interrupted input for the next cycle...");
                            Console.ResetColor();
                            continue; 
                        }
                    } 
                }
                catch (OperationCanceledException) when (appCts.IsCancellationRequested)
                {
                    Console.WriteLine("Main loop cancelled due to application shutdown request.");
                    break; 
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.WriteLine($"\nAn unexpected error occurred in the main loop: {ex.Message}");
                    Console.WriteLine("The loop will attempt to continue. Press Ctrl+C to exit if issues persist.");
                    Console.ResetColor();
                    currentInput = null; 
                    await Task.Delay(1000, appCts.Token); 
                }
            } // End of while loop
            
            // Cleanup is handled in the finally block outside Main
            Console.WriteLine("\nExiting main loop. Disposing resources...");
            audioInput?.Dispose();
            audioOutput?.Dispose();
            // (geminiClient as IDisposable)?.Dispose(); // If GeminiClient were IDisposable
            Console.WriteLine("All resources disposed. Exiting application.");
        }
        // Removed static finally block, cleanup is at the end of Main's try-catch-finally logic or if Main throws.
        // For console apps, OS handles process exit. Explicit cleanup at end of Main is good practice.
    }
}
