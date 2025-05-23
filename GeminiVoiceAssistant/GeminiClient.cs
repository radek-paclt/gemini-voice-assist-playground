using System;
using System.Threading.Tasks;
using Google.Cloud.AIPlatform.V1;
// using Google.Protobuf.WellKnownTypes; // Not currently needed

namespace GeminiVoiceAssistant
{
    public class GeminiClient
    {
        private readonly PredictionServiceClient _predictionServiceClient;
        private readonly GoogleCloudSettings _gcSettings;
        private readonly GeminiSettings _geminiSettings;
        private readonly string _endpoint;

        public GeminiClient(GoogleCloudSettings gcSettings, GeminiSettings geminiSettings)
        {
            _gcSettings = gcSettings ?? throw new ArgumentNullException(nameof(gcSettings));
            _geminiSettings = geminiSettings ?? throw new ArgumentNullException(nameof(geminiSettings));

            if (string.IsNullOrEmpty(_gcSettings.ProjectId))
                throw new ArgumentException("ProjectId in GoogleCloudSettings cannot be null or empty.", nameof(gcSettings));
            if (string.IsNullOrEmpty(_gcSettings.Region))
                throw new ArgumentException("Region in GoogleCloudSettings cannot be null or empty.", nameof(gcSettings));
            if (string.IsNullOrEmpty(_geminiSettings.ModelId))
                throw new ArgumentException("ModelId in GeminiSettings cannot be null or empty.", nameof(geminiSettings));

            _endpoint = $"{_gcSettings.Region}-aiplatform.googleapis.com";
            
            try
            {
                _predictionServiceClient = new PredictionServiceClientBuilder
                {
                    Endpoint = _endpoint
                }.Build();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating PredictionServiceClient: {ex.Message}");
                Console.WriteLine($"Ensure you have authenticated with Google Cloud, set GOOGLE_APPLICATION_CREDENTIALS,");
                Console.WriteLine($"and that the Vertex AI API is enabled for project '{_gcSettings.ProjectId}'.");
                throw;
            }
        }

        public async Task<string> GenerateContentAsync(string inputText)
        {
            if (string.IsNullOrEmpty(inputText))
            {
                return "Input text cannot be empty.";
            }

            Console.WriteLine($"Sending request to Gemini model '{_geminiSettings.ModelId}' with input: \"{inputText}\"");

            var endpointName = EndpointName.FromProjectLocationPublisherModel(_gcSettings.ProjectId, _gcSettings.Region, "google", _geminiSettings.ModelId);
            
            var generateContentRequest = new GenerateContentRequest
            {
                Model = endpointName.ToString()
            };

            // Combine system prompt with user input if system prompt is configured
            string finalUserInput = inputText;
            if (!string.IsNullOrWhiteSpace(_geminiSettings.SystemPrompt))
            {
                finalUserInput = $"{_geminiSettings.SystemPrompt}\n\nUser: {inputText}";
            }

            // Add user input (with optional system prompt prepended)
            var userPrompt = new Content
            {
                Role = "USER",
                Parts = { new Part { Text = finalUserInput } }
            };
            generateContentRequest.Contents.Add(userPrompt);

            try
            {
                GenerateContentResponse response = await _predictionServiceClient.GenerateContentAsync(generateContentRequest);

                if (response.Candidates != null && response.Candidates.Count > 0)
                {
                    var candidate = response.Candidates[0];
                    if (candidate.Content != null && candidate.Content.Parts != null && candidate.Content.Parts.Count > 0)
                    {
                        return candidate.Content.Parts[0].Text;
                    }
                }
                return "No content generated or response format not as expected.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error calling Gemini API: {ex.Message}");
                return $"Error generating content: {ex.Message}";
            }
        }
    }
}
